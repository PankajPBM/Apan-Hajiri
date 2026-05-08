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

    public IReadOnlyList<Employee> GetEmployees() => _employees;

    public Employee AddEmployee(Employee employee)
    {
        _employees.Add(employee);
        return employee;
    }

    public bool UpdateEmployee(Employee employee)
    {
        var index = _employees.FindIndex(e => e.Id == employee.Id);
        if (index < 0)
        {
            return false;
        }

        _employees[index] = employee;
        return true;
    }

    public bool DeleteEmployee(Guid employeeId)
    {
        var removed = _employees.RemoveAll(e => e.Id == employeeId);
        return removed > 0;
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
        if (index < 0)
        {
            return false;
        }

        _devices[index] = device;
        return true;
    }

    public bool DeleteDevice(Guid deviceId)
    {
        var removed = _devices.RemoveAll(d => d.Id == deviceId);
        return removed > 0;
    }

    public IReadOnlyList<AttendanceRecord> GetAttendance(DateOnly? from, DateOnly? to)
    {
        IEnumerable<AttendanceRecord> query = _records;

        if (from.HasValue)
        {
            query = query.Where(r => DateOnly.FromDateTime(r.Timestamp) >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(r => DateOnly.FromDateTime(r.Timestamp) <= to.Value);
        }

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
        if (index < 0)
        {
            return false;
        }

        _records[index] = record;
        return true;
    }

    public bool DeleteAttendance(Guid attendanceId)
    {
        var removed = _records.RemoveAll(r => r.Id == attendanceId);
        return removed > 0;
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
        if (command is not null)
        {
            command.SentUtc = DateTime.UtcNow;
        }
    }

    public void SaveCommandReply(Guid commandId, string replyText)
    {
        var command = _commands.FirstOrDefault(c => c.Id == commandId);
        if (command is not null)
        {
            command.ReplyText = replyText;
        }
    }

    public int ClearPendingCommands(string deviceSerialNumber)
    {
        return _commands.RemoveAll(c =>
            c.SentUtc is null &&
            c.DeviceSerialNumber.Equals(deviceSerialNumber, StringComparison.OrdinalIgnoreCase));
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
        if (row is null)
        {
            return;
        }

        row.Status = status;
        row.ResponseText = responseText;
        row.CompletedUtc = DateTime.UtcNow;
    }

    public IReadOnlyDictionary<string, long> GetDeviceTableStamps(string deviceSerialNumber)
    {
        if (string.IsNullOrWhiteSpace(deviceSerialNumber))
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        var serial = deviceSerialNumber.Trim();
        return _deviceTableStamps.TryGetValue(serial, out var stamps)
            ? new Dictionary<string, long>(stamps, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    }

    public void UpsertDeviceTableStamp(string deviceSerialNumber, string tableName, long stampValue)
    {
        if (string.IsNullOrWhiteSpace(deviceSerialNumber) || string.IsNullOrWhiteSpace(tableName))
        {
            return;
        }

        var serial = deviceSerialNumber.Trim();
        if (!_deviceTableStamps.TryGetValue(serial, out var stamps))
        {
            stamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            _deviceTableStamps[serial] = stamps;
        }

        stamps[tableName.Trim().ToUpperInvariant()] = stampValue;
    }
}
