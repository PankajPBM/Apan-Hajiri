param(
    [ValidateSet("Offline", "Min")]
    [string]$Mode = "Offline",

    [switch]$SkipSingleExeBuild,

    [switch]$SkipFrontendBuild
)

$ErrorActionPreference = "Stop"

$solutionRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildScript = Join-Path $solutionRoot "build-single-exe.ps1"
$setupScript = Join-Path $solutionRoot "installer\E_Attendace.Setup.iss"
$iconPath = Join-Path $solutionRoot "icon\E_Att.ico"
$outputRoot = if ($Mode -eq "Offline") { Join-Path $solutionRoot "artifacts\single-exe" } else { Join-Path $solutionRoot "artifacts\single-exe-min" }
$appExe = Join-Path $outputRoot "E.Attendance.Web\E.Attendance.Web.exe"
$controllerBinDir = Join-Path $outputRoot "E.Attendance.Controller"
$controllerExe = Join-Path $controllerBinDir "E.Attendance.exe"
$installerOut = Join-Path $solutionRoot "installer\exe"
$postgresSearchRoots = @(
    (Join-Path $solutionRoot "installer"),
    (Join-Path $solutionRoot "postgresql"),
    "D:\Pankaj\Project\PB Attendance Management\postgresql"
)
$postgresInstaller = $null
$preferredPostgresInstaller = Join-Path $solutionRoot "installer\postgresql-18.3-1-windows-x64.exe"
$isccCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = ($isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1)

if ([string]::IsNullOrWhiteSpace($iscc))
{
    throw "Inno Setup compiler not found. Install Inno Setup 6 first."
}

if (-not $SkipSingleExeBuild)
{
    if (!(Test-Path $buildScript))
    {
        throw "build-single-exe.ps1 not found at $buildScript"
    }

    $publishOutputRoot = if ($Mode -eq "Offline") { ".\artifacts\single-exe" } else { ".\artifacts\single-exe-min" }

    $webBuildArgs = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $buildScript,
        "-Mode", $Mode,
        "-Target", "Web",
        "-OutputRoot", $publishOutputRoot
    )
    if ($SkipFrontendBuild)
    {
        $webBuildArgs += "-SkipFrontendBuild"
    }

    & powershell @webBuildArgs
    if ($LASTEXITCODE -ne 0)
    {
        throw "Single EXE web build failed."
    }

    & powershell -NoProfile -ExecutionPolicy Bypass -File $buildScript -Mode $Mode -Target Controller -OutputRoot $publishOutputRoot
    if ($LASTEXITCODE -ne 0)
    {
        throw "Single EXE controller build failed."
    }
}

if (!(Test-Path $appExe))
{
    throw "App EXE not found: $appExe"
}

if (!(Test-Path $iconPath))
{
    throw "Icon file not found: $iconPath"
}

if (!(Test-Path $setupScript))
{
    throw "Installer script not found: $setupScript"
}

foreach ($root in $postgresSearchRoots)
{
    if (-not (Test-Path $root))
    {
        continue
    }

    $candidate = Get-ChildItem -Path $root -Filter "postgresql-*-windows-x64.exe" -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1

    if ($candidate)
    {
        $postgresInstaller = $candidate.FullName
        break
    }
}

if (Test-Path $preferredPostgresInstaller)
{
    $postgresInstaller = $preferredPostgresInstaller
}

New-Item -ItemType Directory -Path $installerOut -Force | Out-Null

$isccArgs = @(
    "/DBuildMode=$Mode",
    "/DAppExePath=$appExe",
    "/DSetupIconPath=$iconPath",
    "/DOutputDirPath=$installerOut"
)

if (Test-Path $controllerBinDir)
{
    $isccArgs += "/DControllerBinDir=$controllerBinDir"
}

if (Test-Path $controllerExe)
{
    $isccArgs += "/DControllerExePath=$controllerExe"
}

if ($postgresInstaller)
{
    $isccArgs += "/DPostgresInstallerPath=$postgresInstaller"
    $isccArgs += "/DPostgresInstallerName=$([System.IO.Path]::GetFileName($postgresInstaller))"
    Write-Host "Bundled PostgreSQL installer: $postgresInstaller"
}
else
{
    Write-Warning "PostgreSQL installer not found. Setup will skip automatic PostgreSQL installation."
}

& $iscc @isccArgs $setupScript

if ($LASTEXITCODE -ne 0)
{
    throw "Installer build failed."
}

$installerName = "E-Attendance_{0}_Setup.exe" -f $Mode
$installerPath = Join-Path $installerOut $installerName
if (Test-Path $installerPath)
{
    Write-Host "Installer created: $installerPath"
}
else
{
    Write-Warning "Build finished, but installer file was not found at expected path."
}
