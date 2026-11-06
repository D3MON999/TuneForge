# Runs inside Windows Sandbox only. Exercises TuneForge against a real registry.
# The Sandbox and everything in it disappears when it closes.
param([string] $App = 'C:\tf\app', [string] $Out = 'C:\tf\out')

$ErrorActionPreference = 'Continue'
# This profile exists only inside Windows Sandbox, whichever account runs the script.
if (-not (Test-Path -LiteralPath 'C:\Users\WDAGUtilityAccount')) { throw 'This script must only run inside Windows Sandbox.' }

$cli = Join-Path $App 'tuneforge-cli.exe'
$log = Join-Path $Out 'results.txt'
function Log([string] $Text) { Add-Content -LiteralPath $log -Value $Text -Encoding utf8 }

Log "start $(Get-Date -Format o) build $([Environment]::OSVersion.Version.Build)"
Log "elevated=$(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"

$before = & $cli list
Log "items=$($before.Count)"
$states = @{}
foreach ($line in $before) { $parts = $line -split '\s+'; $states[$parts[2]] = $parts[0] }

$pass = 0; $fail = 0; $skip = 0
foreach ($id in ($states.Keys | Sort-Object)) {
    if ($states[$id] -ne 'not-applied') { $skip++; continue }

    $applied = & $cli apply $id --no-restore-point
    if ($LASTEXITCODE -ne 0) { $fail++; Log "APPLY-FAIL $id :: $($applied -join ' | ')"; continue }

    $now = (& $cli list) -match "\s$([regex]::Escape($id))$"
    if ($now -notmatch '^applied') { $fail++; Log "NOT-DETECTED $id :: $now" }

    $number = [regex]::Match(($applied -join ' '), 'Change number (\d+)').Groups[1].Value
    $undone = & $cli undo $number
    if ($LASTEXITCODE -ne 0) { $fail++; Log "UNDO-FAIL $id :: $($undone -join ' | ')"; continue }

    $after = (& $cli list) -match "\s$([regex]::Escape($id))$"
    if ($after -notmatch '^not-applied') { $fail++; Log "UNDO-WRONG $id :: $after"; continue }
    $pass++
}
Log "tweaks pass=$pass fail=$fail skip=$skip"

Log '--- presets'
foreach ($preset in 'minimal', 'standard', 'advanced') {
    $result = & $cli apply --preset $preset --no-restore-point
    Log "preset $preset exit=$LASTEXITCODE failed=$(@($result | Where-Object { $_ -match '^failed' }).Count) :: $(($result | Where-Object { $_ -match '^failed' }) -join ' | ')"
}

Log '--- cleanup'
New-Item -ItemType Directory -Force "$env:TEMP\tf-test\deep" | Out-Null
foreach ($f in "$env:TEMP\tf-test\old.tmp", "$env:TEMP\tf-test\deep\old2.tmp") {
    Set-Content -LiteralPath $f -Value 'x'
    (Get-Item -LiteralPath $f).LastWriteTimeUtc = (Get-Date).ToUniversalTime().AddDays(-3)
}
Set-Content -LiteralPath "$env:TEMP\tf-test\new.tmp" -Value 'x'
& $cli cleanup scan | ForEach-Object { Log $_ }
& $cli cleanup run user-temp windows-temp thumbnails crash-dumps recycle-bin system-crash shaders browsers | ForEach-Object { Log $_ }
Log "cleanup exit=$LASTEXITCODE old-gone=$(-not (Test-Path "$env:TEMP\tf-test\old.tmp")) new-kept=$(Test-Path "$env:TEMP\tf-test\new.tmp") windows-ok=$(Test-Path "$env:WINDIR\System32\notepad.exe")"

Log '--- features'
& $cli features | ForEach-Object { Log $_ }

Log '--- repairs'
foreach ($repair in 'time', 'network', 'windows-update') {
    $result = & $cli repair $repair
    Log "repair $repair exit=$LASTEXITCODE :: $($result -join ' ')"
}
Log "wuauserv=$((Get-Service wuauserv).StartType) softdist-renamed=$(@(Get-ChildItem $env:WINDIR -Filter 'SoftwareDistribution.old-*').Count)"

Log "done $(Get-Date -Format o)"
Set-Content -LiteralPath (Join-Path $Out 'finished.txt') -Value 'ok'
shutdown.exe /s /t 3
