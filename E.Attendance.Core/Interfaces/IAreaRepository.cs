using E.Attendance.Core.Models;

namespace E.Attendance.Core.Interfaces;

public interface IAreaRepository
{
    Task<IReadOnlyList<Area>> GetAllAsync(CancellationToken ct = default);
    Task<Area?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<Area?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<Area> AddAsync(Area area, CancellationToken ct = default);
    Task UpdateAsync(Area area, CancellationToken ct = default);
    /// <summary>Returns false when the area is protected (ID = 1).</summary>
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);
}
