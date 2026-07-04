using Microsoft.EntityFrameworkCore;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;

namespace E.Attendance.Data.Production;

public sealed class DepartmentRepository : IDepartmentRepository
{
    private readonly ApplicationDbContext _db;

    public DepartmentRepository(ApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<Department>> GetAllAsync(CancellationToken ct = default)
        => await _db.Departments.Include(d => d.Area).OrderBy(d => d.AreaId).ThenBy(d => d.Id).ToListAsync(ct);

    public async Task<IReadOnlyList<Department>> GetByAreaAsync(int areaId, CancellationToken ct = default)
        => await _db.Departments.Where(d => d.AreaId == areaId).OrderBy(d => d.Id).ToListAsync(ct);

    public Task<Department?> GetByIdAsync(int id, CancellationToken ct = default)
        => _db.Departments.Include(d => d.Area).FirstOrDefaultAsync(d => d.Id == id, ct);

    public Task<Department?> GetByAreaAndNameAsync(int areaId, string name, CancellationToken ct = default)
        => _db.Departments.FirstOrDefaultAsync(d => d.AreaId == areaId && d.Name.ToLower() == name.ToLower(), ct);

    public async Task<Department> AddAsync(Department department, CancellationToken ct = default)
    {
        _db.Departments.Add(department);
        await _db.SaveChangesAsync(ct);
        return department;
    }

    public async Task UpdateAsync(Department department, CancellationToken ct = default)
    {
        _db.Departments.Update(department);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        if (id == 1) return false; // Default Department is protected
        var dept = await _db.Departments.FindAsync([id], ct);
        if (dept == null) return false;
        _db.Departments.Remove(dept);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
