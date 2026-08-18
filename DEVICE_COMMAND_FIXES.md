# Device Command / Access-Control Fixes

Detailed record of changes so the reasoning is recoverable later. Newest first.

---

## 2026-07-04 — Device add put in wrong area + infrared face template distribution (2 fixes)

**#1 New area/device add failed with `42804: column "IsDefault" is of type boolean but expression is
of type integer`, silently falling back to Default Area.** `Areas`/`Departments` tables were bootstrapped
with `IsDefault BOOLEAN`, but `SqliteAttendanceRepository.AddArea`/`AddDepartment` (shared by SQLite and
Postgres) always binds that value as a plain C# `int` (1/0), matching how every other flag column in
this schema works. That's fine for a pre-existing row, but `SyncSqlAreaAndDepartment` auto-creates a
matching SQL row via `AddArea`/`AddDepartment` the first time a **brand-new** area gets a device —
hitting the boolean column and throwing. The device-add call failed outright, leaving the device on
whatever fallback area applied earlier. Fix (both `E.Attendance.Web/Program.cs` `EnsurePostgresSchema`
and `installer/E_Attendace.Setup.iss`): `IsDefault` declared `INTEGER`, plus a `DO $$`/`DO $do$`
migration that `ALTER COLUMN ... TYPE INTEGER` for any database that already has it as boolean (runs on
every startup / every install, so no manual DB fix is needed for already-deployed installs).

**#2 Infrared (Type=2) face template push used the wrong command for low-end devices.**
`CreateEmployeeBiometricSyncCommands` sent `DATA UPDATE BIODATA ... Type=2` for infrared templates —
the newer *unified* template command. Confirmed via live command log on a real device (ZKTeco MB2000)
that `DATA QUERY BIODATA` returns `Return=-3` (unsupported) while `DATA QUERY FACE` returns `Return=0`
(understood) — this class of device only speaks the older *legacy* `DATA UPDATE FACE PIN=...\tFID=...
\tValid=1\tSize=...\tTMP=...` syntax (protocol §12.1.1.4), confirmed byte-for-byte against ZKTeco's own
official C# PUSH SDK reference demo (`Commands.cs`: `Command_UpdateFaceTmp`). Added
`DeviceProtocolService.CreateLegacyFaceTemplateCommand` and switched the infrared branch to use it —
the visible-light (Type=9, `BIODATA`) branch is untouched, since that path was already confirmed working.

Also: `HandleFaceTemplatePayload` now self-heals a device's `SupportsInfraredFace`/
`SupportsVisibleLightFace` capability flag to `true` the moment that device uploads a real template —
direct proof of working hardware, more reliable than that device's own `CMD=INFO` reply for low-end
units that never report `FaceFunOn` correctly. Never downgrades an already-`true` flag.

**Confirmed not fixable in software:** a plain uploaded/captured photo can never become a real face
template — it can only ever be pushed as a display/comparison photo (`USERPIC`/`BIOPHOTO`). Turning a
photo into a matchable template requires ZKTeco's own proprietary face-recognition SDK (what their
official apps like CV Security/BioTime bundle internally), which isn't part of this codebase or its
public PUSH SDK distribution — verified by inspecting the full `ZKTeco Push SDK Dis` distribution
(protocol docs 2019–2025, C#/Java reference demos, `ZKTeco_Photo_Crop_Service`) and finding no
feature-extraction code anywhere in it, only the same push-protocol commands this app already implements.

Build: `dotnet build -c Release` → 0 errors, 0 warnings.

---

## 2026-07-01 — Transactions/reports + registration Yes/No fetch (3 fixes)

**#1 Transactions showed wrong user ID.** `TryParseAttendanceLine` (`Program.cs`) treated `OPLOG`
operation lines as punches, using the operation-type code as the employee ID (dashboard showed opcodes
`30`/`101` though the only user is PIN=1). Spec p.86: `OPLOG ${OpType}\t${OpWho}\t...` (opcode first,
not a PIN); attendance is `${Pin}\t${Time}\t${Status}...` via `table=ATTLOG`. Fix: reject `OPLOG` lines
in `TryParseAttendanceLine`. Also deleted 67 mis-parsed `OPLOG` rows from `TransactionDetails`.

**#2 Registration Yes/No fetch behavior.** Non-registration devices had no auto attendance fetch.
Added `attLogFetchUtcBySerial` throttle + a block in the `options=all` handler: a non-registration
device now auto-queues **attendance-logs only** (`DATA QUERY ATTLOG`) on connect (throttled 60s), no
user/bio pull. Registration devices unchanged (full fetch-all on add/enable + realtime user sync).
Manual fetch unchanged (fetches requested data regardless of Yes/No).

**#3 Full fetch never re-pulled attendance.** In `BuildOptionsResponse`, `forceFullDataReset` reset the
user/bio stamps to 0 but left `Stamp`/`OpStamp`/`ATTLOGStamp`/`OPERLOGStamp` untouched — so a fetch-all
re-pulled users/bio but never attendance, leaving old punches stranded on the device. Fix: on
`forceFullDataReset`, set those to `0` too. (fetch-all already calls `MarkFullDataResetPending`.)

**Reports:** the `/api/reports/daily-punch-summary` code is correct — it was empty only because there
was no real attendance data (fake OPLOG removed by #1, real ATTLOG blocked by #3). Fixing #1+#3 lets
real punches land and link to employee PIN=1, so reports populate automatically.

Build: solution Release = 0 errors. All three shipped in `installer\exe\E-Attendance_Offline_Setup.exe`
(2026-07-01 20:30). Source backup: `Backup/working-source-20260701-194618.tgz`.

---

## 2026-06-30 (ROOT FIX) — AccUnLockComb stuck on pending

**Root cause (found by reading the code):** `E.Attendance.Services/Production/ConfigSyncService.cs`
queued `AccUnLockComb` (and privilege commands) inside a `Task.Run` with artificial
`Task.Delay(6000)+4000+5000+6000` calls — so `AccUnLockComb` was queued **~11s AFTER**
`AccTimeZone`/`AccGroup`. By then the SpeedFace-V5L had finished its short active getrequest window
and gone idle, so `AccUnLockComb` was queued too late to be pulled and sat `pending` forever. Live
proof: `AccTimeZone`/`AccGroup` CreatedUtc = 11:10:15, `AccUnLockComb` = 11:10:26.

A secondary cause: `AccTimeZone`/`AccGroup`/`AccUnLockComb` got **no devicecmd reply** (the firmware
applies the access-control config then resets the PUSH session, dropping the reply), so they stayed
"sent, no reply" and the stale-reset recycled them endlessly.

**Fixes:**
1. `ConfigSyncService.cs` — removed the `Task.Delay` sequencing; queue privileges + `AccUnLockComb`
   **immediately** so they ride the same active poll window as `AccTimeZone`/`AccGroup`.
2. `E.Attendance.Web/Program.cs` `BuildPendingCommandPayload` — added `AccTimeZone`/`AccGroup`/
   `AccUnLockComb` to the **fire-and-forget** set (complete on delivery), so they drain after one send
   instead of being recycled forever. (Isolation still delivers each one alone per poll.)

Build: solution Release = 0 errors. Shipped in `installer\exe\E-Attendance_Offline_Setup.exe` (23:46).

**Also (attendance):** all 7 `DeviceTableStamps` were stuck at **9999**, which tells the device
"already uploaded everything" — blocking re-upload of punches. Reset to 0 in the DB as an immediate
unblock; make this reset automatic in the fetch-all/forceFullDataReset path next.

---

## 2026-06-30 (build pipeline) — Why fixes never shipped + correct build process

**Root cause of "my code changes never appear in the running app":** the installer was packaging **stale binaries**.
- The Inno Setup script `installer/E_Attendace.Setup.iss` (header comment) is explicit: **build the Solution in Visual Studio (Release), then run `ISCC` on the .iss. DO NOT use `build-installer.ps1`** — it packages old artifacts.
- The `.iss` reads the Web exe from `E.Attendance.Web\bin\Release\net10.0\E.Attendance.Web.exe` (the normal VS build output). It **falls back** to `...\net10.0\win-x64\E.Attendance.Web.exe` if the first is missing — and a leftover **`win-x64\`** self-contained publish (e.g. from a `dotnet publish -r win-x64`) is NOT refreshed by a normal build, so the installer would ship that stale copy.

**Correct build process (verified working 2026-06-30):**
```
# 1. (optional) remove stale self-contained leftover so the .iss can't pick it up
rm -rf E.Attendance.Web/bin/Release/net10.0/win-x64
# 2. clean Release build of the whole solution (== VS: Build → Build Solution, Release)
dotnet build E-Attendance.sln -c Release
# 3. compile the installer directly with Inno Setup
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\E_Attendace.Setup.iss
# → output: installer\exe\E-Attendance_Offline_Setup.exe   (offline mode bundles installer\postgresql-18.3-3.exe)
```
Verify the shipped code is fresh: `E.Attendance.Web\bin\Release\net10.0\E.Attendance.Web.dll` timestamp should be newer than your last edit; its size grows when code is added (diagnostic build = 1,455,616 bytes).

---

## 2026-06-30 (later) — Idle device heartbeats via ping, not getrequest (diagnostic + likely fix)

**Symptom after the fixes below deployed:** the fetch-all loop stopped (confirmed: `Skipped redundant fetch-all` in logs), and the access-control chain queued correctly — but `AccTimeZone/AccGroup/AccUnLockComb` stayed `SentUtc = NULL` for hours. Live evidence: the device (`192.168.1.73`) keeps making TCP connections to `:8081` every ~24s, but produces **no logs and delivers no commands**.

**Diagnosis:** per spec p.34, an idle device should heartbeat via `getrequest` (which delivers commands); only during big-data upload should it use `ping`. This device heartbeats via **`/iclock/ping`** when idle (handler returned bare `"OK"`, logged nothing) — so it never pulls queued commands. `SupportPing=0` was deployed but the device kept pinging.

**Changes — `E.Attendance.Web/Program.cs`:**
| Location | Change | Why |
|---|---|---|
| `BuildOptionsResponse` (~11653) | **Omit the `SupportPing` line entirely** (was `SupportPing=0`) | Some ZKTeco firmware treats the *presence* of the key as "ping enabled" regardless of value. Omitting forces getrequest heartbeat. |
| `/iclock/ping` handler (~6493) | **Diagnostic log** `PING heartbeat from {sn}` + pending count | See if/when the device pings while commands are pending. |
| `/iclock/getrequest` handler (~6575) | **Diagnostic log** `GETREQUEST poll from {sn} — no commands (OK)` | See if the device switches to getrequest heartbeat. |

**Verification goal:** after rebuild, watch logs. If `PING heartbeat … pending=3` keeps appearing → device still pinging (deeper firmware-config issue). If `GETREQUEST poll …` starts appearing and `AccUnLockComb` gets `SentUtc`+reply → fixed. Build: 0 errors / 0 warnings.

**Note on a competing analysis (rejected):** another AI proposed "device chokes on a command flood / photo upload blocks the socket" and suggested waiting for `Return=0` before releasing each command. Rejected — the live DB shows the full DATA QUERY batch *was* delivered and acted on (device uploaded BIOPHOTO/BIODATA/OPERLOG); and `DATA QUERY`/`CHECK` never reply via `/iclock/devicecmd` (they answer via cdata upload), so blocking on `Return=0` would create a new permanent stall.

---

## 2026-06-30 — Spec-grounded command fixes (stuck AccUnLockComb + no transactions)

**Authority:** `installer/Attendance_PUSH_Communication_Protocol_20250429.txt` (official ZKTeco PUSH spec).
**Device:** SpeedFace-V5L, SN `SYZ8242100057`. **App runs as Windows Service `E-Attendance-API`.**

### Symptoms fixed
- `DATA UPDATE AccUnLockComb ...` stuck on `pending` forever.
- No attendance transactions importing.
- Duplicate `CHECK` / `DATA QUERY USERINFO` commands piling up.

### Root causes (evidence-based)
1. **Fetch-all re-queue loop (the real cause of sticking).** Live `AppLogs` showed **3 fetch-alls in 6 seconds** (dashboard re-POSTing `POST /api/devices/{serial}/fetch-all` + device-add/enable-registration auto-triggers). Each call ran `ClearPendingCommands` (deleting the pending `AccUnLockComb`) and re-queued the whole `DATA QUERY` batch. Because pending commands are ordered oldest-first (`SqliteAttendanceRepository.cs:1184`) and `AccUnLockComb` is queued last, the isolation logic kept truncating the batch *before* it, so it never got its solo poll.
2. **Out-of-spec command ID.** A prior change emitted the full 32-char GUID as the wire command ID. Spec p.85 (`${CmdID}`): *"a length not over 16 digits."* 32 > 16 → out of spec. (It echoed back once for `INFO` with `Return=0`, i.e. firmware tolerated it, but relying on that is fragile.)
3. **`SupportPing=1` in service mode.** Spec p.31: SupportPing keeps a heartbeat alive when the device *"cannot send getrequest during the command processing."* As a Windows Service the UDP ping path is firewall-blocked, so with `1` the device leaned on ping and stopped reliably polling `/iclock/getrequest`.

### Changes applied — `E.Attendance.Web/Program.cs`
| # | Location | Change | Why |
|---|---|---|---|
| 1 | `BuildPendingCommandPayload` (~line 12136) | Command ID reverted to **13-char short alias** `C:{shortId}:{cmd}` (was full GUID `C:{command.Id:N}:`) | Spec mandates CmdID ≤ 16 chars. Reply still resolves via `CommandIdAlias.Map` + prefix-scan fallback (restart-safe). |
| 2 | top-level vars (~line 509) | Added `fetchAllUtcBySerial` dictionary | Backing store for the throttle. |
| 3 | `QueueFetchAllCommandsAsync` (~line 13172) | **Throttle: skip fetch-all if one ran < 60s ago** for that device | Stops the dashboard re-POST loop from re-queuing every poll. |
| 4 | `QueueFetchAllCommandsAsync` (~line 13196) | **Removed `ClearPendingCommands`** (now `clearedPending = 0`) | It was deleting the pending `AccUnLockComb` mid-flight. Re-queue is safe because of dedup + fire-and-forget. |
| 5 | `BuildOptionsResponse` (~line 11645) | `SupportPing=1` → **`SupportPing=0`** | Forces reliable `/iclock/getrequest` polling in service mode (UDP ping blocked by firewall). |

### Left as-is (already correct, confirmed against spec)
- **`Acc*` command isolation** (one command per poll) — spec p.32 confirms the device cannot poll getrequest during command processing, so `AccTimeZone` / `AccGroup` / `AccUnLockComb` / `ENROLL_*` must be delivered alone.
- **`Delay` clamp ≥ 10s**, `PollDelaySeconds = 10`.
- **Stale-reset threshold 2 min** in `QueueFetchAllCommandsAsync`.

### Access-control command set (reference — from `DeviceProtocolService.cs`, spec §12.1.1)
Applied in dependency order: **AccTimeZone → AccGroup → AccUnLockComb** (+ optional AccHoliday). Employees link via `USERINFO Grp={groupId}`.
```
DATA UPDATE AccTimeZone  UID={tzId}  {Sun..Sat}Start=HHmm {Sun..Sat}End=HHmm   # time period (when access allowed)
DATA UPDATE AccGroup     ID={groupId}  Verify=0  ValidHoliday=1  TZ={tzId};0;0  # access group → time period
DATA UPDATE AccUnLockComb UID={combId}  Group1..Group5={groupIds}              # unlock combination → groups
DATA UPDATE AccHoliday   UID={id}  HolidayName=..  StartDate=MMdd  EndDate=MMdd  TimeZone={tzId}
```
Reply format (spec §13): `ID={CmdID}&Return={code}&CMD=DATA` — device echoes the same CmdID it received.

### Build / deploy
- Verified: `dotnet build E.Attendance.Web/E.Attendance.Web.csproj -c Debug` → **0 errors, 0 warnings**.
- User performs the **Release build + install in Visual Studio**. (Stopping the `E-Attendance-API` service needs Administrator.)

### History note (why the command-ID flip-flopped)
- PB Attendance + an earlier session used the full 32-char GUID and it "worked" (lenient firmware).
- A Gemini session (`~/.gemini/antigravity-ide/brain/c966acdc-.../walkthrough.md`) switched to short 13-char IDs.
- The **official spec settles it: ≤ 16 chars → short ID is correct.** The full-GUID's lone success (a 32-char `INFO` reply with `Return=0`) was firmware tolerance, not proof the limit doesn't exist.
