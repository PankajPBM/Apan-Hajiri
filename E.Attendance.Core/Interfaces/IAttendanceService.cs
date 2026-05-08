using E.Attendance.Core.Models;

namespace E.Attendance.Core.Interfaces;

public interface IAttendanceService
{
    IEnumerable<Employee> ListEmployees();
    Employee CreateEmployee(string employeeCode, string fullName, string department);
    bool UpdateEmployeeDepartment(Guid employeeId, string department);
    bool DeactivateEmployee(Guid employeeId);
    Employee GetOrCreateEmployee(string employeeCode);

    IEnumerable<Device> ListDevices();
    Device RegisterDevice(string serialNumber, string name, string ipAddress, string location, string? model = null);
    bool UpdateDeviceInfo(Guid deviceId, string? name, string? model, string? ipAddress, string? location, bool? isRegistrationDevice = null);
    bool ConnectDevice(Guid deviceId);
    bool DisconnectDevice(Guid deviceId);
    bool DeleteDevice(Guid deviceId);
    Device TouchDevice(string serialNumber, string? ipAddress);
    void UpdateDeviceCapabilities(
        string serialNumber,
        bool visibleLight,
        bool infrared,
        bool fingerprint,
        string? faceAlgorithmVersion = null,
        string? fingerprintAlgorithmVersion = null,
        string? model = null,
        string? timeZone = null);
    void UpdateDeviceProtocolInfo(
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
        bool? supportsFingerVein = null);
    IEnumerable<Device> SearchDevices(string? query);
    DeviceCommand QueueDeviceCommand(string deviceSerialNumber, string commandText);
    IReadOnlyList<DeviceCommand> GetPendingCommands(string deviceSerialNumber, int maxCount);
    IReadOnlyList<DeviceCommand> GetDeviceCommands(string deviceSerialNumber, int maxCount);
    void MarkCommandSent(Guid commandId);
    void RecordCommandReply(Guid commandId, string replyText);
    int ClearPendingCommands(string deviceSerialNumber);
    SyncLog AddSyncLog(string deviceSerialNumber, SyncType type, string commandText, SyncStatus status = SyncStatus.Pending);
    IReadOnlyList<SyncLog> GetSyncLogs(string deviceSerialNumber, int maxCount);
    void UpdateSyncLogResult(Guid syncLogId, SyncStatus status, string responseText);

    IReadOnlyDictionary<string, long> GetDeviceTableStamps(string deviceSerialNumber);
    void UpsertDeviceTableStamp(string deviceSerialNumber, string tableName, long stampValue);

    IEnumerable<AttendanceRecord> ListAttendance(DateOnly? from, DateOnly? to);
    AttendanceRecord ClockIn(Guid employeeId, Guid? deviceId = null);
    AttendanceRecord ClockOut(Guid employeeId, Guid? deviceId = null);
    AttendanceRecord AddAttendanceFromDevice(string employeeCode, DateTime timestamp, int status, int verify, string? workcode, string? deviceSerialNumber);
}
