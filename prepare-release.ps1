# Prepares a release, like AdHush: tests, builds and signs here, because the signing key stays on this PC.
# It changes files but commits nothing. Those changes then go up through GitHub Desktop.
#   powershell -ExecutionPolicy Bypass -File prepare-release.ps1 -Notes "What changed"
# Add [important] to the notes to bring the update on screen on PCs where TuneForge is only in the tray.
param([string] $Notes = '')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repository = 'D3MON999/TuneForge'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

try {
    $latestTag = (Invoke-RestMethod "https://api.github.com/repos/$repository/releases/latest" -Headers @{ 'User-Agent' = 'TuneForge-release' } -UseBasicParsing).tag_name
} catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
    $latestTag = $null
}

# An update is only offered if its number is higher, so raise it unless it's already been raised.
$props = "$root\Directory.Build.props"
$original = [IO.File]::ReadAllText($props)
$current = [version] ([regex]::Match($original, '<Version>([^<]*)</Version>').Groups[1].Value)
$version = $current
if ($latestTag -and $current -le [version] $latestTag.TrimStart('v')) {
    $last = [version] $latestTag.TrimStart('v')
    $version = [version] "$($last.Major).$($last.Minor).$($last.Build + 1)"
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

$query = "tag=v$version&title=" + [uri]::EscapeDataString("TuneForge $version") + '&body=' + [uri]::EscapeDataString($Notes)
Write-Host ''
Write-Host "TuneForge $version is ready."
Write-Host "  1. In GitHub Desktop, write a summary (e.g. 'TuneForge $version'), Commit to main, Push origin."
Write-Host "  2. Open https://github.com/$repository/releases/new?$query"
Write-Host "  3. Attach the three files from $root\artifacts\release and publish."
