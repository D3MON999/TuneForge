# Runs inside Windows Sandbox only. Turns every system switch on and off for real.
param([string] $App = 'C:\tf\app', [string] $Out = 'C:\tf\out')

$ErrorActionPreference = 'Continue'
if (-not (Test-Path -LiteralPath 'C:\Users\WDAGUtilityAccount')) { throw 'This script must only run inside Windows Sandbox.' }

$cli = Join-Path $App 'tuneforge-cli.exe'
$log = Join-Path $Out 'switches-results.txt'
function Log([string] $Text) { Add-Content -LiteralPath $log -Value $Text -Encoding utf8 }

Log "start $(Get-Date -Format o)"
$items = & $cli advanced list | Out-String | ConvertFrom-Json | ForEach-Object { $_ }
Log "switches=$(@($items).Count)"
foreach ($item in $items) {
    $id = $item.id
    if ($id -eq 'remove-onedrive' -or $id -eq 'openssh-server') { Log "skip $id"; continue }
    $on = & $cli advanced on $id 2>&1
    $onCode = $LASTEXITCODE
    $off = & $cli advanced off $id 2>&1
    $offCode = $LASTEXITCODE
    Log "$id on=$onCode off=$offCode :: $on :: $off"
}

Log '--- speed'
& $cli speedtest | Out-String | ForEach-Object { Log ($_ -replace '\s+', ' ') }

Log '--- limits'
$set = & $cli limit testapp.exe 10 2>&1
Log "set exit=$LASTEXITCODE :: $set"
Log "list :: $((& $cli limits | Out-String) -replace '\s+', ' ')"
$removed = & $cli limit-remove testapp.exe 2>&1
Log "remove exit=$LASTEXITCODE :: $removed"
Log "list :: $((& $cli limits | Out-String) -replace '\s+', ' ')"

Log "done $(Get-Date -Format o)"
Set-Content -LiteralPath (Join-Path $Out 'switches-finished.txt') -Value 'ok'
