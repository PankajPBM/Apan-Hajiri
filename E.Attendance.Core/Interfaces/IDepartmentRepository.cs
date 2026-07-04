using E.Attendance.Core.Models;

namespace E.Attendance.Core.Interfaces;

public interface IDepartmentRepository
{
    Task<IReadOnlyList<Department>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Department>> GetByAreaAsync(int areaId, CancellationToken ct = default);
    Task<Department?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<Department?> GetByAreaAndNameAsync(int areaId, string name, CancellationToken ct = default);
    Task<Department> AddAsync(Department department, CancellationToken ct = default);
    Task UpdateAsync(Department department, CancellationToken ct = default);
    /// <summary>Returns false when the department is protected (ID = 1).</summary>
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);
}
