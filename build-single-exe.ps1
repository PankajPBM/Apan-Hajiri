param(
    [ValidateSet("Offline", "Min")]
    [string]$Mode = "Offline",

    [ValidateSet("Web", "Controller", "Both")]
    [string]$Target = "Web",

    [string]$OutputRoot = ".\artifacts\single-exe",

    [switch]$SkipFrontendBuild
)

$ErrorActionPreference = "Stop"

function Publish-Project {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$ProfileName,
        [Parameter(Mandatory = $true)][string]$OutputDir
    )

    if (Test-Path -LiteralPath $OutputDir) {
        Get-ChildItem -LiteralPath $OutputDir -Force | Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    }

    dotnet publish $ProjectPath -c Release -p:PublishProfile=$ProfileName -p:PublishDir="$OutputDir\"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $ProjectPath using profile $ProfileName."
    }
}

function Create-ExeOnlyPackage {
    param(
        [Parameter(Mandatory = $true)][string]$PublishedDir,
        [Parameter(Mandatory = $true)][string]$ExeName,
        [Parameter(Mandatory = $true)][string]$ExeOnlyDir
    )

    if (-not (Test-Path -LiteralPath $PublishedDir)) {
        return
    }

    $exePath = Join-Path $PublishedDir $ExeName
    if (-not (Test-Path -LiteralPath $exePath)) {
        return
    }

    if (Test-Path -LiteralPath $ExeOnlyDir) {
        Get-ChildItem -LiteralPath $ExeOnlyDir -Force | Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Force -Path $ExeOnlyDir | Out-Null
    }

    Copy-Item -LiteralPath $exePath -Destination (Join-Path $ExeOnlyDir $ExeName) -Force
}

function Remove-NonRuntimeFiles {
    param(
        [Parameter(Mandatory = $true)][string]$PublishedDir
    )

    if (-not (Test-Path -LiteralPath $PublishedDir)) {
        return
    }

    Get-ChildItem -LiteralPath $PublishedDir -Recurse -File -Filter *.pdb -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue

    Get-ChildItem -LiteralPath $PublishedDir -Recurse -File -Filter *.map -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue

    Get-ChildItem -LiteralPath $PublishedDir -Recurse -File -Filter *.map.br -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue

    Get-ChildItem -LiteralPath $PublishedDir -Recurse -File -Filter *.map.gz -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

function Sync-FrontendStaticExport {
    param(
        [Parameter(Mandatory = $true)][string]$FrontendProjectDir,
        [Parameter(Mandatory = $true)][string]$WebRootDir,
        [switch]$SkipBuild
    )

    $frontendPackage = Join-Path $FrontendProjectDir "package.json"
    if (-not (Test-Path -LiteralPath $frontendPackage)) {
        throw "Frontend package.json not found at $frontendPackage"
    }

    if ($SkipBuild) {
        Write-Host "Skipping frontend build and reusing existing static export."
    } else {
        Write-Host "Building frontend static export..."
        Push-Location $FrontendProjectDir
        try {
            cmd /c npm run build
            if ($LASTEXITCODE -ne 0) {
                throw "Frontend build failed."
            }
        }
        finally {
            Pop-Location
        }
    }

    $frontendOut = Join-Path $FrontendProjectDir "out"
    if (-not (Test-Path -LiteralPath $frontendOut)) {
        throw "Frontend export output not found: $frontendOut"
    }

    if (-not (Test-Path -LiteralPath $WebRootDir)) {
        New-Item -ItemType Directory -Force -Path $WebRootDir | Out-Null
    }

    $existingItems = Get-ChildItem -LiteralPath $WebRootDir -Force -ErrorAction SilentlyContinue
    if ($existingItems) {
        $existingItems | Remove-Item -Recurse -Force
    }

    Copy-Item -Path (Join-Path $frontendOut "*") -Destination $WebRootDir -Recurse -Force
    Write-Host "Frontend assets synced to: $WebRootDir"
}

$solutionRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$webProject = Join-Path $solutionRoot "E.Attendance.Web\E.Attendance.Web.csproj"
$controllerProject = Join-Path $solutionRoot "E.Attendance\E.Attendance.csproj"
$frontendProject = Join-Path $solutionRoot "e-attendance-next"
$webRoot = Join-Path $solutionRoot "E.Attendance.Web\wwwroot"
$profile = if ($Mode -eq "Offline") { "OfflineSingleFile" } else { "MinSingleFile" }

$resolvedOutputRoot = Resolve-Path -LiteralPath $solutionRoot | ForEach-Object { $_.Path }
$resolvedOutputRoot = Join-Path $resolvedOutputRoot $OutputRoot
New-Item -ItemType Directory -Force -Path $resolvedOutputRoot | Out-Null

Write-Host "Mode: $Mode"
Write-Host "Target: $Target"
Write-Host "Publish profile: $profile"
Write-Host ""

if ($Target -eq "Web" -or $Target -eq "Both") {
    $webOut = Join-Path $resolvedOutputRoot "E.Attendance.Web"
    $webExeOnlyOut = Join-Path $resolvedOutputRoot "E.Attendance.Web.ExeOnly"
    Sync-FrontendStaticExport -FrontendProjectDir $frontendProject -WebRootDir $webRoot -SkipBuild:$SkipFrontendBuild
    Write-Host "Publishing E.Attendance.Web..."
    Publish-Project -ProjectPath $webProject -ProfileName $profile -OutputDir $webOut
    Remove-NonRuntimeFiles -PublishedDir $webOut
    Write-Host "E.Attendance.Web published to: $webOut"
    Create-ExeOnlyPackage -PublishedDir $webOut -ExeName "E.Attendance.Web.exe" -ExeOnlyDir $webExeOnlyOut
    Write-Host "E.Attendance.Web EXE-only package: $webExeOnlyOut"
}

if ($Target -eq "Controller" -or $Target -eq "Both") {
    $controllerOut = Join-Path $resolvedOutputRoot "E.Attendance.Controller"
    $controllerExeOnlyOut = Join-Path $resolvedOutputRoot "E.Attendance.Controller.ExeOnly"
    Write-Host "Publishing E.Attendance (Service Controller)..."
    Publish-Project -ProjectPath $controllerProject -ProfileName $profile -OutputDir $controllerOut
    Remove-NonRuntimeFiles -PublishedDir $controllerOut
    Write-Host "E.Attendance controller published to: $controllerOut"
    Create-ExeOnlyPackage -PublishedDir $controllerOut -ExeName "E.Attendance.exe" -ExeOnlyDir $controllerExeOnlyOut
    Write-Host "E.Attendance controller EXE-only package: $controllerExeOnlyOut"
    Write-Host "Note: Controller EXE starts E.Attendance.Web at runtime (no npm/node dependency)."
}

Write-Host ""
Write-Host "Done."
Write-Host "Output folder: $resolvedOutputRoot"
Write-Host ""
Write-Host "Recommended for one-file offline usage:"
Write-Host "  .\\build-single-exe.ps1 -Mode Offline -Target Web"
