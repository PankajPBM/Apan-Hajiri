using System.Text.Json;
using System.Text;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;
using E.Attendance.Data.Production;
using Microsoft.EntityFrameworkCore;

namespace E.Attendance.Services.Production;

public sealed class ConfigSyncService(PbAttendanceDbContext db, IAttendanceService attendanceService, IDeviceProtocolService protocolService)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task SyncShiftsAsync(string json, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            var config = JsonSerializer.Deserialize<ShiftConfigContainer>(json, JsonOptions);
            if (config == null) return;

            // 1. Sync BreakTimes
            var existingBreaks = await db.BreakTimes.ToListAsync(ct);
            foreach (var b in config.BreakDefinitions)
            {
                var name = (b.Name ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var existing = existingBreaks.FirstOrDefault(x => x.BreakName == name);
                if (existing != null)
                {
                    existing.BreakName = name;
                    existing.StartTime = ParseTime(b.StartTime);
                    existing.EndTime = ParseTime(b.EndTime);
                    existing.DurationMinutes = b.DurationMinutes;
                    existing.CalculateType = b.CalculateType;
                    existing.MinimumBreakMinutes = b.MinimumBreakMinutes;
                    existing.IsActive = true;
                }
                else
                {
                    db.BreakTimes.Add(new BreakTimeEntity
                    {
                        BreakName = name,
                        StartTime = ParseTime(b.StartTime),
                        EndTime = ParseTime(b.EndTime),
                        DurationMinutes = b.DurationMinutes,
                        CalculateType = b.CalculateType,
                        MinimumBreakMinutes = b.MinimumBreakMinutes,
                        IsActive = true
                    });
                }
            }

            // 2. Sync Shifts
            var existingShifts = await db.Shifts.ToListAsync(ct);
            foreach (var s in config.ShiftDefinitions)
            {
                var id = TryParseInt(s.Id);
                var existing = existingShifts.FirstOrDefault(x => (id > 0 && x.Id == id) || x.ShiftName == s.Name);
                if (existing != null)
                {
                    existing.ShiftName = s.Name;
                    existing.StartTime = ParseTime(s.StartTime);
                    existing.EndTime = ParseTime(s.EndTime);
                    existing.GraceMinutes = s.GraceMinutes;
                    existing.HalfdayThreshold = s.HalfDayThreshold;
                    existing.OvertimeAfterMinutes = s.OvertimeAfter;
                    existing.NightShift = s.Type == "night";
                }
                else
                {
                    db.Shifts.Add(new ShiftEntity
                    {
                        ShiftName = s.Name,
                        StartTime = ParseTime(s.StartTime),
                        EndTime = ParseTime(s.EndTime),
                        GraceMinutes = s.GraceMinutes,
                        HalfdayThreshold = s.HalfDayThreshold,
                        OvertimeAfterMinutes = s.OvertimeAfter,
                        NightShift = s.Type == "night"
                    });
                }
            }

            await db.SaveChangesAsync(ct);
            
            // Reload updated lists to get IDs
            existingShifts = await db.Shifts.ToListAsync(ct);
            var shiftByName = existingShifts
                .Where(x => !string.IsNullOrWhiteSpace(x.ShiftName))
                .ToDictionary(x => x.ShiftName, x => x, StringComparer.OrdinalIgnoreCase);
            var configShiftKeyToDbId = config.ShiftDefinitions
                .Select(s => new { Key = NormalizeKey(s.Id), Name = (s.Name ?? string.Empty).Trim() })
                .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Name) && shiftByName.ContainsKey(x.Name))
                .ToDictionary(x => x.Key, x => shiftByName[x.Name].Id, StringComparer.OrdinalIgnoreCase);

            var existingBreaksForMap = await db.BreakTimes.ToListAsync(ct);
            var breakByName = existingBreaksForMap
                .Where(x => !string.IsNullOrWhiteSpace(x.BreakName))
                .ToDictionary(x => x.BreakName, x => x, StringComparer.OrdinalIgnoreCase);
            var configBreakKeyToDbId = config.BreakDefinitions
                .Select(b => new { Key = NormalizeKey(b.Id), Name = (b.Name ?? string.Empty).Trim() })
                .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Name) && breakByName.ContainsKey(x.Name))
                .ToDictionary(x => x.Key, x => breakByName[x.Name].Id, StringComparer.OrdinalIgnoreCase);

            // 3. Sync Timetables
            var existingTimetables = await db.Timetables.Include(x => x.Days).ToListAsync(ct);
            foreach (var t in config.Timetables)
            {
                var id = TryParseInt(t.Id);
                var existing = existingTimetables.FirstOrDefault(x => (id > 0 && x.Id == id) || x.TimetableName == t.Name);
                if (existing != null)
                {
                    existing.TimetableName = t.Name;
                    existing.UpdatedAt = DateTime.UtcNow;

                    // Sync Days
                    foreach (var d in t.Days)
                    {
                        var day = existing.Days.FirstOrDefault(x => x.DayOfWeek == d.DayOfWeek);
                        var shiftId = ResolveShiftId(d.ShiftId, configShiftKeyToDbId, shiftByName, config);
                        if (day != null)
                        {
                            day.ShiftId = shiftId;
                            day.IsWeeklyOff = d.IsWeeklyOff;
                            day.CheckInTime = ParseTime(d.CheckInTime);
                            day.CheckInStartTime = ParseTime(d.CheckInStart);
                            day.CheckInEndTime = ParseTime(d.CheckInEnd);
                            day.CheckOutStartTime = ParseTime(d.CheckOutStart);
                            day.CheckOutTime = ParseTime(d.CheckOutTime);
                            day.CheckOutEndTime = ParseTime(d.CheckOutEnd);
                            day.CrossDay = d.CrossDay;
                            day.UseBreakTime = d.UseBreakTime;
                        }
                        else
                        {
                            existing.Days.Add(new TimetableDayEntity
                            {
                                DayOfWeek = d.DayOfWeek,
                                ShiftId = shiftId,
                                IsWeeklyOff = d.IsWeeklyOff,
                                CheckInTime = ParseTime(d.CheckInTime),
                                CheckInStartTime = ParseTime(d.CheckInStart),
                                CheckInEndTime = ParseTime(d.CheckInEnd),
                                CheckOutStartTime = ParseTime(d.CheckOutStart),
                                CheckOutTime = ParseTime(d.CheckOutTime),
                                CheckOutEndTime = ParseTime(d.CheckOutEnd),
                                CrossDay = d.CrossDay,
                                UseBreakTime = d.UseBreakTime
                            });
                        }
                    }
                }
                else
                {
                    var newTimetable = new TimetableEntity
                    {
                        TimetableName = t.Name,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    foreach (var d in t.Days)
                    {
                        var shiftId = ResolveShiftId(d.ShiftId, configShiftKeyToDbId, shiftByName, config);
                        newTimetable.Days.Add(new TimetableDayEntity
                        {
                            DayOfWeek = d.DayOfWeek,
                            ShiftId = shiftId,
                            IsWeeklyOff = d.IsWeeklyOff,
                            CheckInTime = ParseTime(d.CheckInTime),
                            CheckInStartTime = ParseTime(d.CheckInStart),
                            CheckInEndTime = ParseTime(d.CheckInEnd),
                            CheckOutStartTime = ParseTime(d.CheckOutStart),
                            CheckOutTime = ParseTime(d.CheckOutTime),
                            CheckOutEndTime = ParseTime(d.CheckOutEnd),
                            CrossDay = d.CrossDay,
                            UseBreakTime = d.UseBreakTime
                        });
                    }
                    db.Timetables.Add(newTimetable);
                }
            }

            await db.SaveChangesAsync(ct);

            // Reload to ensure IDs are available.
            var dbTimetables = await db.Timetables.Include(x => x.Days).ToListAsync(ct);
            var timetableByName = dbTimetables
                .Where(x => !string.IsNullOrWhiteSpace(x.TimetableName))
                .ToDictionary(x => x.TimetableName, x => x, StringComparer.OrdinalIgnoreCase);
            var configTimetableKeyToDbId = config.Timetables
                .Select(tt => new { Key = NormalizeKey(tt.Id), Name = (tt.Name ?? string.Empty).Trim() })
                .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Name) && timetableByName.ContainsKey(x.Name))
                .ToDictionary(x => x.Key, x => timetableByName[x.Name].Id, StringComparer.OrdinalIgnoreCase);

            // 4. Sync TimetableDayBreakTimes (supports multiple breaks per day)
            var existingDayBreaks = await db.TimetableDayBreakTimes.ToListAsync(ct);
            db.TimetableDayBreakTimes.RemoveRange(existingDayBreaks);

            foreach (var t in config.Timetables)
            {
                var ttName = (t.Name ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(ttName) || !timetableByName.TryGetValue(ttName, out var dbTimetable)) continue;

                foreach (var d in t.Days)
                {
                    var dbDay = dbTimetable.Days.FirstOrDefault(x => x.DayOfWeek == d.DayOfWeek);
                    if (dbDay is null) continue;

                    var desiredKeys = (d.BreakTimeIds?.Count > 0 ? d.BreakTimeIds : (d.BreakTimeId != null ? [d.BreakTimeId] : []))
                        .Select(NormalizeKey)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();

                    var desiredIds = desiredKeys
                        .Select(key => configBreakKeyToDbId.TryGetValue(key, out var id2) ? (int?)id2 : null)
                        .Where(x => x.HasValue)
                        .Select(x => x!.Value)
                        .Distinct()
                        .ToList();

                    if (!d.UseBreakTime || desiredIds.Count == 0)
                    {
                        dbDay.UseBreakTime = false;
                        dbDay.BreakTimeId = null;
                        continue;
                    }

                    dbDay.UseBreakTime = true;
                    dbDay.BreakTimeId = desiredIds[0];

                    for (var i = 0; i < desiredIds.Count; i++)
                    {
                        db.TimetableDayBreakTimes.Add(new TimetableDayBreakTimeEntity
                        {
                            TimetableDayId = dbDay.Id,
                            BreakTimeId = desiredIds[i],
                            SortOrder = i
                        });
                    }
                }
            }

            await db.SaveChangesAsync(ct);

            // 5. Sync Schedule Assignments -> EmployeeTimetables/DepartmentTimetables (used by reports)
            await SyncScheduleAssignmentsAsync(config, configTimetableKeyToDbId, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SyncShiftsAsync Error: {ex.Message}");
        }
    }

    public async Task SyncAccessControlAsync(string json, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            var config = JsonSerializer.Deserialize<AccessControlConfigContainer>(json, JsonOptions);
            if (config == null) return;
            // Deduplicate data commands across the whole sync so the device isn't flooded
            // (combinations re-reference groups already queued, causing dropped replies).
            var queuedOnce = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void QueueOnce(string serial, string command)
            {
                if (queuedOnce.Add($"{serial}|{command}"))
                {
                    attendanceService.QueueDeviceCommand(serial, command);
                }
            }
            // AccUnLockComb must run AFTER its referenced AccGroups are committed on the device
            // (a later poll cycle), otherwise the device ignores it. Collect and flush after a delay.
            var deferredCombos = new List<(string Serial, string Command)>();
            var timePeriodSlotsById = config.TimePeriods
                .Where(x => !string.IsNullOrWhiteSpace(x.Id?.ToString()))
                .ToDictionary(
                    x => x.Id.ToString()!,
                    x =>
                    {
                        var slot = TryParseInt(x.PeriodNo);
                        return slot > 0 ? slot : TryParseInt(x.Id);
                    },
                    StringComparer.OrdinalIgnoreCase);
            var groupSlotsById = config.Groups
                .Where(x => !string.IsNullOrWhiteSpace(x.Id?.ToString()))
                .ToDictionary(
                    x => x.Id.ToString()!,
                    x =>
                    {
                        var slot = TryParseInt(x.GroupNo);
                        return slot > 0 ? slot : TryParseInt(x.Id);
                    },
                    StringComparer.OrdinalIgnoreCase);
            var timePeriodById = config.TimePeriods
                .Where(x => !string.IsNullOrWhiteSpace(x.Id?.ToString()))
                .ToDictionary(x => x.Id.ToString()!, StringComparer.OrdinalIgnoreCase);
            var groupById = config.Groups
                .Where(x => !string.IsNullOrWhiteSpace(x.Id?.ToString()))
                .ToDictionary(x => x.Id.ToString()!, StringComparer.OrdinalIgnoreCase);

            void QueueTimePeriodToDevices(AccessControlTimePeriodJson period, IReadOnlyCollection<string> serials)
            {
                if (serials.Count == 0) return;

                var periodNo = TryParseInt(period.PeriodNo);
                var uid = periodNo > 0 ? periodNo : 1;
                var schedules = period.Days
                    .Where(x => x.IsActive)
                    .Select(x => (x.DayOfWeek, ParseTime(x.StartTime), ParseTime(x.EndTime)));
                var command = protocolService.CreateAccTimeZoneCommand(uid, schedules);
                foreach (var sn in serials)
                {
                    QueueOnce(sn, command);
                }
            }

            void QueueGroupToDevices(AccessControlGroupJson group, IReadOnlyCollection<string> serials)
            {
                if (serials.Count == 0) return;

                foreach (var periodId in group.TimePeriodIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (timePeriodById.TryGetValue(periodId, out var period))
                    {
                        QueueTimePeriodToDevices(period, serials);
                    }
                }

                var groupNo = TryParseInt(group.GroupNo) > 0 ? TryParseInt(group.GroupNo) : 1;
                var tzParts = group.TimePeriodIds.Take(3).Select(x => ResolveDeviceSlot(x, timePeriodSlotsById)).ToList();
                while (tzParts.Count < 3) tzParts.Add(0);

                var verifyMode = TranslateVerifyMode(group.VerificationMode);
                var command = $"DATA UPDATE AccGroup ID={groupNo}\tVerify={verifyMode}\tValidHoliday={(group.IncludeHoliday ? 1 : 0)}\tTZ={string.Join(";", tzParts)}";
                foreach (var sn in serials)
                {
                    QueueOnce(sn, command);
                }
            }

            void QueueCheckToDevices(IReadOnlyCollection<string> serials)
            {
                foreach (var sn in serials.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    attendanceService.QueueDeviceCommand(sn, "CHECK");
                }
            }

            List<string> ResolveTargetSerials(IEnumerable<string> configuredSerials, string? area = null)
            {
                var serials = configuredSerials
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (serials.Count > 0) return serials;

                // Auto-scope to Area if no specific serials provided
                var query = db.Devices.AsNoTracking();
                if (!string.IsNullOrWhiteSpace(area) && !string.Equals(area, "All", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(x => x.Location == area);
                }
                return query.Select(x => x.SerialNumber).Where(x => x != string.Empty).ToList();
            }

            // 1. Sync TimePeriods to Timetables.
            // The EF Timetable read can fail if the DB schema is out of sync (e.g. missing ShiftId
            // column). That persistence is secondary — it must NOT block queueing the access-control
            // commands (AccTimeZone/AccGroup/AccUnLockComb) to the devices, so guard it and continue.
            List<TimetableEntity> existingTimetables = [];
            var canPersistTimetables = true;
            try { existingTimetables = await db.Timetables.Include(x => x.Days).ToListAsync(ct); }
            catch (Exception ex) { canPersistTimetables = false; Console.WriteLine($"SyncAccessControlAsync timetable read skipped: {ex.Message}"); }
            foreach (var p in config.TimePeriods)
            {
                var periodNo = TryParseInt(p.PeriodNo);
                var id = TryParseInt(p.Id);
                if (canPersistTimetables)
                {
                    var existing = existingTimetables.FirstOrDefault(x => (id > 0 && x.Id == id) || x.TimetableName == p.Name);
                    if (existing != null)
                    {
                        existing.TimetableName = p.Name;
                        existing.UpdatedAt = DateTime.UtcNow;
                        foreach (var d in p.Days)
                        {
                            var day = existing.Days.FirstOrDefault(x => x.DayOfWeek == d.DayOfWeek);
                            if (day != null)
                            {
                                day.CheckInTime = ParseTime(d.StartTime);
                                day.CheckOutTime = ParseTime(d.EndTime);
                                day.IsWeeklyOff = !d.IsActive;
                            }
                            else
                            {
                                existing.Days.Add(new TimetableDayEntity
                                {
                                    DayOfWeek = d.DayOfWeek,
                                    CheckInTime = ParseTime(d.StartTime),
                                    CheckOutTime = ParseTime(d.EndTime),
                                    IsWeeklyOff = !d.IsActive
                                });
                            }
                        }
                    }
                    else
                    {
                        var newTimetable = new TimetableEntity
                        {
                            TimetableName = p.Name,
                            IsActive = true,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };
                        foreach (var d in p.Days)
                        {
                            newTimetable.Days.Add(new TimetableDayEntity
                            {
                                DayOfWeek = d.DayOfWeek,
                                CheckInTime = ParseTime(d.StartTime),
                                CheckOutTime = ParseTime(d.EndTime),
                                IsWeeklyOff = !d.IsActive
                            });
                        }
                        db.Timetables.Add(newTimetable);
                    }
                }

                // Real-time PUSH for selected devices (or area-scoped devices)
                var tpTargetSerials = ResolveTargetSerials(p.DeviceSerials, p.Area);
                if (tpTargetSerials.Count > 0)
                {
                    QueueTimePeriodToDevices(p, tpTargetSerials);
                    QueueCheckToDevices(tpTargetSerials);
                }
            }

            // 2. Sync Groups
            foreach (var g in config.Groups)
            {
                var grpTargetSerials = ResolveTargetSerials(g.DeviceSerials, g.Area);
                if (grpTargetSerials.Count > 0)
                {
                    QueueGroupToDevices(g, grpTargetSerials);
                    QueueCheckToDevices(grpTargetSerials);
                }
            }
            
            // 3. Sync Combinations
            var existingCombs = new List<AccCombinationEntity>();
            try { existingCombs = await db.AccCombinations.ToListAsync(ct); } catch { /* table may not exist yet */ }
            foreach (var c in config.Combinations)
            {
                var combinationNo = TryParseInt(c.CombinationNo) > 0 ? TryParseInt(c.CombinationNo) : 1;
                var id = TryParseInt(c.Id);
                var existing = existingCombs.FirstOrDefault(x => (id > 0 && x.Id == id) || x.Name == c.Name);
                if (existing != null)
                {
                    existing.Name = c.Name;
                    var groupIds = ResolveCombinationGroupSlots(c, groupSlotsById);
                    existing.Group1 = groupIds[0];
                    existing.Group2 = groupIds[1];
                    existing.Group3 = groupIds[2];
                    existing.Group4 = groupIds[3];
                    existing.Group5 = groupIds[4];
                }
                else
                {
                    var groupIds = ResolveCombinationGroupSlots(c, groupSlotsById);
                    db.AccCombinations.Add(new AccCombinationEntity
                    {
                        Name = c.Name,
                        Group1 = groupIds[0],
                        Group2 = groupIds[1],
                        Group3 = groupIds[2],
                        Group4 = groupIds[3],
                        Group5 = groupIds[4]
                    });
                }

                // Real-time PUSH for selected devices (or area-scoped devices)
                var combinationTargetSerials = ResolveTargetSerials(c.DeviceSerials, c.Area);
                if (combinationTargetSerials.Count > 0)
                {
                    foreach (var groupId in c.GroupIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (groupById.TryGetValue(groupId, out var group))
                        {
                            QueueGroupToDevices(group, combinationTargetSerials);
                        }
                    }

                    var uid = combinationNo;
                    var groupIds = ResolveCombinationGroupSlots(c, groupSlotsById);
                    var command = protocolService.CreateAccUnLockCombCommand(uid, groupIds);
                    // Defer AccUnLockComb to a later poll so referenced AccGroups are committed first.
                    foreach (var sn in combinationTargetSerials)
                    {
                        deferredCombos.Add((sn, command));
                    }
                }
            }

            // 4. Sync Privileges
            var appEmployees = attendanceService.ListEmployees().Where(x => x.IsActive).ToList();
            List<PersonnelEntity> personnel;
            try { personnel = await db.Personnel.AsNoTracking().ToListAsync(ct); }
            catch (Exception ex) { Console.WriteLine($"SyncAccessControlAsync Personnel query error: {ex.Message}"); personnel = []; }

            // Build privilege USERINFO commands to queue after a delay
            // (device must process AccGroup in one poll before accepting USERINFO with that group)
            var privilegeCommands = new List<(string serial, string command)>();
            foreach (var priv in config.Privileges)
            {
                var groupNo = ResolveDeviceSlot(priv.GroupId, groupSlotsById);
                if (groupNo <= 0) groupNo = 1;
                var effectiveTimePeriodIds = priv.TimePeriodIds;
                var targetSerials = ResolveTargetSerials(priv.DeviceSerials, priv.Area);
                if (!string.IsNullOrWhiteSpace(priv.GroupId) && groupById.TryGetValue(priv.GroupId, out var privilegeGroup))
                {
                    effectiveTimePeriodIds = privilegeGroup.TimePeriodIds;
                }

                var tzParts = effectiveTimePeriodIds.Take(3).Select(x => ResolveDeviceSlot(x, timePeriodSlotsById)).ToList();
                while (tzParts.Count < 3) tzParts.Add(0);
                var tzString = $"0000{tzParts[0]:D4}{tzParts[1]:D4}{tzParts[2]:D4}";
                var verifyMode = TranslateVerifyMode(priv.VerificationMode);

                foreach (var empIdStr in priv.EmployeeIds)
                {
                    var personnelEmp = ResolvePersonnel(personnel, empIdStr);
                    var appEmp = personnelEmp == null ? ResolveAppEmployee(appEmployees, empIdStr) : null;
                    if (personnelEmp == null && appEmp == null) continue;

                    var command = personnelEmp != null
                        ? CreatePrivilegeUserInfoCommand(personnelEmp!, groupNo, tzString, verifyMode)
                        : CreatePrivilegeUserInfoCommand(appEmp!, groupNo, tzString, verifyMode);
                    foreach (var serial in targetSerials)
                        privilegeCommands.Add((serial, command));
                }
            }

            // Queue privileges and AccUnLockComb combos IMMEDIATELY — no artificial delays.
            // ROOT CAUSE of stuck AccUnLockComb: the old Task.Delay sequencing (6s + 4s + 5s + 6s)
            // queued the combo ~11s after AccTimeZone/AccGroup. By then the device had finished its
            // short active getrequest window and gone idle, so AccUnLockComb was queued too late to be
            // pulled and sat pending forever. Queuing everything up front lets it ride the SAME active
            // window as AccTimeZone/AccGroup. Ordering is preserved by CreatedUtc (those were queued
            // earlier above); BuildPendingCommandPayload still delivers each AccUnLockComb alone
            // (isolation) and marks Acc* fire-and-forget so they don't recycle when the device drops
            // the reply after applying the config.
            foreach (var (serial, command) in privilegeCommands)
            {
                attendanceService.QueueDeviceCommand(serial, command);
            }
            foreach (var (serial, command) in deferredCombos)
            {
                attendanceService.QueueDeviceCommand(serial, command);
            }

            try { await db.SaveChangesAsync(ct); } catch (Exception ex) { Console.WriteLine($"SyncAccessControlAsync DB save error: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SyncAccessControlAsync Error: {ex.Message}");
        }
    }

    private static int TranslateVerifyMode(string mode)
    {
        if (int.TryParse(mode, out var numericMode) && numericMode >= -1 && numericMode <= 29)
        {
            return numericMode;
        }

        return mode switch
        {
            "card" => 4,
            "fingerprint" => 1,
            "face" => 15,
            "personnel" => 2,
            "group" => 0,
            _ => 0
        };
    }

    private static int TryParseInt(object? val)
    {
        if (val == null) return 0;
        if (val is int i) return i;
        if (val is string s && int.TryParse(s.Split('-').Last(), out var res)) return res;
        return 0;
    }

    private static int ResolveDeviceSlot(string? value, IReadOnlyDictionary<string, int> slotsById)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var token = value.Trim();
        if (slotsById.TryGetValue(token, out var slot) && slot > 0) return slot;
        return TryParseInt(token);
    }

    private static int[] ResolveCombinationGroupSlots(AccessControlCombinationJson combination, IReadOnlyDictionary<string, int> groupSlotsById)
    {
        var rawIds = combination.GroupIds.Count > 0
            ? combination.GroupIds
            : combination.Groups.Select(x => x.Id).ToList();
        var slots = new int[5];
        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] = i < rawIds.Count ? ResolveDeviceSlot(rawIds[i], groupSlotsById) : 0;
        }
        return slots;
    }

    private static Employee? ResolveAppEmployee(IReadOnlyCollection<Employee> employees, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var key = token.Trim();
        return employees.FirstOrDefault(x =>
            x.Id.ToString().Equals(key, StringComparison.OrdinalIgnoreCase) ||
            x.EmployeeCode.Equals(key, StringComparison.OrdinalIgnoreCase) ||
            x.FullName.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private static PersonnelEntity? ResolvePersonnel(IReadOnlyCollection<PersonnelEntity> personnel, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var key = token.Trim();
        return personnel.FirstOrDefault(x =>
            x.Id.ToString().Equals(key, StringComparison.OrdinalIgnoreCase) ||
            x.EmployeeCode.Equals(key, StringComparison.OrdinalIgnoreCase) ||
            x.FullName.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreatePrivilegeUserInfoCommand(Employee employee, int groupNo, string tzString, int verifyMode)
    {
        var fullName = string.IsNullOrWhiteSpace(employee.FullName) ? employee.EmployeeCode : employee.FullName.Trim();
        var card = string.IsNullOrWhiteSpace(employee.CardNumber) ? string.Empty : employee.CardNumber.Trim();
        var safeVerify = verifyMode < 0 ? 0 : verifyMode;
        return $"DATA UPDATE USERINFO PIN={employee.EmployeeCode}\tName={fullName}\tPri={(int)employee.Privilege}\tPasswd={employee.Password ?? string.Empty}\tCard={card}\tGrp={groupNo}\tTZ=0000000000000000\tVerify={safeVerify}";
    }

    private static string CreatePrivilegeUserInfoCommand(PersonnelEntity employee, int groupNo, string tzString, int verifyMode)
    {
        var fullName = string.IsNullOrWhiteSpace(employee.FullName) ? employee.EmployeeCode : employee.FullName;
        var safeVerify = verifyMode < 0 ? 0 : verifyMode;
        return $"DATA UPDATE USERINFO PIN={employee.EmployeeCode}\tName={fullName}\tPri=0\tPasswd=\tCard=\tGrp={groupNo}\tTZ=0000000000000000\tVerify={safeVerify}";
    }

    private static TimeOnly ParseTime(string time)
    {
        if (string.IsNullOrWhiteSpace(time)) return new TimeOnly(0, 0);
        if (TimeOnly.TryParse(time, out var res)) return res;
        return new TimeOnly(0, 0);
    }

    private static string NormalizeKey(object? val)
    {
        if (val is null) return string.Empty;
        if (val is string s) return s.Trim();
        return val.ToString()?.Trim() ?? string.Empty;
    }

    private static DateOnly? ParseDateOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateOnly.TryParse(value.Trim(), out var res)) return res;
        return null;
    }

    private static int? ResolveShiftId(
        object? rawShiftId,
        IReadOnlyDictionary<string, int> configShiftKeyToDbId,
        IReadOnlyDictionary<string, ShiftEntity> shiftByName,
        ShiftConfigContainer config)
    {
        if (rawShiftId is null) return null;

        var parsed = TryParseInt(rawShiftId);
        if (parsed > 0) return parsed;

        var key = NormalizeKey(rawShiftId);
        if (string.IsNullOrWhiteSpace(key)) return null;

        if (configShiftKeyToDbId.TryGetValue(key, out var mapped)) return mapped;

        // Sometimes the payload may already contain the shift name.
        if (shiftByName.TryGetValue(key, out var byName)) return byName.Id;

        // Or the shift key might be a client-side ID (e.g. "general") that we can resolve via ShiftDefinitions.
        var def = config.ShiftDefinitions.FirstOrDefault(x => string.Equals(NormalizeKey(x.Id), key, StringComparison.OrdinalIgnoreCase));
        if (def is not null && !string.IsNullOrWhiteSpace(def.Name) && shiftByName.TryGetValue(def.Name.Trim(), out var byDefName))
        {
            return byDefName.Id;
        }

        return null;
    }

    private async Task SyncScheduleAssignmentsAsync(
        ShiftConfigContainer config,
        IReadOnlyDictionary<string, int> configTimetableKeyToDbId,
        CancellationToken ct)
    {
        // Shift setup should be the source of truth for schedule assignments.
        var existingEmp = await db.EmployeeTimetables.ToListAsync(ct);
        var existingDept = await db.DepartmentTimetables.ToListAsync(ct);
        db.EmployeeTimetables.RemoveRange(existingEmp);
        db.DepartmentTimetables.RemoveRange(existingDept);

        var personnel = await db.Personnel.AsNoTracking().ToListAsync(ct);
        var personnelByCode = personnel
            .Where(p => !string.IsNullOrWhiteSpace(p.EmployeeCode))
            .ToDictionary(p => p.EmployeeCode.Trim(), p => p, StringComparer.OrdinalIgnoreCase);

        var groupMembers = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in config.ShiftGroups.Where(x => x.IsActive))
        {
            var key = NormalizeKey(g.Id);
            if (string.IsNullOrWhiteSpace(key)) continue;
            var members = (g.MemberEmployeeCodes ?? [])
                .Select(x => (x ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(code => personnelByCode.TryGetValue(code, out var p) ? (Guid?)p.Id : null)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .Distinct()
                .ToList();
            groupMembers[key] = members;
        }

        var planByKey = config.ShiftPlans
            .Where(x => x.IsActive)
            .ToDictionary(x => NormalizeKey(x.Id), x => x, StringComparer.OrdinalIgnoreCase);

        static int Priority(ShiftAssignmentJson a)
        {
            var t = (a.TargetType ?? string.Empty).Trim().ToLowerInvariant();
            return t == "employee" ? 3 : t == "group" ? 2 : 1;
        }

        static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
        static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

        List<(DateOnly EffectiveFrom, int TimetableId)> ExpandPlan(
            ShiftPlanJson plan,
            DateOnly from,
            DateOnly to,
            IReadOnlyDictionary<string, int> timetableMap)
        {
            var entries = new List<(DateOnly, int)>();
            int? last = null;

            for (var date = from; date <= to; date = date.AddDays(1))
            {
                var key = ResolveTimetableKeyForDate(plan, date);
                if (string.IsNullOrWhiteSpace(key) || !timetableMap.TryGetValue(key, out var ttId))
                {
                    continue;
                }

                if (last != ttId)
                {
                    entries.Add((date, ttId));
                    last = ttId;
                }
            }

            return entries;
        }

        var employeeSchedules = new Dictionary<Guid, SortedDictionary<DateOnly, int>>();
        var departmentSchedules = new Dictionary<int, SortedDictionary<DateOnly, int>>();

        foreach (var assignment in config.ShiftAssignments.Where(x => x.IsActive).OrderBy(x => Priority(x)))
        {
            var planKey = NormalizeKey(assignment.ShiftId);
            if (string.IsNullOrWhiteSpace(planKey) || !planByKey.TryGetValue(planKey, out var plan))
            {
                continue;
            }

            var planFrom = ParseDateOnly(plan.EffectiveFrom);
            if (!planFrom.HasValue) continue;
            var planTo = ParseDateOnly(plan.EffectiveTo) ?? planFrom.Value.AddDays(365);

            var assignFrom = ParseDateOnly(assignment.EffectiveFrom) ?? planFrom.Value;
            var assignTo = ParseDateOnly(assignment.EffectiveTo) ?? planTo;

            var rangeFrom = Max(planFrom.Value, assignFrom);
            var rangeTo = Min(planTo, assignTo);
            if (rangeTo < rangeFrom) continue;

            var planEntries = ExpandPlan(plan, rangeFrom, rangeTo, configTimetableKeyToDbId);
            if (planEntries.Count == 0) continue;

            var targetType = (assignment.TargetType ?? string.Empty).Trim().ToLowerInvariant();
            if (targetType == "employee")
            {
                var code = (assignment.EmployeeCode ?? string.Empty).Trim();
                if (!personnelByCode.TryGetValue(code, out var person)) continue;

                var schedule = employeeSchedules.TryGetValue(person.Id, out var s) ? s : (employeeSchedules[person.Id] = new SortedDictionary<DateOnly, int>());
                foreach (var (effectiveFrom, timetableId) in planEntries)
                {
                    schedule[effectiveFrom] = timetableId;
                }
            }
            else if (targetType == "group")
            {
                var groupKey = NormalizeKey(assignment.GroupId);
                if (string.IsNullOrWhiteSpace(groupKey) || !groupMembers.TryGetValue(groupKey, out var members) || members.Count == 0) continue;

                foreach (var personId in members)
                {
                    var schedule = employeeSchedules.TryGetValue(personId, out var s) ? s : (employeeSchedules[personId] = new SortedDictionary<DateOnly, int>());
                    foreach (var (effectiveFrom, timetableId) in planEntries)
                    {
                        schedule[effectiveFrom] = timetableId;
                    }
                }
            }
            else
            {
                var deptRaw = (assignment.Department ?? string.Empty).Trim();
                if (string.Equals(deptRaw, "all", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(deptRaw))
                {
                    // Apply to everyone as employee-level assignments.
                    foreach (var person in personnel)
                    {
                        var schedule = employeeSchedules.TryGetValue(person.Id, out var s) ? s : (employeeSchedules[person.Id] = new SortedDictionary<DateOnly, int>());
                        foreach (var (effectiveFrom, timetableId) in planEntries)
                        {
                            schedule[effectiveFrom] = timetableId;
                        }
                    }

                    continue;
                }

                var deptId = TryParseInt(deptRaw);
                if (deptId <= 0)
                {
                    Console.WriteLine($"SyncScheduleAssignmentsAsync: skipping department assignment '{deptRaw}' (cannot resolve department id).");
                    continue;
                }

                var scheduleDept = departmentSchedules.TryGetValue(deptId, out var sd) ? sd : (departmentSchedules[deptId] = new SortedDictionary<DateOnly, int>());
                foreach (var (effectiveFrom, timetableId) in planEntries)
                {
                    scheduleDept[effectiveFrom] = timetableId;
                }
            }
        }

        var employeeRows = new List<EmployeeTimetableEntity>();
        foreach (var (personId, schedule) in employeeSchedules)
        {
            int? last = null;
            foreach (var (effectiveFrom, timetableId) in schedule)
            {
                if (last == timetableId) continue;
                employeeRows.Add(new EmployeeTimetableEntity
                {
                    PersonnelId = personId,
                    TimetableId = timetableId,
                    EffectiveFrom = effectiveFrom,
                    IsActive = true
                });
                last = timetableId;
            }
        }

        var deptRows = new List<DepartmentTimetableEntity>();
        foreach (var (deptId, schedule) in departmentSchedules)
        {
            int? last = null;
            foreach (var (effectiveFrom, timetableId) in schedule)
            {
                if (last == timetableId) continue;
                deptRows.Add(new DepartmentTimetableEntity
                {
                    DepartmentId = deptId,
                    TimetableId = timetableId,
                    EffectiveFrom = effectiveFrom,
                    IsActive = true
                });
                last = timetableId;
            }
        }

        db.EmployeeTimetables.AddRange(employeeRows);
        db.DepartmentTimetables.AddRange(deptRows);
        await db.SaveChangesAsync(ct);
    }

    private static string? ResolveTimetableKeyForDate(ShiftPlanJson plan, DateOnly date)
    {
        var cycle = (plan.CycleType ?? string.Empty).Trim().ToLowerInvariant();
        if (cycle == "daily")
        {
            var key = NormalizeKey(plan.DailyTimetableId);
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }

        if (cycle == "weekly")
        {
            var dow = (int)date.DayOfWeek;
            var rule = plan.WeeklyRules?.FirstOrDefault(x => x.DayOfWeek == dow);
            var key = NormalizeKey(rule?.TimetableId);
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }

        if (cycle == "monthly")
        {
            var dom = date.Day;
            var rule = plan.MonthlyRules?.FirstOrDefault(x => x.DayOfMonth == dom);
            var key = NormalizeKey(rule?.TimetableId);
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }

        // Fallback to daily if unknown.
        var fallback = NormalizeKey(plan.DailyTimetableId);
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    private sealed class ShiftConfigContainer
    {
        public List<ShiftDefinitionJson> ShiftDefinitions { get; set; } = [];
        public List<ShiftAssignmentJson> ShiftAssignments { get; set; } = [];
        public List<ShiftGroupJson> ShiftGroups { get; set; } = [];
        public List<ShiftPlanJson> ShiftPlans { get; set; } = [];
        public List<BreakDefinitionJson> BreakDefinitions { get; set; } = [];
        public List<TimetableDefinitionJson> Timetables { get; set; } = [];
    }

    private sealed class ShiftDefinitionJson
    {
        public object Id { get; set; } = 0;
        public string Name { get; set; } = "";
        public string StartTime { get; set; } = "09:00";
        public string EndTime { get; set; } = "18:00";
        public int GraceMinutes { get; set; }
        public int HalfDayThreshold { get; set; }
        public int OvertimeAfter { get; set; }
        public string Type { get; set; } = "general";
    }

    private sealed class ShiftAssignmentJson
    {
        public object Id { get; set; } = "";
        public string TargetType { get; set; } = "employee"; // employee | department | group
        public string EmployeeCode { get; set; } = "";
        public string EmployeeName { get; set; } = "";
        public string Department { get; set; } = "";
        public string GroupId { get; set; } = "";
        public string GroupName { get; set; } = "";
        public object ShiftId { get; set; } = "";
        public string EffectiveFrom { get; set; } = ""; // yyyy-MM-dd
        public string? EffectiveTo { get; set; } // yyyy-MM-dd
        public bool IsActive { get; set; } = true;
    }

    private sealed class ShiftGroupJson
    {
        public object Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> MemberEmployeeCodes { get; set; } = [];
        public bool IsActive { get; set; } = true;
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }

    private sealed class ShiftPlanWeeklyRuleJson
    {
        public int DayOfWeek { get; set; }
        public object TimetableId { get; set; } = "";
    }

    private sealed class ShiftPlanMonthlyRuleJson
    {
        public int DayOfMonth { get; set; }
        public object TimetableId { get; set; } = "";
    }

    private sealed class ShiftPlanJson
    {
        public object Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string CycleType { get; set; } = "weekly"; // daily | weekly | monthly
        public string EffectiveFrom { get; set; } = "";
        public string? EffectiveTo { get; set; }
        public bool IsCyclic { get; set; }
        public bool IsActive { get; set; } = true;
        public object? DailyTimetableId { get; set; }
        public List<ShiftPlanWeeklyRuleJson> WeeklyRules { get; set; } = [];
        public List<ShiftPlanMonthlyRuleJson> MonthlyRules { get; set; } = [];
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }

    private sealed class BreakDefinitionJson
    {
        public object Id { get; set; } = 0;
        public string Name { get; set; } = "";
        public string StartTime { get; set; } = "13:00";
        public string EndTime { get; set; } = "14:00";
        public int DurationMinutes { get; set; } = 60;
        public string CalculateType { get; set; } = "auto_deduct";
        public int MinimumBreakMinutes { get; set; }
        public bool IsActive { get; set; } = true;
    }

    private sealed class TimetableDefinitionJson
    {
        public object Id { get; set; } = 0;
        public string Name { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public List<TimetableDayJson> Days { get; set; } = [];
    }

    private sealed class TimetableDayJson
    {
        public int DayOfWeek { get; set; }
        public object? ShiftId { get; set; }
        public bool IsWeeklyOff { get; set; }
        public string CheckInTime { get; set; } = "10:00";
        public string CheckInStart { get; set; } = "00:00";
        public string CheckInEnd { get; set; } = "10:15";
        public string CheckOutStart { get; set; } = "16:45";
        public string CheckOutTime { get; set; } = "17:00";
        public string CheckOutEnd { get; set; } = "23:59";
        public bool CrossDay { get; set; }
        public bool UseBreakTime { get; set; }
        public object? BreakTimeId { get; set; }
        public List<object> BreakTimeIds { get; set; } = [];
    }

    private sealed class AccessControlTimePeriodJson
    {
        public object Id { get; set; } = "";
        public string Area { get; set; } = "";
        public string Department { get; set; } = "";
        public string PeriodNo { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> DeviceSerials { get; set; } = [];
        public List<AccessControlDayRuleJson> Days { get; set; } = [];
    }

    private sealed class AccessControlDayRuleJson
    {
        public int DayOfWeek { get; set; }
        public string StartTime { get; set; } = "00:00:00";
        public string EndTime { get; set; } = "23:59:00";
        public bool IsActive { get; set; }
    }

    private sealed class AccessControlGroupJson
    {
        public object Id { get; set; } = "";
        public string Area { get; set; } = "";
        public string Department { get; set; } = "";
        public string GroupNo { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> DeviceSerials { get; set; } = [];
        public List<string> TimePeriodIds { get; set; } = [];
        public string VerificationMode { get; set; } = "any";
        public bool IncludeHoliday { get; set; }
    }

    private sealed class AccessControlCombinationJson
    {
        public object Id { get; set; } = "";
        public string Area { get; set; } = "";
        public string Department { get; set; } = "";
        public string CombinationNo { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> DeviceSerials { get; set; } = [];
        public List<string> GroupIds { get; set; } = [];
        public List<AccessControlGroupRefJson> Groups { get; set; } = [];
    }

    private sealed class AccessControlGroupRefJson
    {
        public string Id { get; set; } = "";
    }

    private sealed class AccessControlPrivilegeJson
    {
        public object Id { get; set; } = "";
        public string Area { get; set; } = "";
        public string Department { get; set; } = "";
        public List<string> DeviceSerials { get; set; } = [];
        public List<string> EmployeeIds { get; set; } = [];
        public string GroupId { get; set; } = "";
        public string VerificationMode { get; set; } = "any";
        public List<string> TimePeriodIds { get; set; } = [];
    }

    private sealed class AccessControlConfigContainer
    {
        public List<AccessControlTimePeriodJson> TimePeriods { get; set; } = [];
        public List<AccessControlGroupJson> Groups { get; set; } = [];
        public List<AccessControlCombinationJson> Combinations { get; set; } = [];
        public List<AccessControlPrivilegeJson> Privileges { get; set; } = [];
    }
}
