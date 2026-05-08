using Microsoft.AspNetCore.Mvc;
using E.Attendance.Core.Interfaces;

namespace E.Attendance.Web;

[ApiController]
[Route("api/v2/employee")]
public sealed class EmployeeController : ControllerBase
{
    private readonly IUnitOfWork _uow;

    public EmployeeController(IUnitOfWork uow)
    {
        _uow = uow;
    }

    [HttpGet("active")]
    public async Task<IActionResult> Active(CancellationToken cancellationToken)
    {
        var rows = await _uow.Employees.GetAllActiveAsync(cancellationToken);
        return Ok(rows);
    }
}
