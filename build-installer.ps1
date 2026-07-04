param(
    [ValidateSet("Offline", "Min")]
    [string]$Mode = "Offline",
    [switch]$SkipFrontendBuild
)

$ErrorActionPreference = "Stop"

$solutionRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$setupScript  = Join-Path $solutionRoot "installer\E_Attendace.Setup.iss"
$iconPath     = Join-Path $solutionRoot "icon\E_Att.ico"
$installerOut = Join-Path $solutionRoot "installer\exe"

# ── Always use VS Release build output — never stale artifacts ────────────
$appExe        = Join-Path $solutionRoot "E.Attendance.Web\bin\Release\net10.0\E.Attendance.Web.exe"
$controllerExe = Join-Path $solutionRoot "E.Attendance\bin\Release\net10.0-windows\E.Attendance.exe"
$controllerBin = Join-Path $solutionRoot "E.Attendance\bin\Release\net10.0-windows"

$isccCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = ($isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1)

if ([string]::IsNullOrWhiteSpace($iscc)) {
    throw "Inno Setup 6 not found. Download from https://jrsoftware.org/issetup.php"
}

# ── Step 1: Build the solution (Release, no incremental) ──────────────────
Write-Host "Building solution in Release mode..."
$sln = Join-Path $solutionRoot "E-Attendance.sln"
& dotnet build $sln -c Release --no-incremental --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

# ── Step 2: Verify output exists ──────────────────────────────────────────
if (-not (Test-Path $appExe))        { throw "Build output not found: $appExe" }
if (-not (Test-Path $controllerExe)) { throw "Build output not found: $controllerExe" }

Write-Host "Web EXE:        $appExe"
Write-Host "Controller EXE: $controllerExe"

# ── Step 3: Find PostgreSQL bundled installer ─────────────────────────────
$postgresInstaller = $null
$pgSearchRoots = @(
    (Join-Path $solutionRoot "installer"),
    (Join-Path $solutionRoot "postgresql")
)
foreach ($root in $pgSearchRoots) {
    if (-not (Test-Path $root)) { continue }
    $candidate = Get-ChildItem -Path $root -Filter "postgresql-*-windows-x64.exe" -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if ($candidate) { $postgresInstaller = $candidate.FullName; break }
}

# ── Step 4: Run InnoSetup with correct (bin\Release) paths ───────────────
New-Item -ItemType Directory -Path $installerOut -Force | Out-Null

$isccArgs = @(
    "/DBuildMode=$Mode",
    "/DAppExePath=$appExe",
    "/DControllerExePath=$controllerExe",
    "/DControllerBinDir=$controllerBin",
    "/DSetupIconPath=$iconPath",
    "/DOutputDirPath=$installerOut"
)

if ($postgresInstaller) {
    $isccArgs += "/DPostgresInstallerPath=$postgresInstaller"
    $isccArgs += "/DPostgresInstallerName=$([System.IO.Path]::GetFileName($postgresInstaller))"
    Write-Host "PostgreSQL: $postgresInstaller"
} else {
    Write-Warning "PostgreSQL installer not found in installer\ folder."
}

Write-Host "Running InnoSetup..."
& $iscc @isccArgs $setupScript
if ($LASTEXITCODE -ne 0) { throw "InnoSetup failed." }

$result = Join-Path $installerOut "E-Attendance_${Mode}_Setup.exe"
if (Test-Path $result) {
    Write-Host ""
    Write-Host "SUCCESS: $result"
} else {
    Write-Warning "Build finished but installer file not found at expected path."
}
