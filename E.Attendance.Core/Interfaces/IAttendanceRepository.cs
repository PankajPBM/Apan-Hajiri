using E.Attendance.Core.Models;

namespace E.Attendance.Core.Interfaces;

public interface IAttendanceRepository
{
    IReadOnlyList<Employee> GetEmployees();
    Employee AddEmployee(Employee employee);
    bool UpdateEmployee(Employee employee);
    bool DeleteEmployee(Guid employeeId);
    Employee? FindEmployeeByCode(string employeeCode);

    IReadOnlyList<Device> GetDevices();
    Device AddDevice(Device device);
    Device? FindDeviceById(Guid deviceId);
    Device? FindDeviceBySerial(string serialNumber);
    bool UpdateDevice(Device device);
    bool DeleteDevice(Guid deviceId);

    IReadOnlyList<AttendanceRecord> GetAttendance(DateOnly? from, DateOnly? to);
    AttendanceRecord AddAttendance(AttendanceRecord record);
    bool UpdateAttendance(AttendanceRecord record);
    bool DeleteAttendance(Guid attendanceId);

    IReadOnlyList<DeviceCommand> GetPendingCommands(string deviceSerialNumber, int maxCount);
    IReadOnlyList<DeviceCommand> GetDeviceCommands(string deviceSerialNumber, int maxCount);
    DeviceCommand EnqueueCommand(DeviceCommand command);
    void MarkCommandSent(Guid commandId);
    void SaveCommandReply(Guid commandId, string replyText);
    int ClearPendingCommands(string deviceSerialNumber);

    SyncLog AddSyncLog(SyncLog log);
    IReadOnlyList<SyncLog> GetSyncLogs(string deviceSerialNumber, int maxCount);
    void UpdateSyncLogResult(Guid syncLogId, SyncStatus status, string responseText);

    IReadOnlyDictionary<string, long> GetDeviceTableStamps(string deviceSerialNumber);
    void UpsertDeviceTableStamp(string deviceSerialNumber, string tableName, long stampValue);
}
