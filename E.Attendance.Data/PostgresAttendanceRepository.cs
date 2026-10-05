using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;

namespace E.Attendance.Data;

public sealed class PostgresAttendanceRepository : IAttendanceRepository
{
    public readonly SqliteAttendanceRepository _inner;

    public PostgresAttendanceRepository(string postgresConnectionString)
    {
        _inner = new SqliteAttendanceRepository(postgresConnectionString, usePostgreSql: true);
    }

    // Area methods
    public IReadOnlyList<Area> GetAreas() => _inner.GetAreas();
    public Area? GetAreaById(int areaId) => _inner.GetAreaById(areaId);
    public Area AddArea(Area area) => _inner.AddArea(area);
    public bool UpdateArea(Area area) => _inner.UpdateArea(area);
    public bool DeleteArea(int areaId) => _inner.DeleteArea(areaId);

    // Department methods
    public IReadOnlyList<Department> GetDepartments() => _inner.GetDepartments();
    public IReadOnlyList<Department> GetDepartmentsByArea(int areaId) => _inner.GetDepartmentsByArea(areaId);
    public Department? GetDepartmentById(int departmentId) => _inner.GetDepartmentById(departmentId);
    public Department AddDepartment(Department department) => _inner.AddDepartment(department);
    public bool UpdateDepartment(Department department) => _inner.UpdateDepartment(department);
    public bool DeleteDepartment(int departmentId) => _inner.DeleteDepartment(departmentId);

    // EmployeeDepartment mapping methods
    public IReadOnlyList<EmployeeDepartment> GetEmployeeDepartments(Guid employeeId) => _inner.GetEmployeeDepartments(employeeId);
    public IReadOnlyList<EmployeeDepartment> GetDepartmentEmployees(int departmentId) => _inner.GetDepartmentEmployees(departmentId);
    public bool AddEmployeeDepartment(EmployeeDepartment employeeDepartment) => _inner.AddEmployeeDepartment(employeeDepartment);
    public bool RemoveEmployeeDepartment(Guid employeeId, int departmentId) => _inner.RemoveEmployeeDepartment(employeeId, departmentId);
    public bool RemoveAllEmployeeDepartments(Guid employeeId) => _inner.RemoveAllEmployeeDepartments(employeeId);
    public bool ValidateEmployeeSameArea(Guid employeeId, int newDepartmentId, int newAreaId) => _inner.ValidateEmployeeSameArea(employeeId, newDepartmentId, newAreaId);

    // Employee methods
    public IReadOnlyList<Employee> GetEmployees() => _inner.GetEmployees();
    public Employee AddEmployee(Employee employee) => _inner.AddEmployee(employee);
    public bool UpdateEmployee(Employee employee) => _inner.UpdateEmployee(employee);
    public bool DeleteEmployee(Guid employeeId) => _inner.DeleteEmployee(employeeId);
    public Employee? FindEmployeeByCode(string employeeCode) => _inner.FindEmployeeByCode(employeeCode);
    public IReadOnlyList<Employee> GetEmployeesByDepartment(int departmentId) => _inner.GetEmployeesByDepartment(departmentId);
    public IReadOnlyList<Employee> GetEmployeesByArea(int areaId) => _inner.GetEmployeesByArea(areaId);

    // Device methods
    public IReadOnlyList<Device> GetDevices() => _inner.GetDevices();
    public IReadOnlyList<Device> GetDevicesByAreaDepartment(int areaId, int departmentId) => _inner.GetDevicesByAreaDepartment(areaId, departmentId);
    public Device AddDevice(Device device) => _inner.AddDevice(device);
    public Device? FindDeviceById(Guid deviceId) => _inner.FindDeviceById(deviceId);
    public Device? FindDeviceBySerial(string serialNumber) => _inner.FindDeviceBySerial(serialNumber);
    public bool UpdateDevice(Device device) => _inner.UpdateDevice(device);
    public bool DeleteDevice(Guid deviceId) => _inner.DeleteDevice(deviceId);

    // Attendance methods
    public IReadOnlyList<AttendanceRecord> GetAttendance(DateOnly? from, DateOnly? to) => _inner.GetAttendance(from, to);
    public IReadOnlyList<AttendanceRecord> GetAttendanceByAreaDepartment(int areaId, int departmentId, DateOnly? from, DateOnly? to) => _inner.GetAttendanceByAreaDepartment(areaId, departmentId, from, to);
    public AttendanceRecord AddAttendance(AttendanceRecord record) => _inner.AddAttendance(record);
    public bool UpdateAttendance(AttendanceRecord record) => _inner.UpdateAttendance(record);
    public bool DeleteAttendance(Guid attendanceId) => _inner.DeleteAttendance(attendanceId);

    // Device Commands
    public IReadOnlyList<DeviceCommand> GetPendingCommands(string deviceSerialNumber, int maxCount) => _inner.GetPendingCommands(deviceSerialNumber, maxCount);
    public IReadOnlyList<DeviceCommand> GetDeviceCommands(string deviceSerialNumber, int maxCount) => _inner.GetDeviceCommands(deviceSerialNumber, maxCount);
    public DeviceCommand EnqueueCommand(DeviceCommand command) => _inner.EnqueueCommand(command);
    public void MarkCommandSent(Guid commandId) => _inner.MarkCommandSent(commandId);
    public void SaveCommandReply(Guid commandId, string replyText) => _inner.SaveCommandReply(commandId, replyText);
    public int ClearPendingCommands(string deviceSerialNumber) => _inner.ClearPendingCommands(deviceSerialNumber);
    public bool DeleteDeviceCommand(Guid commandId) => _inner.DeleteDeviceCommand(commandId);
    public int ResetStaleSentCommands(string deviceSerialNumber, int olderThanMinutes) => _inner.ResetStaleSentCommands(deviceSerialNumber, olderThanMinutes);

    // Sync Logs
    public SyncLog AddSyncLog(SyncLog log) => _inner.AddSyncLog(log);
    public IReadOnlyList<SyncLog> GetSyncLogs(string deviceSerialNumber, int maxCount) => _inner.GetSyncLogs(deviceSerialNumber, maxCount);
    public void UpdateSyncLogResult(Guid syncLogId, SyncStatus status, string responseText) => _inner.UpdateSyncLogResult(syncLogId, status, responseText);

    // Device Table Stamps
    public IReadOnlyDictionary<string, long> GetDeviceTableStamps(string deviceSerialNumber) => _inner.GetDeviceTableStamps(deviceSerialNumber);
    public void UpsertDeviceTableStamp(string deviceSerialNumber, string tableName, long stampValue) => _inner.UpsertDeviceTableStamp(deviceSerialNumber, tableName, stampValue);

    // Sync Queue methods
    public SyncQueueEntry EnqueueSync(SyncQueueEntry entry) => _inner.EnqueueSync(entry);
    public IReadOnlyList<SyncQueueEntry> GetPendingSync(int maxCount = 100) => _inner.GetPendingSync(maxCount);
    public IReadOnlyList<SyncQueueEntry> GetFailedSync(int maxCount = 100) => _inner.GetFailedSync(maxCount);
    public bool UpdateSyncQueueStatus(long syncQueueId, SyncQueueStatus status, string? errorMessage = null) => _inner.UpdateSyncQueueStatus(syncQueueId, status, errorMessage);
    public bool IncrementSyncRetryCount(long syncQueueId) => _inner.IncrementSyncRetryCount(syncQueueId);
    public int ProcessSyncQueue(int batchSize = 50) => _inner.ProcessSyncQueue(batchSize);
    public int RetryFailedSync(int maxRetries = 3) => _inner.RetryFailedSync(maxRetries);

    // Unified User methods
    public void UpsertUnifiedUser(
        string pin,
        string? name,
        int? privilege,
        string? password,
        string? primaryCard,
        string? secondaryCard,
        int? groupId,
        string? timezoneProfile,
        int? verifyMode,
        DateTime? validFrom,
        DateTime? validTo,
        bool? expiresEnabled,
        string? phone,
        int? gender,
        int? nation,
        string? idNumber)
        => _inner.UpsertUnifiedUser(pin, name, privilege, password, primaryCard, secondaryCard, groupId, timezoneProfile, verifyMode, validFrom, validTo, expiresEnabled, phone, gender, nation, idNumber);

    public bool DeleteUnifiedUser(string pin) => _inner.DeleteUnifiedUser(pin);
    public int DeleteEmployeeArtifactsByCode(string employeeCode) => _inner.DeleteEmployeeArtifactsByCode(employeeCode);

    public void UpsertBiometricTemplate(
        string userPin,
        int type,
        int no,
        int idx,
        bool valid,
        bool duress,
        int? majorVer,
        int? minorVer,
        int? format,
        string templateData)
        => _inner.UpsertBiometricTemplate(userPin, type, no, idx, valid, duress, majorVer, minorVer, format, templateData);

    public void UpsertBiometricPhoto(
        string userPin,
        int type,
        int no,
        int idx,
        int? format,
        string content)
        => _inner.UpsertBiometricPhoto(userPin, type, no, idx, format, content);

    public void RunInTransaction(Action action)
        => _inner.RunInTransaction(action);

    public T RunInTransaction<T>(Func<T> action)
        => _inner.RunInTransaction(action);

    public IReadOnlyDictionary<string, UnifiedUserSnapshot> GetUnifiedUsersByPins(IEnumerable<string> pins)
        => _inner.GetUnifiedUsersByPins(pins);
    public IReadOnlyList<UnifiedUserSnapshot> GetAllUnifiedUsers()
        => _inner.GetAllUnifiedUsers();

    public IReadOnlyDictionary<string, List<BiometricTemplateSnapshot>> GetBiometricTemplatesByPins(IEnumerable<string> pins)
        => _inner.GetBiometricTemplatesByPins(pins);

    public IReadOnlyDictionary<string, List<BiometricPhotoSnapshot>> GetBiometricPhotosByPins(IEnumerable<string> pins)
        => _inner.GetBiometricPhotosByPins(pins);

    // Timetable methods
    public TimetableEntity? GetTimetableById(int timetableId)
        => _inner.GetTimetableById(timetableId);

    public IReadOnlyList<TimetableEntity> GetAllTimetables()
    {
        try { return _inner.GetAllTimetables(); }
        catch { return Array.Empty<TimetableEntity>(); }
    }

    public IReadOnlyList<TimetableDayEntity> GetTimetableDays(int timetableId)
        => _inner.GetTimetableDays(timetableId);

    public IReadOnlyList<EmployeeTimetableEntity> GetEmployeeTimetables(int timetableId)
        => _inner.GetEmployeeTimetables(timetableId);

    public IReadOnlyList<DepartmentTimetableEntity> GetDepartmentTimetables(int timetableId)
    {
        try { return _inner.GetDepartmentTimetables(timetableId); }
        catch { return Array.Empty<DepartmentTimetableEntity>(); }
    }

    public IReadOnlyList<DepartmentTimetableEntity> GetAllDepartmentTimetables()
    {
        try { return _inner.GetAllDepartmentTimetables(); }
        catch { return Array.Empty<DepartmentTimetableEntity>(); }
    }

    public IReadOnlyList<EmployeeTimetableEntity> GetEmployeeTimetablesByPersonnel(Guid personnelId)
    {
        try { return _inner.GetEmployeeTimetablesByPersonnel(personnelId); }
        catch { return Array.Empty<EmployeeTimetableEntity>(); }
    }

    public IReadOnlyList<DepartmentTimetableEntity> GetDepartmentTimetablesByDepartment(int departmentId)
    {
        try { return _inner.GetDepartmentTimetablesByDepartment(departmentId); }
        catch { return Array.Empty<DepartmentTimetableEntity>(); }
    }

    public IReadOnlyList<HolidayEntity> GetAllHolidays()
    {
        try { return _inner.GetAllHolidays(); }
        catch { return Array.Empty<HolidayEntity>(); }
    }

    public IReadOnlyList<AccCombinationEntity> GetAllAccCombinations()
    {
        try { return _inner.GetAllAccCombinations(); }
        catch { return Array.Empty<AccCombinationEntity>(); }
    }
}