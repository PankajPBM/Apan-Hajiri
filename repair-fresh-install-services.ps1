param(
    [string]$InstallDir = "C:\Program Files\E-Attendance",
    [string]$PostgresPassword = "PB@14891277",
    [string]$UiAdminPassword = "PB@TechZone"
)

$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script as Administrator."
}

$pgRoot = Join-Path $InstallDir "PostgreSQL\18"
$bin = Join-Path $pgRoot "bin"
$data = Join-Path $pgRoot "data"
$initdb = Join-Path $bin "initdb.exe"
$pgCtl = Join-Path $bin "pg_ctl.exe"
$psql = Join-Path $bin "psql.exe"

foreach ($path in @($initdb, $pgCtl, $psql)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing PostgreSQL tool: $path"
    }
}

function Invoke-Checked {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory = ""
    )

    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -WorkingDirectory $WorkingDirectory -NoNewWindow -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "$FilePath failed with exit code $($process.ExitCode)."
    }
}

$serviceName = "E-Attendance-PostgreSQL"
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if (-not (Test-Path -LiteralPath (Join-Path $data "PG_VERSION"))) {
    if (Test-Path -LiteralPath $data) {
        $backup = "$data.broken-$(Get-Date -Format yyyyMMdd-HHmmss)"
        Rename-Item -LiteralPath $data -NewName (Split-Path $backup -Leaf)
    }

    New-Item -ItemType Directory -Path $data -Force | Out-Null
    $pwFile = Join-Path $env:TEMP "e_att_pg_pw.txt"
    Set-Content -LiteralPath $pwFile -Value $PostgresPassword -NoNewline
    try {
        Invoke-Checked $initdb @(
            "--pgdata=$data",
            "--username=postgres",
            "--pwfile=$pwFile",
            "--encoding=UTF8",
            "--locale=C",
            "--auth=md5"
        ) $bin
    }
    finally {
        Remove-Item -LiteralPath $pwFile -Force -ErrorAction SilentlyContinue
    }

    Add-Content -LiteralPath (Join-Path $data "postgresql.conf") -Value "`nport = 1277`nlisten_addresses = '127.0.0.1'`n"
    Add-Content -LiteralPath (Join-Path $data "pg_hba.conf") -Value "`nhost all all 127.0.0.1/32 md5`nhost all all ::1/128 md5`n"
}

icacls $pgRoot /grant "NT AUTHORITY\NetworkService:(OI)(CI)F" /T /C | Out-Null

if (-not $service) {
    Invoke-Checked $pgCtl @(
        "register",
        "-N", $serviceName,
        "-D", $data,
        "-o", "-p 1277"
    ) $bin
}

sc.exe config $serviceName obj= "NT AUTHORITY\NetworkService" start= auto | Out-Null
sc.exe description $serviceName "E-Attendance PostgreSQL database service used by the E-Attendance API and reporting system." | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
sc.exe failureflag $serviceName 1 | Out-Null

$service = Get-Service -Name $serviceName -ErrorAction Stop
if ($service.Status -ne "Running") {
    Start-Service -Name $serviceName
    $service.WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
}

$env:PGPASSWORD = $PostgresPassword
$bootstrapSql = @"
SELECT 'CREATE DATABASE "E_Attendace"'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'E_Attendace')\gexec
DO `$`$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'E_Attendace') THEN
    CREATE ROLE "E_Attendace" WITH LOGIN PASSWORD '$PostgresPassword';
  ELSE
    ALTER ROLE "E_Attendace" WITH LOGIN PASSWORD '$PostgresPassword';
  END IF;
END
`$`$;
"@
$bootstrapFile = Join-Path $env:TEMP "e_att_bootstrap.sql"
Set-Content -LiteralPath $bootstrapFile -Value $bootstrapSql
Invoke-Checked $psql @("-h", "127.0.0.1", "-p", "1277", "-U", "postgres", "-f", $bootstrapFile) $bin
Remove-Item -LiteralPath $bootstrapFile -Force -ErrorAction SilentlyContinue

$appSql = @"
CREATE SCHEMA IF NOT EXISTS "E_Attendace";
GRANT ALL PRIVILEGES ON SCHEMA "E_Attendace" TO "E_Attendace";
CREATE TABLE IF NOT EXISTS "E_Attendace"."auth_users" (
  "Id" SERIAL PRIMARY KEY,
  "UserName" TEXT NOT NULL UNIQUE,
  "PasswordHash" TEXT NOT NULL DEFAULT '',
  "FullName" TEXT NULL,
  "Role" TEXT NOT NULL DEFAULT 'Operator',
  "Status" TEXT NOT NULL DEFAULT 'active',
  "FeatureAccessJson" jsonb NOT NULL DEFAULT '{}'::jsonb,
  "CreatedAtUtc" timestamptz NOT NULL DEFAULT NOW(),
  "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_auth_users_UserName" ON "E_Attendace"."auth_users" (LOWER(TRIM("UserName")));
INSERT INTO "E_Attendace"."auth_users" ("UserName","PasswordHash","FullName","Role","Status","FeatureAccessJson")
VALUES ('admin', md5('$UiAdminPassword'), 'Admin User', 'Admin', 'active', '{}')
ON CONFLICT DO NOTHING;
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA "E_Attendace" TO "E_Attendace";
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA "E_Attendace" TO "E_Attendace";
"@
$appFile = Join-Path $env:TEMP "e_att_app.sql"
Set-Content -LiteralPath $appFile -Value $appSql
Invoke-Checked $psql @("-h", "127.0.0.1", "-p", "1277", "-U", "postgres", "-d", "E_Attendace", "-f", $appFile) $bin
Remove-Item -LiteralPath $appFile -Force -ErrorAction SilentlyContinue

foreach ($svc in @("E-Attendance-API", "E-Attendance-Web")) {
    $existing = Get-Service -Name $svc -ErrorAction SilentlyContinue
    if ($existing) {
        Restart-Service -Name $svc -Force
        (Get-Service -Name $svc).WaitForStatus("Running", [TimeSpan]::FromSeconds(45))
    }
}

Write-Host "Repair complete."
Write-Host "UI login: admin / $UiAdminPassword"
Write-Host "DB login: postgres / $PostgresPassword"
