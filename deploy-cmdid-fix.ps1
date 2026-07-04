# Deploy the command-ID fetch fix to the installed E-Attendance service.
# RIGHT-CLICK this file -> "Run with PowerShell" as Administrator,
# OR open an elevated PowerShell and run:  powershell -ExecutionPolicy Bypass -File .\deploy-cmdid-fix.ps1

$ErrorActionPreference = "Stop"

$svc     = "E-Attendance-API"
$srcDll  = "D:\Pankaj\Project\E-Attendance\E.Attendance.Web\bin\Release\net10.0\E.Attendance.Web.dll"
$destDir = "C:\Program Files\E-Attendance"
$destDll = Join-Path $destDir "E.Attendance.Web.dll"
$backup  = Join-Path $destDir ("E.Attendance.Web.dll.bak-" + (Get-Date -Format "yyyyMMdd-HHmmss"))

# Verify elevation
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: Run this script as Administrator." -ForegroundColor Red; exit 1 }

if (-not (Test-Path $srcDll)) { Write-Host "ERROR: New DLL not found at $srcDll. Build Release first." -ForegroundColor Red; exit 1 }

Write-Host "1) Backing up current installed DLL -> $backup"
Copy-Item $destDll $backup -Force

Write-Host "2) Stopping service $svc ..."
Stop-Service -Name $svc -Force
(Get-Service $svc).WaitForStatus("Stopped", "00:00:30")
Write-Host "   Status: $((Get-Service $svc).Status)"

Write-Host "3) Copying new DLL into $destDir ..."
Copy-Item $srcDll $destDll -Force
Write-Host "   New timestamp: $((Get-Item $destDll).LastWriteTime)"

Write-Host "4) Starting service $svc ..."
Start-Service -Name $svc
(Get-Service $svc).WaitForStatus("Running", "00:00:30")
Write-Host "   Status: $((Get-Service $svc).Status)" -ForegroundColor Green

Write-Host "`nDone. Rollback if needed:  Stop-Service $svc; Copy-Item '$backup' '$destDll' -Force; Start-Service $svc" -ForegroundColor Yellow
