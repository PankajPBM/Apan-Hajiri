using E.Attendance.Core.Models;

namespace E.Attendance.Core.Interfaces;

public interface ISyncLogRepository
{
    Task<SyncLog> AddAsync(SyncLog log, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncLog>> GetByDeviceAsync(string serialNumber, int take = 200, CancellationToken cancellationToken = default);
    Task UpdateResultAsync(Guid syncLogId, SyncStatus status, string responseText, CancellationToken cancellationToken = default);
}
