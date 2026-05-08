using Microsoft.AspNetCore.Mvc;
using E.Attendance.Core.Interfaces;
using E.Attendance.Services;

namespace E.Attendance.Web;

[ApiController]
[Route("api/v2/device")]
public sealed class DeviceController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly ISyncService _syncService;

    public DeviceController(IUnitOfWork uow, ISyncService syncService)
    {
        _uow = uow;
        _syncService = syncService;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var rows = await _uow.Devices.GetAllAsync(cancellationToken);
        return Ok(rows.Select(d => new
        {
            d.Id,
            serialNumber = d.SerialNumber,
            deviceName = d.Name,
            status = d.IsOnline ? "Online" : "Offline",
            d.SupportsVisibleLightFace,
            d.SupportsInfraredFace,
            d.SupportsFingerprint,
            d.TimeZone
        }));
    }

    [HttpPost("sync/{deviceSerial}")]
    public async Task<IActionResult> QueueSync(string deviceSerial, CancellationToken cancellationToken)
    {
        var count = await _syncService.QueueDeviceSyncAsync(deviceSerial, cancellationToken);
        if (count == 0)
        {
            return NotFound(new { message = "Device not found or no active employee." });
        }

        return Ok(new { message = "Sync commands queued", commandCount = count });
    }
}
