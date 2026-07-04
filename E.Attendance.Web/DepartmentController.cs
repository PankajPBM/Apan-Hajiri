using Microsoft.AspNetCore.Mvc;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;

namespace E.Attendance.Web;

[ApiController]
[Route("api/v2/department")]
public sealed class DepartmentController : ControllerBase
{
    private readonly IUnitOfWork _uow;

    public DepartmentController(IUnitOfWork uow) => _uow = uow;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? areaId, CancellationToken ct)
    {
        var depts = areaId.HasValue
            ? await _uow.Departments.GetByAreaAsync(areaId.Value, ct)
            : await _uow.Departments.GetAllAsync(ct);

        return Ok(depts.Select(d => new
        {
            d.Id,
            d.Name,
            d.AreaId,
            areaName = d.Area?.Name,
            d.IsDefault
        }));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var dept = await _uow.Departments.GetByIdAsync(id, ct);
        if (dept == null) return NotFound();
        return Ok(new { dept.Id, dept.Name, dept.AreaId, areaName = dept.Area?.Name, dept.IsDefault });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DepartmentRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { message = "Department name is required." });
        if (req.AreaId <= 0)
            return BadRequest(new { message = "AreaId is required." });

        var area = await _uow.Areas.GetByIdAsync(req.AreaId, ct);
        if (area == null)
            return BadRequest(new { message = $"Area {req.AreaId} does not exist." });

        var existing = await _uow.Departments.GetByAreaAndNameAsync(req.AreaId, req.Name.Trim(), ct);
        if (existing != null)
            return Conflict(new { message = $"Department '{req.Name}' already exists in this area." });

        var dept = await _uow.Departments.AddAsync(new Department
        {
            Name = req.Name.Trim(),
            AreaId = req.AreaId
        }, ct);

        return CreatedAtAction(nameof(Get), new { id = dept.Id }, new { dept.Id, dept.Name, dept.AreaId });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] DepartmentRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { message = "Department name is required." });

        var dept = await _uow.Departments.GetByIdAsync(id, ct);
        if (dept == null) return NotFound();

        var duplicate = await _uow.Departments.GetByAreaAndNameAsync(dept.AreaId, req.Name.Trim(), ct);
        if (duplicate != null && duplicate.Id != id)
            return Conflict(new { message = $"Department name '{req.Name}' already exists in this area." });

        dept.Name = req.Name.Trim();
        dept.UpdatedAt = DateTime.UtcNow;
        await _uow.Departments.UpdateAsync(dept, ct);
        return Ok(new { dept.Id, dept.Name, dept.AreaId });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (id == 1)
            return BadRequest(new { message = "Default Department cannot be deleted." });

        var deleted = await _uow.Departments.DeleteAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }
}

public sealed record DepartmentRequest(string Name, int AreaId = 0);
