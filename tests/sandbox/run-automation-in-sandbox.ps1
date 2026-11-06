# Runs inside Windows Sandbox only. Checks the real memory cleaner and the Start with Windows setting.
param([string] $App = 'C:\tf\app', [string] $Out = 'C:\tf\out')

$ErrorActionPreference = 'Continue'
if (-not (Test-Path -LiteralPath 'C:\Users\WDAGUtilityAccount')) { throw 'This script must only run inside Windows Sandbox.' }

$cli = Join-Path $App 'tuneforge-cli.exe'
$log = Join-Path $Out 'automation-results.txt'
function Log([string] $Text) { Add-Content -LiteralPath $log -Value $Text -Encoding utf8 }

Log "start $(Get-Date -Format o)"

$memory = & $cli memory clean | ConvertFrom-Json
Log "memory exit=$LASTEXITCODE processes=$($memory.processes) beforeMB=$([int]($memory.usedBeforeBytes / 1MB)) afterMB=$([int]($memory.usedAfterBytes / 1MB)) freedMB=$([int]($memory.freedBytes / 1MB))"
Log "explorer-style services still running=$(@(Get-Service | Where-Object Status -eq Running).Count)"

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Log "autostart before=$(& $cli autostart status)"
Log "autostart on=$(& $cli autostart on) value=$((Get-ItemProperty -Path $run -Name TuneForge -ErrorAction SilentlyContinue).TuneForge)"
Log "autostart off=$(& $cli autostart off) gone=$($null -eq (Get-ItemProperty -Path $run -Name TuneForge -ErrorAction SilentlyContinue))"

Log "done $(Get-Date -Format o)"
Set-Content -LiteralPath (Join-Path $Out 'automation-finished.txt') -Value 'ok'
