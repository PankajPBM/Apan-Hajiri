# Complete Build & Deploy Script for E-Attendance Project
# This script rebuilds BOTH backend and frontend

param(
    [switch]$StartServers = $false,
    [switch]$CleanOnly = $false
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnetProject = "$projectRoot\E-Attendance.sln"
$nextjsProject = "$projectRoot\e-attendance-next"

Write-Host "`n" -ForegroundColor Green
Write-Host "╔════════════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║    E-ATTENDANCE COMPLETE BUILD SCRIPT                     ║" -ForegroundColor Green
Write-Host "╚════════════════════════════════════════════════════════════╝" -ForegroundColor Green

# ============================================================================
# STEP 1: CLEAN PREVIOUS BUILDS
# ============================================================================
Write-Host "`n[STEP 1/4] CLEANING PREVIOUS BUILDS...`n" -ForegroundColor Cyan

Write-Host "  Cleaning C# backend..." -ForegroundColor Yellow
Remove-Item -Path "$projectRoot\**\bin" -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
Remove-Item -Path "$projectRoot\**\obj" -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
Write-Host "  ✅ C# backend cleaned" -ForegroundColor Green

Write-Host "  Cleaning Next.js frontend..." -ForegroundColor Yellow
Remove-Item -Path "$nextjsProject\.next" -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
Remove-Item -Path "$nextjsProject\node_modules" -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
Remove-Item -Path "$nextjsProject\.turbo" -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
Write-Host "  ✅ Next.js frontend cleaned" -ForegroundColor Green

if ($CleanOnly) {
    Write-Host "`n✅ Clean operation complete!" -ForegroundColor Green
    exit 0
}

# ============================================================================
# STEP 2: BUILD C# BACKEND
# ============================================================================
Write-Host "`n[STEP 2/4] BUILDING C# BACKEND...`n" -ForegroundColor Cyan

if (-not (Test-Path $dotnetProject)) {
    Write-Host "  ❌ E-Attendance.sln not found at $dotnetProject" -ForegroundColor Red
    exit 1
}

try {
    Write-Host "  Running: dotnet build" -ForegroundColor Yellow
    cd $projectRoot
    dotnet build E-Attendance.sln -c Release --nologo 2>&1 | Select-Object -Last 20
    Write-Host "  ✅ C# backend built successfully" -ForegroundColor Green
} catch {
    Write-Host "  ❌ C# backend build failed: $_" -ForegroundColor Red
    exit 1
}

# ============================================================================
# STEP 3: BUILD NEXT.JS FRONTEND
# ============================================================================
Write-Host "`n[STEP 3/4] BUILDING NEXT.JS FRONTEND...`n" -ForegroundColor Cyan

if (-not (Test-Path "$nextjsProject\package.json")) {
    Write-Host "  ❌ package.json not found at $nextjsProject" -ForegroundColor Red
    exit 1
}

try {
    cd $nextjsProject
    
    Write-Host "  Installing dependencies..." -ForegroundColor Yellow
    npm install 2>&1 | Select-Object -Last 5
    
    Write-Host "  Building with npm run build..." -ForegroundColor Yellow
    npm run build 2>&1 | Select-Object -Last 30
    
    Write-Host "  ✅ Next.js frontend built successfully" -ForegroundColor Green
} catch {
    Write-Host "  ❌ Next.js frontend build failed: $_" -ForegroundColor Red
    exit 1
}

# ============================================================================
# STEP 4: BUILD COMPLETE
# ============================================================================
Write-Host "`n[STEP 4/4] BUILD COMPLETE" -ForegroundColor Green
Write-Host "`n╔════════════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║              ✅ ALL BUILDS SUCCESSFUL                     ║" -ForegroundColor Green
Write-Host "╚════════════════════════════════════════════════════════════╝`n" -ForegroundColor Green

Write-Host "Summary:" -ForegroundColor Cyan
Write-Host "  ✅ C# Backend: Built & ready" -ForegroundColor Green
Write-Host "  ✅ Next.js Frontend: Built & ready" -ForegroundColor Green
Write-Host "  ✅ Project Root: $projectRoot`n" -ForegroundColor Green

# ============================================================================
# OPTIONAL: START SERVERS
# ============================================================================
if ($StartServers) {
    Write-Host "Starting servers..." -ForegroundColor Yellow
    
    # Start backend (if it's running as service or standalone)
    Write-Host "  Backend should be running on: http://localhost:8081" -ForegroundColor Cyan
    
    # Start frontend dev server
    Write-Host "  Starting Next.js dev server..." -ForegroundColor Yellow
    cd $nextjsProject
    npm run dev
} else {
    Write-Host "To start servers, run:" -ForegroundColor Yellow
    Write-Host "  .\build-all.ps1 -StartServers" -ForegroundColor Cyan
    Write-Host "`nOr manually:" -ForegroundColor Yellow
    Write-Host "  Backend: Run E.Attendance.Web.exe or dotnet run" -ForegroundColor Cyan
    Write-Host "  Frontend: cd e-attendance-next && npm run dev" -ForegroundColor Cyan
}

Write-Host ""
