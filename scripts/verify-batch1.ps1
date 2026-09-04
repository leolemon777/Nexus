# Batch-1 verification runner for docs/spec-plan-serial-plot-parse-replay.md
# Runs the four gates in order: Electron tests -> Vite build -> UI layout audit -> smoke.
# audit-ui-layout and smoke load dist/index.html, so the build must run before them.
#
# Usage (from anywhere, no Git Bash required):
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-batch1.ps1
# Logs are written to evidence\b1\<step>-<timestamp>.log
# Exit code = number of failed steps (0 = all green).

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$outDir = Join-Path $root "evidence\b1"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"

$steps = @(
  @{ Name = "01-test-electron";   Cmd = "npm";  Args = @("run", "test:electron") },
  @{ Name = "02-vite-build";      Cmd = "npm";  Args = @("run", "build") },
  @{ Name = "03-audit-ui-layout"; Cmd = "node"; Args = @("scripts/audit-ui-layout.cjs") },
  @{ Name = "04-smoke-electron";  Cmd = "npm";  Args = @("run", "smoke:electron") }
)

$results = @()
foreach ($step in $steps) {
  $log = Join-Path $outDir ("{0}-{1}.log" -f $step.Name, $stamp)
  Write-Host ""
  Write-Host ("==> {0} ..." -f $step.Name) -ForegroundColor Cyan
  $global:LASTEXITCODE = 0
  & $step.Cmd @($step.Args) 2>&1 | Tee-Object -FilePath $log
  $exit = 0
  if ($null -ne $LASTEXITCODE) { $exit = $LASTEXITCODE }
  $results += [pscustomobject]@{ Step = $step.Name; ExitCode = $exit; Log = $log }
}

Write-Host ""
Write-Host "==== Batch-1 verification summary ====" -ForegroundColor Cyan
$failed = 0
foreach ($r in $results) {
  $status = "PASS"
  $color = "Green"
  if ($r.ExitCode -ne 0) { $status = "FAIL"; $color = "Red"; $failed++ }
  Write-Host ("{0}  {1}  (exit {2})  log: {3}" -f $status, $r.Step, $r.ExitCode, $r.Log) -ForegroundColor $color
}
Write-Host ""
if ($failed -eq 0) {
  Write-Host "All four gates green. Batch 1 can be closed." -ForegroundColor Green
} else {
  Write-Host ("{0} step(s) failed - see logs above." -f $failed) -ForegroundColor Red
}
exit $failed
