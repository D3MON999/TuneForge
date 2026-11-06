# Publishes a new version. Installed copies offer it within 6 hours. Add -Important to bring it on screen.
#   ./publish.ps1 -Version 0.1.1 -Notes "What changed"
# Tests everything, builds and signs the installer, pushes the code and creates the release.
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [Parameter(Mandatory)] [string] $Notes,
    # Brings the update on screen on PCs where TuneForge is only in the tray.
    [switch] $Important
)

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

if (& $git -C $root status --porcelain) { throw 'Commit or discard your changes first.' }

# Uses the sign-in Git already saved. It's only sent to GitHub and is never shown or stored here.
$lookup = New-Object Diagnostics.ProcessStartInfo $git, 'credential fill'
$lookup.UseShellExecute = $false
$lookup.CreateNoWindow = $true
$lookup.RedirectStandardInput = $true
$lookup.RedirectStandardOutput = $true
$process = [Diagnostics.Process]::Start($lookup)
$request = [Text.Encoding]::ASCII.GetBytes("protocol=https`nhost=github.com`n`n")
$process.StandardInput.BaseStream.Write($request, 0, $request.Length)
$process.StandardInput.Close()
$saved = $process.StandardOutput.ReadToEnd() -split "`r?`n"
$process.WaitForExit()
$token = ($saved | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
if (-not $token) { throw 'Git has no GitHub sign-in. Push once to sign in, then try again.' }
$headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'TuneForge-publish' }

$target = Invoke-RestMethod "https://api.github.com/repos/$repository" -Headers $headers -UseBasicParsing
if ($target.private) { throw "$repository is private. Installed copies can't see it, so make it public first." }

$props = "$root\Directory.Build.props"
$text = [IO.File]::ReadAllText($props)
[IO.File]::WriteAllText($props, ($text -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"), (New-Object Text.UTF8Encoding($false)))

& "$root\release.ps1"

if (& $git -C $root status --porcelain) {
    Invoke-Git add Directory.Build.props
    Invoke-Git commit -q -m "Release $Version"
}
Invoke-Git push origin HEAD

$release = Invoke-RestMethod "https://api.github.com/repos/$repository/releases" -Method Post -Headers $headers -ContentType 'application/json' -UseBasicParsing -Body (@{
    tag_name = "v$Version"
    name     = "TuneForge $Version"
    body     = $(if ($Important) { "[important]`n$Notes" } else { $Notes })
} | ConvertTo-Json)

$installer = "$root\artifacts\release\TuneForge-Setup-$Version.exe"
foreach ($file in $installer, "$installer.sig", "$installer.sha256") {
    $name = Split-Path $file -Leaf
    Write-Host "== Upload $name"
    Invoke-RestMethod "https://uploads.github.com/repos/$repository/releases/$($release.id)/assets?name=$name" -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile $file -UseBasicParsing | Out-Null
}

Write-Host "Published: $($release.html_url)"
