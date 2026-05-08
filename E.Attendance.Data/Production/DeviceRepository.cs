using Microsoft.EntityFrameworkCore;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;

namespace E.Attendance.Data.Production;

public sealed class DeviceRepository : IDeviceRepository
{
    private readonly ApplicationDbContext _db;

    public DeviceRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public Task<Device?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _db.Devices.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<Device?> GetBySerialAsync(string serialNumber, CancellationToken cancellationToken = default)
        => _db.Devices.FirstOrDefaultAsync(x => x.SerialNumber == serialNumber, cancellationToken);

    public async Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken cancellationToken = default)
        => await _db.Devices.OrderByDescending(x => x.LastSeenUtc).ToListAsync(cancellationToken);

    public async Task<Device> AddAsync(Device device, CancellationToken cancellationToken = default)
    {
        _db.Devices.Add(device);
        await _db.SaveChangesAsync(cancellationToken);
        return device;
    }

    public async Task UpdateAsync(Device device, CancellationToken cancellationToken = default)
    {
        _db.Devices.Update(device);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
