# Run through Release.cmd after committing and pushing in GitHub Desktop.
# Tests and builds the signed installer on this PC, because the signing key must stay here.
# It then opens GitHub's release page filled in, and the folder holding the three files to upload.
param([switch] $Preview)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repository = 'D3MON999/TuneForge'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$git = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $git) {
    $git = (Get-ChildItem "$env:LOCALAPPDATA\GitHubDesktop\app-*\resources\app\git\cmd\git.exe" | Sort-Object FullName | Select-Object -Last 1).FullName
}
if (-not $git) { throw 'Git was not found.' }

function Invoke-Git {
    & $git -C $root @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args[0]) failed." }
}

if (& $git -C $root status --porcelain) { throw 'You have uncommitted changes. Commit and push them in GitHub Desktop first.' }

Invoke-Git fetch -q --tags origin
if ((& $git -C $root rev-list --count 'HEAD..origin/main') -ne '0') { throw 'GitHub has newer commits. Pull them in GitHub Desktop first.' }

try {
    $latestTag = (Invoke-RestMethod "https://api.github.com/repos/$repository/releases/latest" -Headers @{ 'User-Agent' = 'TuneForge-release' } -UseBasicParsing).tag_name
} catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
    $latestTag = $null
}

# An update is only offered if its number is higher, so raise it unless you already have.
$props = "$root\Directory.Build.props"
$original = [IO.File]::ReadAllText($props)
$current = [version] ([regex]::Match($original, '<Version>([^<]*)</Version>').Groups[1].Value)
$version = $current
if ($latestTag -and $current -le [version] $latestTag.TrimStart('v')) {
    $last = [version] $latestTag.TrimStart('v')
    $version = [version] "$($last.Major).$($last.Minor).$($last.Build + 1)"
}

$notes = ''
if ($latestTag -and (& $git -C $root tag --list $latestTag)) {
    $lines = @(& $git -C $root log --no-merges --format=%s "$latestTag..HEAD") | Where-Object { $_ -notmatch '^Version \d' }
    $notes = ($lines | ForEach-Object { "- $_" }) -join "`n"
}

$query = "tag=v$version&title=" + [uri]::EscapeDataString("TuneForge $version") + '&body=' + [uri]::EscapeDataString($notes)
$page = "https://github.com/$repository/releases/new?$query"
$installer = "$root\artifacts\release\TuneForge-Setup-$version.exe"

if ($Preview) {
    Write-Host "Latest release: $latestTag"
    Write-Host "This release:   $version"
    Write-Host "Notes:`n$notes"
    return
}

if ($version -ne $current) {
    [IO.File]::WriteAllText($props, ($original -replace '<Version>[^<]*</Version>', "<Version>$version</Version>"), (New-Object Text.UTF8Encoding($false)))
}

try {
    & "$root\release.ps1"
} catch {
    [IO.File]::WriteAllText($props, $original, (New-Object Text.UTF8Encoding($false)))
    throw
}

if (& $git -C $root status --porcelain) {
    Invoke-Git add Directory.Build.props
    Invoke-Git commit -q -m "Version $version"
}
Invoke-Git push -q origin HEAD

Start-Process explorer.exe "/select,`"$installer`""
Start-Process $page
Write-Host ''
Write-Host "TuneForge $version is ready. Now, on the page that just opened:"
Write-Host '  1. Check the notes. Add [important] to the notes if everyone must see it straight away.'
Write-Host '  2. Drag in the three TuneForge-Setup files from the folder that just opened.'
Write-Host '  3. Click Publish release.'
