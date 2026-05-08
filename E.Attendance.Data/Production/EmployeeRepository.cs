using Microsoft.EntityFrameworkCore;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;

namespace E.Attendance.Data.Production;

public sealed class EmployeeRepository : IEmployeeRepository
{
    private readonly ApplicationDbContext _db;

    public EmployeeRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public Task<Employee?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _db.Employees.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<Employee?> GetByCodeAsync(string employeeCode, CancellationToken cancellationToken = default)
        => _db.Employees.FirstOrDefaultAsync(x => x.EmployeeCode == employeeCode, cancellationToken);

    public async Task<IReadOnlyList<Employee>> GetAllActiveAsync(CancellationToken cancellationToken = default)
        => await _db.Employees.Where(x => x.IsActive).OrderBy(x => x.EmployeeCode).ToListAsync(cancellationToken);

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
}
