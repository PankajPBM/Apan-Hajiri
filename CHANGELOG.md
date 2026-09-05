# Changelog

Release-level summary. For detailed device/protocol fix history see
[DEVICE_COMMAND_FIXES.md](DEVICE_COMMAND_FIXES.md); for how the system is put together see
[ARCHITECTURE.md](ARCHITECTURE.md).

## Versioning

The release version must be bumped together in the two shipped .NET apps:
[E.Attendance.Web/E.Attendance.Web.csproj](E.Attendance.Web/E.Attendance.Web.csproj) and
[E.Attendance/E.Attendance.csproj](E.Attendance/E.Attendance.csproj). Keep `<Version>`,
`<FileVersion>`, and `<AssemblyVersion>` aligned. The frontend package version in
[e-attendance-next/package.json](e-attendance-next/package.json) should match the same release.
Use [bump-version.ps1](bump-version.ps1) to update all version fields together.

- The installer (`installer/E_Attendace.Setup.iss`) reads the version straight off the compiled
  `E.Attendance.Web.exe` at ISCC compile time (`GetVersionNumbersString`) — it is never a separately
  typed value, so the installer can't drift out of sync with the app it's packaging.
- The running app exposes its own version at `GET /api/version` (public, no login required) and
  displays it in the sidebar footer, so anyone looking at a live install — or support staff on a
  call — can confirm exactly what build is running.

To release a new version: run `.\bump-version.ps1 <version>`, rebuild (Release), recompile the
installer, and add an entry below. Or run `.\build-installer.ps1 -Version <version>` to bump,
build Release, and compile the installer in one flow.

## Unreleased

## [1.1.14] - 2026-09-05

Weekend & holiday overtime calculation fixes, OT & Device Duplicate Punch sidebar menu additions, sidebar accordion navigation, and shift OT rules alignment.

### Added
- **Sidebar OT Navigation:** Added `OT` directly under `Shifts` in `Shift Setup` sidebar menu, routing to the OT configuration dashboard.
- **Sidebar Device Duplicate Punch Navigation:** Added `Device Duplicate Punch` option directly under `Privilege` in the `Access Control` sidebar menu.
- **Sidebar Accordion Dropdown:** Expanding any sidebar dropdown automatically closes any previously opened dropdown menu.

### Fixed
- **Worked Hours on Weekend / Holiday:** Prevented phantom 60-minute workday lunch break deduction from wiping short weekend or holiday punch spans down to 0.
- **Weekend / Holiday Work Counted in Overtime:** All hours worked on weekly offs and holidays are now directly credited to overtime in attendance reports and processing engine.
- **OT Dashboard Shift Display:** Removed hardcoded `General` fallback; OT rules table now exclusively lists user-created shifts (`shiftPlans`) with their actual names and parameters.

## [1.1.13] - 2026-09-03

Prerequisite runtimes auto-install, PostgreSQL B-Tree index limit fix, and automatic Windows Firewall & Defender integration.

### Added
- **Automatic .NET 10 Runtimes Installer:** Bundled and silently installs `aspnetcore-runtime-10.0.11-win-x64.exe` and `windowsdesktop-runtime-10.0.11-win-x64.exe` on fresh Windows 10/11 machines if missing.
- **Windows Firewall Automation:** Automatic inbound rules for `E.Attendance.Web.exe` (all ports), `E.Attendance.exe`, selected Web port, selected API port, and ZKTeco UDP port 4370.
- **Windows Defender Exclusions:** Automatic folder exclusion for install directory and process exclusions for executables.
- **Dynamic Port Reconfiguration:** Changing ports in Service Controller UI or CLI dynamically reconfigures firewall rules.
- **Installer Setup Task:** Added configurable permission checkbox during installation.

### Fixed
- **PostgreSQL 8,191-byte Index Limit:** Replaced raw line unique constraint with `md5("RawLine")` on `TransactionDetails` table, eliminating `54000: index row requires 10256 bytes, maximum size is 8191`.
- **Punch Queue Deadlock:** Added consecutive failure safety and bounded raw line length to prevent poison packets from blocking attendance punches.

## [1.1.12] - 2026-08-17

PostgreSQL connection string and service name alignment fix.

### Fixed
- Connection string in `appsettings.json` now includes `Password=PB@14891277` explicitly, so the
  Windows Service (running as `LocalSystem`/`NetworkService`) never hits `28P01: password
  authentication failed` — the runtime resolver path was silently failing inside a service context
  where the `PB_ATTENDANCE_DB_PASSWORD` environment variable is not available.
- PostgreSQL Windows service name standardised to `E_Attendance_PostgreSQL` (underscore) throughout
  `appsettings.json`, `WebServiceManager.cs`, and `TryStartPostgreSqlService` in `Program.cs`,
  matching the name the Inno Setup installer registers. `E-Attendance-PostgreSQL` (hyphen) moved to
  the `LegacyPostgreSqlServiceName` fallback — both names are probed at startup so existing installs
  with either naming continue to work.
- Duplicate `using` directives inserted into `WebServiceManager.cs` by a bad multi-replace are
  removed; file now compiles with 0 errors, 0 warnings.

## [1.1.11] - 2026-08-16

Fresh-install PostgreSQL service repair.

### Fixed
- Fresh install now keeps the official PostgreSQL installer as a fallback when portable
  PostgreSQL init/register fails, preventing API health from staying off because no DB service was
  installed.
- Portable PostgreSQL service setup now grants `NetworkService` access to the installed PostgreSQL
  folder before service startup.

## [1.1.10] - 2026-08-16

Security, reporting, installer, and versioning hardening.

### Fixed
- Reports now refresh with the current department/employee filters and show warnings when punch-row
  or date-range caps may make a large report incomplete.
- Legacy report endpoints now apply defensive row caps for direct leave, holiday-work, and CSV export
  requests.
- UI default admin password is consistently `PB@TechZone`; PostgreSQL password remains
  `PB@14891277`.
- Installer packaging is faster and smaller by skipping duplicate/precompressed files and avoiding
  the full PostgreSQL installer when portable PostgreSQL is bundled.
- Version fields are aligned across Web, Controller, frontend package metadata, and installer output.

## [1.1.9] - 2026-07-25

Web service creation repair.

### Fixed
- Installer now creates `E-Attendance-Web` without a Windows service dependency, avoiding false
  1068 failures when API dependency state changes during reinstall.
- Installer now retries Web service creation after clearing stale Web service entries and logs
  `sc create` exit codes for troubleshooting.
- Service Controller repair path now also retries Web service creation and reports the actual
  `sc.exe` output if Windows refuses to create it.

## [1.1.8] - 2026-07-24

PostgreSQL fresh-install repair.

### Fixed
- Installer no longer registers PostgreSQL against an empty `data` folder when portable `initdb`
  fails to create `PG_VERSION`.
- PostgreSQL service now uses `NetworkService`, which matches PostgreSQL's Windows service model,
  while API/Web remain `LocalSystem`.
- Broken PostgreSQL data folders without `PG_VERSION` are removed so the official PostgreSQL
  installer can create a valid cluster instead of leaving service error 1067.

## [1.1.7] - 2026-07-24

Power service reinstall hardening.

### Fixed
- Installer now power-removes stale API, Web, and PostgreSQL Windows service definitions before
  reinstalling them, including legacy service names.
- Fresh PostgreSQL reinstall now removes both current and legacy PostgreSQL services before deleting
  PostgreSQL files and data.
- API/Web services are recreated as `LocalSystem` auto-start services with restart-on-failure.
- API startup now waits for the actual selected PostgreSQL dependency to be running, preventing
  Windows service start error 1068 on reinstall.

## [1.1.6] - 2026-07-24

PostgreSQL dependency cleanup.

### Fixed
- Fresh install cleanup now removes both `E-Attendance-PostgreSQL` and legacy
  `E_Attendance_PostgreSQL` before reinstalling PostgreSQL.
- Update installs now avoid creating a broken duplicate PostgreSQL service when the legacy service
  is already the running database service.
- API service dependency now points to the PostgreSQL service that actually exists/runs, preventing
  Windows service start error 1068.
- Service dashboard now displays the actual PostgreSQL service name being used.

## [1.1.5] - 2026-07-24

PostgreSQL service installation hardening.

### Fixed
- Installer now explicitly registers `E-Attendance-PostgreSQL` with bundled `pg_ctl.exe` if the
  PostgreSQL silent installer copies files but does not create the Windows service.
- PostgreSQL ready check now retries that service-registration fallback before skipping database
  bootstrap.
- PostgreSQL service now gets auto-start, description, and failure-restart settings from one shared
  installer path.

## [1.1.4] - 2026-07-24

Windows service installer correction.

### Fixed
- Installer now creates separate `E-Attendance-API` and `E-Attendance-Web` Windows services.
- API service now runs only the API/device/background-worker role, while Web service runs only the
  frontend role to avoid port conflicts and duplicate workers.
- Service Controller dashboard can show and manage both API and Web service rows.
- Upgrade/uninstall cleanup now stops and removes both API and Web services.

## [1.1.3] — 2026-07-23

Branding asset refresh.

### Changed
- Replaced the application, installer, desktop shortcut, and web favicon icon set with a cleaner
  shield/fingerprint/person mark that stays more readable at small sizes.

## [1.1.2] — 2026-07-23

PostgreSQL 18 path alignment patch.

### Fixed
- Installer now installs bundled PostgreSQL 18 under `PostgreSQL\18` instead of the old
  `PostgreSQL\15` folder name.
- Database bootstrap and backup/restore tool lookup now search the bundled PostgreSQL 18 `bin`
  folder.
- Updates remain compatible with existing installs that already have PostgreSQL 18 files under the
  earlier `PostgreSQL\15` folder name.

## [1.1.1] — 2026-07-23

Installer performance patch.

### Changed
- Installer compression now favors faster extraction/install time instead of the smallest possible
  setup EXE.
- PostgreSQL's bundled installer is no longer recompressed inside the Inno Setup package, reducing
  wasted install-time extraction work.

## [1.1.0] — 2026-07-23

Production hardening release after setup, access-control, service, reporting, and shift-management
updates.

### Added
- OT setup under Shift Setup: Shift → Time Table → OT → Break Time.
- Per-shift OT selection and "OT after minutes" rules.
- OT report calculation now respects global OT enablement, per-shift OT, weekly-off work as OT, and
  holiday work as OT.
- Windows service naming aligned to the `E-Attendance-*` naming pattern.

### Fixed
- New Area creation now also initializes default access-control data such as time period, access
  group, and combination.
- Area/Department creation save flow now avoids leaving the creation dashboard stuck after a
  successful save.
- Shift, timetable, and break save buttons now show saving state to prevent accidental duplicate
  submissions.
- Reports received department/employee filtering and leave/holiday/OT handling improvements.

### Changed
- Version metadata now bumps with each shipped change across app, web service, frontend package, and
  changelog.

## [1.0.0] — 2026-07-05

Initial production release.

### Fixed
- Admin/user session bugs: password silently reset to a hardcoded default on unrelated saves;
  non-admin login could resolve to a fake full-access admin session (`normalizeUsers`/`loginUser`
  misuse, present in two duplicated copies of the same logic).
- User management (create/edit/delete) was reachable by any authenticated user, not just Admin —
  both the API (`/api/auth/users`) and the Settings "User" tab.
- New devices added to a freshly-created area were silently placed in Default Area/Department
  (`Areas`/`Departments.IsDefault` schema mismatch — boolean column vs. integer binding).
- Moving an employee to a different area left their enrollment stale on devices in their old area.
- Infrared (Type=2) face template sync used the wrong device command for low-end/older hardware
  (unified `BIODATA` instead of legacy `FACE`); device capability flags now self-heal from a real
  template upload instead of relying solely on a device's own (sometimes inaccurate) `INFO` reply.
- Device clock sync only ran on add/enable, never on a plain reconnect/reboot.
- QR self-service leave application broken by an overly-broad global 401-redirect.
- Reports: leave-day rows matched against the wrong field and showed no remark text.
- Logs/Transactions Area & Department filters only listed areas/departments that already had a
  device, instead of the full Setup list.
- `/timetable` was a second, fully independent implementation of Timetable/Break management,
  disconnected from the actively-maintained `/shifts` tabs — consolidated onto one page.

### Added
- Column-visibility preferences (persisted) on the Devices and Employees tables.
- CSV export with Area/Department/Device/date filters on the Logs and Transactions pages.
- Collapsible per-day rows on the Timetable setup page (was 7 always-expanded cards).
- `GET /api/version` + in-app version display (sidebar footer).
- **Database backup & restore.** Full `pg_dump` backups (manual or scheduled) configurable from
  Settings → Backup on the web dashboard, with history/retention/download. Defaults to enabled,
  weekly at 12:00 server local time (out of the box, no setup required). Restore is intentionally
  tray-app-only (destructive, needs OS-level service control) and takes its own safety backup of
  the current database immediately before replacing it. See
  [ARCHITECTURE.md §11.5](ARCHITECTURE.md#115-backup--restore) for the full design.

### Changed
- Routine device heartbeat log entries (idle poll, ping, connect handshake) no longer persist to
  the database — kept only in the in-memory live view — to prevent unbounded log growth at
  multi-device scale.
- Sidebar timezone/language settings, branding color contrast, and various button/label
  consistency fixes across Shifts, Employees, and Reports.

### Known limitations (see [ARCHITECTURE.md](ARCHITECTURE.md#12-known-limitations--open-items-as-of-this-document))
- Unsalted MD5 passwords; no device authentication on `/iclock/*`; plain HTTP; English-only UI.
- No face-recognition SDK bundled — a face template can only come from enrolling directly at a
  device, never from an uploaded photo.
- Device-side attendance-log deletion is all-or-nothing (`CLEAR LOG`) — the ZKTeco PUSH protocol
  has no date-range-scoped delete command. Software-side (database) retention is a separate,
  buildable feature that isn't implemented yet.
