param(
    [string]$OutputRoot = "",
    [string]$RuntimeIdentifier = "linux-x64",
    [switch]$SkipFrontendBuild
)

$ErrorActionPreference = "Stop"

$solutionRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $solutionRoot "artifacts\linux"
}

$frontendProject = Join-Path $solutionRoot "e-attendance-next"
$webProject = Join-Path $solutionRoot "E.Attendance.Web\E.Attendance.Web.csproj"
$webRoot = Join-Path $solutionRoot "E.Attendance.Web\wwwroot"
$publishDir = Join-Path $OutputRoot "E.Attendance.Web"
$dotnetCliHome = Join-Path $solutionRoot ".dotnet_cli_home"
$nugetPackages = Join-Path $solutionRoot "_nuget"

Write-Host "Preparing Linux package..."
Write-Host "Output root: $OutputRoot"
Write-Host "Runtime: $RuntimeIdentifier"

if (-not (Test-Path -LiteralPath $OutputRoot)) {
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
}
if (-not (Test-Path -LiteralPath $dotnetCliHome)) {
    New-Item -ItemType Directory -Path $dotnetCliHome -Force | Out-Null
}
if (-not (Test-Path -LiteralPath $nugetPackages)) {
    New-Item -ItemType Directory -Path $nugetPackages -Force | Out-Null
}

$env:DOTNET_CLI_HOME = $dotnetCliHome
$env:NUGET_PACKAGES = $nugetPackages

Write-Host ""
if (-not $SkipFrontendBuild) {
    Write-Host "1) Building Next.js static frontend..."
    Push-Location $frontendProject
    try {
        npm run build
        if ($LASTEXITCODE -ne 0) {
            throw "Frontend build failed."
        }
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Host "1) Skipping frontend build and using existing static output..."
}

$frontendOut = Join-Path $frontendProject "out"
if (-not (Test-Path -LiteralPath $frontendOut)) {
    throw "Frontend output folder not found: $frontendOut"
}

if (-not $SkipFrontendBuild) {
    if (-not (Test-Path -LiteralPath $webRoot)) {
        New-Item -ItemType Directory -Path $webRoot -Force | Out-Null
    }

    Get-ChildItem -LiteralPath $webRoot -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
    Copy-Item -Path (Join-Path $frontendOut "*") -Destination $webRoot -Recurse -Force
    Write-Host "Frontend synced to: $webRoot"
}
else {
    Write-Host "Using existing frontend files from: $webRoot"
}

Write-Host ""
Write-Host "2) Publishing ASP.NET backend for Linux..."
if (Test-Path -LiteralPath $publishDir) {
    Get-ChildItem -LiteralPath $publishDir -Force | Remove-Item -Recurse -Force
}
else {
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
}

dotnet publish $webProject `
    -c Release `
    -r $RuntimeIdentifier `
    --disable-parallel `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

Write-Host ""
Write-Host "3) Writing Linux launch script..."
$runScriptPath = Join-Path $publishDir "run.sh"
$runScriptLines = @(
    '#!/usr/bin/env bash',
    'set -euo pipefail',
    '',
    'SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"',
    'cd "$SCRIPT_DIR"',
    '',
    'chmod +x ./E.Attendance.Web || true',
    'exec ./E.Attendance.Web'
)
$runScriptContent = $runScriptLines -join "`n"
[System.IO.File]::WriteAllText(
    $runScriptPath,
    $runScriptContent,
    (New-Object System.Text.UTF8Encoding($false))
)

$readmePath = Join-Path $OutputRoot "README-Linux.txt"
@"
E-Attendance Linux Package
===========================

Main app folder:
$publishDir

Run on Linux:
1. Copy folder E.Attendance.Web to Linux machine
2. cd E.Attendance.Web
3. chmod +x E.Attendance.Web run.sh
4. ./run.sh

Notes:
- This package contains the web app/backend for Linux.
- Windows-only controller app (E.Attendance WinForms) is not included in Linux package.
"@ | Set-Content -Path $readmePath -Encoding UTF8

Write-Host ""
Write-Host "Done."
Write-Host "Linux package created at: $OutputRoot"
