using Microsoft.EntityFrameworkCore;
using E.Attendance.Data.Production;
using E.Attendance.Core.Models;

namespace E.Attendance.Services.Production;

public sealed class AttendanceProcessingEngine(PbAttendanceDbContext db)
{
    public async Task<AttendanceProcessResult> ProcessAsync(CancellationToken cancellationToken = default)
    {
        var unprocessed = await db.RawPushLogs
            .Where(x => !x.Processed)
            .OrderBy(x => x.ReceivedAt)
            .Take(5000)
            .ToListAsync(cancellationToken);

        if (unprocessed.Count == 0)
        {
            return new AttendanceProcessResult(0, 0);
        }

        var codes = unprocessed
            .Select(x => x.EmployeeCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var personnel = await db.Personnel
            .Where(x => codes.Contains(x.EmployeeCode))
            .ToDictionaryAsync(x => x.EmployeeCode, StringComparer.OrdinalIgnoreCase, cancellationToken);
        if (personnel.Count == 0)
        {
            foreach (var item in unprocessed)
            {
                item.Processed = true;
            }
            await db.SaveChangesAsync(cancellationToken);
            return new AttendanceProcessResult(unprocessed.Count, 0);
        }

        var personnelIds = personnel.Values.Select(x => x.Id).ToHashSet();
        var deptIds = personnel.Values
            .Where(x => x.DepartmentId.HasValue)
            .Select(x => x.DepartmentId!.Value)
            .Distinct()
            .ToHashSet();

        var shiftById = await db.Shifts.AsNoTracking().ToDictionaryAsync(x => x.Id, cancellationToken);
        var breakTimesById = await db.BreakTimes.AsNoTracking()
            .Where(x => x.IsActive)
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var timetableDayBreaks = await db.TimetableDayBreakTimes.AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);
        var breaksByTimetableDayId = timetableDayBreaks
            .GroupBy(x => x.TimetableDayId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .Select(x => breakTimesById.TryGetValue(x.BreakTimeId, out var bt) ? bt : null)
                    .Where(x => x is not null)
                    .Cast<BreakTimeEntity>()
                    .ToList());
        var timetables = await db.Timetables.AsNoTracking().Where(x => x.IsActive).ToListAsync(cancellationToken);
        var defaultTimetableId = timetables
            .OrderBy(x => string.Equals(x.TimetableName, "Default Weekly", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.Id)
            .Select(x => (int?)x.Id)
            .FirstOrDefault();
        var timetableIds = timetables.Select(x => x.Id).ToHashSet();
        var timetableDays = await db.TimetableDays.AsNoTracking()
            .Where(x => timetableIds.Contains(x.TimetableId))
            .ToListAsync(cancellationToken);
        var daysByTimetable = timetableDays
            .GroupBy(x => x.TimetableId)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(x => x.DayOfWeek, x => x));

        var employeeTimetables = await db.EmployeeTimetables.AsNoTracking()
            .Where(x => x.IsActive && personnelIds.Contains(x.EmployeeId))
            .ToListAsync(cancellationToken);
        var departmentTimetables = await db.DepartmentTimetables.AsNoTracking()
            .Where(x => x.IsActive && deptIds.Contains(x.DepartmentId))
            .ToListAsync(cancellationToken);

        var minDate = DateOnly.FromDateTime(unprocessed.Min(x => x.PunchTime)).AddDays(-2);
        var maxDate = DateOnly.FromDateTime(unprocessed.Max(x => x.PunchTime)).AddDays(2);
        var holidays = await db.Holidays.AsNoTracking()
            .Where(x => x.IsActive && ((x.HolidayDate >= minDate && x.HolidayDate <= maxDate) || x.IsRecurring))
            .ToListAsync(cancellationToken);
        var approvedLeaves = await db.LeaveRequests.AsNoTracking()
            .Where(x =>
                x.Status == "approved" &&
                personnelIds.Contains(x.EmployeeId) &&
                x.FromDate <= maxDate &&
                x.ToDate >= minDate)
            .ToListAsync(cancellationToken);
        var policy = await db.AttendancePolicies.AsNoTracking()
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken) ?? new AttendancePolicyEntity();

        var groupedRows = unprocessed
            .Select(log =>
            {
                if (string.IsNullOrWhiteSpace(log.EmployeeCode) ||
                    !personnel.TryGetValue(log.EmployeeCode, out var person))
                {
                    return null;
                }

                DailySchedule Resolve(DateOnly date) => ResolveSchedule(
                    person,
                    date,
                    defaultTimetableId,
                    shiftById,
                    employeeTimetables,
                    departmentTimetables,
                    daysByTimetable,
                    holidays,
                    approvedLeaves);

                var attendanceDate = ResolveAttendanceDate(log.PunchTime, Resolve);
                return new GroupedRawRow(log, person, attendanceDate);
            })
            .Where(x => x is not null)
            .Cast<GroupedRawRow>()
            .GroupBy(x => new { x.Person.EmployeeCode, x.AttendanceDate })
            .ToList();

        var recordsUpserted = 0;
        foreach (var group in groupedRows)
        {
            var ordered = group
                .Select(x => x.Log)
                .OrderBy(x => x.PunchTime)
                .ToList();

            var person = group.First().Person;
            var attendanceDate = group.Key.AttendanceDate;

            var schedule = ResolveSchedule(
                person,
                attendanceDate,
                defaultTimetableId,
                shiftById,
                employeeTimetables,
                departmentTimetables,
                daysByTimetable,
                holidays,
                approvedLeaves);
            var shift = schedule.Shift;
            var dayRule = schedule.TimetableDay;

            var (firstIn, lastOut) = ResolvePunches(ordered, attendanceDate, shift, dayRule);

            var lateMinutes = 0;
            var earlyExitMinutes = 0;
            var overtimeMinutes = 0;
            var approvedLeave = schedule.ApprovedLeave;
            var hasHalfDayLeave = approvedLeave?.IsHalfDay == true;
            var strictShiftRules = !schedule.IsHoliday && !schedule.IsWeeklyOff && (approvedLeave is null || hasHalfDayLeave);
            var workedMinutes = firstIn.HasValue && lastOut.HasValue && lastOut.Value >= firstIn.Value
                ? Math.Max(0, (int)Math.Round((lastOut.Value - firstIn.Value).TotalMinutes))
                : 0;

            if (firstIn.HasValue && (shift is not null || dayRule is not null))
            {
                var startTime = shift?.StartTime ?? dayRule!.CheckInTime;
                var endTime = shift?.EndTime ?? dayRule!.CheckOutTime;
                var graceMinutes = shift?.GraceMinutes ?? 0;
                var overtimeAfterMinutes = shift?.OvertimeAfterMinutes ?? 0;
                var crossDay = (shift?.NightShift ?? false) || (dayRule?.CrossDay ?? false);

                var shiftStart = attendanceDate.ToDateTime(startTime);
                var shiftEnd = attendanceDate.ToDateTime(endTime);
                if (crossDay || shiftEnd <= shiftStart)
                {
                    shiftEnd = shiftEnd.AddDays(1);
                }

                var expectedStart = shiftStart;
                var expectedEnd = shiftEnd;

                if (workedMinutes > 0 && dayRule?.UseBreakTime == true)
                {
                    var breakItems = ResolveBreakTimes(dayRule, breakTimesById, breaksByTimetableDayId);
                    if (breakItems.Count > 0)
                    {
                        var breakDeduction = 0;
                        foreach (var breakTime in breakItems)
                        {
                            breakDeduction += CalculateBreakDeduction(ordered, attendanceDate, breakTime, shiftStart, shiftEnd);
                        }
                        workedMinutes = Math.Max(0, workedMinutes - breakDeduction);
                    }
                }

                var standard = (int)Math.Round((shiftEnd - shiftStart).TotalMinutes);
                if (hasHalfDayLeave)
                {
                    var midpoint = shiftStart.AddMinutes(standard / 2d);
                    var halfDayType = NormalizeHalfDayType(approvedLeave!.HalfDayType);
                    if (halfDayType == "first_half")
                    {
                        // First-half leave means employee works the second half.
                        expectedStart = midpoint;
                        expectedEnd = shiftEnd;
                    }
                    else
                    {
                        // Second-half leave means employee works the first half.
                        expectedStart = shiftStart;
                        expectedEnd = midpoint;
                    }

                    standard = (int)Math.Round((expectedEnd - expectedStart).TotalMinutes);
                }

                if (strictShiftRules)
                {
                    lateMinutes = Math.Max(0, (int)Math.Round((firstIn.Value - expectedStart).TotalMinutes) - graceMinutes);
                    if (lastOut.HasValue)
                    {
                        earlyExitMinutes = Math.Max(0, (int)Math.Round((expectedEnd - lastOut.Value).TotalMinutes));
                    }
                }

                overtimeMinutes = Math.Max(0, workedMinutes - standard - Math.Max(0, overtimeAfterMinutes));
            }

            if (schedule.IsHoliday && workedMinutes > 0 && policy.HolidayWorkAsOvertime)
            {
                overtimeMinutes = Math.Max(overtimeMinutes, workedMinutes);
            }

            if (schedule.IsWeeklyOff && workedMinutes > 0 && policy.WeeklyOffWorkAsOvertime)
            {
                overtimeMinutes = Math.Max(overtimeMinutes, workedMinutes);
            }

            var totalWorkHours = workedMinutes > 0
                ? (decimal)Math.Round(workedMinutes / 60m, 2)
                : 0m;

            string status;
            if (schedule.IsHoliday)
            {
                status = totalWorkHours <= 0 ? "Holiday" : "Holiday Work";
            }
            else if (approvedLeave is not null)
            {
                if (approvedLeave.IsHalfDay)
                {
                    status = totalWorkHours <= 0
                        ? "Half Leave"
                        : strictShiftRules && lateMinutes > 0 ? "Half Leave Late" : "Half Leave";
                }
                else
                {
                    status = totalWorkHours <= 0 ? "Leave" : "Leave Work";
                }
            }
            else if (schedule.IsWeeklyOff)
            {
                status = totalWorkHours <= 0 ? "Off" : "Off Work";
            }
            else
            {
                status = totalWorkHours <= 0 ? "Absent" : strictShiftRules && lateMinutes > 0 ? "Late" : "Present";
            }

            var existing = await db.AttendanceRecords.FirstOrDefaultAsync(
                x => x.EmployeeId == person.Id && x.AttendanceDate == attendanceDate,
                cancellationToken);

            if (existing is null)
            {
                db.AttendanceRecords.Add(new AttendanceRecordEntity
                {
                    EmployeeId = person.Id,
                    AttendanceDate = attendanceDate,
                    FirstIn = firstIn,
                    LastOut = lastOut,
                    TotalWorkHours = totalWorkHours,
                    LateMinutes = lateMinutes,
                    EarlyExitMinutes = earlyExitMinutes,
                    OvertimeMinutes = overtimeMinutes,
                    Status = status,
                    ProcessedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.FirstIn = firstIn;
                existing.LastOut = lastOut;
                existing.TotalWorkHours = totalWorkHours;
                existing.LateMinutes = lateMinutes;
                existing.EarlyExitMinutes = earlyExitMinutes;
                existing.OvertimeMinutes = overtimeMinutes;
                existing.Status = status;
                existing.ProcessedAt = DateTime.UtcNow;
            }

            recordsUpserted++;
        }

        foreach (var item in unprocessed)
        {
            item.Processed = true;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new AttendanceProcessResult(unprocessed.Count, recordsUpserted);
    }

    private static DateOnly ResolveAttendanceDate(DateTime punchTime, Func<DateOnly, DailySchedule> resolveSchedule)
    {
        var currentDate = DateOnly.FromDateTime(punchTime);
        var clock = TimeOnly.FromDateTime(punchTime);

        var today = resolveSchedule(currentDate);
        if (BelongsToPreviousDate(clock, today))
        {
            return currentDate.AddDays(-1);
        }

        var previousDate = currentDate.AddDays(-1);
        var previous = resolveSchedule(previousDate);
        if (BelongsToPreviousDate(clock, previous))
        {
            return previousDate;
        }

        return currentDate;
    }

    private static bool BelongsToPreviousDate(TimeOnly clock, DailySchedule schedule)
    {
        if (schedule.Shift is not null && schedule.Shift.NightShift)
        {
            return clock <= schedule.Shift.EndTime;
        }

        if (schedule.TimetableDay is null || !schedule.TimetableDay.CrossDay)
        {
            return false;
        }

        // When timetable uses cross-day, punches after midnight up to the "Check-out end" time belong to the previous attendance date.
        return clock <= schedule.TimetableDay.CheckOutEndTime;
    }

    private static DailySchedule ResolveSchedule(
        PersonnelEntity person,
        DateOnly date,
        int? defaultTimetableId,
        IReadOnlyDictionary<int, ShiftEntity> shiftById,
        IReadOnlyList<EmployeeTimetableEntity> employeeTimetables,
        IReadOnlyList<DepartmentTimetableEntity> departmentTimetables,
        IReadOnlyDictionary<int, Dictionary<int, TimetableDayEntity>> daysByTimetable,
        IReadOnlyList<HolidayEntity> holidays,
        IReadOnlyList<LeaveRequestEntity> approvedLeaves)
    {
        var employeeAssignment = employeeTimetables
            .Where(x => x.EmployeeId == person.Id && x.EffectiveFrom <= date)
            .OrderByDescending(x => x.EffectiveFrom)
            .FirstOrDefault();

        var departmentAssignment = person.DepartmentId.HasValue
            ? departmentTimetables
                .Where(x => x.DepartmentId == person.DepartmentId.Value && x.EffectiveFrom <= date)
                .OrderByDescending(x => x.EffectiveFrom)
                .FirstOrDefault()
            : null;

        var timetableId = employeeAssignment?.TimetableId ?? departmentAssignment?.TimetableId ?? defaultTimetableId;
        TimetableDayEntity? dayRule = null;
        if (timetableId.HasValue &&
            daysByTimetable.TryGetValue(timetableId.Value, out var days) &&
            days.TryGetValue((int)date.DayOfWeek, out var day))
        {
            dayRule = day;
        }

        ShiftEntity? shift = null;
        if (dayRule is not null && !dayRule.IsWeeklyOff && dayRule.ShiftId.HasValue)
        {
            shiftById.TryGetValue(dayRule.ShiftId.Value, out shift);
        }

        if (shift is null && person.ShiftId.HasValue)
        {
            shiftById.TryGetValue(person.ShiftId.Value, out shift);
        }

        var holiday = holidays.FirstOrDefault(x =>
            ((x.HolidayDate == date) ||
             (x.IsRecurring && x.HolidayDate.Month == date.Month && x.HolidayDate.Day == date.Day)) &&
            (x.DepartmentId is null || x.DepartmentId == person.DepartmentId));

        var approvedLeave = holiday is null
            ? approvedLeaves.FirstOrDefault(x =>
                x.EmployeeId == person.Id &&
                x.FromDate <= date &&
                x.ToDate >= date &&
                string.Equals(x.Status, "approved", StringComparison.OrdinalIgnoreCase))
            : null;

        return new DailySchedule(
            shift,
            IsHoliday: holiday is not null,
            IsWeeklyOff: holiday is null && approvedLeave is null && dayRule?.IsWeeklyOff == true,
            ApprovedLeave: approvedLeave,
            TimetableDay: dayRule);
    }

    private static IReadOnlyList<BreakTimeEntity> ResolveBreakTimes(
        TimetableDayEntity dayRule,
        IReadOnlyDictionary<int, BreakTimeEntity> breakTimesById,
        IReadOnlyDictionary<int, List<BreakTimeEntity>> breaksByTimetableDayId)
    {
        if (breaksByTimetableDayId.TryGetValue(dayRule.Id, out var list) && list.Count > 0)
        {
            return list;
        }

        if (dayRule.BreakTimeId.HasValue && breakTimesById.TryGetValue(dayRule.BreakTimeId.Value, out var single))
        {
            return [single];
        }

        return [];
    }

    private static int CalculateBreakDeduction(
        IReadOnlyList<RawPushLogEntity> orderedLogs,
        DateOnly attendanceDate,
        BreakTimeEntity breakTime,
        DateTime shiftStart,
        DateTime shiftEnd)
    {
        var crossesDay = shiftEnd.Date > shiftStart.Date;
        var shiftStartClock = TimeOnly.FromDateTime(shiftStart);

        var breakStart = attendanceDate.ToDateTime(breakTime.StartTime);
        var breakEnd = attendanceDate.ToDateTime(breakTime.EndTime);

        if (crossesDay)
        {
            if (breakTime.StartTime < shiftStartClock) breakStart = breakStart.AddDays(1);
            if (breakTime.EndTime < shiftStartClock) breakEnd = breakEnd.AddDays(1);
        }

        if (breakEnd <= breakStart)
        {
            breakEnd = breakEnd.AddDays(1);
        }

        var clampedStart = MaxDateTime(shiftStart, breakStart);
        var clampedEnd = MinDateTime(shiftEnd, breakEnd);
        if (clampedEnd <= clampedStart)
        {
            return 0;
        }

        var windowMinutes = (int)Math.Round((clampedEnd - clampedStart).TotalMinutes);
        if (windowMinutes <= 0)
        {
            return 0;
        }

        var calc = (breakTime.CalculateType ?? "auto_deduct").Trim().ToLowerInvariant();

        if (calc == "required_punch")
        {
            // Only deduct when a break is actually punched.
            var breakPunches = orderedLogs
                .Where(x => x.PunchTime >= clampedStart && x.PunchTime <= clampedEnd && IsBreakState(x.PunchState))
                .OrderBy(x => x.PunchTime)
                .ToList();

            if (breakPunches.Count < 2)
            {
                return 0;
            }

            var actualMinutes = (int)Math.Round((breakPunches[^1].PunchTime - breakPunches[0].PunchTime).TotalMinutes);
            if (actualMinutes <= 0)
            {
                return 0;
            }

            if (breakTime.MinimumBreakMinutes > 0 && actualMinutes < breakTime.MinimumBreakMinutes)
            {
                return 0;
            }

            var cappedByDefinition = breakTime.DurationMinutes > 0 ? Math.Min(actualMinutes, breakTime.DurationMinutes) : actualMinutes;
            return Math.Min(windowMinutes, cappedByDefinition);
        }

        // auto_deduct: deduct fixed duration (bounded by time window)
        if (breakTime.DurationMinutes <= 0)
        {
            return 0;
        }

        return Math.Min(breakTime.DurationMinutes, windowMinutes);
    }

    private static DateTime MaxDateTime(DateTime a, DateTime b)
        => a >= b ? a : b;

    private static DateTime MinDateTime(DateTime a, DateTime b)
        => a <= b ? a : b;

    private static string NormalizeHalfDayType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "second_half";
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "firsthalf" => "first_half",
            "first-half" => "first_half",
            "first_half" => "first_half",
            "secondhalf" => "second_half",
            "second-half" => "second_half",
            "second_half" => "second_half",
            _ => "second_half"
        };
    }

    private static bool IsInState(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           !value.Contains("break", StringComparison.OrdinalIgnoreCase) &&
           (string.Equals(value.Trim(), "in", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value.Trim(), "checkin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value.Trim(), "0", StringComparison.OrdinalIgnoreCase));

    private static bool IsOutState(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           !value.Contains("break", StringComparison.OrdinalIgnoreCase) &&
           (string.Equals(value.Trim(), "out", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value.Trim(), "checkout", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value.Trim(), "1", StringComparison.OrdinalIgnoreCase));

    private static bool IsBreakState(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Contains("break", StringComparison.OrdinalIgnoreCase);

    private static (DateTime? FirstIn, DateTime? LastOut) ResolvePunches(
        IReadOnlyList<RawPushLogEntity> ordered,
        DateOnly attendanceDate,
        ShiftEntity? shift,
        TimetableDayEntity? dayRule)
    {
        if (ordered.Count == 0)
        {
            return (null, null);
        }

        DateTime? firstIn = null;
        DateTime? lastOut = null;

        if (dayRule is not null && !dayRule.IsWeeklyOff)
        {
            // Check-in window is always anchored to attendance date.
            var inStart = attendanceDate.ToDateTime(dayRule.CheckInStartTime);
            var inEnd = attendanceDate.ToDateTime(dayRule.CheckInEndTime);
            if (inEnd < inStart)
            {
                inEnd = inEnd.AddDays(1);
            }

            var inWindow = ordered.Where(x => x.PunchTime >= inStart && x.PunchTime <= inEnd).ToList();
            if (inWindow.Count > 0)
            {
                firstIn = inWindow.FirstOrDefault(x => IsInState(x.PunchState))?.PunchTime ?? inWindow[0].PunchTime;
            }

            // Check-out window can roll into the next day for cross-day shifts.
            var crossDay = (shift?.NightShift ?? false) || dayRule.CrossDay;
            var checkoutAnchor = shift?.StartTime ?? dayRule.CheckInTime;

            var outStart = attendanceDate.ToDateTime(dayRule.CheckOutStartTime);
            var outEnd = attendanceDate.ToDateTime(dayRule.CheckOutEndTime);

            if (crossDay)
            {
                if (dayRule.CheckOutStartTime < checkoutAnchor) outStart = outStart.AddDays(1);
                if (dayRule.CheckOutEndTime < checkoutAnchor) outEnd = outEnd.AddDays(1);
            }

            if (outEnd < outStart)
            {
                outEnd = outEnd.AddDays(1);
            }

            var outWindow = ordered.Where(x => x.PunchTime >= outStart && x.PunchTime <= outEnd).ToList();
            if (outWindow.Count > 0)
            {
                lastOut = outWindow.LastOrDefault(x => IsOutState(x.PunchState))?.PunchTime ?? outWindow[^1].PunchTime;
            }
        }

        firstIn ??= ordered.FirstOrDefault(x => IsInState(x.PunchState))?.PunchTime;
        firstIn ??= ordered[0].PunchTime;

        lastOut ??= ordered.LastOrDefault(x => IsOutState(x.PunchState))?.PunchTime;
        lastOut ??= ordered[^1].PunchTime;

        if (firstIn.HasValue && lastOut.HasValue && lastOut.Value < firstIn.Value)
        {
            // Fallback: keep stable ordering if windows create an invalid pair.
            firstIn = ordered[0].PunchTime;
            lastOut = ordered[^1].PunchTime;
        }

        return (firstIn, lastOut);
    }

    private sealed record GroupedRawRow(RawPushLogEntity Log, PersonnelEntity Person, DateOnly AttendanceDate);
    private sealed record DailySchedule(
        ShiftEntity? Shift,
        bool IsHoliday,
        bool IsWeeklyOff,
        LeaveRequestEntity? ApprovedLeave,
        TimetableDayEntity? TimetableDay);
}

public sealed record AttendanceProcessResult(int ProcessedLogCount, int UpsertedRecordCount);
