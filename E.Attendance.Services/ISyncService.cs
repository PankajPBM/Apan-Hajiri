namespace E.Attendance.Services;

public interface ISyncService
{
    Task<int> QueueDeviceSyncAsync(string deviceSerialNumber, CancellationToken cancellationToken = default);
}
