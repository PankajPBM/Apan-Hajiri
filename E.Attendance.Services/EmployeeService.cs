using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;

namespace E.Attendance.Services;

public sealed class EmployeeService : IEmployeeService
{
    private readonly IUnitOfWork _uow;

    public EmployeeService(IUnitOfWork uow)
    {
        _uow = uow;
    }

    public Task<IReadOnlyList<Employee>> GetActiveAsync(CancellationToken cancellationToken = default)
        => _uow.Employees.GetAllActiveAsync(cancellationToken);
}
