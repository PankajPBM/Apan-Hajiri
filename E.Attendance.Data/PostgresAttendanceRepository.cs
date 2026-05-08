using E.Attendance.Core.Interfaces;

namespace E.Attendance.Data;

public sealed class PostgresAttendanceRepository : IAttendanceRepository
{
    private readonly SqliteAttendanceRepository _inner;

    public PostgresAttendanceRepository(string postgresConnectionString)
    {
        _inner = new SqliteAttendanceRepository(postgresConnectionString, usePostgreSql: true);
    }

    public IReadOnlyList<E.Attendance.Core.Models.Employee> GetEmployees() => _inner.GetEmployees();
    public E.Attendance.Core.Models.Employee AddEmployee(E.Attendance.Core.Models.Employee employee) => _inner.AddEmployee(employee);
    public bool UpdateEmployee(E.Attendance.Core.Models.Employee employee) => _inner.UpdateEmployee(employee);
    public bool DeleteEmployee(Guid employeeId) => _inner.DeleteEmployee(employeeId);
    public E.Attendance.Core.Models.Employee? FindEmployeeByCode(string employeeCode) => _inner.FindEmployeeByCode(employeeCode);
    public IReadOnlyList<E.Attendance.Core.Models.Device> GetDevices() => _inner.GetDevices();
    public E.Attendance.Core.Models.Device AddDevice(E.Attendance.Core.Models.Device device) => _inner.AddDevice(device);
    public E.Attendance.Core.Models.Device? FindDeviceById(Guid deviceId) => _inner.FindDeviceById(deviceId);
    public E.Attendance.Core.Models.Device? FindDeviceBySerial(string serialNumber) => _inner.FindDeviceBySerial(serialNumber);
    public bool UpdateDevice(E.Attendance.Core.Models.Device device) => _inner.UpdateDevice(device);
    public bool DeleteDevice(Guid deviceId) => _inner.DeleteDevice(deviceId);
    public IReadOnlyList<E.Attendance.Core.Models.AttendanceRecord> GetAttendance(DateOnly? from, DateOnly? to) => _inner.GetAttendance(from, to);
    public E.Attendance.Core.Models.AttendanceRecord AddAttendance(E.Attendance.Core.Models.AttendanceRecord record) => _inner.AddAttendance(record);
    public bool UpdateAttendance(E.Attendance.Core.Models.AttendanceRecord record) => _inner.UpdateAttendance(record);
    public bool DeleteAttendance(Guid attendanceId) => _inner.DeleteAttendance(attendanceId);
    public IReadOnlyList<E.Attendance.Core.Models.DeviceCommand> GetPendingCommands(string deviceSerialNumber, int maxCount) => _inner.GetPendingCommands(deviceSerialNumber, maxCount);
    public IReadOnlyList<E.Attendance.Core.Models.DeviceCommand> GetDeviceCommands(string deviceSerialNumber, int maxCount) => _inner.GetDeviceCommands(deviceSerialNumber, maxCount);
    public E.Attendance.Core.Models.DeviceCommand EnqueueCommand(E.Attendance.Core.Models.DeviceCommand command) => _inner.EnqueueCommand(command);
    public void MarkCommandSent(Guid commandId) => _inner.MarkCommandSent(commandId);
    public void SaveCommandReply(Guid commandId, string replyText) => _inner.SaveCommandReply(commandId, replyText);
    public int ClearPendingCommands(string deviceSerialNumber) => _inner.ClearPendingCommands(deviceSerialNumber);
    public E.Attendance.Core.Models.SyncLog AddSyncLog(E.Attendance.Core.Models.SyncLog log) => _inner.AddSyncLog(log);
    public IReadOnlyList<E.Attendance.Core.Models.SyncLog> GetSyncLogs(string deviceSerialNumber, int maxCount) => _inner.GetSyncLogs(deviceSerialNumber, maxCount);
    public void UpdateSyncLogResult(Guid syncLogId, E.Attendance.Core.Models.SyncStatus status, string responseText) => _inner.UpdateSyncLogResult(syncLogId, status, responseText);
    public IReadOnlyDictionary<string, long> GetDeviceTableStamps(string deviceSerialNumber) => _inner.GetDeviceTableStamps(deviceSerialNumber);
    public void UpsertDeviceTableStamp(string deviceSerialNumber, string tableName, long stampValue) => _inner.UpsertDeviceTableStamp(deviceSerialNumber, tableName, stampValue);

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
}
