using Microsoft.AspNetCore.Mvc;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;

namespace E.Attendance.Web;

[ApiController]
[Route("api/v2/area")]
public sealed class AreaController : ControllerBase
{
    private readonly IUnitOfWork _uow;

    public AreaController(IUnitOfWork uow) => _uow = uow;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var areas = await _uow.Areas.GetAllAsync(ct);
        return Ok(areas.Select(a => new
        {
            a.Id,
            a.Name,
            a.IsDefault,
            departments = a.Departments.Select(d => new { d.Id, d.Name, d.IsDefault })
        }));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var area = await _uow.Areas.GetByIdAsync(id, ct);
        if (area == null) return NotFound();
        var depts = await _uow.Departments.GetByAreaAsync(id, ct);
        return Ok(new
        {
            area.Id,
            area.Name,
            area.IsDefault,
            departments = depts.Select(d => new { d.Id, d.Name, d.IsDefault })
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AreaRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { message = "Area name is required." });

        var existing = await _uow.Areas.GetByNameAsync(req.Name.Trim(), ct);
        if (existing != null)
            return Conflict(new { message = $"Area '{req.Name}' already exists." });

        var area = await _uow.Areas.AddAsync(new Area { Name = req.Name.Trim() }, ct);

        // Auto-create default department for the new area
        await _uow.Departments.AddAsync(new Department
        {
            Name = $"{area.Name} Department",
            AreaId = area.Id
        }, ct);

        return CreatedAtAction(nameof(Get), new { id = area.Id }, new { area.Id, area.Name });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] AreaRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { message = "Area name is required." });

        var area = await _uow.Areas.GetByIdAsync(id, ct);
        if (area == null) return NotFound();

        var duplicate = await _uow.Areas.GetByNameAsync(req.Name.Trim(), ct);
        if (duplicate != null && duplicate.Id != id)
            return Conflict(new { message = $"Area name '{req.Name}' is already used by another area." });

        area.Name = req.Name.Trim();
        area.UpdatedAt = DateTime.UtcNow;
        await _uow.Areas.UpdateAsync(area, ct);
        return Ok(new { area.Id, area.Name });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (id == 1)
            return BadRequest(new { message = "Default Area cannot be deleted." });

        var deleted = await _uow.Areas.DeleteAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }
}

public sealed record AreaRequest(string Name);
