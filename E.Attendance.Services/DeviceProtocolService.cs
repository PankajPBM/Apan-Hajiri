using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using E.Attendance.Core.Models;

namespace E.Attendance.Services;

public sealed class DeviceProtocolService : IDeviceProtocolService
{
    private static readonly Random CmdRandom = new();

    public string GenerateCmdId()
    {
        lock (CmdRandom)
        {
            return CmdRandom.Next(1000, 9999).ToString();
        }
    }

    public static int TimezoneToMinutes(string timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone))
        {
            return 345;
        }

        var token = timezone.Trim();
        var sign = token.StartsWith('-') ? -1 : 1;
        token = token.TrimStart('+', '-');
        var parts = token.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var hours) &&
            int.TryParse(parts[1], out var minutes))
        {
            return sign * ((hours * 60) + minutes);
        }

        if (int.TryParse(token, out var directMinutes))
        {
            return directMinutes;
        }

        return 345;
    }

    public string CreateUserInfoCommand(Employee employee, Device device)
    {
        var name = string.IsNullOrWhiteSpace(employee.FullName) ? employee.EmployeeCode : employee.FullName.Trim();
        var verifyMode = IsSupportedVerifyMode(employee.VerifyMode) ? employee.VerifyMode : GetVerifyMode(device);
        var gender = string.Equals(employee.Gender, "Female", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        var password = NormalizeDevicePassword(employee.Password);
        
        // Map DepartmentId to device Grp (Group). Fallback to group 1 if no department.
        var groupId = employee.DepartmentId > 0 ? employee.DepartmentId : 1;

        return $"DATA UPDATE USERINFO PIN={employee.EmployeeCode}\tName={name}\t" +
               $"Pri={(int)employee.Privilege}\tPasswd={password}\t" +
               $"Card={employee.CardNumber}\t" +
               $"Grp={groupId}\tTZ=0000000000000000\tVerify={verifyMode}\t" +
               $"Phone={employee.MobileNumber}\tGender={gender}";
    }

    public string CreateAccTimeZoneCommand(int timetableId, IEnumerable<(int DayOfWeek, TimeOnly Start, TimeOnly End)> dailySchedules)
    {
        // UID is the Time Period ID on the device (mapped from TimetableId)
        var sb = new StringBuilder();
        sb.Append($"DATA UPDATE AccTimeZone UID={timetableId}");

        var days = new string[] { "Sun", "Mon", "Tues", "Wed", "Thurs", "Fri", "Sat" };
        var scheduleMap = dailySchedules.ToDictionary(x => x.DayOfWeek, x => x);

        for (int i = 0; i < 7; i++)
        {
            var dayPrefix = days[i];
            if (scheduleMap.TryGetValue(i, out var schedule))
            {
                // Format: HHmm (e.g., 09:00 -> 0900)
                var startStr = schedule.Start.ToString("HHmm");
                var endStr = schedule.End.ToString("HHmm");
                sb.Append($"\t{dayPrefix}Start={startStr}\t{dayPrefix}End={endStr}");
            }
            else
            {
                // Weekly off or undefined: 00:00 to 00:00
                sb.Append($"\t{dayPrefix}Start=0000\t{dayPrefix}End=0000");
            }
        }

        return sb.ToString();
    }

    public string CreateAccGroupCommand(object id, int timetableId)
    {
        // Map Department or Personnel to an Access Group (ID)
        // Link the Access Group to the Time Period (TZ)
        // TZ format for AccGroup is simple: ID;0;0 where ID is the Time Period UID.
        // For devices, we need a numeric ID or a string identifier.
        // If it's a Guid, we'll use a hash or a specific mapping, but for now we'll use ToString().
        return $"DATA UPDATE AccGroup ID={id}\tVerify=0\tValidHoliday=1\tTZ={timetableId};0;0";
    }

    public string CreateAccUnLockCombCommand(int combinationId, int[] groupIds)
    {
        var ids = new int[5];
        for (int i = 0; i < 5; i++)
        {
            ids[i] = i < groupIds.Length ? groupIds[i] : 0;
        }

        return $"DATA UPDATE AccUnLockComb UID={combinationId}\tGroup1={ids[0]}\tGroup2={ids[1]}\tGroup3={ids[2]}\tGroup4={ids[3]}\tGroup5={ids[4]}";
    }

    public string CreateBlacklistCommand(string idNumber, string name, int gender, int nation)
    {
        return $"DATA UPDATE BLACKLIST IDNum={idNumber}\tName={name}\tGender={gender}\tNation={nation}";
    }

    public string CreateWhitelistCommand(string idNumber, string name, int gender, int nation, DateTime startDate, DateTime endDate)
    {
        var start = startDate.ToString("yyyy-MM-dd HH:mm:ss");
        var end = endDate.ToString("yyyy-MM-dd HH:mm:ss");
        return $"DATA UPDATE WHITELIST IDNum={idNumber}\tName={name}\tGender={gender}\tNation={nation}\tStartDate={start}\tEndDate={end}";
    }

    public string CreateRemoteUnlockCommand()
    {
        return "AC_UNLOCK";
    }

    public string CreateRemoteEnrollCommand(string pin, int type, int retry, int overwrite, string? cardNo)
    {
        // 12.6 Remote Enrollment Command
        // Fingerprint (type=1): ENROLL_FP
        // Card (type=0 or type=11): ENROLL_MF
        // Unified (face, palm, etc.): ENROLL_BIO
        
        if (type == 1)
        {
            return $"ENROLL_FP PIN={pin}\tFID=0\tRETRY={retry}\tOVERWRITE={overwrite}";
        }
        
        if (type == 0 || type == 11) // Assuming 11 for card if not unified
        {
            return $"ENROLL_MF PIN={pin}\tRETRY={retry}";
        }

        // Unified enrollment (Face, Palm, etc.)
        return $"ENROLL_BIO TYPE={type}\tPIN={pin}\tCardNo={cardNo ?? string.Empty}\tRETRY={retry}\tOVERWRITE={overwrite}";
    }

    public string CreateAccHolidayCommand(int holidayId, string holidayName, DateOnly startDate, DateOnly endDate, int timeZoneId)
    {
        if (holidayId <= 0) return string.Empty;
        var name = (holidayName ?? string.Empty).Trim();
        if (name.Length == 0) name = $"Holiday {holidayId}";
        return $"DATA UPDATE AccHoliday UID={holidayId}\tHolidayName={name}\tStartDate={startDate:MMdd}\tEndDate={endDate:MMdd}\tTimeZone={timeZoneId}";
    }

    public int GetVerifyMode(Device device)
    {
        if (device.SupportsVisibleLightFace || device.SupportsInfraredFace) return 15;
        if (device.SupportsFingerprint) return 1;
        return 4;
    }

    private static bool IsSupportedVerifyMode(int verifyMode) => verifyMode is >= -1 and <= 29 or 200;

    private static string NormalizeDevicePassword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var token = value.Trim();
        if (token.Length < 6 || token.Any(ch => !char.IsDigit(ch))) return string.Empty;
        return token;
    }

    public string CreateUnifiedTemplateCommand(Employee employee, string templateBase64, bool isVisibleLight)
    {
        if (IsLikelyPhotoPayload(templateBase64)) return string.Empty;
        var normalizedTemplate = NormalizeTemplatePayload(templateBase64);
        if (string.IsNullOrWhiteSpace(normalizedTemplate)) return string.Empty;
        var type = isVisibleLight ? 9 : 2;
        var majorVer = isVisibleLight ? "58" : "7";
        var tmp = PrependRandomPrefix(normalizedTemplate);
        return $"DATA UPDATE BIODATA Pin={employee.EmployeeCode}\tNo=0\tIndex=0\tValid=1\tDuress=0\t" +
               $"Type={type}\tMajorVer={majorVer}\tMinorVer=0\tFormat=0\tTmp={tmp}";
    }

    // Legacy face-template command (protocol §12.1.1.4) — distinct from the newer unified BIODATA
    // command above. Low-end/older infrared-face devices (e.g. ZKTeco MB-series) are documented
    // against this older "DATA UPDATE FACE" syntax, not BIODATA, so a real captured infrared
    // template needs this format to actually be understood/applied by that class of device.
    public string CreateLegacyFaceTemplateCommand(Employee employee, string templateBase64, int faceId = 0)
    {
        if (IsLikelyPhotoPayload(templateBase64)) return string.Empty;
        var normalizedTemplate = NormalizeTemplatePayload(templateBase64);
        if (string.IsNullOrWhiteSpace(normalizedTemplate)) return string.Empty;
        var tmp = PrependRandomPrefix(normalizedTemplate);
        return $"DATA UPDATE FACE PIN={employee.EmployeeCode}\tFID={faceId}\tValid=1\tSize={tmp.Length}\tTMP={tmp}";
    }

    public string CreateComparisonPhotoCommand(Employee employee, string photoBase64, int biometricType = 9)
    {
        var normalizedPhoto = NormalizePhotoPayload(photoBase64);
        if (string.IsNullOrWhiteSpace(normalizedPhoto)) return string.Empty;
        var type = biometricType == 2 ? 2 : 9;
        return $"DATA UPDATE BIOPHOTO PIN={employee.EmployeeCode}\tType={type}\tNo=0\tIndex=0\t" +
               $"Size={normalizedPhoto.Length}\tContent={normalizedPhoto}\tFormat=0\tPostBackTmpFlag=1";
    }

    public string CreateProfilePhotoCommand(Employee employee, string photoBase64)
    {
        var normalizedPhoto = NormalizePhotoPayload(photoBase64);
        if (string.IsNullOrWhiteSpace(normalizedPhoto)) return string.Empty;
        return $"DATA UPDATE USERPIC PIN={employee.EmployeeCode}\tSize={normalizedPhoto.Length}\tContent={normalizedPhoto}";
    }

    public string CreateFingerprintCommand(Employee employee, string fingerprintBase64, int fingerIndex)
    {
        return $"DATA UPDATE FINGERTMP PIN={employee.EmployeeCode}\tFID={fingerIndex}\tSize={fingerprintBase64.Length}\tValid=1\tTMP={fingerprintBase64}";
    }

    public string CreateTimezoneCommand(Device device)
    {
        var minutes = TimezoneToMinutes(device.TimeZone);
        // Protocol (Attendance_PUSH_Communication_Protocol_20250429 §12.4.1) defines the command
        // keyword as singular "SET OPTION" ("C:${CmdID}:SET${SP}OPTION${SP}${Key}=${Value}"). The
        // plural "SET OPTIONS" used here previously doesn't match that keyword, so devices ACK it
        // with Return=0 (generic/unrecognized-command ack) without ever applying the key=value pair
        // — the same failure mode confirmed for MachineTZ/DateTime below.
        return $"SET OPTION TimeZone={minutes}";
    }

    public string CreateMachineTzCommand(Device device)
    {
        var token = NormalizeMachineTz(device.TimeZone);
        return $"SET OPTION MachineTZ={token}";
    }

    public string CreateTimeSyncCommand(DateTime pcTimeLocal)
    {
        var zkEpoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var seconds = Convert.ToInt64((pcTimeLocal - zkEpoch).TotalSeconds);
        return $"SET OPTION DateTime={seconds}";
    }

    public string CreateDeleteUserCommand(string pin) => $"DATA DELETE USERINFO PIN={pin.Trim()}";

    public string CreateRebootCommand() => "REBOOT";

    public IReadOnlyList<(SyncType type, string command)> CreateBulkSyncCommands(
        IEnumerable<Employee> employees, 
        Device device,
        IEnumerable<TimetableEntity> timetables,
        IEnumerable<DepartmentTimetableEntity> departmentTimetables,
        IEnumerable<HolidayEntity> holidays,
        IEnumerable<AccCombinationEntity> combinations)
    {
        var commands = new List<(SyncType type, string command)>
        {
            (SyncType.Configuration, CreateMachineTzCommand(device)),
            (SyncType.Configuration, CreateTimezoneCommand(device)),
            (SyncType.Configuration, CreateTimeSyncCommand(DateTime.Now))
        };

        // 1. Sync Time Periods (AccTimeZone)
        foreach (var timetable in timetables.Where(x => x.IsActive))
        {
            var dailySchedules = timetable.Days.Select(d => {
                var start = d.CheckInStartTime;
                var end = d.CheckOutEndTime;
                if (d.CrossDay || end <= start) end = new TimeOnly(23, 59);
                return (d.DayOfWeek, start, end);
            });
            commands.Add((SyncType.AccessControl, CreateAccTimeZoneCommand(timetable.Id, dailySchedules)));
        }

        // 2. Sync Access Groups (AccGroup)
        foreach (var mapping in departmentTimetables.Where(x => x.IsActive))
        {
            commands.Add((SyncType.AccessControl, CreateAccGroupCommand(mapping.DepartmentId, mapping.TimetableId)));
        }

        // 3. Sync Holidays (AccHoliday)
        foreach (var holiday in holidays.Where(h => h.IsActive))
        {
            commands.Add((SyncType.Holiday, CreateAccHolidayCommand(holiday.Id, holiday.HolidayName, holiday.HolidayDate, holiday.HolidayDate, holiday.TimeZoneId ?? 1)));
        }

        // 4. Sync Combinations (AccUnLockComb)
        foreach (var comb in combinations.Where(c => c.IsActive))
        {
            var groupIds = new[] { comb.Group1, comb.Group2, comb.Group3, comb.Group4, comb.Group5 };
            commands.Add((SyncType.AccessControl, CreateAccUnLockCombCommand(comb.Id, groupIds)));
        }

        // 5. Sync Employees: Active users enrolled with biometrics; Inactive users removed from device
        foreach (var employee in employees.Where(e => e.IsActive))
        {
            commands.Add((SyncType.UserInfo, CreateUserInfoCommand(employee, device)));
            commands.AddRange(CreateEmployeeBiometricSyncCommands(employee, device));
        }

        foreach (var employee in employees.Where(e => !e.IsActive))
        {
            commands.Add((SyncType.UserInfo, CreateDeleteUserCommand(employee.EmployeeCode)));
        }

        return commands;
    }

    // Fingerprint templates + face templates (matching data, not just the display photo) + profile
    // photo for one employee on one device. Shared by CreateBulkSyncCommands (the full manual sync)
    // and the lighter per-employee sync path (QueueEmployeePhotoCommandsForDevice in Program.cs) —
    // previously that path only pushed UserInfo + photo, silently skipping fingerprint/face template
    // data whenever an employee was synced outside of a full manual device sync.
    public IReadOnlyList<(SyncType type, string command)> CreateEmployeeBiometricSyncCommands(Employee employee, Device device)
    {
        var commands = new List<(SyncType type, string command)>();

        if (device.SupportsFingerprint && !string.IsNullOrWhiteSpace(employee.FingerprintTemplates))
        {
            try
            {
                var fpEntries = JsonSerializer.Deserialize<List<FingerprintTemplateEntry>>(
                    employee.FingerprintTemplates,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (fpEntries != null)
                {
                    foreach (var fp in fpEntries)
                    {
                        var fpCommand = CreateFingerprintCommand(employee, fp.Template, fp.FingerIndex);
                        if (!string.IsNullOrWhiteSpace(fpCommand)) commands.Add((SyncType.Fingerprint, fpCommand));
                    }
                }
            }
            catch
            {
                // Ignore parsing issues for malformed template json
            }
        }
        if (!string.IsNullOrWhiteSpace(employee.FaceTemplateVisible) && device.SupportsVisibleLightFace)
        {
            var visibleTemplateCommand = CreateUnifiedTemplateCommand(employee, employee.FaceTemplateVisible, isVisibleLight: true);
            if (!string.IsNullOrWhiteSpace(visibleTemplateCommand)) commands.Add((SyncType.FaceTemplate, visibleTemplateCommand));
        }
        if (!string.IsNullOrWhiteSpace(employee.FaceTemplateInfrared) && device.SupportsInfraredFace)
        {
            var infraredTemplateCommand = CreateLegacyFaceTemplateCommand(employee, employee.FaceTemplateInfrared);
            if (!string.IsNullOrWhiteSpace(infraredTemplateCommand)) commands.Add((SyncType.FaceTemplate, infraredTemplateCommand));
        }
        if (!string.IsNullOrWhiteSpace(employee.ProfilePhoto))
        {
            var userPhotoCommand = CreateProfilePhotoCommand(employee, employee.ProfilePhoto);
            if (!string.IsNullOrWhiteSpace(userPhotoCommand)) commands.Add((SyncType.ProfilePhoto, userPhotoCommand));

            // BIODATA (above) is the mathematical face-matching template, not a viewable photo — it
            // only gets generated when a real template (FaceTemplateVisible/Infrared) already exists.
            // BIOPHOTO is the actual comparison PHOTO the device matches against at check-in, and this
            // method previously never sent it at all (only QueueEmployeePhotoCommandsForDevice in
            // Program.cs did, which isn't on the manual "Sync Data" / CreateBulkSyncCommands path) —
            // so "Sync Data" pushed the profile picture (USERPIC) and, when a template already
            // existed, the face template, but never the photo actually used for face verification.
            // Push whatever photo is on file as the comparison photo too: Type=9 for visible-light,
            // Type=2 for infrared, based on the device's own reported capability (BioPhotoFun=1 or
            // FaceFunOn=1 — some low-end devices support face matching but never report BioPhotoFun).
            var supportsVisibleLightPhoto = device.SupportsVisibleLightFace || device.VisiLightFunOn;
            var supportsInfraredPhoto = !supportsVisibleLightPhoto && device.SupportsInfraredFace && (device.BioPhotoFun || device.FaceFunOn);
            if (supportsVisibleLightPhoto)
            {
                var visiblePhotoCommand = CreateComparisonPhotoCommand(employee, employee.ProfilePhoto, biometricType: 9);
                if (!string.IsNullOrWhiteSpace(visiblePhotoCommand)) commands.Add((SyncType.FaceTemplate, visiblePhotoCommand));
            }
            if (supportsInfraredPhoto)
            {
                var infraredPhotoCommand = CreateComparisonPhotoCommand(employee, employee.ProfilePhoto, biometricType: 2);
                if (!string.IsNullOrWhiteSpace(infraredPhotoCommand)) commands.Add((SyncType.FaceTemplate, infraredPhotoCommand));
            }
        }

        return commands;
    }

    private static string NormalizeMachineTz(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone)) return "+0545";
        var token = timezone.Trim();
        if (Regex.IsMatch(token, @"^[+-]\d{4}$")) return token;
        var sign = token.StartsWith('-') ? "-" : "+";
        token = token.TrimStart('+', '-');
        var parts = token.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var hh) && int.TryParse(parts[1], out var mm)) return $"{sign}{hh:00}{mm:00}";
        return "+0545";
    }

    private static string PrependRandomPrefix(string templateBase64)
    {
        if (string.IsNullOrWhiteSpace(templateBase64)) return string.Empty;
        try
        {
            var rawTemplate = Convert.FromBase64String(templateBase64);
            var randomPrefix = new byte[16];
            RandomNumberGenerator.Fill(randomPrefix);
            var combined = new byte[16 + rawTemplate.Length];
            Buffer.BlockCopy(randomPrefix, 0, combined, 0, 16);
            Buffer.BlockCopy(rawTemplate, 0, combined, 16, rawTemplate.Length);
            return Convert.ToBase64String(combined);
        }
        catch { return templateBase64; }
    }

    private static string NormalizeTemplatePayload(string? templateBase64) => string.IsNullOrWhiteSpace(templateBase64) ? string.Empty : Regex.Replace(templateBase64.Trim(), @"\s+", string.Empty);

    private static string NormalizePhotoPayload(string? photoBase64)
    {
        if (string.IsNullOrWhiteSpace(photoBase64)) return string.Empty;
        var trimmed = photoBase64.Trim();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = trimmed.IndexOf(',');
            if (commaIndex >= 0 && commaIndex + 1 < trimmed.Length) trimmed = trimmed[(commaIndex + 1)..];
            else return string.Empty;
        }
        return Regex.Replace(trimmed, @"\s+", string.Empty);
    }

    private static bool IsLikelyPhotoPayload(string? value) => !string.IsNullOrWhiteSpace(value) && value.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    private sealed record FingerprintTemplateEntry(int FingerIndex, string Template);
}
