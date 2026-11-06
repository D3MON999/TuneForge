# Runs inside Windows Sandbox only, as a standard user with no admin rights.
# Boosts a stand-in game for real, checks everything is undone, and tries the power plan tools.
param([string] $App = 'C:\tf\app')

$ErrorActionPreference = 'Continue'
if (-not (Test-Path -LiteralPath 'C:\Users\WDAGUtilityAccount')) { throw 'This script must only run inside Windows Sandbox.' }

$cli = Join-Path $App 'tuneforge-cli.exe'
$log = 'C:\Users\Public\tf-gaming.txt'
function Log([string] $Text) { Add-Content -LiteralPath $log -Value $Text -Encoding utf8 }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Log "start user=$env:USERNAME admin=$admin"

$dir = "$env:LOCALAPPDATA\tf-fake"
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item "$env:WINDIR\System32\ping.exe" "$dir\fakegame.exe" -Force
$plans = & $cli power list | ConvertFrom-Json
Log "plans :: $(($plans | ForEach-Object { "$($_.name)$(if($_.active){'*'})" }) -join ', ')"

Start-Process "$dir\fakegame.exe" -ArgumentList '-n','12','127.0.0.1' -WindowStyle Hidden
Start-Sleep -Seconds 1
& $cli boost-test fakegame "$dir\settings.json" | ForEach-Object { Log $_ }
Log "boost exit=$LASTEXITCODE"
Log "settings boost left=$((Get-Content "$dir\settings.json" -Raw | ConvertFrom-Json).boost)"

function Plans { (& $cli power list | ConvertFrom-Json) | ForEach-Object { $_ } }

$count = @(Plans).Count
$added = & $cli power add-ultimate
$code = $LASTEXITCODE
Log "add-ultimate exit=$code :: $added plans=$(@(Plans).Count) was=$count"
$other = Plans | Where-Object { -not $_.active } | Select-Object -First 1
& $cli power use $other.id | Out-Null
$code = $LASTEXITCODE
Log "use exit=$code wanted=$($other.name) now=$((Plans | Where-Object { $_.active }).name)"

Log 'done'
