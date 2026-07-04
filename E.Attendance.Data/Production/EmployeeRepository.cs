using Microsoft.EntityFrameworkCore;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;

namespace E.Attendance.Data.Production;

public sealed class EmployeeRepository : IEmployeeRepository
{
    private readonly ApplicationDbContext _db;

    public EmployeeRepository(ApplicationDbContext db) => _db = db;

    public Task<Employee?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _db.Employees
            .Include(e => e.EmployeeDepartments).ThenInclude(ed => ed.Department)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<Employee?> GetByCodeAsync(string employeeCode, CancellationToken cancellationToken = default)
        => _db.Employees
            .Include(e => e.EmployeeDepartments).ThenInclude(ed => ed.Department)
            .FirstOrDefaultAsync(x => x.EmployeeCode == employeeCode, cancellationToken);

    public async Task<IReadOnlyList<Employee>> GetAllActiveAsync(CancellationToken cancellationToken = default)
        => await _db.Employees
            .Include(e => e.EmployeeDepartments).ThenInclude(ed => ed.Department)
            .Where(x => x.IsActive)
            .OrderBy(x => x.EmployeeCode)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Employee>> GetByAreaAsync(int areaId, CancellationToken cancellationToken = default)
        => await _db.Employees
            .Include(e => e.EmployeeDepartments).ThenInclude(ed => ed.Department)
            .Where(e => e.IsActive && e.AreaId == areaId)
            .OrderBy(e => e.EmployeeCode)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Employee>> GetByDepartmentAsync(int departmentId, CancellationToken cancellationToken = default)
        => await _db.Employees
            .Include(e => e.EmployeeDepartments).ThenInclude(ed => ed.Department)
            .Where(e => e.IsActive && e.EmployeeDepartments.Any(ed => ed.DepartmentId == departmentId))
            .OrderBy(e => e.EmployeeCode)
            .ToListAsync(cancellationToken);

    public async Task<Employee> AddAsync(Employee employee, CancellationToken cancellationToken = default)
    {
        _db.Employees.Add(employee);
        await _db.SaveChangesAsync(cancellationToken);
        return employee;
    }

    public async Task UpdateAsync(Employee employee, CancellationToken cancellationToken = default)
    {
        _db.Employees.Update(employee);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetDepartmentsAsync(Guid employeeId, IEnumerable<int> departmentIds, CancellationToken cancellationToken = default)
    {
        var existing = await _db.EmployeeDepartments
            .Where(ed => ed.EmployeeId == employeeId)
            .ToListAsync(cancellationToken);

        _db.EmployeeDepartments.RemoveRange(existing);

        foreach (var deptId in departmentIds.Distinct())
        {
            _db.EmployeeDepartments.Add(new EmployeeDepartment
            {
                EmployeeId = employeeId,
                DepartmentId = deptId,
                AssignedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
