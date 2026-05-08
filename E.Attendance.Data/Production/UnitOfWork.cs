using E.Attendance.Core.Interfaces;

namespace E.Attendance.Data.Production;

public sealed class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _db;

    public UnitOfWork(
        ApplicationDbContext db,
        IEmployeeRepository employees,
        IDeviceRepository devices,
        ISyncLogRepository syncLogs)
    {
        _db = db;
        Employees = employees;
        Devices = devices;
        SyncLogs = syncLogs;
    }

    public IEmployeeRepository Employees { get; }
    public IDeviceRepository Devices { get; }
    public ISyncLogRepository SyncLogs { get; }

    public Task<int> CompleteAsync(CancellationToken cancellationToken = default)
        => _db.SaveChangesAsync(cancellationToken);

    public void Dispose()
    {
        _db.Dispose();
    }
}
