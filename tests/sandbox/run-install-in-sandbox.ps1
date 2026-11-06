# Runs inside Windows Sandbox only. Installs TuneForge for real, checks it, then uninstalls it.
param([string] $Setup = 'C:\tf\installer', [string] $Out = 'C:\tf\out')

$ErrorActionPreference = 'Continue'
if (-not (Test-Path -LiteralPath 'C:\Users\WDAGUtilityAccount')) { throw 'This script must only run inside Windows Sandbox.' }

$log = Join-Path $Out 'install-results.txt'
function Log([string] $Text) { Add-Content -LiteralPath $log -Value $Text -Encoding utf8 }

$installer = (Get-ChildItem $Setup -Filter 'TuneForge-Setup-*.exe' | Select-Object -First 1).FullName
$folder = "$env:ProgramFiles\TuneForge"
Log "start $(Get-Date -Format o) installer=$(Split-Path $installer -Leaf) mb=$([int]((Get-Item $installer).Length / 1MB))"

$process = Start-Process $installer -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/LOG=C:\Windows\Temp\tf-setup.log' -PassThru -Wait
Log "install exit=$($process.ExitCode)"
foreach ($file in 'TuneForge.exe', 'TuneForge.Engine.exe', 'tuneforge-cli.exe', 'wwwroot\index.html') {
    Log "$file=$(Test-Path (Join-Path $folder $file))"
}

$rights = (Get-Acl $folder).Access | Where-Object { $_.IdentityReference -match 'Users$' -and $_.FileSystemRights -match 'Write|Modify|FullControl' -and $_.IdentityReference -notmatch 'Authenticated' }
Log "users-can-write=$(@($rights).Count -gt 0)"
Log "uninstall-entry=$(@(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' | Where-Object { $_.PSChildName -like '*7E0C2B6A*' }).Count)"
Log "start-menu=$(Test-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\TuneForge.lnk")"

$cli = Join-Path $folder 'tuneforge-cli.exe'
Log "cli tweaks=$(@(& $cli list).Count) exit=$LASTEXITCODE"
Log "cli health=$((& $cli health | ConvertFrom-Json).score) exit=$LASTEXITCODE"
Get-Process TuneForge -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false

Remove-Item -Force 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run\TuneForge' -ErrorAction SilentlyContinue
& $cli autostart on | Out-Null
$process = Start-Process (Join-Path $folder 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -Wait
Start-Sleep -Seconds 5
Log "uninstall exit=$($process.ExitCode)"
Log "folder-gone=$(-not (Test-Path $folder))"
Log "autostart-gone=$($null -eq (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name TuneForge -ErrorAction SilentlyContinue))"

Log "done $(Get-Date -Format o)"
Set-Content -LiteralPath (Join-Path $Out 'install-finished.txt') -Value 'ok'
