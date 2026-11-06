# Runs inside Windows Sandbox only, as a throwaway admin user with no desktop session.
# Removes a real built-in app and runs winget install, update and uninstall through TuneForge.
# Needs internet inside the Sandbox. Everything disappears when it closes.
param([string] $App = 'C:\tf\app', [string] $Out = 'C:\tf\out')

$ErrorActionPreference = 'Continue'
if (-not (Test-Path -LiteralPath 'C:\Users\WDAGUtilityAccount')) { throw 'This script must only run inside Windows Sandbox.' }

$cli = Join-Path $App 'tuneforge-cli.exe'
$log = Join-Path $Out 'apps-results.txt'
$dir = 'C:\Windows\Temp\tf-apps'
function Log([string] $Text) { Add-Content -LiteralPath $log -Value $Text -Encoding utf8 }

New-Item -ItemType Directory -Force $dir | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'
Log "start $(Get-Date -Format o) user=$env:USERNAME build $([Environment]::OSVersion.Version.Build)"

Log '--- setup'
try {
    # Sandbox has no winget or winget source. Normal PCs already do.
    if (-not (Get-AppxPackage -Name Microsoft.DesktopAppInstaller)) {
        $base = 'https://github.com/microsoft/winget-cli/releases/latest/download'
        Invoke-WebRequest "$base/DesktopAppInstaller_Dependencies.zip" -OutFile "$dir\deps.zip" -UseBasicParsing
        Invoke-WebRequest "$base/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle" -OutFile "$dir\winget.msixbundle" -UseBasicParsing
        Expand-Archive "$dir\deps.zip" "$dir\deps" -Force
        Get-ChildItem "$dir\deps" -Recurse -Filter '*x64*.appx' | ForEach-Object { Add-AppxPackage -Path $_.FullName -ErrorAction SilentlyContinue }
        Add-AppxPackage -Path "$dir\winget.msixbundle" -ErrorAction Stop
    }
    if (-not (Get-AppxPackage -Name Microsoft.Winget.Source)) {
        Invoke-WebRequest 'https://cdn.winget.microsoft.com/cache/source.msix' -OutFile "$dir\source.msix" -UseBasicParsing
        Add-AppxPackage -Path "$dir\source.msix" -ErrorAction Stop
    }

    # Windows Terminal is in the catalog, and Sandbox has none of the catalog's apps.
    $release = Invoke-RestMethod 'https://api.github.com/repos/microsoft/terminal/releases/latest' -UseBasicParsing
    $asset = $release.assets | Where-Object { $_.name -match '^Microsoft\.WindowsTerminal_.*_8wekyb3d8bbwe\.msixbundle$' } | Select-Object -First 1
    Invoke-WebRequest $asset.browser_download_url -OutFile "$dir\terminal.msixbundle" -UseBasicParsing
    Add-AppxProvisionedPackage -Online -PackagePath "$dir\terminal.msixbundle" -SkipLicense -ErrorAction Stop | Out-Null
    Add-AppxPackage -Path "$dir\terminal.msixbundle" -ErrorAction Stop
    Log 'setup ok'
}
catch { Log "setup FAILED: $($_.Exception.Message)" }

Log '--- built-in'
$name = 'Microsoft.WindowsTerminal'
Log "before installed=$(@(Get-AppxPackage -AllUsers -Name $name).Count) provisioned=$(@(Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq $name).Count)"
Log "list :: $(@(& $cli builtin list | Where-Object { $_ -match [regex]::Escape($name) }) -join ' | ')"
$result = & $cli builtin remove $name
Log "remove exit=$LASTEXITCODE :: $result"
Log "after installed=$(@(Get-AppxPackage -AllUsers -Name $name).Count) provisioned=$(@(Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq $name).Count)"
Log "list :: $(@(& $cli builtin list | Where-Object { $_ -match [regex]::Escape($name) }) -join ' | ')"
$refused = & $cli builtin remove 'Microsoft.WindowsStore' 2>&1
Log "store refused exit=$LASTEXITCODE :: $refused"

Log '--- winget'
$soft = & $cli software list 2>&1
Log "software exit=$LASTEXITCODE count=$(@($soft).Count)"
$install = & $cli software install 7zip.7zip 2>&1
Log "install exit=$LASTEXITCODE :: $install"
Log "7z=$(Test-Path 'C:\Program Files\7-Zip\7z.exe')"
$soft = & $cli software list 2>&1
Log "software shows :: $(@($soft | Where-Object { $_ -match '7zip' }) -join ' | ')"

$locked = & $cli programs uninstall Microsoft.AppInstaller 2>&1
Log "locked uninstall exit=$LASTEXITCODE :: $locked"

$winget = "$env:LOCALAPPDATA\Microsoft\WindowsApps\winget.exe"
& $cli programs uninstall 7zip.7zip | ForEach-Object { Log "clean :: $_" }
& $winget install --id 7zip.7zip --exact --version 24.08 --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity | Out-Null
Log "old-version exit=$LASTEXITCODE"
Log "update offered :: $(@(& $cli programs list | Where-Object { $_ -match '7zip' }) -join ' | ')"
$update = & $cli programs update 7zip.7zip 2>&1
Log "update exit=$LASTEXITCODE :: $update"
Log "after update :: $(@(& $cli programs list | Where-Object { $_ -match '7zip' }) -join ' | ')"
$uninstall = & $cli programs uninstall 7zip.7zip 2>&1
Log "uninstall exit=$LASTEXITCODE :: $uninstall"
Log "7z-gone=$(-not (Test-Path 'C:\Program Files\7-Zip\7z.exe'))"

Log "done $(Get-Date -Format o)"
Set-Content -LiteralPath (Join-Path $Out 'apps-finished.txt') -Value 'ok'
