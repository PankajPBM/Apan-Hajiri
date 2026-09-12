# ============================================================
# E-Attendance Service Repair Script
# RIGHT-CLICK this file -> "Run with PowerShell" as Administrator
# Or open PowerShell as Admin and run:
#   powershell -ExecutionPolicy Bypass -File "d:\Pankaj\Project\E-Attendance\fix-services-admin.ps1"
# ============================================================

param()

# Self-elevate if not admin
if (-NOT ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warning "Not running as Administrator. Re-launching with elevation..."
    Start-Process PowerShell -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$pgBinPath   = "C:\Program Files\E-Attendance\PostgreSQL\15\bin"
$pgDataPath  = "C:\Program Files\E-Attendance\PostgreSQL\18\data"
$pgPort      = 1277
$pgPassword  = "YOUR_PASSWORD_HERE"
$pgSvcName   = "E-Attendance-PostgreSQL"
$apiSvcName  = "E-Attendance-API"
$webSvcName  = "E-Attendance-Web"
$webExe      = "C:\Program Files\E-Attendance\E.Attendance.Web.exe"
$initdbExe   = "$pgBinPath\initdb.exe"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "   E-Attendance Service Repair Script   " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# STEP 1: Stop all services
Write-Host "[1/7] Stopping all E-Attendance services..." -ForegroundColor Yellow
Stop-Service -Name $webSvcName  -Force -ErrorAction SilentlyContinue
Stop-Service -Name $apiSvcName  -Force -ErrorAction SilentlyContinue
Stop-Service -Name $pgSvcName   -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
Write-Host "      Done." -ForegroundColor Green

# STEP 2: Initialize PostgreSQL data directory
Write-Host ""
Write-Host "[2/7] Checking PostgreSQL data directory..." -ForegroundColor Yellow
Write-Host "      Path: $pgDataPath" -ForegroundColor Gray

$pgVersionFile = Join-Path $pgDataPath "PG_VERSION"
if (-not (Test-Path $pgVersionFile)) {
    Write-Host "      Data directory is EMPTY. Initializing with initdb..." -ForegroundColor Yellow

    # Fix ACL on data directory
    Write-Host "      Granting permissions to current user and SYSTEM..." -ForegroundColor Gray
    $acl = Get-Acl $pgDataPath
    $inherit = [System.Security.AccessControl.InheritanceFlags]"ContainerInherit,ObjectInherit"
    $propagate = [System.Security.AccessControl.PropagationFlags]::None
    $ruleSystem = New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\SYSTEM", "FullControl", $inherit, $propagate, "Allow")
    $ruleAdmin  = New-Object System.Security.AccessControl.FileSystemAccessRule("BUILTIN\Administrators", "FullControl", $inherit, $propagate, "Allow")
    $acl.SetAccessRule($ruleSystem)
    $acl.SetAccessRule($ruleAdmin)
    Set-Acl -Path $pgDataPath -AclObject $acl
    Write-Host "      Permissions set." -ForegroundColor Gray

    # Write password file
    $pwFile = "$env:TEMP\pg_pw_init.txt"
    [System.IO.File]::WriteAllText($pwFile, $pgPassword, [System.Text.Encoding]::UTF8)

    # Run initdb
    $initArgs = "--pgdata=`"$pgDataPath`" --username=postgres --pwfile=`"$pwFile`" --encoding=UTF8 --locale=C --auth=md5"
    Write-Host "      Running initdb..." -ForegroundColor Gray
    $proc = Start-Process -FilePath $initdbExe -ArgumentList $initArgs -NoNewWindow -Wait -PassThru
    Remove-Item $pwFile -Force -ErrorAction SilentlyContinue

    if ($proc.ExitCode -ne 0) {
        Write-Host "      ERROR: initdb failed (exit code $($proc.ExitCode))" -ForegroundColor Red
        Write-Host "      Try manually deleting all files in: $pgDataPath" -ForegroundColor Yellow
        Write-Host "      Then re-run this script." -ForegroundColor Yellow
        exit 1
    }
    Write-Host "      initdb succeeded!" -ForegroundColor Green

    # Configure pg_hba.conf for md5 auth
    $hbaFile = Join-Path $pgDataPath "pg_hba.conf"
    if (Test-Path $hbaFile) {
        $hba = Get-Content $hbaFile -Raw
        $hba = $hba -replace '(host\s+all\s+all\s+127\.0\.0\.1/32\s+)\w+', '${1}md5'
        $hba = $hba -replace '(host\s+all\s+all\s+::1/128\s+)\w+',        '${1}md5'
        Set-Content -Path $hbaFile -Value $hba -Encoding UTF8
        Write-Host "      pg_hba.conf: md5 auth enabled." -ForegroundColor Green
    }

    # Set port in postgresql.conf
    $confFile = Join-Path $pgDataPath "postgresql.conf"
    if (Test-Path $confFile) {
        $conf = Get-Content $confFile -Raw
        if ($conf -match '#port\s*=\s*\d+') {
            $conf = $conf -replace '#port\s*=\s*\d+', "port = $pgPort"
        } elseif ($conf -match 'port\s*=\s*\d+') {
            $conf = $conf -replace 'port\s*=\s*\d+', "port = $pgPort"
        } else {
            $conf = $conf + "`nport = $pgPort`n"
        }
        Set-Content -Path $confFile -Value $conf -Encoding UTF8
        Write-Host "      postgresql.conf: port set to $pgPort." -ForegroundColor Green
    }
} else {
    $pgVer = Get-Content $pgVersionFile -ErrorAction SilentlyContinue
    Write-Host "      Already initialized (PostgreSQL $pgVer). Skipping initdb." -ForegroundColor Green
}

# STEP 3: Start PostgreSQL
Write-Host ""
Write-Host "[3/7] Starting PostgreSQL service..." -ForegroundColor Yellow
Start-Service -Name $pgSvcName -ErrorAction SilentlyContinue
Start-Sleep -Seconds 8

$pgStatus = (Get-Service -Name $pgSvcName -ErrorAction SilentlyContinue).Status
if ($pgStatus -eq "Running") {
    Write-Host "      PostgreSQL: RUNNING" -ForegroundColor Green
} else {
    Write-Host "      WARNING: PostgreSQL is '$pgStatus'" -ForegroundColor Red
    Write-Host "      Check Event Viewer - Windows Logs - System for errors about '$pgSvcName'" -ForegroundColor Yellow
}

# STEP 4: Extend SCM timeout
Write-Host ""
Write-Host "[4/7] Extending service startup timeout to 120 seconds..." -ForegroundColor Yellow
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control" -Name "ServicesPipeTimeout" -Value 120000 -Type DWord
Write-Host "      Done." -ForegroundColor Green

# STEP 5: Recreate E-Attendance-API
Write-Host ""
Write-Host "[5/7] Recreating E-Attendance-API service..." -ForegroundColor Yellow
$apiSvc = Get-Service -Name $apiSvcName -ErrorAction SilentlyContinue
if ($null -ne $apiSvc) {
    Stop-Service -Name $apiSvcName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    & sc.exe delete $apiSvcName | Out-Null
    Start-Sleep -Seconds 1
}

$pgExists = $null -ne (Get-Service -Name $pgSvcName -ErrorAction SilentlyContinue)
if ($pgExists) {
    & sc.exe create $apiSvcName binPath= "`"$webExe`" --service-mode --service-role=api" start= delayed-auto DisplayName= "E-Attendance API Service" depend= $pgSvcName
} else {
    & sc.exe create $apiSvcName binPath= "`"$webExe`" --service-mode --service-role=api" start= delayed-auto DisplayName= "E-Attendance API Service"
}
Start-Sleep -Seconds 1
if ($null -ne (Get-Service -Name $apiSvcName -ErrorAction SilentlyContinue)) {
    & sc.exe failure $apiSvcName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
    & sc.exe failureflag $apiSvcName 1 | Out-Null
    & sc.exe description $apiSvcName "E-Attendance API, device communication, background workers, backup scheduler, and health monitoring." | Out-Null
    Write-Host "      E-Attendance-API: CREATED" -ForegroundColor Green
} else {
    Write-Host "      ERROR: Failed to create E-Attendance-API!" -ForegroundColor Red
}

# STEP 6: Recreate E-Attendance-Web
Write-Host ""
Write-Host "[6/7] Recreating E-Attendance-Web service..." -ForegroundColor Yellow
$webSvc = Get-Service -Name $webSvcName -ErrorAction SilentlyContinue
if ($null -ne $webSvc) {
    Stop-Service -Name $webSvcName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    & sc.exe delete $webSvcName | Out-Null
    Start-Sleep -Seconds 1
}
$apiExists = $null -ne (Get-Service -Name $apiSvcName -ErrorAction SilentlyContinue)
if ($apiExists) {
    & sc.exe create $webSvcName binPath= "`"$webExe`" --service-mode --service-role=web" start= delayed-auto DisplayName= "E-Attendance Web UI Service" depend= $apiSvcName
} else {
    & sc.exe create $webSvcName binPath= "`"$webExe`" --service-mode --service-role=web" start= delayed-auto DisplayName= "E-Attendance Web UI Service"
}
Start-Sleep -Seconds 1
if ($null -ne (Get-Service -Name $webSvcName -ErrorAction SilentlyContinue)) {
    & sc.exe failure $webSvcName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
    & sc.exe failureflag $webSvcName 1 | Out-Null
    & sc.exe description $webSvcName "E-Attendance Web UI frontend service." | Out-Null
    Write-Host "      E-Attendance-Web: CREATED" -ForegroundColor Green
} else {
    Write-Host "      ERROR: Failed to create E-Attendance-Web!" -ForegroundColor Red
}

# STEP 7: Start services
Write-Host ""
Write-Host "[7/7] Starting API and Web services..." -ForegroundColor Yellow
if ($pgStatus -eq "Running") {
    Start-Service -Name $apiSvcName -ErrorAction SilentlyContinue
    Write-Host "      Waiting 15 seconds for API to fully initialize..." -ForegroundColor Gray
    Start-Sleep -Seconds 15
    $apiStatus = (Get-Service -Name $apiSvcName -ErrorAction SilentlyContinue).Status
    Write-Host "      API: $apiStatus" -ForegroundColor $(if ($apiStatus -eq "Running") { "Green" } else { "Yellow" })
    if ($apiStatus -eq "Running") {
        Start-Service -Name $webSvcName -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 5
        $webStatus = (Get-Service -Name $webSvcName -ErrorAction SilentlyContinue).Status
        Write-Host "      Web: $webStatus" -ForegroundColor $(if ($webStatus -eq "Running") { "Green" } else { "Yellow" })
    }
} else {
    Write-Host "      Skipped: PostgreSQL is not running. Fix PostgreSQL first." -ForegroundColor Yellow
}
# Final summary
Write-Host ""
Write-Host "============= FINAL STATUS =============" -ForegroundColor Cyan
@($pgSvcName, $apiSvcName, $webSvcName) | ForEach-Object {
    $s = Get-Service -Name $_ -ErrorAction SilentlyContinue
    $status = if ($null -ne $s) { $s.Status.ToString() } else { "NOT INSTALLED" }
    $color = switch ($status) { "Running" { "Green" } "Stopped" { "Yellow" } default { "Red" } }
    Write-Host "  $($_): $status" -ForegroundColor $color
}
Write-Host ""
Write-Host "If PostgreSQL keeps failing, check Event Viewer System log." -ForegroundColor White
Write-Host ""
