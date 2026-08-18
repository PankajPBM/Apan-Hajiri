# E-Attendance — Architecture

This document describes how the E-Attendance system is built and how its pieces fit together: the backend, the frontend, the database, the ZKTeco device integration, authentication, and the main end-to-end workflows. It reflects the system as it actually exists in this repository, not an idealized design.

---

## 1. What this software is

E-Attendance is a self-hosted, on-premise biometric attendance management system. It runs entirely on a single Windows machine (as a Windows Service + bundled PostgreSQL instance) and communicates with ZKTeco biometric terminals (fingerprint/face/card) over their **PUSH SDK** protocol. Employees, admins, and managers use a web dashboard (served by the same backend) to manage devices, employees, shifts, leave, and reports. Employees without dashboard access can also submit manual punches or leave requests via QR-code kiosk pages on their own phones.

**Tech stack:**
- **Backend:** .NET 10, ASP.NET Core **minimal APIs** (almost the entire HTTP surface lives in one very large `Program.cs`, ~18,000 lines, plus a smaller EF-Core-based module for Leave/Holiday/Timetable in `ProductionApiEndpoints.cs`).
- **Database:** PostgreSQL (bundled and installed alongside the app; a legacy SQLite code path also exists in `SqliteAttendanceRepository` but production always runs Postgres).
- **Frontend:** Next.js (App Router), built as a **static export** (`output: export`) and served as static files by the ASP.NET Core app's `wwwroot` — there is no separate Node server in production.
- **Device protocol:** ZKTeco "PUSH SDK Attendance PUSH Communication Protocol" (HTTP-based; devices poll the server, the server never opens a connection to a device).
- **Packaging:** Inno Setup installer (`installer/E_Attendace.Setup.iss`) that installs the Windows Service, bundles PostgreSQL, and registers everything.

---

## 2. Solution / repository structure

```
E-Attendance/
├── E.Attendance.Core/        Domain models + repository/service interfaces (POCOs, enums)
├── E.Attendance.Data/        Data access — two parallel implementations (see §5)
├── E.Attendance.Services/    Business logic services (AttendanceService, DeviceProtocolService, ...)
├── E.Attendance.Web/         The actual running application — Program.cs (minimal API host),
│                             Production/ProductionApiEndpoints.cs (EF-based Leave/Holiday/Timetable API),
│                             Licensing/, wwwroot/ (built frontend)
├── E.Attendance/             WinForms tray/service-control utility (installer helper, NOT the web app)
├── e-attendance-next/        Next.js frontend (static export)
├── installer/                Inno Setup script + protocol/reference docs
└── DEVICE_COMMAND_FIXES.md   Running log of empirically-discovered device protocol quirks
```

**Important:** `E.Attendance` (no suffix) is a small WinForms utility used during install/service-management (bootstrap the DB, set ports, install the Windows Service, open the dashboard) — it is *not* the main application and has nothing to do with attendance logic. The actual product is `E.Attendance.Web`.

---

## 3. High-level architecture

```
┌─────────────────────────┐        HTTP (PUSH SDK protocol)        ┌──────────────────────┐
│  ZKTeco biometric        │◄──────────────────────────────────────►│                      │
│  terminals (FP/Face/Card)│   /iclock/cdata, /iclock/getrequest,   │                      │
│  (SpeedFace, MB2000, ...) │   /iclock/devicecmd, /iclock/ping      │                      │
└─────────────────────────┘                                         │   E.Attendance.Web   │
                                                                     │  (ASP.NET Core        │
┌─────────────────────────┐        HTTP + JSON (/api/*)             │   minimal API host,   │
│  Dashboard (browser)      │◄──────────────────────────────────────►│   Windows Service)    │
│  Next.js static export    │   JWT bearer auth (mostly)             │                      │
└─────────────────────────┘                                         │  Serves:              │
                                                                     │   - /api/*  (JSON)    │
┌─────────────────────────┐        HTTP + JSON (public endpoints)   │   - /iclock/* (device)│
│  QR kiosk pages           │◄──────────────────────────────────────►│   - wwwroot/* (SPA)   │
│  /check-in, /apply-leave  │   No login required (intentional)      │                      │
│  (employee's own phone)   │                                         └──────────┬───────────┘
└─────────────────────────┘                                                     │
                                                                                 │ Npgsql (raw ADO.NET
                                                                                 │  + some EF Core)
                                                                       ┌─────────▼───────────┐
                                                                       │  PostgreSQL           │
                                                                       │  schema: E_Attendace  │
                                                                       │  (bundled, installed  │
                                                                       │   as its own service) │
                                                                       └──────────────────────┘
```

Everything — device traffic, the dashboard API, and the static frontend files — is served by the **same** ASP.NET Core process, typically on two ports (an API port and a separate "web UI" port; both point at the same process, differentiated by a port-routing middleware).

---

## 4. Backend structure

### 4.1 `Program.cs` — the center of gravity

Almost the entire backend lives in one file, written as ASP.NET Core **top-level statements** (no `Startup.cs`). Roughly, top to bottom:

1. **Bootstrap**: resolve content root/web root, parse CLI args, build `WebApplication`, configure Windows Service hosting.
2. **Service registration**: `AddControllers()`, JWT bearer authentication (`AddAuthentication().AddJwtBearer(...)`), CORS policy (`IsAllowedFrontendOrigin` — permissive for LAN/private IPs, rejects public hostnames with dots), `AddDbContext<PbAttendanceDbContext>()` for the EF-Core-based subsystem.
3. **License/trial gating**: a `LicenseGuard` checked on startup and refreshed periodically; blocks `/api/*` with `423 Locked` if invalid (except `/api/license/*` itself).
4. **Middleware pipeline** (order matters a lot here — see §8 for the exact chain): CORS → global exception handler (device-safe: never returns non-OK to `/iclock/*`) → authentication/authorization → **license gate** → **JWT allow-list gate** (custom, see §7) → **port-routing gate** (API port vs. Web UI port) → static files → controllers.
5. **~200+ minimal API endpoint definitions** (`app.MapGet/MapPost/MapPut/MapDelete(...)`) covering employees, devices, device commands, transactions/attendance, access control, personnel setup, auth, app-config, and the device-facing `/iclock/*` routes.
6. **Local/static helper functions** (the back half of the file) — parsing, command-building, DB access, capability inference, etc. C# top-level statements allow local functions declared later in the file to be called earlier, which is why the file reads top-down as "endpoints, then helpers."

### 4.2 The two backend "modules"

There are effectively **two overlapping API subsystems**, built at different times with different data-access styles:

| | Program.cs (majority) | `Production/ProductionApiEndpoints.cs` |
|---|---|---|
| Covers | Employees, Devices, device commands, attendance/transactions, personnel setup, auth, access control, licensing | Leave requests, Leave types, Holidays, Timetables |
| Data access | Raw `Npgsql`/ADO.NET via `IAttendanceRepository` (`PostgresAttendanceRepository` → `SqliteAttendanceRepository` in Postgres mode) | EF Core (`PbAttendanceDbContext`) |
| Employee ID type | `TEXT` columns throughout (see §6) | `Guid`-typed EF properties mapped onto the same `TEXT` columns via a `GuidToStringConverter` |

This split exists because EF Core's automatic schema creation (`Database.EnsureCreated()`) once raced ahead of the raw-SQL bootstrap script and created `uuid` columns where the rest of the app expected `TEXT` — a recurring source of `operator does not exist: uuid = character varying` errors. The established fix pattern (used repeatedly) is: **never `.Include()` a raw-repository-backed navigation from an EF `DbContext` query** — resolve employee code/name/area/department via `AttendanceService.ListEmployees()` after the EF query, not via EF joins.

### 4.3 Services layer (`E.Attendance.Services`)

- **`AttendanceService`** — the main façade over the repository layer: employee CRUD, device registration/listing, command queueing (`QueueDeviceCommand`), attendance record queries. Almost every endpoint in `Program.cs` goes through this.
- **`DeviceProtocolService`** — pure command-string builders for the ZKTeco protocol: `CreateUserInfoCommand`, `CreateEmployeeBiometricSyncCommands`, `CreateRemoteEnrollCommand` (`ENROLL_FP`/`ENROLL_BIO`), `CreateTimezoneCommand`/`CreateMachineTzCommand`/`CreateTimeSyncCommand`, `CreateUnifiedTemplateCommand`, `CreateComparisonPhotoCommand`, etc. No I/O — just protocol string construction, unit-testable in isolation.
- **`ConfigSyncService`** (Production) — pushes access-control config (time periods, groups, combinations) and shift/timetable config out to devices when the dashboard saves them.
- **`AttendanceProcessingEngine`** (Production) — attendance-rule calculation support used by Reports (schedule resolution, late/early/overtime math).
- **`ZkPushServer`** — an alternate/earlier device-listener scaffold; the live device protocol handling actually lives directly in `Program.cs`'s `/iclock/*` endpoints, not here.

### 4.4 Data layer (`E.Attendance.Data`)

- **`PostgresAttendanceRepository`** — thin wrapper; internally just constructs a `SqliteAttendanceRepository` with `usePostgreSql: true`. (Naming is legacy/misleading — this class is the actual Postgres implementation used in production.)
- **`SqliteAttendanceRepository`** — the real implementation, branches its SQL dialect (`"..."` Postgres-quoted identifiers vs. bare SQLite) based on a constructor flag. Handles employees, devices, device commands, sync logs, biometric templates/photos, unified users (`auth_users`-adjacent tables used for device-side user records, distinct from software login `auth_users`).
- **`Production/*Repository.cs` + `PbAttendanceDbContext`** — EF Core side, used only by the Leave/Holiday/Timetable module.

---

## 5. Database

- **Engine:** PostgreSQL, schema name **`E_Attendace`** (note: intentionally *not* "Attendance" — a long-standing naming quirk baked throughout the codebase; always use this exact spelling in SQL).
- **Schema management:** no formal migration tool. `Program.cs` runs a large set of idempotent `CREATE TABLE IF NOT EXISTS` / `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` / conditional constraint-fix blocks (`TryExec(...)`, `DO $$ ... END $$` blocks) on every startup — a self-healing bootstrap rather than versioned migrations.
- **Connection string:** `Host=127.0.0.1;Port=1277;Database=E_Attendace;Username=E_Attendace;Password=PB@14891277;Search Path=E_Attendace`. The password is **embedded directly** in `appsettings.json` — do not remove it. The `ResolveExpectedPostgreSqlPassword()` runtime-resolver path is a fallback only; when running as a Windows Service under `LocalSystem`/`NetworkService`, the `PB_ATTENDANCE_DB_PASSWORD` environment variable is not visible and the runtime resolver silently returns the wrong value, causing `28P01` auth failures on every API call including login.
- **The TEXT-id convention:** `Employees.Id`, `Devices.Id`, `TransactionDetails.Id`, etc. are `TEXT` columns holding GUID-*formatted* strings (not native `uuid`). This was a deliberate fix after EF's `uuid` auto-creation conflicted with the raw-repository layer's expectations. Any new EF entity referencing one of these must use a `GuidToStringConverter`, and any EF query must avoid `.Include()`-ing across that boundary.
- **Key tables:**
  - `Employees`, `Devices`, `DeviceCommands`, `SyncLogs`, `DeviceSyncLogs` — core device/employee/command-queue state.
  - `TransactionDetails` — **this is the actual attendance-record store** (not a separate "AttendanceRecords" table in the live code path). Every device punch (`ATTLOG`/`OPERLOG`) and every manual/QR punch request lands here, with `ParsedOk` distinguishing status: `0` = failed to parse (kept for audit, hidden from dashboard), `1` = accepted device data, `2` = pending manual/QR request awaiting admin approval, `3` = rejected.
  - `biometric_templates` — per-employee, per-finger/face biometric templates, keyed by `(user_pin, type, no, idx, major_ver)`. The `major_ver` column is part of the uniqueness key so that a template captured on a v10-algorithm device and one captured on a v13-algorithm device coexist rather than overwrite each other; device sync then picks whichever version matches the target device's own reported algorithm version.
  - `biometric_photos` — profile/comparison photos, same per-user keying.
  - `LeaveRequests`, `LeaveTypes`, `LeaveBalances`, `LeaveRemarks`, `LeaveAttachments` — EF-Core-managed (Production module). `LeaveRequests.PersonnelId` is `TEXT` underneath but exposed as `Guid EmployeeId` in the EF model.
  - `auth_users` — software dashboard login accounts. `Id` is `BIGSERIAL` (plain integer), **not** a GUID — important when correlating with anything that expects a `Guid` (e.g., don't try to pass a software user's id as a `Guid ActorId` on leave-decision endpoints).
  - `app_config` — a generic `(ConfigKey TEXT, JsonValue jsonb)` key/value store used for settings blobs (`"settings"`, `"shifts"`, `"access-control"`, license-adjacent config) via `ReadAppConfigJson`/`SaveAppConfigJson` helpers.

---

## 6. Device integration (ZKTeco PUSH protocol)

Devices are **PUSH clients**: they periodically poll the server; the server never dials out to a device. Reference doc: `installer/Attendance_PUSH_Communication_Protocol_20250429.txt` (this is the actual, exhaustively-searched source of truth for the protocol this session — not every command a device *appears* to support is actually documented or implemented by its firmware, and this doc should be checked before assuming a command exists).

### 6.1 Endpoints (all under `/iclock/`, always public — devices can't log in)

- **`GET /iclock/cdata?options=all`** — a device's boot/reconnect handshake. Server replies with a `GET OPTION FROM:` block (poll delay, time zone, transfer flags, `PushProtVer`, etc.) built by `BuildOptionsResponse`. This is also where auto clock-sync (`SHELL date -s` / `hwclock -w`) and, for registration devices, a full data fetch get triggered.
- **`POST /iclock/cdata?table=ATTLOG|OPERLOG|USERINFO|BIODATA|...`** — the device pushing actual data (attendance punches, user records, biometric templates/photos).
- **`GET /iclock/getrequest`** — idle heartbeat / "do you have any commands for me?" poll. The server's queued `DeviceCommand` rows are delivered here (or piggybacked on other responses when a device's firmware doesn't call this endpoint reliably — several firmware-specific workarounds exist for this, documented inline in `Program.cs`).
- **`POST /iclock/devicecmd`** — the device reporting the result of a command it executed (`ID=...&Return=...&CMD=...`, optionally followed by a payload like the full `CMD=INFO` capability dump).
- **`GET /iclock/ping`** — legacy heartbeat; intentionally **not** advertised via the `SupportPing` key in the options response, because some firmware treats the mere *presence* of that key as "ping is the only channel," starving `/iclock/getrequest` of traffic and never delivering queued commands.

### 6.2 Command queue

Server → device commands are just short text lines (e.g., `SET OPTION TimeZone=345`, `DATA UPDATE USERINFO PIN=...`, `ENROLL_BIO TYPE=9 PIN=...`), queued via `AttendanceService.QueueDeviceCommand(serial, text)` and stored as `DeviceCommand` rows. Delivery wraps each in `C:{commandId}:{text}` automatically — callers never build that envelope themselves. Replies are matched back by command ID and recorded on the same row.

**Known landmines** (do not reintroduce):
- `RELOAD OPTIONS` reboots the device and wipes its command queue — never chain it after commands you need to survive (e.g., never add it to the "sync data" flow).
- `ClearPendingCommands` on a fetch-all wipes *other* pending commands too (e.g., access-control pushes) — don't call it there; a time-based throttle is used instead.
- Command IDs must stay ≤16 characters (device firmware limit).
- A device's `CMD=INFO` reply carries genuine capability flags (`FingerFunOn`, `FaceFunOn`, `BioPhotoFun`, `VisilightFun`, `FingerprintAlgorithmVersion`, etc.) that drive routing decisions (which biometric type to push, whether to attempt visible-light vs. infrared face templates) — this is the authoritative source for device capability, more reliable than assuming based on model name.

### 6.3 What's *not* solvable purely server-side

Verified this session: things like a device-local "Duplicate Punch Period" setting, or reliable face-vs-fingerprint capability signaling, are **not** exposed as documented PUSH-protocol commands — attempts to guess a `SET OPTION` key for them get a syntactically-successful `Return=0` ACK but silently no-op, because ZKTeco firmware ACKs any well-formed `SET OPTION` regardless of whether the key is implemented. Where this happened, the fix was moved server-side instead (e.g., duplicate-punch suppression is done by the server dropping a repeat `ATTLOG`/`OPERLOG` punch within a configured window, before it's persisted — the same approach ZKTeco's own `ZKBioTime` software manual documents for this exact feature).

---

## 7. Authentication & security model

- **Dashboard auth:** JWT bearer tokens. `POST /api/auth/login` checks credentials against `auth_users.PasswordHash` (currently **unsalted MD5** — a known, documented weakness, not yet fixed) and issues a signed token (HMAC, key generated once and persisted to `%ProgramData%\E_Attendance\auth-signing.key`).
- **Enforcement:** a custom middleware (`IsPubliclyAllowedApiRequest`) gates every `/api/*` request — anything not explicitly allow-listed requires a valid bearer token, or gets `401`. The allow-list is intentionally narrow:
  - `POST /api/auth/login`, `/api/license/*` (needed to render the login page).
  - `GET /api/personnel/verify/{code}` — QR kiosk employee lookup.
  - `POST /api/attendance/request` (QR manual punch) and `POST /api/leave` (QR/self-service leave) — both intentionally public, because the QR pages have no login, but both only ever write to a **pending** state requiring admin approval, bounding the risk of leaving them open.
  - `GET /api/leave/types` — needed to populate the leave-type dropdown on the public apply-leave page.
- **Frontend token handling:** a single global `fetch` patch (`lib/auth-fetch.ts`, installed once via the root layout) attaches the bearer token to every `/api/*` call automatically, and redirects to `/login` on a `401` — **except** on the public kiosk pages (`/apply-leave`, `/check-in`), which have no session to lose and must not be yanked to a login screen just because one of their calls legitimately 401s.
- **Device auth:** **none.** `/iclock/*` trusts whatever serial number a request claims, gated only by "is this serial already registered in our `Devices` table" (unregistered serials are silently ignored, not processed) — there is no cryptographic proof a request actually came from that physical device. A `SecretKey` column exists on `Devices` but nothing currently validates it. This is a known, currently-open gap (mitigation planned: IP-pinning with a warn-only mode, not yet implemented as of this document).
- **CORS:** `IsAllowedFrontendOrigin` allows `localhost`/loopback/private-IP/no-dot-hostname origins (i.e., anything that looks like a LAN address), rejects public-looking hostnames.
- **Licensing:** a separate `LicenseGuard` gate blocks all of `/api/*` (except `/api/license/*`) with `423 Locked` if the license/trial is invalid — checked at startup and refreshed every 30s.

---

## 8. Middleware pipeline order (why order matters here)

Roughly, in registration order:
1. CORS (`UseCors("frontend")`)
2. Global exception handler — must be first so it can catch anything below; special-cased to always answer `200 OK` to `/iclock/*` even on an unhandled exception (some device firmware treats any non-OK response as "upload failed" and retries the identical payload forever, invisibly, if this isn't in place).
3. `UseAuthentication()` / `UseAuthorization()` — populates `context.User` from the JWT, if present.
4. License gate — blocks `/api/*` (except `/api/license/*`) if the license isn't valid.
5. JWT allow-list gate (`IsPubliclyAllowedApiRequest`) — the auth enforcement described in §7.
6. Port-routing gate — this app can be configured with separate "API port" and "Web UI port"; this middleware rejects device/API traffic arriving on the wrong port, and serves the SPA only on the Web UI port.
7. Static files (`UseDefaultFiles`/`UseStaticFiles`) — serves the built Next.js export from `wwwroot`.
8. Controllers / minimal API endpoint dispatch.

---

## 9. Frontend architecture

- **Framework:** Next.js App Router, `output: "export"` — **fully static**, no server-side rendering or API routes in production. All data fetching happens client-side against the backend's `/api/*` REST endpoints.
- **Route groups:**
  - `(auth)/login` — the login page.
  - `(dashboard)/*` — everything behind login: devices, employees, access-control (time period/groups/combination/privilege), assign (shift scheduling), attendance (Transactions), leaves (Leave + Holiday management), reports, logs (aliases to `notifications`), settings, setup, shift/timetable.
  - `apply-leave` and `check-in` — **top-level, outside the dashboard group** — the public QR kiosk pages, deliberately not behind the auth guard.
- **API base resolution:** `lib/api-bases.ts` builds a list of candidate backend origins to try (preferred/cached origin, current page origin, a dynamically-discovered API port via `/api-config.json`, same-port fallback) — necessary because the frontend is a static export that could be opened from any LAN address, and the API/Web UI ports can differ.
- **Auth session:** stored client-side (`localStorage`/`sessionStorage` depending on "remember me"), separate from the bearer token (also localStorage/sessionStorage). The dashboard layout (`(dashboard)/layout-client.tsx`) is a client-side guard: it redirects to `/login` if no session, and re-validates the session against the server on mount.
- **Styling:** Tailwind CSS, hand-built components (no component library). Sidebar/branding colors are dynamic — driven by a `primaryColor` branding setting, with text/icon colors computed for contrast (`pickForegroundColor`) rather than hardcoded, so the UI stays legible regardless of the configured brand color.

---

## 10. Key end-to-end workflows

### 10.1 Adding a device
1. Admin adds a device (manually, or promotes an auto-"discovered" device that's already been polling the server).
2. Server queues an `INFO` command + a time-sync command trio + (if "Registration Device" = Yes) a full `DATA QUERY` fetch-all sequence (`USERINFO`, `BIODATA`, `FINGERTMP`, `FACE`, `BIOPHOTO`, `USERPIC`), gated by device capability flags so low-end devices aren't sent queries they don't support.
3. If "Clear All Data" was also selected, a `CLEAR DATA` command is queued first and **all** data ingestion (including `ATTLOG`/`OPERLOG`, not just the explicit fetch) is suppressed until the device confirms it's actually empty — otherwise a device's pre-clear backlog could slip in during the race between "clear queued" and "clear executed."

### 10.2 Employee ↔ device biometric sync
- On employee create/update, or on "Sync Data," the employee's stored biometric templates/photos (from `biometric_templates`/`biometric_photos`) are hydrated onto the in-memory `Employee` object (`HydrateEmployeeBiometricsForDeviceSync`), picking the template version that matches the *target device's* reported fingerprint algorithm version.
- `CreateEmployeeBiometricSyncCommands` then builds the actual push commands: `USERINFO`, fingerprint `DATA UPDATE FINGERTMP`, face template (**visible-light Type=9 uses the newer unified `DATA UPDATE BIODATA`**; **infrared Type=2 uses the older legacy `DATA UPDATE FACE`** command instead — low-end/older infrared-only devices (e.g. ZKTeco MB-series) are documented against the legacy syntax, not `BIODATA`, and reject/ignore the unified command for that type), and — reusing the *same* profile photo — both `USERPIC` (display photo) and `BIOPHOTO` (the actual face-verification comparison photo, for devices that support photo-based matching).
- **A face template can only ever come from physically enrolling the person's face at a device.** A plain uploaded/captured photo can be pushed as a display or comparison photo (`USERPIC`/`BIOPHOTO`) but never becomes a matchable biometric template — that conversion requires a proprietary face-recognition algorithm (the kind ZKTeco's own official software bundles as a separate, licensed SDK) that this codebase does not have. Once a device uploads a real template via legacy `FACE`/unified `BIODATA`, `HandleFaceTemplatePayload` self-heals that device's `SupportsInfraredFace`/`SupportsVisibleLightFace` capability flag to `true` — this is more reliable than the `CMD=INFO` capability flag for some low-end devices that never report it correctly.

### 10.3 Attendance capture
- **Device-sourced:** `POST /iclock/cdata?table=ATTLOG|OPERLOG` → parsed → inserted into `TransactionDetails` with `ParsedOk=1` (auto-accepted, no approval needed).
- **Manual/QR-sourced:** `POST /api/attendance/request` (no login) → inserted with `ParsedOk=2` (pending) → requires an admin to approve via the Transactions page before it counts as real attendance.
- An optional server-side "Duplicate Punch Period" filter (when configured) drops a repeat `ATTLOG`/`OPERLOG` punch from the same employee within N minutes, before persistence — mirrors the same feature in ZKTeco's own commercial software, implemented as pure server-side logic since no device-side equivalent exists.

### 10.4 Leave application (two entry points, one pipeline)
- **QR kiosk** (`/apply-leave`, no login): verify employee code → `POST /api/leave` → row created with `Status="pending"`.
- **Dashboard** (`Leaves and Holiday` page, logged-in admin): same `POST /api/leave` endpoint via a modal.
- Both flows land in the same `LeaveRequests` table and appear together in the dashboard's Leave Requests list (filterable by area/department/employee/status), with Approve/Reject actions calling `PUT /api/leave/{id}/approve|reject`.
- Once **approved**, the leave automatically feeds into **Reports**: `GET /api/leave?status=approved` is fetched by the Reports page and used to mark an employee "On Leave" for any date falling within the approved range — no separate wiring needed, since both leave sources share the same underlying table and status.

### 10.5 Reports
- The Reports page fetches approved punch rows from `GET /api/transactions` with `approvedOnly=true`, then calculates daily/weekly/monthly/late/absent/overtime views in the browser using the current shift, holiday, and leave settings.
- Report transaction loading is capped at 12,000 punch rows and date expansion is capped at 740 days. When either cap is reached, the UI shows a warning so large reports are not mistaken for complete totals; users should narrow date, department, employee, or search filters for exact long-range totals.
- Legacy `/api/reports/*` list/export endpoints also apply defensive row caps for direct API usage: 5,000 rows for leave history/holiday-work lists and 20,000 rows for CSV attendance export.

### 10.6 Logs / observability
- `AppLogs` is a flat `(timestamp, level, source, message, detail)` log table, written via `AddLog(...)` calls scattered through the backend — it has no structured device/area/department columns. The dashboard's Logs page filters by Area/Department/Device by matching the selected device's serial number against the free-text message/detail, using the device list (which *does* carry area/department) to resolve which serials belong to a given area/department — an approximation, not a structural join, since the underlying log rows aren't tied to a device by foreign key.

---

## 11. Deployment

- Packaged via **Inno Setup** (`installer/E_Attendace.Setup.iss`): installs the `E.Attendance.Web` binary as two Windows Services (`E-Attendance-API` and `E-Attendance-Web`), bundles and installs PostgreSQL as its own Windows Service (`E_Attendance_PostgreSQL`, underscore — match this name exactly in `appsettings.json` and `WebServiceManager.cs`), and installs the small `E.Attendance` WinForms tray utility for service control/DB bootstrap/port configuration.
- The Next.js frontend is built (`npm run build`, static export to `out/`) and copied into `E.Attendance.Web/wwwroot` as part of the .NET build (`SyncBundledFrontend`/`BuildBundledFrontend` MSBuild targets in the `.csproj`) — a Release build of the backend also rebuilds and re-bundles the frontend automatically.
- The user (not this assistant) performs Release builds and installer runs in Visual Studio; verification during development is done via `dotnet build` (Debug) plus direct `psql` inspection of the live database, not by running deploy scripts.

---

## 11.5 Backup & restore

- **Backup** is a full `pg_dump` of the whole database (`--format=plain --clean --if-exists --no-owner`) — since every piece of app state (settings, employees, devices, photos, license state) already lives in Postgres, one dump is a complete backup. Triggered manually or on a schedule (daily/weekly/monthly) from the web dashboard's **Settings → Backup** tab (admin-only), tracked in `BackupHistory`/`BackupSchedule` tables, with automatic pruning to the configured retention count. A background poll loop (`Program.cs`, 10-minute interval) runs due scheduled backups.
- **Restore is deliberately not exposed over the web API at all** — it's a destructive operation only reachable from the `E.Attendance` WinForms tray app's "Restore Backup" button, which is the one component with the OS-level ability to stop the backend service before replaying the dump and restart it afterward. The tray requires the `postgres` admin password as a confirmation gate (validated via a real login probe, not just a string compare) before touching anything.
- **Safety net beyond a plain replay**: immediately before restoring, the tray takes its own dump of the *current* database into the same backup folder (`safety_before_restore_<timestamp>.psql`) — so a bad restore has an undo path. The backup file is also sanity-checked (`BackupService.LooksLikePostgresDump`) for a real `pg_dump` header before ever being run against production data.
- Both the web backend and the tray app share one implementation (`E.Attendance.Services/BackupService.cs`) for the actual `pg_dump`/`psql` process execution, instead of two independently-maintained copies of the same logic.
- Not yet implemented: offsite/cloud backup replication (local disk only, so a disk failure can take out both live data and backups if they're on the same volume — the configurable backup location lets an admin point it at a network share to mitigate this, but nothing does that automatically), and a bounded restore timeout (10 minutes) that just fails outright on an unusually large database rather than extending or resuming.

---

## 12. Known limitations / open items (as of this document)

- Passwords hashed with unsalted MD5 (`auth_users.PasswordHash`) — no salting, fast to crack if the DB is ever exfiltrated.
- No cryptographic device authentication on `/iclock/*` — a device's identity is only as strong as knowing its serial number.
- Plain HTTP throughout (no TLS) — both dashboard and device traffic are unencrypted on the LAN.
- Server binds `0.0.0.0` — reachable from any interface the host machine has, not just localhost.
- No functioning multi-language support — the Settings "Language" option is intentionally locked to English pending a real i18n implementation.
- Two parallel data-access styles (raw ADO.NET vs. EF Core) for historical reasons — new work touching Leave/Holiday/Timetable should stay in the EF module; everything else should stay in the raw-repository style to avoid the `uuid`/`TEXT` mismatch class of bugs.
- No face-recognition/feature-extraction SDK is bundled — this app can only receive/redistribute face templates that a device itself already generated via physical enrollment; it cannot turn an uploaded photo into a matchable template the way ZKTeco's own commercial software can (that requires a separately-licensed proprietary algorithm SDK this project doesn't have).
- A device's `SupportsInfraredFace`/`SupportsVisibleLightFace` capability flags rely on its own `CMD=INFO` reply being accurate; some low-end/older devices never report this correctly. The flags self-heal once that device actually uploads a real template, but a device that has *never* had anyone enrolled on it will show as incapable until either its `INFO` reply improves or someone enrolls there first.
