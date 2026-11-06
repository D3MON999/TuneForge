# Runs inside Windows Sandbox only. Changes DNS for real and checks each change took effect.
param([string] $App = 'C:\tf\app', [string] $Out = 'C:\tf\out')

$ErrorActionPreference = 'Continue'
if (-not (Test-Path -LiteralPath 'C:\Users\WDAGUtilityAccount')) { throw 'This script must only run inside Windows Sandbox.' }

$cli = Join-Path $App 'tuneforge-cli.exe'
$log = Join-Path $Out 'network-results.txt'
function Log([string] $Text) { Add-Content -LiteralPath $log -Value $Text -Encoding utf8 }
function Servers { ((& $cli dns status | ConvertFrom-Json).servers | Where-Object { $_ -notmatch ':' }) -join ',' }

Log "start $(Get-Date -Format o)"
Log "system exit=$(& $cli system | Out-Null; $LASTEXITCODE)"
Log "live :: $((& $cli live | ConvertFrom-Json | ConvertTo-Json -Compress))"
Log "health :: $((& $cli health | ConvertFrom-Json).score)"
& $cli report 'C:\tf\out\sandbox-report.txt' | ForEach-Object { Log $_ }

$original = Servers
Log "original=$original"
$result = & $cli dns set cloudflare
Log "set exit=$LASTEXITCODE :: $result :: now=$(Servers)"
$result = & $cli dns set google
Log "second exit=$LASTEXITCODE :: $result :: now=$(Servers)"
Log "resolve :: $([bool](Resolve-DnsName example.com -Type A -ErrorAction SilentlyContinue))"
$result = & $cli dns custom 9.9.9.9 149.112.112.112
Log "custom exit=$LASTEXITCODE :: $result :: now=$(Servers)"
$result = & $cli dns set previous
Log "previous exit=$LASTEXITCODE :: $result :: now=$(Servers) match=$((Servers) -eq $original)"
$result = & $cli dns set previous 2>&1
Log "previous-again exit=$LASTEXITCODE :: $result"
$result = & $cli dns set quad9
Log "quad9 exit=$LASTEXITCODE :: now=$(Servers)"
$result = & $cli dns set automatic
Log "automatic exit=$LASTEXITCODE :: $result :: now=$(Servers)"
$result = & $cli dns set not-real 2>&1
Log "bad exit=$LASTEXITCODE :: $result"

Log "done $(Get-Date -Format o)"
Set-Content -LiteralPath (Join-Path $Out 'network-finished.txt') -Value 'ok'
