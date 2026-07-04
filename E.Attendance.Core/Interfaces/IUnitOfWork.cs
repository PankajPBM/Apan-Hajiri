using E.Attendance.Core.Models;

namespace E.Attendance.Core.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IAreaRepository Areas { get; }
    IDepartmentRepository Departments { get; }
    IEmployeeRepository Employees { get; }
    IDeviceRepository Devices { get; }
    ISyncLogRepository SyncLogs { get; }
    ITimetableRepository Timetables { get; }
    IHolidayRepository Holidays { get; }
    IAccCombinationRepository AccCombinations { get; }
    Task<int> CompleteAsync(CancellationToken cancellationToken = default);
}

public interface IHolidayRepository
{
    Task<IReadOnlyList<HolidayEntity>> GetAllAsync(CancellationToken ct = default);
}

public interface IAccCombinationRepository
{
    Task<IReadOnlyList<AccCombinationEntity>> GetAllAsync(CancellationToken ct = default);
}
