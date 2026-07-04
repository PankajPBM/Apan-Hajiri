using E.Attendance.Core.Models;

namespace E.Attendance.Core.Interfaces;

public interface IEmployeeRepository
{
    Task<Employee?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Employee?> GetByCodeAsync(string employeeCode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Employee>> GetAllActiveAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Employee>> GetByAreaAsync(int areaId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Employee>> GetByDepartmentAsync(int departmentId, CancellationToken cancellationToken = default);
    Task<Employee> AddAsync(Employee employee, CancellationToken cancellationToken = default);
    Task UpdateAsync(Employee employee, CancellationToken cancellationToken = default);
    /// <summary>Sets the full department mapping for an employee (replaces existing).</summary>
    Task SetDepartmentsAsync(Guid employeeId, IEnumerable<int> departmentIds, CancellationToken cancellationToken = default);
}
