using E.Attendance.Core.Models;

namespace E.Attendance.Services;

public interface IEmployeeService
{
    Task<IReadOnlyList<Employee>> GetActiveAsync(CancellationToken cancellationToken = default);
}
