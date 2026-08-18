param(
    [string]$Configuration = "release"
)

$ErrorActionPreference = "Stop"

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$electronDist = Join-Path $projectRoot "node_modules\electron\dist"
$rustCore = Join-Path $projectRoot "rust-core\target\$Configuration\nexus-rust-core.exe"
$webDist = Join-Path $projectRoot "dist"
$portableParent = Join-Path $projectRoot "output\portable"
$portableRoot = Join-Path $portableParent "Nexus 2.0"
$appRoot = Join-Path $portableRoot "resources\app"
$runtimeBin = Join-Path $portableRoot "resources\bin"

foreach ($requiredPath in @(
    (Join-Path $electronDist "electron.exe"),
    $rustCore,
    (Join-Path $webDist "index.html"),
    (Join-Path $projectRoot "package.json")
)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "缺少打包输入：$requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $portableParent | Out-Null
$resolvedParent = (Resolve-Path -LiteralPath $portableParent).Path.TrimEnd('\')
$expectedRoot = [System.IO.Path]::GetFullPath($portableRoot).TrimEnd('\')
if (-not $expectedRoot.StartsWith("$resolvedParent\", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "便携包输出路径越出预期目录：$expectedRoot"
}
if (Test-Path -LiteralPath $portableRoot) {
    $existingRoot = (Resolve-Path -LiteralPath $portableRoot).Path.TrimEnd('\')
    if ($existingRoot -ne $expectedRoot) {
        throw "拒绝清理未确认的输出目录：$existingRoot"
    }
    Remove-Item -LiteralPath $existingRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $portableRoot, $appRoot, $runtimeBin | Out-Null
Copy-Item -Path (Join-Path $electronDist "*") -Destination $portableRoot -Recurse -Force
Move-Item -LiteralPath (Join-Path $portableRoot "electron.exe") -Destination (Join-Path $portableRoot "Nexus 2.0.exe")

# 替换 exe 图标(2026-08-17:不再使用 Electron 默认图标)
$rcedit = Join-Path $projectRoot "node_modulesceditincedit-x64.exe"
$icon = Join-Path $projectRoot "icon.ico"
if ((Test-Path -LiteralPath $rcedit) -and (Test-Path -LiteralPath $icon)) {
  & $rcedit (Join-Path $portableRoot "Nexus 2.0.exe") --set-icon $icon
  if ($LASTEXITCODE -ne 0) { throw "rcedit 设置图标失败(exit $LASTEXITCODE)" }
} else {
  Write-Warning "未找到 rcedit 或 icon.ico,跳过图标设置(继续使用 Electron 默认图标)"
}

Copy-Item -LiteralPath (Join-Path $projectRoot "package.json") -Destination $appRoot
Copy-Item -LiteralPath (Join-Path $projectRoot "THIRD_PARTY_NOTICES.md") -Destination $appRoot
Copy-Item -LiteralPath (Join-Path $projectRoot "electron") -Destination $appRoot -Recurse
Copy-Item -LiteralPath $webDist -Destination $appRoot -Recurse
Copy-Item -LiteralPath $rustCore -Destination (Join-Path $runtimeBin "nexus-rust-core.exe")

$sourceNodeModules = Join-Path $projectRoot "node_modules"
$targetNodeModules = Join-Path $appRoot "node_modules"
$sourceNodeModulesPrefix = $sourceNodeModules.TrimEnd('\') + '\'
New-Item -ItemType Directory -Force -Path $targetNodeModules | Out-Null
$dependencyPaths = @(& npm.cmd ls --omit=dev --parseable --all 2>$null)
if ($LASTEXITCODE -ne 0) {
    throw "无法读取 npm 生产依赖树。"
}
foreach ($dependencyPath in $dependencyPaths) {
    if (-not $dependencyPath -or $dependencyPath -eq $projectRoot) {
        continue
    }
    if (-not $dependencyPath.StartsWith($sourceNodeModulesPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "npm 返回了 node_modules 之外的依赖路径：$dependencyPath"
    }
    $relative = $dependencyPath.Substring($sourceNodeModulesPrefix.Length)
    if ($relative -match '(^|\\)node_modules(\\|$)') {
        continue
    }
    $destination = Join-Path $targetNodeModules $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath $dependencyPath -Destination $destination -Recurse
}

$packageJson = Get-Content -LiteralPath (Join-Path $projectRoot "package.json") -Raw | ConvertFrom-Json
$rustHash = (Get-FileHash -LiteralPath $rustCore -Algorithm SHA256).Hash
$buildInfo = @(
    "Product=Nexus 2.0"
    "Version=$($packageJson.version)"
    "PackagedAtUtc=$([DateTime]::UtcNow.ToString('O'))"
    "Electron=$($packageJson.devDependencies.electron)"
    "RustCoreSha256=$rustHash"
) -join "`r`n"
Set-Content -LiteralPath (Join-Path $portableRoot "BUILD-INFO.txt") -Value $buildInfo -Encoding utf8NoBOM

$size = (Get-ChildItem -LiteralPath $portableRoot -Recurse -File | Measure-Object Length -Sum).Sum
[pscustomobject]@{
    PortableRoot = $portableRoot
    Executable = Join-Path $portableRoot "Nexus 2.0.exe"
    RustCore = Join-Path $runtimeBin "nexus-rust-core.exe"
    Bytes = $size
    MiB = [math]::Round($size / 1MB, 2)
}
