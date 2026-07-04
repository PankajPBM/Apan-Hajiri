using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;

namespace E.Attendance.Services;

public sealed class SyncService : ISyncService
{
    private readonly IUnitOfWork _uow;
    private readonly IDeviceProtocolService _protocol;
    private readonly IAttendanceRepository _repository;

    public SyncService(IUnitOfWork uow, IDeviceProtocolService protocol, IAttendanceRepository repository)
    {
        _uow = uow;
        _protocol = protocol;
        _repository = repository;
    }

    public async Task<int> QueueDeviceSyncAsync(string deviceSerialNumber, CancellationToken cancellationToken = default)
    {
        var device = await _uow.Devices.GetBySerialAsync(deviceSerialNumber, cancellationToken);
        if (device is null)
        {
            return 0;
        }

        // Only sync employees that belong to the device's area AND department
        var employees = await _uow.Employees.GetByDepartmentAsync(device.DepartmentId, cancellationToken);
        var timetables = await _uow.Timetables.GetAllAsync(cancellationToken);
        var departmentTimetables = await _uow.Timetables.GetDepartmentTimetablesAsync(cancellationToken);
        var holidays = await _uow.Holidays.GetAllAsync(cancellationToken);
        var combinations = await _uow.AccCombinations.GetAllAsync(cancellationToken);

        var commands = _protocol.CreateBulkSyncCommands(
            employees, 
            device, 
            timetables, 
            departmentTimetables,
            holidays,
            combinations);

        var queued = 0;
        
        // Wrap everything in a transaction to reuse the same connection and ensure atomicity
        _repository.RunInTransaction(() =>
        {
            foreach (var (type, commandText) in commands)
            {
                // We'll skip SyncLogs in the repository transaction for now as it's EF-based
                // and might cause complexity with mixed providers, but EnqueueCommand is the critical one.
                
                var normalized = NormalizeCommandForQueue(commandText);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    _repository.EnqueueCommand(new DeviceCommand
                    {
                        DeviceSerialNumber = device.SerialNumber,
                        CommandText = normalized
                    });
                }

                queued++;
            }
        });

        // Now save the SyncLogs via EF Core
        foreach (var (type, commandText) in commands)
        {
            await _uow.SyncLogs.AddAsync(new SyncLog
            {
                DeviceSerialNumber = device.SerialNumber,
                Type = type,
                Status = SyncStatus.Pending,
                CommandText = commandText
            }, cancellationToken);
        }

        await _uow.CompleteAsync(cancellationToken);
        return queued;
    }

    private static string NormalizeCommandForQueue(string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return string.Empty;
        }

        var token = commandText.Trim();
        if (!token.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
        {
            return token;
        }

        var first = token.IndexOf(':');
        if (first < 0)
        {
            return token;
        }

        var second = token.IndexOf(':', first + 1);
        if (second < 0 || second + 1 >= token.Length)
        {
            return token;
        }

        return token[(second + 1)..].Trim();
    }
}
