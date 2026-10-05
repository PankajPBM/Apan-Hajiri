using E.Attendance.Core.Models;

namespace E.Attendance.Core.Interfaces;

public interface IAttendanceRepository
{
    // Area methods
    IReadOnlyList<Area> GetAreas();
    Area? GetAreaById(int areaId);
    Area AddArea(Area area);
    bool UpdateArea(Area area);
    bool DeleteArea(int areaId);

    // Department methods
    IReadOnlyList<Department> GetDepartments();
    IReadOnlyList<Department> GetDepartmentsByArea(int areaId);
    Department? GetDepartmentById(int departmentId);
    Department AddDepartment(Department department);
    bool UpdateDepartment(Department department);
    bool DeleteDepartment(int departmentId);

    // EmployeeDepartment mapping methods
    IReadOnlyList<EmployeeDepartment> GetEmployeeDepartments(Guid employeeId);
    IReadOnlyList<EmployeeDepartment> GetDepartmentEmployees(int departmentId);
    bool AddEmployeeDepartment(EmployeeDepartment employeeDepartment);
    bool RemoveEmployeeDepartment(Guid employeeId, int departmentId);
    bool RemoveAllEmployeeDepartments(Guid employeeId);
    bool ValidateEmployeeSameArea(Guid employeeId, int newDepartmentId, int newAreaId);

    // Employee methods (updated to use mapping)
    IReadOnlyList<Employee> GetEmployees();
    Employee AddEmployee(Employee employee);
    bool UpdateEmployee(Employee employee);
    bool DeleteEmployee(Guid employeeId);
    Employee? FindEmployeeByCode(string employeeCode);
    IReadOnlyList<Employee> GetEmployeesByDepartment(int departmentId);
    IReadOnlyList<Employee> GetEmployeesByArea(int areaId);

    // Device methods (updated with Area/Department validation)
    IReadOnlyList<Device> GetDevices();
    IReadOnlyList<Device> GetDevicesByAreaDepartment(int areaId, int departmentId);
    Device AddDevice(Device device);
    Device? FindDeviceById(Guid deviceId);
    Device? FindDeviceBySerial(string serialNumber);
    bool UpdateDevice(Device device);
    bool DeleteDevice(Guid deviceId);

    // Attendance methods (updated with AreaId/DepartmentId)
    IReadOnlyList<AttendanceRecord> GetAttendance(DateOnly? from, DateOnly? to);
    IReadOnlyList<AttendanceRecord> GetAttendanceByAreaDepartment(int areaId, int departmentId, DateOnly? from, DateOnly? to);
    AttendanceRecord AddAttendance(AttendanceRecord record);
    bool UpdateAttendance(AttendanceRecord record);
    bool DeleteAttendance(Guid attendanceId);

    // Device Commands
    IReadOnlyList<DeviceCommand> GetPendingCommands(string deviceSerialNumber, int maxCount);
    IReadOnlyList<DeviceCommand> GetDeviceCommands(string deviceSerialNumber, int maxCount);
    DeviceCommand EnqueueCommand(DeviceCommand command);
    void MarkCommandSent(Guid commandId);
    void SaveCommandReply(Guid commandId, string replyText);
    int ClearPendingCommands(string deviceSerialNumber);
    bool DeleteDeviceCommand(Guid commandId);
    int ResetStaleSentCommands(string deviceSerialNumber, int olderThanMinutes);

    // Sync Logs
    SyncLog AddSyncLog(SyncLog log);
    IReadOnlyList<SyncLog> GetSyncLogs(string deviceSerialNumber, int maxCount);
    void UpdateSyncLogResult(Guid syncLogId, SyncStatus status, string responseText);

    // Device Table Stamps
    IReadOnlyDictionary<string, long> GetDeviceTableStamps(string deviceSerialNumber);
    void UpsertDeviceTableStamp(string deviceSerialNumber, string tableName, long stampValue);

    // Sync Queue methods
    SyncQueueEntry EnqueueSync(SyncQueueEntry entry);
    IReadOnlyList<SyncQueueEntry> GetPendingSync(int maxCount = 100);
    IReadOnlyList<SyncQueueEntry> GetFailedSync(int maxCount = 100);
    bool UpdateSyncQueueStatus(long syncQueueId, SyncQueueStatus status, string? errorMessage = null);
    bool IncrementSyncRetryCount(long syncQueueId);
    int ProcessSyncQueue(int batchSize = 50);
    int RetryFailedSync(int maxRetries = 3);

    // Timetable methods
    TimetableEntity? GetTimetableById(int timetableId);
    IReadOnlyList<TimetableEntity> GetAllTimetables();
    IReadOnlyList<TimetableDayEntity> GetTimetableDays(int timetableId);
    IReadOnlyList<EmployeeTimetableEntity> GetEmployeeTimetables(int timetableId);
    IReadOnlyList<DepartmentTimetableEntity> GetDepartmentTimetables(int timetableId);
    IReadOnlyList<DepartmentTimetableEntity> GetAllDepartmentTimetables();
    IReadOnlyList<EmployeeTimetableEntity> GetEmployeeTimetablesByPersonnel(Guid personnelId);
    IReadOnlyList<DepartmentTimetableEntity> GetDepartmentTimetablesByDepartment(int departmentId);
    
    // Holiday and Access Control methods
    IReadOnlyList<HolidayEntity> GetAllHolidays();
    IReadOnlyList<AccCombinationEntity> GetAllAccCombinations();

    // Transaction support
    void RunInTransaction(Action action);
    T RunInTransaction<T>(Func<T> action);
}

// Sync Queue models
public sealed class SyncQueueEntry
{
    public long Id { get; init; }
    public string EntityType { get; set; } = string.Empty; // Employee, Device, Area, Department, Biometric, Attendance
    public string EntityId { get; set; } = string.Empty;   // Guid or int as string
    public string Operation { get; set; } = string.Empty;  // Insert, Update, Delete
    public SyncQueueStatus Status { get; set; } = SyncQueueStatus.Pending;
    public int RetryCount { get; set; } = 0;
    public int MaxRetries { get; set; } = 3;
    public string? Payload { get; set; } // JSON serialized entity
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
}

public enum SyncQueueStatus
{
    Pending = 0,
    Processing = 1,
    Success = 2,
    Failed = 3
}