# Builds the UI and the .NET projects, then runs all tests.
param([ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$dotnet = "$env:LOCALAPPDATA\TuneForgeTools\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

function Invoke-Step([string] $Name, [scriptblock] $Action) {
    Write-Host "== $Name"
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

Push-Location "$root\src\ui"
try {
    Invoke-Step 'Install UI packages' { npm.cmd ci --no-fund --no-audit }
    Invoke-Step 'Test UI' { npm.cmd test }
    Invoke-Step 'Build UI' { npm.cmd run build }
} finally {
    Pop-Location
}

Invoke-Step 'Build .NET' { & $dotnet build "$root\TuneForge.slnx" -c $Configuration --nologo -v minimal }
Invoke-Step 'Test engine' { & $dotnet test "$root\TuneForge.slnx" -c $Configuration --no-build --nologo }

Write-Host "Output: $root\artifacts\app\$Configuration\TuneForge.exe"
