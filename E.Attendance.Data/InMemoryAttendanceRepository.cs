using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;

namespace E.Attendance.Data;

public sealed class InMemoryAttendanceRepository : IAttendanceRepository
{
    private readonly List<Employee> _employees = new();
    private readonly List<Device> _devices = new();
    private readonly List<AttendanceRecord> _records = new();
    private readonly List<DeviceCommand> _commands = new();
    private readonly List<SyncLog> _syncLogs = new();
    private readonly Dictionary<string, Dictionary<string, long>> _deviceTableStamps = new(StringComparer.OrdinalIgnoreCase);

    // New in-memory stores for added entities
    private readonly List<Area> _areas = new();
    private readonly List<Department> _departments = new();
    private readonly List<EmployeeDepartment> _employeeDepartments = new();
    private readonly List<SyncQueueEntry> _syncQueue = new();

    public IReadOnlyList<Employee> GetEmployees() => _employees;

    public Employee AddEmployee(Employee employee)
    {
        _employees.Add(employee);
        return employee;
    }

    public bool UpdateEmployee(Employee employee)
    {
        var index = _employees.FindIndex(e => e.Id == employee.Id);
        if (index < 0) return false;
        _employees[index] = employee;
        return true;
    }

    public bool DeleteEmployee(Guid employeeId)
    {
        return _employees.RemoveAll(e => e.Id == employeeId) > 0;
    }

    public Employee? FindEmployeeByCode(string employeeCode)
        => _employees.FirstOrDefault(e => string.Equals(e.EmployeeCode, employeeCode, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<Device> GetDevices() => _devices;

    public Device AddDevice(Device device)
    {
        _devices.Add(device);
        return device;
    }

    public Device? FindDeviceById(Guid deviceId)
        => _devices.FirstOrDefault(d => d.Id == deviceId);

    public Device? FindDeviceBySerial(string serialNumber)
        => _devices.FirstOrDefault(d => string.Equals(d.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));

    public bool UpdateDevice(Device device)
    {
        var index = _devices.FindIndex(d => d.Id == device.Id);
        if (index < 0) return false;
        _devices[index] = device;
        return true;
    }

    public bool DeleteDevice(Guid deviceId)
    {
        return _devices.RemoveAll(d => d.Id == deviceId) > 0;
    }

    public IReadOnlyList<AttendanceRecord> GetAttendance(DateOnly? from, DateOnly? to)
    {
        IEnumerable<AttendanceRecord> query = _records;
        if (from.HasValue) query = query.Where(r => DateOnly.FromDateTime(r.Timestamp) >= from.Value);
        if (to.HasValue) query = query.Where(r => DateOnly.FromDateTime(r.Timestamp) <= to.Value);
        return query.OrderByDescending(r => r.Timestamp).ToList();
    }

    public AttendanceRecord AddAttendance(AttendanceRecord record)
    {
        _records.Add(record);
        return record;
    }

    public bool UpdateAttendance(AttendanceRecord record)
    {
        var index = _records.FindIndex(r => r.Id == record.Id);
        if (index < 0) return false;
        _records[index] = record;
        return true;
    }

    public bool DeleteAttendance(Guid attendanceId)
    {
        return _records.RemoveAll(r => r.Id == attendanceId) > 0;
    }

    public IReadOnlyList<DeviceCommand> GetPendingCommands(string deviceSerialNumber, int maxCount)
    {
        return _commands
            .Where(c => c.DeviceSerialNumber.Equals(deviceSerialNumber, StringComparison.OrdinalIgnoreCase) && c.SentUtc is null)
            .OrderBy(c => c.CreatedUtc)
            .Take(maxCount)
            .ToList();
    }

    public IReadOnlyList<DeviceCommand> GetDeviceCommands(string deviceSerialNumber, int maxCount)
    {
        return _commands
            .Where(c => c.DeviceSerialNumber.Equals(deviceSerialNumber, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.CreatedUtc)
            .Take(maxCount)
            .ToList();
    }

    public DeviceCommand EnqueueCommand(DeviceCommand command)
    {
        _commands.Add(command);
        return command;
    }

    public void MarkCommandSent(Guid commandId)
    {
        var command = _commands.FirstOrDefault(c => c.Id == commandId);
        if (command is not null) command.SentUtc = DateTime.UtcNow;
    }

    public void SaveCommandReply(Guid commandId, string replyText)
    {
        var command = _commands.FirstOrDefault(c => c.Id == commandId);
        if (command is not null) command.ReplyText = replyText;
    }

    public int ClearPendingCommands(string deviceSerialNumber)
    {
        return _commands.RemoveAll(c =>
            c.SentUtc is null &&
            c.DeviceSerialNumber.Equals(deviceSerialNumber, StringComparison.OrdinalIgnoreCase));
    }

    public bool DeleteDeviceCommand(Guid commandId)
    {
        return _commands.RemoveAll(c => c.Id == commandId) > 0;
    }

    public int ResetStaleSentCommands(string deviceSerialNumber, int olderThanMinutes)
    {
        var threshold = DateTime.UtcNow.AddMinutes(-olderThanMinutes);
        var stale = _commands.Where(c =>
            c.DeviceSerialNumber.Equals(deviceSerialNumber, StringComparison.OrdinalIgnoreCase) &&
            c.SentUtc is not null &&
            c.ReplyText is null &&
            c.SentUtc < threshold).ToList();
        foreach (var c in stale) c.SentUtc = null;
        return stale.Count;
    }

    public SyncLog AddSyncLog(SyncLog log)
    {
        _syncLogs.Add(log);
        return log;
    }

    public IReadOnlyList<SyncLog> GetSyncLogs(string deviceSerialNumber, int maxCount)
    {
        return _syncLogs
            .Where(s => s.DeviceSerialNumber.Equals(deviceSerialNumber, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.CreatedUtc)
            .Take(maxCount)
            .ToList();
    }

    public void UpdateSyncLogResult(Guid syncLogId, SyncStatus status, string responseText)
    {
        var row = _syncLogs.FirstOrDefault(s => s.Id == syncLogId);
        if (row is null) return;
        row.Status = status;
        row.ResponseText = responseText;
        row.CompletedUtc = DateTime.UtcNow;
    }

    public IReadOnlyDictionary<string, long> GetDeviceTableStamps(string deviceSerialNumber)
    {
        if (string.IsNullOrWhiteSpace(deviceSerialNumber))
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var serial = deviceSerialNumber.Trim();
        return _deviceTableStamps.TryGetValue(serial, out var stamps)
            ? new Dictionary<string, long>(stamps, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    }

    public void UpsertDeviceTableStamp(string deviceSerialNumber, string tableName, long stampValue)
    {
        if (string.IsNullOrWhiteSpace(deviceSerialNumber) || string.IsNullOrWhiteSpace(tableName)) return;
        var serial = deviceSerialNumber.Trim();
        if (!_deviceTableStamps.TryGetValue(serial, out var stamps))
        {
            stamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            _deviceTableStamps[serial] = stamps;
        }
        stamps[tableName.Trim().ToUpperInvariant()] = stampValue;
    }

    // Timetable methods
    public TimetableEntity? GetTimetableById(int timetableId) => null;
    public IReadOnlyList<TimetableEntity> GetAllTimetables() => Array.Empty<TimetableEntity>();
    public IReadOnlyList<TimetableDayEntity> GetTimetableDays(int timetableId) => Array.Empty<TimetableDayEntity>();
    public IReadOnlyList<EmployeeTimetableEntity> GetEmployeeTimetables(int timetableId) => Array.Empty<EmployeeTimetableEntity>();
    public IReadOnlyList<DepartmentTimetableEntity> GetDepartmentTimetables(int timetableId) => Array.Empty<DepartmentTimetableEntity>();
    public IReadOnlyList<DepartmentTimetableEntity> GetAllDepartmentTimetables() => Array.Empty<DepartmentTimetableEntity>();
    public IReadOnlyList<EmployeeTimetableEntity> GetEmployeeTimetablesByPersonnel(Guid personnelId) => Array.Empty<EmployeeTimetableEntity>();
    public IReadOnlyList<DepartmentTimetableEntity> GetDepartmentTimetablesByDepartment(int departmentId) => Array.Empty<DepartmentTimetableEntity>();
    
    public IReadOnlyList<HolidayEntity> GetAllHolidays() => Array.Empty<HolidayEntity>();
    public IReadOnlyList<AccCombinationEntity> GetAllAccCombinations() => Array.Empty<AccCombinationEntity>();

    // ===== AREA CRUD =====
    public IReadOnlyList<Area> GetAreas() => _areas;
    public Area? GetAreaById(int areaId) => _areas.FirstOrDefault(a => a.Id == areaId);
    public Area AddArea(Area area) { _areas.Add(area); return area; }
    public bool UpdateArea(Area area) { var i = _areas.FindIndex(a => a.Id == area.Id); if (i < 0) return false; _areas[i] = area; return true; }
    public bool DeleteArea(int areaId) => _areas.RemoveAll(a => a.Id == areaId) > 0;

    // ===== DEPARTMENT CRUD =====
    public IReadOnlyList<Department> GetDepartments() => _departments;
    public IReadOnlyList<Department> GetDepartmentsByArea(int areaId) => _departments.Where(d => d.AreaId == areaId).ToList();
    public Department? GetDepartmentById(int departmentId) => _departments.FirstOrDefault(d => d.Id == departmentId);
    public Department AddDepartment(Department department) { _departments.Add(department); return department; }
    public bool UpdateDepartment(Department department) { var i = _departments.FindIndex(d => d.Id == department.Id); if (i < 0) return false; _departments[i] = department; return true; }
    public bool DeleteDepartment(int departmentId) => _departments.RemoveAll(d => d.Id == departmentId) > 0;

    // ===== EMPLOYEE-DEPARTMENT MAPPING =====
    public IReadOnlyList<EmployeeDepartment> GetEmployeeDepartments(Guid employeeId) => _employeeDepartments.Where(ed => ed.EmployeeId == employeeId).ToList();
    public IReadOnlyList<EmployeeDepartment> GetDepartmentEmployees(int departmentId) => _employeeDepartments.Where(ed => ed.DepartmentId == departmentId).ToList();
    public bool AddEmployeeDepartment(EmployeeDepartment employeeDepartment) { _employeeDepartments.Add(employeeDepartment); return true; }
    public bool RemoveEmployeeDepartment(Guid employeeId, int departmentId) => _employeeDepartments.RemoveAll(ed => ed.EmployeeId == employeeId && ed.DepartmentId == departmentId) > 0;
    public bool RemoveAllEmployeeDepartments(Guid employeeId) => _employeeDepartments.RemoveAll(ed => ed.EmployeeId == employeeId) > 0;
    public bool ValidateEmployeeSameArea(Guid employeeId, int newDepartmentId, int newAreaId) => true;

    // ===== EMPLOYEE EXTENSIONS =====
    public IReadOnlyList<Employee> GetEmployeesByDepartment(int departmentId) => _employees.Where(e => _employeeDepartments.Any(ed => ed.EmployeeId == e.Id && ed.DepartmentId == departmentId)).ToList();
    public IReadOnlyList<Employee> GetEmployeesByArea(int areaId) => _employees.Where(e => _employeeDepartments.Any(ed => ed.EmployeeId == e.Id && _departments.Any(d => d.Id == ed.DepartmentId && d.AreaId == areaId))).ToList();

    // ===== DEVICE EXTENSIONS =====
    public IReadOnlyList<Device> GetDevicesByAreaDepartment(int areaId, int departmentId) => _devices.Where(d => d.AreaId == areaId && d.DepartmentId == departmentId).ToList();

    // ===== ATTENDANCE EXTENSIONS =====
    public IReadOnlyList<AttendanceRecord> GetAttendanceByAreaDepartment(int areaId, int departmentId, DateOnly? from, DateOnly? to)
    {
        IEnumerable<AttendanceRecord> query = _records.Where(r => r.AreaId == areaId && r.DepartmentId == departmentId);
        if (from.HasValue) query = query.Where(r => DateOnly.FromDateTime(r.Timestamp) >= from.Value);
        if (to.HasValue) query = query.Where(r => DateOnly.FromDateTime(r.Timestamp) <= to.Value);
        return query.OrderByDescending(r => r.Timestamp).ToList();
    }

    // ===== SYNC QUEUE =====
    public SyncQueueEntry EnqueueSync(SyncQueueEntry entry) { _syncQueue.Add(entry); return entry; }
    public IReadOnlyList<SyncQueueEntry> GetPendingSync(int maxCount = 100) => _syncQueue.Where(s => s.Status == SyncQueueStatus.Pending).OrderBy(s => s.CreatedAt).Take(maxCount).ToList();
    public IReadOnlyList<SyncQueueEntry> GetFailedSync(int maxCount = 100) => _syncQueue.Where(s => s.Status == SyncQueueStatus.Failed && s.RetryCount < s.MaxRetries).OrderBy(s => s.NextRetryAt).Take(maxCount).ToList();
    public bool UpdateSyncQueueStatus(long syncQueueId, SyncQueueStatus status, string? errorMessage = null) { var s = _syncQueue.FirstOrDefault(x => x.Id == syncQueueId); if (s == null) return false; s.Status = status; s.ErrorMessage = errorMessage; if (status == SyncQueueStatus.Success || status == SyncQueueStatus.Failed) s.ProcessedAt = DateTime.UtcNow; return true; }
    public bool IncrementSyncRetryCount(long syncQueueId) { var s = _syncQueue.FirstOrDefault(x => x.Id == syncQueueId); if (s == null) return false; s.RetryCount++; s.Status = SyncQueueStatus.Pending; s.NextRetryAt = DateTime.UtcNow.AddMinutes(5); return true; }
    public int ProcessSyncQueue(int batchSize = 50) { var pending = GetPendingSync(batchSize); foreach (var e in pending) UpdateSyncQueueStatus(e.Id, SyncQueueStatus.Success); return pending.Count; }
    public int RetryFailedSync(int maxRetries = 3) { var failed = GetFailedSync(100); int count = 0; foreach (var e in failed) { if (e.RetryCount < maxRetries) { UpdateSyncQueueStatus(e.Id, SyncQueueStatus.Pending); count++; } } return count; }

    public void RunInTransaction(Action action) => action();
    public T RunInTransaction<T>(Func<T> action) => action();
}