# Tests everything, then builds the signed installer in artifacts\release.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$dotnet = "$env:LOCALAPPDATA\TuneForgeTools\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { throw 'Inno Setup 6 was not found.' }
$key = "$env:LOCALAPPDATA\TuneForgeTools\release-key.pem"
if (-not (Test-Path $key)) { throw "The signing key was not found at $key." }

function Invoke-Step([string] $Name, [scriptblock] $Action) {
    Write-Host "== $Name"
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

$version = ([xml] (Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version
$output = "$root\artifacts\release"
$app = "$output\app"
if (Test-Path $output) { Remove-Item -Recurse -Force $output }
New-Item -ItemType Directory -Force $app | Out-Null

Invoke-Step 'Test and build' { & "$root\build.ps1" -Configuration Release; $global:LASTEXITCODE = 0 }

foreach ($project in 'TuneForge.Host', 'TuneForge.Engine', 'TuneForge.Cli') {
    Invoke-Step "Publish $project" {
        & $dotnet publish "$root\src\$project\$project.csproj" -c Release -r win-x64 --self-contained true -o $app --nologo -v quiet
    }
}

Invoke-Step 'Build installer' {
    & $iscc /Qp "/DAppVersion=$version" "/DAppFolder=$app" "/DOutputFolder=$output" "$root\installer\TuneForge.iss"
}

$installer = "$output\TuneForge-Setup-$version.exe"
Invoke-Step 'Sign installer' {
    & $dotnet run --project "$root\tools\TuneForge.ReleaseTool\TuneForge.ReleaseTool.csproj" -c Release --no-launch-profile -- sign $installer $key "$installer.sig"
}

$hash = (Get-FileHash -Algorithm SHA256 $installer).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$installer.sha256", "$hash  $(Split-Path $installer -Leaf)`n")
Remove-Item -Recurse -Force $app
Write-Host "Installer: $installer"
Write-Host "SHA-256: $hash"
