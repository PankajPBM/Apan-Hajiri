using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;
using E.Attendance.Data;
using E.Attendance.Data.Production;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace E.Attendance.Services;

public sealed class AttendanceService : IAttendanceService
{
    private readonly IAttendanceRepository _repository;
    private readonly IDeviceProtocolService _protocolService;

    public AttendanceService(IAttendanceRepository repository, IDeviceProtocolService protocolService)
    {
        _repository = repository;
        _protocolService = protocolService;
    }

    public IEnumerable<Employee> ListEmployees() => _repository.GetEmployees();

    public IEnumerable<TimetableEntity> ListTimetables()
    {
        var timetables = _repository.GetAllTimetables();
        foreach (var timetable in timetables)
        {
            timetable.Days = _repository.GetTimetableDays(timetable.Id).ToList();
        }
        return timetables;
    }

    public IEnumerable<DepartmentTimetableEntity> ListDepartmentTimetables()
        => _repository.GetAllDepartmentTimetables();

    public Employee CreateEmployee(string employeeCode, string fullName, string department)
        => CreateEmployee(employeeCode, fullName, string.Empty, [department], department);

    public Employee CreateEmployee(
        string employeeCode,
        string fullName,
        string area,
        IEnumerable<string> departments,
        string? primaryDepartment = null)
    {
        if (string.IsNullOrWhiteSpace(employeeCode))
        {
            throw new ArgumentException("Employee code is required.", nameof(employeeCode));
        }

        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new ArgumentException("Full name is required.", nameof(fullName));
        }

        var normalizedCode = employeeCode.Trim();
        var duplicate = _repository.GetEmployees().Any(e =>
            string.Equals(e.EmployeeCode, normalizedCode, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            throw new InvalidOperationException("Employee code already exists.");
        }

        var normalizedDepartments = NormalizeDepartments(departments, primaryDepartment);
        var employee = new Employee
        {
            EmployeeCode = normalizedCode,
            FullName = fullName.Trim(),
        };
        employee.SetAssignments(area, normalizedDepartments, normalizedDepartments.FirstOrDefault());

        return _repository.AddEmployee(employee);
    }

    public bool UpdateEmployeeDepartment(Guid employeeId, string department)
    {
        var employee = _repository.GetEmployees().FirstOrDefault(e => e.Id == employeeId);
        if (employee is null)
        {
            return false;
        }

        employee.SetAssignments(employee.Area, [department], department);
        var updated = _repository.UpdateEmployee(employee);

        if (updated)
        {
            // Sync only to devices in the same area as the employee
            SyncEmployeeToAreaDevices(employee);
        }

        return updated;
    }

    public bool UpdateEmployeeAssignments(
        Guid employeeId,
        string employeeCode,
        string fullName,
        string area,
        IEnumerable<string> departments,
        string? primaryDepartment = null)
    {
        if (string.IsNullOrWhiteSpace(employeeCode))
        {
            throw new ArgumentException("Employee code is required.", nameof(employeeCode));
        }

        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new ArgumentException("Full name is required.", nameof(fullName));
        }

        var employee = _repository.GetEmployees().FirstOrDefault(e => e.Id == employeeId);
        if (employee is null)
        {
            return false;
        }

        var duplicate = _repository.GetEmployees().Any(e =>
            e.Id != employeeId &&
            string.Equals(e.EmployeeCode, employeeCode.Trim(), StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            throw new InvalidOperationException("Employee code already exists.");
        }

        var normalizedDepartments = NormalizeDepartments(departments, primaryDepartment);
        employee.EmployeeCode = employeeCode.Trim();
        employee.FullName = fullName.Trim();
        employee.SetAssignments(area, normalizedDepartments, normalizedDepartments.FirstOrDefault());
        return _repository.UpdateEmployee(employee);
    }

    public int TransferEmployees(
        IEnumerable<Guid> employeeIds,
        string area,
        IEnumerable<string> departments,
        string? primaryDepartment = null)
    {
        var ids = employeeIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        var normalizedDepartments = NormalizeDepartments(departments, primaryDepartment);
        var updated = 0;
        foreach (var employee in _repository.GetEmployees().Where(e => ids.Contains(e.Id)))
        {
            employee.SetAssignments(area, normalizedDepartments, normalizedDepartments.FirstOrDefault());
            if (_repository.UpdateEmployee(employee))
            {
                updated++;
            }
        }

        return updated;
    }

    public Employee GetOrCreateEmployee(string employeeCode)
    {
        if (string.IsNullOrWhiteSpace(employeeCode))
        {
            throw new ArgumentException("Employee code is required.", nameof(employeeCode));
        }

        var normalizedCode = employeeCode.Trim();
        var existing = _repository.GetEmployees().FirstOrDefault(e =>
            string.Equals(e.EmployeeCode, normalizedCode, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var employee = new Employee
        {
            EmployeeCode = normalizedCode,
            FullName = normalizedCode,
            AreaId = 1,
            DepartmentId = 1
        };

        return _repository.AddEmployee(employee);
    }

    public bool DeactivateEmployee(Guid employeeId)
    {
        var employee = _repository.GetEmployees().FirstOrDefault(e => e.Id == employeeId);
        if (employee is null)
        {
            return false;
        }

        employee.IsActive = false;
        return _repository.UpdateEmployee(employee);
    }

    public IEnumerable<Device> ListDevices()
        => OrderDevices(_repository.GetDevices());

    public IReadOnlyDictionary<string, long> GetDeviceTableStamps(string deviceSerialNumber)
        => _repository.GetDeviceTableStamps(deviceSerialNumber);

    public void UpsertDeviceTableStamp(string deviceSerialNumber, string tableName, long stampValue)
        => _repository.UpsertDeviceTableStamp(deviceSerialNumber, tableName, stampValue);

    public Device RegisterDevice(string serialNumber, string name, string ipAddress, string location, string? model = null)
    {
        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            throw new ArgumentException("Serial number is required.", nameof(serialNumber));
        }

        var trimmedModel = model?.Trim() ?? string.Empty;

        if (_repository.FindDeviceBySerial(serialNumber.Trim()) is { } existing)
        {
            // Only update name if it's currently empty, to preserve user-defined aliases.
            if (string.IsNullOrWhiteSpace(existing.Name) && !string.IsNullOrWhiteSpace(name))
            {
                existing.Name = name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(trimmedModel))
            {
                var modelEchoesIncomingName =
                    !string.IsNullOrWhiteSpace(name) &&
                    string.Equals(trimmedModel, name.Trim(), StringComparison.OrdinalIgnoreCase);

                var isNoneModel = string.Equals(existing.Model, "None", StringComparison.OrdinalIgnoreCase) ||
                                 string.IsNullOrWhiteSpace(existing.Model);

                // Update model if:
                // 1. It's currently "None" or empty (initialization)
                // 2. The incoming model is NOT just an echo of the incoming name
                if (isNoneModel || !modelEchoesIncomingName)
                {
                    existing.Model = trimmedModel;
                }
            }
            existing.IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? existing.IpAddress : ipAddress.Trim();
            existing.Location = string.IsNullOrWhiteSpace(location) ? existing.Location : location.Trim();
            existing.IsRegistered = true;
            _repository.UpdateDevice(existing);
            return existing;
        }

        var realName = string.IsNullOrWhiteSpace(name) ? serialNumber.Trim() : name.Trim();
        var device = new Device
        {
            SerialNumber = serialNumber.Trim(),
            Name = realName,
            Model = trimmedModel,
            IpAddress = ipAddress?.Trim() ?? string.Empty,
            Location = location?.Trim() ?? string.Empty,
            TimeZone = "+5:45",
            IsOnline = false,
            Status = DeviceStatus.Offline,
            LastSeenUtc = null
        };

        device.IsRegistered = true;
        return _repository.AddDevice(device);
    }

    public bool UpdateDeviceInfo(Guid deviceId, string? name, string? model, string? ipAddress, string? location, bool? isRegistrationDevice = null)
    {
        var device = _repository.FindDeviceById(deviceId);
        if (device is null)
        {
            return false;
        }

        var trimmedName = name?.Trim();
        var trimmedModel = model?.Trim();

        if (!string.IsNullOrWhiteSpace(trimmedName))
        {
            device.Name = trimmedName;
        }

        if (!string.IsNullOrWhiteSpace(trimmedModel))
        {
            var modelEchoesIncomingName =
                !string.IsNullOrWhiteSpace(trimmedName) &&
                string.Equals(trimmedModel, trimmedName, StringComparison.OrdinalIgnoreCase);

            var isNoneModel = string.Equals(device.Model, "None", StringComparison.OrdinalIgnoreCase) ||
                             string.IsNullOrWhiteSpace(device.Model);

            // Update model if:
            // 1. It's currently "None" or empty (initialization)
            // 2. The incoming model is NOT just an echo of the display name
            if (isNoneModel || !modelEchoesIncomingName)
            {
                device.Model = trimmedModel;
            }
        }

        if (ipAddress is not null)
        {
            device.IpAddress = ipAddress.Trim();
        }

        if (location is not null)
        {
            device.Location = location.Trim();
        }

        if (isRegistrationDevice.HasValue)
        {
            device.IsRegistrationDevice = isRegistrationDevice.Value;
        }

        return _repository.UpdateDevice(device);
    }

    public bool ConnectDevice(Guid deviceId)
    {
        var device = _repository.FindDeviceById(deviceId);
        if (device is null)
        {
            return false;
        }

        if (!device.IsRegistered)
        {
            throw new InvalidOperationException("Device must be registered before connecting. Use the Add Device option to register this device first.");
        }

        device.IsOnline = true;
        device.Status = DeviceStatus.Online;
        device.LastSeenUtc = DateTime.UtcNow;
        var updated = _repository.UpdateDevice(device);
        if (updated)
        {
            // Queue timezone sync command to apply selected timezone to device
            QueueDeviceCommand(device.SerialNumber, $"SETTIMEZONE {device.TimeZone}");
        }

        return updated;
    }

    public bool DisconnectDevice(Guid deviceId)
    {
        var device = _repository.FindDeviceById(deviceId);
        if (device is null)
        {
            return false;
        }

        device.IsOnline = false;
        device.Status = DeviceStatus.Offline;
        return _repository.UpdateDevice(device);
    }

    public bool DeleteDevice(Guid deviceId)
        => _repository.DeleteDevice(deviceId);

    public Device TouchDevice(string serialNumber, string? ipAddress)
    {
        var device = _repository.FindDeviceBySerial(serialNumber);
        if (device is null)
        {
            return new Device
            {
                SerialNumber = serialNumber.Trim(),
                Name = serialNumber.Trim(),
                Model = string.Empty,
                IpAddress = ipAddress?.Trim() ?? string.Empty,
                Location = string.Empty,
                TimeZone = "+5:45",
                Status = DeviceStatus.Online,
                IsOnline = false,
                IsRegistered = false,
                LastSeenUtc = DateTime.UtcNow
            };
        }

        device.LastSeenUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            device.IpAddress = ipAddress.Trim();
        }
        _repository.UpdateDevice(device);
        return device;
    }
    public void UpdateDeviceCapabilities(
        string serialNumber,
        bool visibleLight,
        bool infrared,
        bool fingerprint,
        string? faceAlgorithmVersion = null,
        string? fingerprintAlgorithmVersion = null,
        string? model = null,
        string? timeZone = null)
    {
        var device = _repository.FindDeviceBySerial(serialNumber.Trim());
        if (device is null) return;

        var changed = false;
        if (device.SupportsVisibleLightFace != visibleLight) { device.SupportsVisibleLightFace = visibleLight; changed = true; }
        if (device.SupportsInfraredFace != infrared) { device.SupportsInfraredFace = infrared; changed = true; }
        if (device.SupportsFingerprint != fingerprint) { device.SupportsFingerprint = fingerprint; changed = true; }
        if (!string.IsNullOrWhiteSpace(faceAlgorithmVersion))
        {
            var normalized = faceAlgorithmVersion.Trim();
            if (!string.Equals(device.FaceAlgorithmVersion, normalized, StringComparison.OrdinalIgnoreCase))
            {
                device.FaceAlgorithmVersion = normalized;
                changed = true;
            }
        }
        if (!string.IsNullOrWhiteSpace(fingerprintAlgorithmVersion))
        {
            var normalized = fingerprintAlgorithmVersion.Trim();
            if (!string.Equals(device.FingerprintAlgorithmVersion, normalized, StringComparison.OrdinalIgnoreCase))
            {
                device.FingerprintAlgorithmVersion = normalized;
                changed = true;
            }
        }
        if (!string.IsNullOrWhiteSpace(model))
        {
            var normalized = model.Trim();
            if (!string.Equals(device.Model, normalized, StringComparison.OrdinalIgnoreCase))
            {
                // Prevent overwriting a real model with a name-mirrored value
                var modelEchoesDeviceName = !string.IsNullOrWhiteSpace(device.Name) && 
                                            string.Equals(normalized, device.Name, StringComparison.OrdinalIgnoreCase);
                
                var isNoneModel = string.Equals(device.Model, "None", StringComparison.OrdinalIgnoreCase) ||
                                 string.IsNullOrWhiteSpace(device.Model);

                // Update model if:
                // 1. It's currently "None" or empty (initialization)
                // 2. The incoming model is NOT just an echo of the device name
                if (isNoneModel || !modelEchoesDeviceName)
                {
                    device.Model = normalized;
                    changed = true;
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(timeZone))
        {
            var normalized = timeZone.Trim();
            if (!string.Equals(device.TimeZone, normalized, StringComparison.OrdinalIgnoreCase))
            {
                device.TimeZone = normalized;
                changed = true;
            }
        }

        if (changed)
        {
            _repository.UpdateDevice(device);
        }
    }

    public void UpdateDeviceProtocolInfo(
        string serialNumber,
        string? firmwareVersion = null,
        string? deviceType = null,
        string? pushVer = null,
        int? language = null,
        string? multiBioDataSupport = null,
        string? multiBioPhotoSupport = null,
        string? multiBioVersion = null,
        string? maxMultiBioDataCount = null,
        string? maxMultiBioPhotoCount = null,
        bool? fingerFunOn = null,
        bool? faceFunOn = null,
        bool? visiLightFunOn = null,
        bool? bioDataFun = null,
        bool? bioPhotoFun = null,
        string? capabilitiesJson = null,
        bool? supportsPalm = null,
        bool? supportsFingerVein = null)
    {
        var device = _repository.FindDeviceBySerial(serialNumber.Trim());
        if (device is null) return;

        var changed = false;
        if (firmwareVersion is not null && !string.Equals(device.FirmwareVersion, firmwareVersion, StringComparison.Ordinal))
        {
            device.FirmwareVersion = firmwareVersion;
            changed = true;
        }
        if (deviceType is not null && !string.Equals(device.DeviceType, deviceType, StringComparison.Ordinal))
        {
            device.DeviceType = deviceType;
            changed = true;
        }
        if (pushVer is not null && !string.Equals(device.PushVer, pushVer, StringComparison.Ordinal))
        {
            device.PushVer = pushVer;
            changed = true;
        }
        if (language.HasValue && device.Language != language.Value)
        {
            device.Language = language.Value;
            changed = true;
        }
        if (multiBioDataSupport is not null && !string.Equals(device.MultiBioDataSupport, multiBioDataSupport, StringComparison.Ordinal))
        {
            device.MultiBioDataSupport = multiBioDataSupport;
            changed = true;
        }
        if (multiBioPhotoSupport is not null && !string.Equals(device.MultiBioPhotoSupport, multiBioPhotoSupport, StringComparison.Ordinal))
        {
            device.MultiBioPhotoSupport = multiBioPhotoSupport;
            changed = true;
        }
        if (multiBioVersion is not null && !string.Equals(device.MultiBioVersion, multiBioVersion, StringComparison.Ordinal))
        {
            device.MultiBioVersion = multiBioVersion;
            changed = true;
        }
        if (maxMultiBioDataCount is not null && !string.Equals(device.MaxMultiBioDataCount, maxMultiBioDataCount, StringComparison.Ordinal))
        {
            device.MaxMultiBioDataCount = maxMultiBioDataCount;
            changed = true;
        }
        if (maxMultiBioPhotoCount is not null && !string.Equals(device.MaxMultiBioPhotoCount, maxMultiBioPhotoCount, StringComparison.Ordinal))
        {
            device.MaxMultiBioPhotoCount = maxMultiBioPhotoCount;
            changed = true;
        }
        if (fingerFunOn.HasValue && device.FingerFunOn != fingerFunOn.Value) { device.FingerFunOn = fingerFunOn.Value; changed = true; }
        if (faceFunOn.HasValue && device.FaceFunOn != faceFunOn.Value) { device.FaceFunOn = faceFunOn.Value; changed = true; }
        if (visiLightFunOn.HasValue && device.VisiLightFunOn != visiLightFunOn.Value) { device.VisiLightFunOn = visiLightFunOn.Value; changed = true; }
        if (bioDataFun.HasValue && device.BioDataFun != bioDataFun.Value) { device.BioDataFun = bioDataFun.Value; changed = true; }
        if (bioPhotoFun.HasValue && device.BioPhotoFun != bioPhotoFun.Value) { device.BioPhotoFun = bioPhotoFun.Value; changed = true; }
        if (capabilitiesJson is not null && !string.Equals(device.CapabilitiesJson, capabilitiesJson, StringComparison.Ordinal))
        {
            device.CapabilitiesJson = capabilitiesJson;
            changed = true;
        }
        if (supportsPalm.HasValue && device.SupportsPalm != supportsPalm.Value) { device.SupportsPalm = supportsPalm.Value; changed = true; }
        if (supportsFingerVein.HasValue && device.SupportsFingerVein != supportsFingerVein.Value) { device.SupportsFingerVein = supportsFingerVein.Value; changed = true; }

        if (changed)
        {
            _repository.UpdateDevice(device);
        }
    }

    public DeviceCommand QueueDeviceCommand(string deviceSerialNumber, string commandText)
    {
        if (string.IsNullOrWhiteSpace(deviceSerialNumber))
        {
            throw new ArgumentException("Device serial number is required.", nameof(deviceSerialNumber));
        }

        if (string.IsNullOrWhiteSpace(commandText))
        {
            throw new ArgumentException("Command text is required.", nameof(commandText));
        }

        var command = new DeviceCommand
        {
            DeviceSerialNumber = deviceSerialNumber.Trim(),
            CommandText = NormalizeDeviceCommand(commandText)
        };

        return _repository.EnqueueCommand(command);
    }

    private static string NormalizeDeviceCommand(string commandText)
    {
        var token = commandText.Trim();
        if (token.StartsWith("DATA QUERY USERINFO", StringComparison.OrdinalIgnoreCase) &&
            !token.Contains("PIN=", StringComparison.OrdinalIgnoreCase))
        {
            return "DATA QUERY USERINFO PIN=*";
        }

        return token;
    }

    public IReadOnlyList<DeviceCommand> GetPendingCommands(string deviceSerialNumber, int maxCount)
        => _repository.GetPendingCommands(deviceSerialNumber, maxCount);

    public IReadOnlyList<DeviceCommand> GetDeviceCommands(string deviceSerialNumber, int maxCount)
        => _repository.GetDeviceCommands(deviceSerialNumber, maxCount);

    public void MarkCommandSent(Guid commandId)
        => _repository.MarkCommandSent(commandId);

    public void RecordCommandReply(Guid commandId, string replyText)
        => _repository.SaveCommandReply(commandId, replyText);

    public int ClearPendingCommands(string deviceSerialNumber)
    {
        if (string.IsNullOrWhiteSpace(deviceSerialNumber))
        {
            return 0;
        }

        return _repository.ClearPendingCommands(deviceSerialNumber.Trim());
    }

    public bool DeleteDeviceCommand(Guid commandId)
        => _repository.DeleteDeviceCommand(commandId);

    public int ResetStaleSentCommands(string deviceSerialNumber, int olderThanMinutes)
    {
        if (string.IsNullOrWhiteSpace(deviceSerialNumber))
        {
            return 0;
        }

        return _repository.ResetStaleSentCommands(deviceSerialNumber.Trim(), olderThanMinutes);
    }

    /// <summary>
    /// Generates and queues AccTimeZone commands for a timetable to all relevant devices.
    /// </summary>
    public void SyncTimetableToDevices(int timetableId)
    {
        // Get the timetable with its days
        var timetable = GetTimetableWithDays(timetableId);
        if (timetable == null)
        {
            return;
        }

        // Convert to the format expected by CreateAccTimeZoneCommand
        var dailySchedules = new List<(int DayOfWeek, TimeOnly Start, TimeOnly End)>();
        foreach (var day in timetable.Days)
        {
            // If it's a weekly off, use 00:00-00:00
            if (day.IsWeeklyOff)
            {
                dailySchedules.Add((day.DayOfWeek, new TimeOnly(0, 0), new TimeOnly(0, 0)));
            }
            else
            {
                // Use CheckInTime and CheckOutTime for the schedule
                // Handle cross-day shifts
                TimeOnly startTime = day.CheckInTime;
                TimeOnly endTime = day.CheckOutTime;
                
                // If cross-day and check-out is earlier than check-in, 
                // we still use the times as-is since the device handles cross-day scheduling
                dailySchedules.Add((day.DayOfWeek, startTime, endTime));
            }
        }

        // Generate the AccTimeZone command
        string commandText = _protocolService.CreateAccTimeZoneCommand(timetable.Id, dailySchedules);

        // Get all devices that should receive this timetable
        // This includes devices assigned to employees or departments that use this timetable
        var devices = GetDevicesForTimetable(timetableId);
        
        // Queue the command for each device
        foreach (var device in devices)
        {
            QueueDeviceCommand(device.SerialNumber, commandText);
        }
    }

    /// <summary>
    /// Generates and queues AccGroup commands for timetable assignments to all relevant devices.
    /// </summary>
    public void SyncTimetableAssignmentsToDevices(int timetableId)
    {
        // Get all devices that should receive assignments for this timetable
        var devices = GetDevicesForTimetable(timetableId);
        
        // Get employee timetable assignments for this timetable
        var employeeAssignments = GetEmployeeTimetableAssignments(timetableId);
        foreach (var assignment in employeeAssignments)
        {
            string commandText = _protocolService.CreateAccGroupCommand(assignment.PersonnelId, timetableId);
            foreach (var device in devices)
            {
                QueueDeviceCommand(device.SerialNumber, commandText);
            }
        }
        
        // Get department timetable assignments for this timetable
        var departmentAssignments = GetDepartmentTimetableAssignments(timetableId);
        foreach (var assignment in departmentAssignments)
        {
            string commandText = _protocolService.CreateAccGroupCommand(assignment.DepartmentId, timetableId);
            foreach (var device in devices)
            {
                QueueDeviceCommand(device.SerialNumber, commandText);
            }
        }
    }

    // Helper methods to get timetable data and related information
    private TimetableEntity? GetTimetableWithDays(int timetableId)
    {
        // Create a temporary repository to get timetable data
        // Using the same connection string as the main repository
        if (_repository is SqliteAttendanceRepository sqliteRepo)
        {
            return GetTimetableWithDaysFromSqlite(sqliteRepo, timetableId);
        }
        else if (_repository is PostgresAttendanceRepository postgresRepo)
        {
            return GetTimetableWithDaysFromPostgres(postgresRepo, timetableId);
        }
        else
        {
            // Fallback - create a new sqlite repository with a default path
            // In a real implementation, we'd want to get the connection string properly
            var tempRepo = new SqliteAttendanceRepository(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "E_Attendance",
                    "pb-attendance.db"));
            return GetTimetableWithDaysFromSqlite(tempRepo, timetableId);
        }
    }

    private TimetableEntity? GetTimetableWithDaysFromSqlite(SqliteAttendanceRepository repo, int timetableId)
    {
        const string sql = """
            SELECT t.Id, t.TimetableName, t.IsActive, t.CreatedAt, t.UpdatedAt,
                   td.Id as DayId, td.TimetableId, td.DayOfWeek, td.IsWeeklyOff,
                   td.CheckInTime, td.CheckInStartTime, td.CheckInEndTime,
                   td.CheckOutStartTime, td.CheckOutTime, td.CheckOutEndTime,
                   td.CrossDay, td.UseBreakTime, td.BreakTimeId
            FROM Timetables t
            LEFT JOIN TimetableDays td ON t.Id = td.TimetableId
            WHERE t.Id = $timetableId
            ORDER BY td.DayOfWeek
            """;

        var timetableDays = new List<TimetableDayEntity>();
        TimetableEntity? timetable = null;

        repo.Query(sql, db =>
        {
            db.Parameters.AddWithValue("$timetableId", timetableId);
        }, reader =>
        {
            // Initialize timetable on first row
            if (timetable == null)
            {
                timetable = new TimetableEntity
                {
                    Id = reader.GetInt32(0),
                    TimetableName = reader.GetString(1),
                    IsActive = reader.GetBoolean(2),
                    CreatedAt = ReadDateTime(reader, 3),
                    UpdatedAt = ReadDateTime(reader, 4),
                    Days = timetableDays
                };
            }

            // Add day if it exists
            if (!reader.IsDBNull(5)) // DayId is not null
            {
                var day = new TimetableDayEntity
                {
                    Id = reader.GetInt32(5),
                    TimetableId = reader.GetInt32(6),
                    DayOfWeek = reader.GetInt32(7),
                    IsWeeklyOff = reader.GetBoolean(8),
                    CheckInTime = ReadTimeSpan(reader, 9) != TimeSpan.Zero ? TimeOnly.FromTimeSpan(ReadTimeSpan(reader, 9)) : new TimeOnly(0, 0),
                    CheckInStartTime = ReadTimeSpan(reader, 10) != TimeSpan.Zero ? TimeOnly.FromTimeSpan(ReadTimeSpan(reader, 10)) : new TimeOnly(0, 0),
                    CheckInEndTime = ReadTimeSpan(reader, 11) != TimeSpan.Zero ? TimeOnly.FromTimeSpan(ReadTimeSpan(reader, 11)) : new TimeOnly(0, 0),
                    CheckOutStartTime = ReadTimeSpan(reader, 12) != TimeSpan.Zero ? TimeOnly.FromTimeSpan(ReadTimeSpan(reader, 12)) : new TimeOnly(0, 0),
                    CheckOutTime = ReadTimeSpan(reader, 13) != TimeSpan.Zero ? TimeOnly.FromTimeSpan(ReadTimeSpan(reader, 13)) : new TimeOnly(0, 0),
                    CheckOutEndTime = ReadTimeSpan(reader, 14) != TimeSpan.Zero ? TimeOnly.FromTimeSpan(ReadTimeSpan(reader, 14)) : new TimeOnly(0, 0),
                    CrossDay = reader.GetBoolean(15),
                    UseBreakTime = reader.GetBoolean(16),
                    BreakTimeId = reader.IsDBNull(17) ? null : reader.GetInt32(17)
                };
                timetableDays.Add(day);
            }
        });

        return timetable;
    }

    private TimetableEntity? GetTimetableWithDaysFromPostgres(PostgresAttendanceRepository repo, int timetableId)
    {
        // For now, delegate to sqlite implementation since PostgresAttendanceRepository wraps it
        if (repo._inner is SqliteAttendanceRepository sqliteRepo)
        {
            return GetTimetableWithDaysFromSqlite(sqliteRepo, timetableId);
        }
        else
        {
            // Fallback
            return GetTimetableWithDaysFromSqlite(new SqliteAttendanceRepository(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "E_Attendance",
                    "pb-attendance.db")), timetableId);
        }
    }

    private List<Device> GetDevicesForTimetable(int timetableId)
    {
        // Get all devices that have employees or departments assigned to this timetable
        var devices = new List<Device>();
        
        if (_repository is SqliteAttendanceRepository sqliteRepo)
        {
            devices.AddRange(GetDevicesForTimetableFromSqlite(sqliteRepo, timetableId));
        }
        else if (_repository is PostgresAttendanceRepository postgresRepo)
        {
            if (postgresRepo._inner is SqliteAttendanceRepository sqliteInner)
            {
                devices.AddRange(GetDevicesForTimetableFromSqlite(sqliteInner, timetableId));
            }
        }

        // If we couldn't determine specific devices, return all devices (safe fallback)
        if (devices.Count == 0)
        {
            if (_repository is SqliteAttendanceRepository sqliteRepo2)
            {
                devices.AddRange(GetAllDevicesFromSqlite(sqliteRepo2));
            }
            else if (_repository is PostgresAttendanceRepository postgresRepo2)
            {
                if (postgresRepo2._inner is SqliteAttendanceRepository sqliteInner2)
                {
                    devices.AddRange(GetAllDevicesFromSqlite(sqliteInner2));
                }
            }
        }

        return devices.DistinctBy(d => d.Id).ToList();
    }

    private List<Device> GetDevicesForTimetableFromSqlite(SqliteAttendanceRepository repo, int timetableId)
    {
        var devices = new List<Device>();
        
        // Get devices for employees assigned to this timetable
        const string employeeSql = """
            SELECT DISTINCT d.Id, d.SerialNumber, d.Name, d.Model, d.IpAddress, d.Location,
                   d.TimeZone, d.SupportsVisibleLightFace, d.SupportsInfraredFace, d.SupportsFingerprint,
                   d.FaceAlgorithmVersion, d.FingerprintAlgorithmVersion, d.FirmwareVersion,
                   d.DeviceType, d.PushVer, d.Language, d.MultiBioDataSupport, d.MultiBioPhotoSupport,
                   d.MultiBioVersion, d.MaxMultiBioDataCount, d.MaxMultiBioPhotoCount,
                   d.FingerFunOn, d.FaceFunOn, d.VisiLightFunOn, d.BioDataFun, d.BioPhotoFun,
                   d.SupportsPalm, d.SupportsFingerVein, d.CapabilitiesJson,
                   d.CreatedAtUtc, d.UpdatedAtUtc, d.Status, d.IsRegistrationDevice, d.IsOnline, d.LastSeenUtc
            FROM Devices d
            JOIN Employees e ON 1=1
            JOIN EmployeeTimetables et ON e.Id = et.PersonnelId
            WHERE et.TimetableId = $timetableId
            AND d.IsRegistered = 1
            """;

        repo.Query(employeeSql, db =>
        {
            db.Parameters.AddWithValue("$timetableId", timetableId);
        }, reader =>
        {
            devices.Add(ReadDeviceFromReader(reader));
        });

        // Get devices for departments assigned to this timetable
        const string departmentSql = """
            SELECT DISTINCT d.Id, d.SerialNumber, d.Name, d.Model, d.IpAddress, d.Location,
                   d.TimeZone, d.SupportsVisibleLightFace, d.SupportsInfraredFace, d.SupportsFingerprint,
                   d.FaceAlgorithmVersion, d.FingerprintAlgorithmVersion, d.FirmwareVersion,
                   d.DeviceType, d.PushVer, d.Language, d.MultiBioDataSupport, d.MultiBioPhotoSupport,
                   d.MultiBioVersion, d.MaxMultiBioDataCount, d.MaxMultiBioPhotoCount,
                   d.FingerFunOn, d.FaceFunOn, d.VisiLightFunOn, d.BioDataFun, d.BioPhotoFun,
                   d.SupportsPalm, d.SupportsFingerVein, d.CapabilitiesJson,
                   d.CreatedAtUtc, d.UpdatedAtUtc, d.Status, d.IsRegistrationDevice, d.IsOnline, d.LastSeenUtc
            FROM Devices d
            JOIN Employees e ON 1=1
            JOIN DepartmentTimetables dt ON e.DepartmentId = dt.DepartmentId
            WHERE dt.TimetableId = $timetableId
            AND d.IsRegistered = 1
            """;

        repo.Query(departmentSql, db =>
        {
            db.Parameters.AddWithValue("$timetableId", timetableId);
        }, reader =>
        {
            devices.Add(ReadDeviceFromReader(reader));
        });

        return devices;
    }

    private List<Device> GetAllDevicesFromSqlite(SqliteAttendanceRepository repo)
    {
        var devices = new List<Device>();
        
        const string sql = """
            SELECT Id, SerialNumber, Name, Model, IpAddress, Location,
                   TimeZone, SupportsVisibleLightFace, SupportsInfraredFace, SupportsFingerprint,
                   FaceAlgorithmVersion, FingerprintAlgorithmVersion, FirmwareVersion,
                   DeviceType, PushVer, Language, MultiBioDataSupport, MultiBioPhotoSupport,
                   MultiBioVersion, MaxMultiBioDataCount, MaxMultiBioPhotoCount,
                   FingerFunOn, FaceFunOn, VisiLightFunOn, BioDataFun, BioPhotoFun,
                   SupportsPalm, SupportsFingerVein, CapabilitiesJson,
                   CreatedAtUtc, UpdatedAtUtc, Status, IsRegistrationDevice, IsOnline, LastSeenUtc
            FROM Devices
            WHERE IsRegistered = 1
            """;

        repo.Query(sql, db =>
        {
            // No parameters
        }, reader =>
        {
            devices.Add(ReadDeviceFromReader(reader));
        });

        return devices;
    }

    private Device ReadDeviceFromReader(DbDataReader reader)
    {
        return new Device
        {
            Id = reader.GetGuid(0),
            SerialNumber = reader.GetString(1),
            Name = reader.GetString(2),
            Model = reader.GetString(3),
            IpAddress = reader.GetString(4),
            Location = reader.GetString(5),
            TimeZone = reader.GetString(6),
            SupportsVisibleLightFace = reader.GetBoolean(7),
            SupportsInfraredFace = reader.GetBoolean(8),
            SupportsFingerprint = reader.GetBoolean(9),
            FaceAlgorithmVersion = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            FingerprintAlgorithmVersion = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            FirmwareVersion = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            DeviceType = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            PushVer = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
            Language = reader.GetInt32(15),
            MultiBioDataSupport = reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
            MultiBioPhotoSupport = reader.IsDBNull(17) ? string.Empty : reader.GetString(17),
            MultiBioVersion = reader.IsDBNull(18) ? string.Empty : reader.GetString(18),
            MaxMultiBioDataCount = reader.IsDBNull(19) ? string.Empty : reader.GetString(19),
            MaxMultiBioPhotoCount = reader.IsDBNull(20) ? string.Empty : reader.GetString(20),
            FingerFunOn = reader.GetBoolean(21),
            FaceFunOn = reader.GetBoolean(22),
            VisiLightFunOn = reader.GetBoolean(23),
            BioDataFun = reader.GetBoolean(24),
            BioPhotoFun = reader.GetBoolean(25),
            SupportsPalm = reader.GetBoolean(26),
            SupportsFingerVein = reader.GetBoolean(27),
            CapabilitiesJson = reader.IsDBNull(28) ? string.Empty : reader.GetString(28),
            CreatedAtUtc = reader.IsDBNull(29) ? DateTime.UtcNow : ReadDateTime(reader, 29),
            UpdatedAtUtc = reader.IsDBNull(30) ? DateTime.UtcNow : ReadDateTime(reader, 30),
            Status = Enum.TryParse<DeviceStatus>(reader.IsDBNull(31) ? string.Empty : reader.GetString(31), out var status) ? status : DeviceStatus.Offline,
            IsRegistrationDevice = reader.GetBoolean(32),
            IsOnline = reader.GetBoolean(33),
            LastSeenUtc = reader.IsDBNull(34) ? null : ReadDateTime(reader, 34)
        };
    }

    private List<EmployeeTimetableEntity> GetEmployeeTimetableAssignments(int timetableId)
    {
        var assignments = new List<EmployeeTimetableEntity>();
        
        if (_repository is SqliteAttendanceRepository sqliteRepo)
        {
            const string sql = """
                SELECT Id, PersonnelId, TimetableId, EffectiveFrom, IsActive
                FROM EmployeeTimetables
                WHERE TimetableId = $timetableId
                AND IsActive = 1
                """;

            sqliteRepo.Query(sql, (DbCommand db) =>
            {
                db.Parameters.AddWithValue("$timetableId", timetableId);
            }, reader =>
            {
                assignments.Add(new EmployeeTimetableEntity
                {
                    Id = reader.GetInt32(0),
                    PersonnelId = ReadGuid(reader, 1),
                    TimetableId = reader.GetInt32(2),
                    EffectiveFrom = DateOnly.Parse(reader.GetString(3)),
                    IsActive = reader.GetBoolean(4)
                });
            });
        }
        else if (_repository is PostgresAttendanceRepository postgresRepo)
        {
            if (postgresRepo._inner is SqliteAttendanceRepository sqliteInner)
            {
                const string sql = """
                    SELECT Id, PersonnelId, TimetableId, EffectiveFrom, IsActive
                    FROM EmployeeTimetables
                    WHERE TimetableId = $timetableId
                    AND IsActive = 1
                    """;

                sqliteInner.Query(sql, (DbCommand db) =>
                {
                    db.Parameters.AddWithValue("$timetableId", timetableId);
                }, reader =>
                {
                    assignments.Add(new EmployeeTimetableEntity
                    {
                        Id = reader.GetInt32(0),
                        PersonnelId = ReadGuid(reader, 1),
                        TimetableId = reader.GetInt32(2),
                        EffectiveFrom = DateOnly.Parse(reader.GetString(3)),
                        IsActive = reader.GetBoolean(4)
                    });
                });
            }
        }

        return assignments;
    }

    private Guid ReadGuid(DbDataReader reader, int index)
    {
        var val = reader.GetValue(index);
        if (val is Guid guid) return guid;
        if (val is string s && Guid.TryParse(s, out var parsed)) return parsed;
        return Guid.Empty;
    }

    private List<DepartmentTimetableEntity> GetDepartmentTimetableAssignments(int timetableId)
    {
        var assignments = new List<DepartmentTimetableEntity>();
        
        if (_repository is SqliteAttendanceRepository sqliteRepo)
        {
            const string sql = """
                SELECT Id, DepartmentId, TimetableId, EffectiveFrom, IsActive
                FROM DepartmentTimetables
                WHERE TimetableId = $timetableId
                AND IsActive = 1
                """;

            sqliteRepo.Query(sql, (DbCommand db) =>
            {
                db.Parameters.AddWithValue("$timetableId", timetableId);
            }, reader =>
            {
                assignments.Add(new DepartmentTimetableEntity
                {
                    Id = reader.GetInt32(0),
                    DepartmentId = reader.GetInt32(1),
                    TimetableId = reader.GetInt32(2),
                    EffectiveFrom = DateOnly.Parse(reader.GetString(3)),
                    IsActive = reader.GetBoolean(4)
                });
            });
        }
        else if (_repository is PostgresAttendanceRepository postgresRepo)
        {
            if (postgresRepo._inner is SqliteAttendanceRepository sqliteInner)
            {
                const string sql = """
                    SELECT Id, DepartmentId, TimetableId, EffectiveFrom, IsActive
                    FROM DepartmentTimetables
                    WHERE TimetableId = $timetableId
                    AND IsActive = 1
                    """;

                sqliteInner.Query(sql, (DbCommand db) =>
                {
                    db.Parameters.AddWithValue("$timetableId", timetableId);
                }, reader =>
                {
                    assignments.Add(new DepartmentTimetableEntity
                    {
                        Id = reader.GetInt32(0),
                        DepartmentId = reader.GetInt32(1),
                        TimetableId = reader.GetInt32(2),
                        EffectiveFrom = DateOnly.Parse(reader.GetString(3)),
                        IsActive = reader.GetBoolean(4)
                    });
                });
            }
        }

        return assignments;
    }

    private DateTime ReadDateTime(DbDataReader reader, int index)
    {
        var val = reader.GetValue(index);
        if (val == null) return DateTime.UtcNow;
        if (val is DateTime dt) return dt;
        if (val is string s && DateTime.TryParse(s, out var parsed)) return parsed;
        return DateTime.UtcNow;
    }

    private TimeSpan ReadTimeSpan(DbDataReader reader, int index)
    {
        var val = reader.GetValue(index);
        if (val == null) return TimeSpan.Zero;
        if (val is TimeSpan ts) return ts;
        if (val is string s && TimeSpan.TryParse(s, out var parsed)) return parsed;
        return TimeSpan.Zero;
    }

    public SyncLog AddSyncLog(
        string deviceSerialNumber,
        SyncType type,
        string commandText,
        SyncStatus status = SyncStatus.Pending)
    {
        if (string.IsNullOrWhiteSpace(deviceSerialNumber))
        {
            throw new ArgumentException("Device serial number is required.", nameof(deviceSerialNumber));
        }

        if (string.IsNullOrWhiteSpace(commandText))
        {
            throw new ArgumentException("Command text is required.", nameof(commandText));
        }

        var row = new SyncLog
        {
            DeviceSerialNumber = deviceSerialNumber.Trim(),
            Type = type,
            Status = status,
            CommandText = commandText.Trim()
        };

        return _repository.AddSyncLog(row);
    }

    public IReadOnlyList<SyncLog> GetSyncLogs(string deviceSerialNumber, int maxCount)
        => _repository.GetSyncLogs(deviceSerialNumber, maxCount);

    public void UpdateSyncLogResult(Guid syncLogId, SyncStatus status, string responseText)
        => _repository.UpdateSyncLogResult(syncLogId, status, responseText);

    public IEnumerable<Device> SearchDevices(string? query)
    {
        var devices = _repository.GetDevices();
        if (string.IsNullOrWhiteSpace(query))
        {
            return OrderDevices(devices);
        }

        var tokens = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            return OrderDevices(devices);
        }

        var filtered = devices.Where(device => MatchesDevice(device, tokens)).ToList();
        return filtered.Count == 0
            ? OrderDevices(devices)
            : OrderDevices(filtered);
    }

    private static IEnumerable<Device> OrderDevices(IEnumerable<Device> devices)
        => devices
            .OrderByDescending(IsAvailable)
            .ThenByDescending(d => d.LastSeenUtc ?? DateTime.MinValue)
            .ThenBy(d => d.SerialNumber, StringComparer.OrdinalIgnoreCase);

    private static bool MatchesDevice(Device device, IReadOnlyList<string> tokens)
        => tokens.Any(token =>
            (device.SerialNumber?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (device.Name?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (device.IpAddress?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (device.Location?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false));

    private static bool IsAvailable(Device device)
        => device.IsOnline &&
           (!device.LastSeenUtc.HasValue || DateTime.UtcNow - device.LastSeenUtc.Value < TimeSpan.FromMinutes(5));

    public IEnumerable<AttendanceRecord> ListAttendance(DateOnly? from, DateOnly? to)
        => _repository.GetAttendance(from, to);

    public AttendanceRecord ClockIn(Guid employeeId, Guid? deviceId = null)
        => AddRecord(employeeId, AttendanceStatus.In, deviceId);

    public AttendanceRecord ClockOut(Guid employeeId, Guid? deviceId = null)
        => AddRecord(employeeId, AttendanceStatus.Out, deviceId);

    public AttendanceRecord AddAttendanceFromDevice(string employeeCode, DateTime timestamp, int status, int verify, string? workcode, string? deviceSerialNumber)
    {
        var normalizedCode = employeeCode?.Trim() ?? string.Empty;
        var employee = string.IsNullOrWhiteSpace(normalizedCode)
            ? null
            : _repository.GetEmployees().FirstOrDefault(e =>
                string.Equals(e.EmployeeCode, normalizedCode, StringComparison.OrdinalIgnoreCase));

        if (employee is null || !employee.IsActive)
        {
            // Do not auto-create employee or record punches for unknown/inactive employees
            return new AttendanceRecord
            {
                EmployeeId = employee?.Id ?? Guid.Empty,
                EmployeeCode = normalizedCode,
                Timestamp = timestamp,
                Status = AttendanceStatus.Unknown,
                StatusCode = status,
                VerifyMode = verify,
                Workcode = workcode,
                DeviceSerialNumber = deviceSerialNumber
            };
        }

        var device = string.IsNullOrWhiteSpace(deviceSerialNumber) ? null : _repository.FindDeviceBySerial(deviceSerialNumber);

        var record = new AttendanceRecord
        {
            EmployeeId = employee.Id,
            EmployeeCode = employee.EmployeeCode,
            Timestamp = timestamp,
            Status = status switch
            {
                0 => AttendanceStatus.In,
                1 => AttendanceStatus.Out,
                3 => AttendanceStatus.BreakIn,
                4 => AttendanceStatus.BreakOut,
                _ => AttendanceStatus.Unknown
            },
            StatusCode = status,
            VerifyMode = verify,
            Workcode = workcode,
            DeviceId = device?.Id,
            DeviceSerialNumber = device?.SerialNumber ?? deviceSerialNumber,
            AreaId = device?.AreaId ?? employee.AreaId,
            DepartmentId = device?.DepartmentId ?? employee.DepartmentId
        };

        return _repository.AddAttendance(record);
    }

    private AttendanceRecord AddRecord(Guid employeeId, AttendanceStatus status, Guid? deviceId)
    {
        var employee = _repository.GetEmployees().FirstOrDefault(e => e.Id == employeeId);
        if (employee is null)
        {
            throw new InvalidOperationException("Employee not found.");
        }

        if (!employee.IsActive)
        {
            throw new InvalidOperationException("Employee is inactive.");
        }

        var record = new AttendanceRecord
        {
            EmployeeId = employeeId,
            EmployeeCode = employee.EmployeeCode,
            Status = status,
            DeviceId = deviceId,
            Timestamp = DateTime.Now
        };

        return _repository.AddAttendance(record);
    }

    public void SyncEmployeeToDevices(Employee employee) => SyncEmployeeToAreaDevices(employee);

    /// <summary>
    /// Queues a USER_INFO sync command to every device that shares the employee's
    /// AreaId AND any of the employee's department IDs.
    /// </summary>
    private void SyncEmployeeToAreaDevices(Employee employee)
    {
        var deptIds = employee.GetDepartmentIds().ToHashSet();
        // Fall back to DepartmentId field when EmployeeDepartments collection not loaded
        if (deptIds.Count == 0 && employee.DepartmentId > 0)
            deptIds.Add(employee.DepartmentId);

        var devices = _repository.GetDevices()
            .Where(d => d.AreaId == employee.AreaId && deptIds.Contains(d.DepartmentId))
            .ToList();

        foreach (var device in devices)
        {
            var cmd = _protocolService.CreateUserInfoCommand(employee, device);
            QueueDeviceCommand(device.SerialNumber, cmd);
        }
    }

    private static List<string> NormalizeDepartments(IEnumerable<string> departments, string? primaryDepartment)
    {
        var normalized = (departments ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var preferredPrimary = primaryDepartment?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(preferredPrimary))
        {
            normalized.RemoveAll(value => string.Equals(value, preferredPrimary, StringComparison.OrdinalIgnoreCase));
            normalized.Insert(0, preferredPrimary);
        }

        if (normalized.Count == 0)
        {
            throw new InvalidOperationException("Select at least one department.");
        }

        return normalized;
    }
}




