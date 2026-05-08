namespace E.Attendance.Core.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IEmployeeRepository Employees { get; }
    IDeviceRepository Devices { get; }
    ISyncLogRepository SyncLogs { get; }
    Task<int> CompleteAsync(CancellationToken cancellationToken = default);
}
