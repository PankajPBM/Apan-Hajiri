using Microsoft.EntityFrameworkCore;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;

namespace E.Attendance.Data.Production;

public sealed class AreaRepository : IAreaRepository
{
    private readonly ApplicationDbContext _db;

    public AreaRepository(ApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<Area>> GetAllAsync(CancellationToken ct = default)
        => await _db.Areas.Include(a => a.Departments).OrderBy(a => a.Id).ToListAsync(ct);

    public Task<Area?> GetByIdAsync(int id, CancellationToken ct = default)
        => _db.Areas.Include(a => a.Departments).FirstOrDefaultAsync(a => a.Id == id, ct);

    public Task<Area?> GetByNameAsync(string name, CancellationToken ct = default)
        => _db.Areas.FirstOrDefaultAsync(a => a.Name.ToLower() == name.ToLower(), ct);

    public async Task<Area> AddAsync(Area area, CancellationToken ct = default)
    {
        _db.Areas.Add(area);
        await _db.SaveChangesAsync(ct);
        return area;
    }

    public async Task UpdateAsync(Area area, CancellationToken ct = default)
    {
        _db.Areas.Update(area);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        if (id == 1) return false; // Default Area is protected
        var area = await _db.Areas.FindAsync([id], ct);
        if (area == null) return false;
        _db.Areas.Remove(area);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
