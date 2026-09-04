[CmdletBinding()]
param(
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]] $CargoArgs
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolchainPreflight = Join-Path $repoRoot "scripts\toolchain-preflight.cjs"
if (Test-Path -LiteralPath $toolchainPreflight) {
    & node $toolchainPreflight
    if ($LASTEXITCODE -ne 0) {
        throw "工具链 preflight 失败(exit $LASTEXITCODE)。"
    }
}

function Find-Library([string] $fileName, [string[]] $roots) {
  foreach ($root in $roots) {
    if (-not (Test-Path -LiteralPath $root)) { continue }
    $hit = Get-ChildItem -LiteralPath $root -Filter $fileName -File -Recurse -ErrorAction SilentlyContinue |
      Where-Object { $_.FullName -match '\\x64\\' } |
      Select-Object -First 1
    if ($hit) { return $hit.FullName }
  }
  return $null
}

$programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
$msvcRoots = @(
  "E:\\VS Studio\\VC\\Tools\\MSVC",
  "C:\\Program Files\\Microsoft Visual Studio",
  "C:\\Program Files (x86)\\Microsoft Visual Studio"
)
$sdkRoots = @(
  (Join-Path $programFilesX86 "Windows Kits\\10\\Lib"),
  "C:\\Program Files\\Windows Kits\\10\\Lib"
)

$msvcrt = Find-Library "msvcrt.lib" $msvcRoots
if (-not $msvcrt) {
  throw "未找到 x64 msvcrt.lib；请安装 MSVC C++ 工具后再运行。"
}

$sdkRoot = $sdkRoots | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $sdkRoot) {
  throw "未找到 Windows SDK Lib 目录；请安装 Windows 10/11 SDK 后再运行。"
}
$sdkVersion = Get-ChildItem -LiteralPath $sdkRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdkVersion) {
  throw "Windows SDK Lib 目录为空。"
}
$sdkLibRoot = $sdkVersion.FullName
$ucrt = Join-Path $sdkLibRoot "ucrt\\x64"
$um = Join-Path $sdkLibRoot "um\\x64"
if (-not (Test-Path -LiteralPath $ucrt) -or -not (Test-Path -LiteralPath $um)) {
  throw "Windows SDK 缺少 ucrt\\x64 或 um\\x64 库目录：$sdkLibRoot"
}

$previousLib = $env:LIB
$previousCorePath = $env:NEXUS_RUST_CORE_PATH
try {
  # 只修改当前 PowerShell 进程，避免把机器级 LIB 或 VS 配置改成隐式状态。
  $env:LIB = "$(Split-Path -Parent $msvcrt);$ucrt;$um"
  Push-Location $repoRoot
  & cargo test --manifest-path rust-core/Cargo.toml --test s7_jsonl_e2e --test enip_jsonl_e2e --test ads_jsonl_e2e --test mqtt_jsonl_e2e --test iec104_jsonl_e2e --test dnp3_jsonl_e2e --test dlt645_jsonl_e2e --test cjt188_jsonl_e2e --test bacnet_ip_jsonl_e2e --test knx_jsonl_e2e --test keyence_jsonl_e2e --test ls_xgt_jsonl_e2e --test panasonic_jsonl_e2e --test delta_jsonl_e2e --test inovance_jsonl_e2e --test xinjie_jsonl_e2e --test fatek_jsonl_e2e --test fuji_sph_jsonl_e2e --test ge_srtp_jsonl_e2e --test custom_frame_jsonl_e2e @CargoArgs
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  # 独立 MQTT Broker 互操作测试需要普通 sidecar 可执行文件，而不是 cargo test harness。
  & cargo build --manifest-path rust-core/Cargo.toml --bin nexus-rust-core
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
  $env:NEXUS_RUST_CORE_PATH = Join-Path $repoRoot "rust-core\target\debug\nexus-rust-core.exe"
  & node --test scripts/mqtt-aedes-integration.test.cjs
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  # Independent BACnet/IP stack interoperability (bacstack is a separate MIT
  # JavaScript implementation, not the Rust production codec).
  & node --test scripts/bacnet-bacstack-integration.test.cjs
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  # Independent KNXnet/IP codec interoperability (knx is a separate MIT
  # JavaScript implementation, not the Rust production codec).
  & node --test scripts/knx-independent-stack-integration.test.cjs
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

  # Keep the R0 soak harness itself executable; this short run does not
  # replace the separately scheduled 1h/8h soak evidence.
  & node --test scripts/soak-r0.test.cjs
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
  Pop-Location
  $env:LIB = $previousLib
  $env:NEXUS_RUST_CORE_PATH = $previousCorePath
}
