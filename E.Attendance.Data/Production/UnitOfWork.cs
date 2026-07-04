using E.Attendance.Core.Interfaces;

namespace E.Attendance.Data.Production;

public sealed class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _db;

    public UnitOfWork(
        ApplicationDbContext db,
        IAreaRepository areas,
        IDepartmentRepository departments,
        IEmployeeRepository employees,
        IDeviceRepository devices,
        ISyncLogRepository syncLogs,
        ITimetableRepository timetables,
        IHolidayRepository holidays,
        IAccCombinationRepository accCombinations)
    {
        _db = db;
        Areas = areas;
        Departments = departments;
        Employees = employees;
        Devices = devices;
        SyncLogs = syncLogs;
        Timetables = timetables;
        Holidays = holidays;
        AccCombinations = accCombinations;
    }

    public IAreaRepository Areas { get; }
    public IDepartmentRepository Departments { get; }
    public IEmployeeRepository Employees { get; }
    public IDeviceRepository Devices { get; }
    public ISyncLogRepository SyncLogs { get; }
    public ITimetableRepository Timetables { get; }
    public IHolidayRepository Holidays { get; }
    public IAccCombinationRepository AccCombinations { get; }

    public Task<int> CompleteAsync(CancellationToken cancellationToken = default)
        => _db.SaveChangesAsync(cancellationToken);

    public void Dispose() => _db.Dispose();
}
