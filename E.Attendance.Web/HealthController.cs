using Microsoft.AspNetCore.Mvc;
using E.Attendance.Data.Production;

namespace E.Attendance.Web;

[ApiController]
[Route("api/v2/health")]
public sealed class HealthController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public HealthController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public IActionResult Get()
    {
        return _db.Database.CanConnect()
            ? Ok(new { status = "ok", timestampUtc = DateTime.UtcNow })
            : StatusCode(503, new { status = "db_unavailable" });
    }
}
