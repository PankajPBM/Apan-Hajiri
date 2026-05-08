using Microsoft.EntityFrameworkCore;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;

namespace E.Attendance.Data.Production;

public sealed class SyncLogRepository : ISyncLogRepository
{
    private readonly ApplicationDbContext _db;

    public SyncLogRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<SyncLog> AddAsync(SyncLog log, CancellationToken cancellationToken = default)
    {
        _db.SyncLogs.Add(log);
        await _db.SaveChangesAsync(cancellationToken);
        return log;
    }

    public async Task<IReadOnlyList<SyncLog>> GetByDeviceAsync(string serialNumber, int take = 200, CancellationToken cancellationToken = default)
    {
        return await _db.SyncLogs
            .Where(x => x.DeviceSerialNumber == serialNumber)
            .OrderByDescending(x => x.CreatedUtc)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateResultAsync(Guid syncLogId, SyncStatus status, string responseText, CancellationToken cancellationToken = default)
    {
        var row = await _db.SyncLogs.FirstOrDefaultAsync(x => x.Id == syncLogId, cancellationToken);
        if (row is null)
        {
            return;
        }

        row.Status = status;
        row.ResponseText = responseText;
        row.CompletedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }
}
