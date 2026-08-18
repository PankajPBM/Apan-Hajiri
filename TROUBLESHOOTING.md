# E-Attendance — Troubleshooting Guide

Real problems encountered against real hardware and installs, with exact symptoms and fixes.
Newest first. See [ARCHITECTURE.md](ARCHITECTURE.md) for background on how the system is built.

---

## T-001 — Login fails with `28P01: password authentication failed for user "E_Attendace"`

**Symptom:** The web UI loads, but every login attempt (even correct credentials) returns:
> `Login failed: 28P01: password authentication failed for user "E_Attendace"`

**Root cause:** The installed `appsettings.json` in `C:\Program Files\E-Attendance\` has no
`Password=` field in the PostgreSQL connection string. The `ResolveExpectedPostgreSqlPassword()`
fallback works fine when the app is run interactively (dev machine, normal terminal), but when
running as a Windows Service under `LocalSystem` or `NetworkService`, environment variables set
for other users are invisible and the resolver silently fails.

**Diagnosis:**
```powershell
Get-Content "C:\Program Files\E-Attendance\appsettings.json"
# Look for Password= in the PostgreSQLConnection string — if it's absent, that's the cause.
```

**Fix (run as Administrator):**
```powershell
$content = '{
  "ConnectionStrings": {
    "PostgreSQLConnection": "Host=127.0.0.1;Port=1277;Database=E_Attendace;Username=E_Attendace;Password=PB@14891277;Search Path=E_Attendace;Include Error Detail=True;Maximum Pool Size=100;Connection Lifetime=300"
  },
  "DatabaseSettings": {
    "UsePostgreSql": true,
    "ServiceName": "E_Attendance_PostgreSQL",
    "CommandTimeout": 30,
    "MaxRetryCount": 3
  }
}'
[System.IO.File]::WriteAllText("C:\Program Files\E-Attendance\appsettings.json", $content)
sc.exe stop "E-Attendance-API"
sc.exe stop "E-Attendance-Web"
Start-Sleep -Seconds 3
sc.exe start "E-Attendance-API"
Start-Sleep -Seconds 10
sc.exe start "E-Attendance-Web"
```

**Permanent fix:** The password is now embedded in `appsettings.json` in source — any new
installer build will include it. Do not remove the `Password=` field from the connection string.

---

## T-002 — Transactions page shows zero records after fresh install

**Symptom:** Login works, all pages open, but Transactions / Attendance show no data at all even
though devices are connected and punching.

**Root cause:** `EnsurePostgresSchema` (the function that creates all tables) runs in a background
task after startup. If the database connection fails at startup (e.g., due to T-001 above), the
background task logs an error and does not retry reliably — leaving the database with the schema
created but zero tables.

**Diagnosis:**
```powershell
$env:PGPASSWORD = "PB@14891277"
& "C:\Program Files\E-Attendance\PostgreSQL\15\bin\psql.exe" -h 127.0.0.1 -p 1277 -U postgres -d "E_Attendace" -c "\dt E_Attendace.*"
# If output says "Did not find any tables" — the schema bootstrap never ran.
```

**Fix:** After fixing T-001 and restarting the services, the API re-runs `EnsurePostgresSchema`
on startup. Wait ~15 seconds after the services restart, then refresh the browser. All tables will
be created automatically on the first successful connection.

If tables still don't appear after restart, check the Application event log:
```powershell
Get-WinEvent -LogName Application -MaxEvents 50 |
  Where-Object {$_.ProviderName -match "E.Attendance"} |
  Select-Object TimeCreated, LevelDisplayName, @{N="Msg";E={$_.Message.Substring(0,300)}} |
  Format-List
```

---

## T-003 — Services fail to start: `7003: depends on E_Attendance_PostgreSQL which might not be installed`

**Symptom:** In Event Viewer → System, you see error 7003:
> `The E-Attendance Service service depends on the following service: E_Attendance_PostgreSQL. This service might not be installed.`

**Root cause:** A stale old single-service install (`E-Attendance Service`) has a dependency on
`E_Attendance_PostgreSQL`, but the PostgreSQL service was installed under a different name (e.g.
`E-Attendance-PostgreSQL` with a hyphen).

**Diagnosis:**
```powershell
Get-Service | Where-Object {$_.Name -match "Attend|Postgres"} |
  Select-Object Name, Status, StartType | Format-Table -AutoSize
```

**Fix:** A fresh install (removing the old service) resolves this. The new split-service install
(`E-Attendance-API` + `E-Attendance-Web` + `E_Attendance_PostgreSQL`) does not have this
dependency declaration. If you can't reinstall immediately, delete the stale service:
```powershell
sc.exe delete "E-Attendance Service"
```

---

## T-004 — PostgreSQL service name mismatch (`E_Attendance_PostgreSQL` vs `E-Attendance-PostgreSQL`)

**Symptom:** PostgreSQL installed and running but the API logs `Unable to auto-start PostgreSQL
service` or the Service Controller tray app shows PostgreSQL as stopped/unknown.

**Root cause:** The Inno Setup installer registers the service as `E_Attendance_PostgreSQL`
(underscore). Earlier versions of the code used `E-Attendance-PostgreSQL` (hyphen). If these are
out of sync, the auto-start probe misses the service.

**Current state (v1.1.12+):** Both names are probed in order. `E_Attendance_PostgreSQL` is
`ExpectedPostgreSqlServiceName`; `E-Attendance-PostgreSQL` is `LegacyPostgreSqlServiceName`.
`GetPostgreSqlServiceCandidates()` tries both at startup.

**Manual check:**
```powershell
sc query "E_Attendance_PostgreSQL"
sc query "E-Attendance-PostgreSQL"
```

**Manual start (whichever exists):**
```powershell
sc.exe start "E_Attendance_PostgreSQL"
```

---

## T-005 — `E_Attendace` spelling throughout code and database

**This is intentional.** The schema name, database name, PostgreSQL username, and connection string
all use `E_Attendace` (missing the 'n' in "Attendance"). This is a long-standing naming quirk
baked throughout the entire codebase and installer — do not "fix" the spelling, doing so will
break the database connection.

Affected identifiers:
- PostgreSQL database: `E_Attendace`
- PostgreSQL user: `E_Attendace`
- PostgreSQL schema: `E_Attendace`
- Connection string `Database=`, `Username=`, `Search Path=`: all `E_Attendace`

---

## T-006 — `relation "E_Attendace.AccCombinations" does not exist` in Application log

**Symptom:** Application event log shows EF Core query errors referencing missing tables such as
`AccCombinations`, `AccTimeZones`, etc.

**Root cause:** These tables are created by `EnsurePostgresSchema` on first startup. If the
database connection was broken at startup (T-001), tables were never created. Once T-001 and T-002
are fixed and the services are restarted, these tables will be created automatically.

---

## T-007 — Next.js build lock error during `dotnet build`

**Symptom:**
```
Unable to acquire lock at e-attendance-next\.next\lock, is another instance of next build running?
```

**Root cause:** Two parallel `dotnet build` runs tried to build the Next.js frontend simultaneously,
leaving a stale `.next\lock` file.

**Fix:**
```powershell
Remove-Item -Force "d:\Pankaj\Project\E-Attendance\e-attendance-next\.next\lock" -ErrorAction SilentlyContinue
Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
```
Then re-run `dotnet build`.

---

## T-008 — Service restart fails with `Access is denied` from normal PowerShell

**Symptom:**
```
Restart-Service : Cannot open E-Attendance-API service on computer '.'.
```
or
```
[SC] OpenService FAILED 5: Access is denied.
```

**Root cause:** Stopping/starting Windows Services requires administrator privileges.

**Fix:** Always run PowerShell as **Administrator** for service management:
- Right-click **Start** → **Windows PowerShell (Admin)** or **Terminal (Admin)**
- Then run `sc.exe stop "E-Attendance-API"` etc.

---

## Credential reference

| Credential | Value |
|---|---|
| Web UI admin username | `admin` |
| Web UI admin password | `PB@TechZone` |
| PostgreSQL superuser | `postgres` |
| PostgreSQL superuser password | `PB@14891277` |
| PostgreSQL app user | `E_Attendace` |
| PostgreSQL app password | `PB@14891277` |
| PostgreSQL port | `1277` |

> **Note:** The web UI admin password (`PB@TechZone`) and the PostgreSQL password (`PB@14891277`)
> are different. `28P01` errors are always about PostgreSQL authentication, not the web login.

---

## Quick health check

Run this block to verify all three services are up and the database is reachable:

```powershell
# Services
Get-Service | Where-Object {$_.Name -match "Attend|Postgres"} |
  Select-Object Name, Status | Format-Table -AutoSize

# Ports
netstat -ano | Select-String "LISTENING" | Select-String ":8001|:8081|:1277"

# DB connectivity
$env:PGPASSWORD = "PB@14891277"
& "C:\Program Files\E-Attendance\PostgreSQL\15\bin\psql.exe" `
  -h 127.0.0.1 -p 1277 -U "E_Attendace" -d "E_Attendace" -c "SELECT COUNT(*) FROM ""E_Attendace"".""TransactionDetails"";"

# Web UI
try { $r = Invoke-WebRequest "http://127.0.0.1:8001" -TimeoutSec 5; "UI: HTTP $($r.StatusCode)" } catch { "UI Error: $_" }
try { $r = Invoke-WebRequest "http://127.0.0.1:8081/api/version" -TimeoutSec 5; "API: $($r.Content)" } catch { "API Error: $_" }
```

Expected output: all 3 services Running, ports 1277/8001/8081 LISTENING, transaction count (any
number ≥ 0), UI HTTP 200, API returns a version JSON.
