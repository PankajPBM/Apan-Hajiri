using Microsoft.AspNetCore.Mvc;
using E.Attendance.Core.Interfaces;

namespace E.Attendance.Web;

[ApiController]
[Route("api/v2/sync")]
public sealed class SyncController : ControllerBase
{
    private readonly IUnitOfWork _uow;

    public SyncController(IUnitOfWork uow)
    {
        _uow = uow;
    }

    [HttpGet("logs/{deviceSerial}")]
    public async Task<IActionResult> Logs(string deviceSerial, [FromQuery] int take = 200, CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 2000)
        {
            take = 200;
        }

        var rows = await _uow.SyncLogs.GetByDeviceAsync(deviceSerial, take, cancellationToken);
        return Ok(rows);
    }
}
