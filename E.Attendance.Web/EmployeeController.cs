using Microsoft.AspNetCore.Mvc;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;

namespace E.Attendance.Web;

[ApiController]
[Route("api/v2/employee")]
public sealed class EmployeeController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly IAttendanceService _svc;

    public EmployeeController(IUnitOfWork uow, IAttendanceService svc)
    {
        _uow = uow;
        _svc = svc;
    }

    [HttpGet("active")]
    public async Task<IActionResult> Active([FromQuery] int? areaId, [FromQuery] int? departmentId, CancellationToken ct)
    {
        IReadOnlyList<Employee> rows;

        if (departmentId.HasValue)
            rows = await _uow.Employees.GetByDepartmentAsync(departmentId.Value, ct);
        else if (areaId.HasValue)
            rows = await _uow.Employees.GetByAreaAsync(areaId.Value, ct);
        else
            rows = await _uow.Employees.GetAllActiveAsync(ct);

        return Ok(rows.Select(e => MapEmployee(e)));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var emp = await _uow.Employees.GetByIdAsync(id, ct);
        if (emp == null) return NotFound();
        return Ok(MapEmployee(emp));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] EmployeeCreateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.EmployeeCode))
            return BadRequest(new { message = "Employee code (ID) is required." });
        if (string.IsNullOrWhiteSpace(req.FullName))
            return BadRequest(new { message = "Full name is required." });
        if (req.AreaId <= 0)
            return BadRequest(new { message = "AreaId is required." });
        if (req.DepartmentIds == null || req.DepartmentIds.Count == 0)
            return BadRequest(new { message = "At least one DepartmentId is required." });

        // Validate area exists
        var area = await _uow.Areas.GetByIdAsync(req.AreaId, ct);
        if (area == null) return BadRequest(new { message = $"Area {req.AreaId} not found." });

        // Validate all departments exist and belong to the same area
        var departments = new List<Department>();
        foreach (var deptId in req.DepartmentIds.Distinct())
        {
            var dept = await _uow.Departments.GetByIdAsync(deptId, ct);
            if (dept == null) return BadRequest(new { message = $"Department {deptId} not found." });
            if (dept.AreaId != req.AreaId)
                return BadRequest(new { message = $"Department {deptId} does not belong to Area {req.AreaId}. All departments must be under the same area." });
            departments.Add(dept);
        }

        // Check unique employee code
        var existing = await _uow.Employees.GetByCodeAsync(req.EmployeeCode.Trim(), ct);
        if (existing != null)
            return Conflict(new { message = $"Employee code '{req.EmployeeCode}' already exists." });

        var primaryDeptId = req.PrimaryDepartmentId ?? req.DepartmentIds[0];
        var primaryDept = departments.FirstOrDefault(d => d.Id == primaryDeptId) ?? departments[0];

        var employee = new Employee
        {
            EmployeeCode = req.EmployeeCode.Trim(),
            FullName = req.FullName.Trim(),
            Position = req.Position ?? string.Empty,
            CardNumber = req.CardNumber ?? string.Empty,
            AreaId = req.AreaId,
            DepartmentId = primaryDept.Id,
            Area = area.Name,
            Department = primaryDept.Name,
            Departments = departments.Select(d => d.Name).ToList(),
            IsActive = true
        };

        await _uow.Employees.AddAsync(employee, ct);

        // Persist multi-department mapping
        await _uow.Employees.SetDepartmentsAsync(employee.Id, req.DepartmentIds, ct);

        // Real-time sync: push to all devices in the employee's area/departments
        SyncEmployeeToDevices(employee, departments);

        return CreatedAtAction(nameof(Get), new { id = employee.Id }, MapEmployee(employee));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] EmployeeUpdateRequest req, CancellationToken ct)
    {
        var employee = await _uow.Employees.GetByIdAsync(id, ct);
        if (employee == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(req.FullName)) employee.FullName = req.FullName.Trim();
        if (!string.IsNullOrWhiteSpace(req.Position)) employee.Position = req.Position.Trim();
        if (!string.IsNullOrWhiteSpace(req.CardNumber)) employee.CardNumber = req.CardNumber.Trim();

        List<Department>? departments = null;

        if (req.AreaId.HasValue && req.DepartmentIds is { Count: > 0 })
        {
            var area = await _uow.Areas.GetByIdAsync(req.AreaId.Value, ct);
            if (area == null) return BadRequest(new { message = $"Area {req.AreaId} not found." });

            departments = new List<Department>();
            foreach (var deptId in req.DepartmentIds.Distinct())
            {
                var dept = await _uow.Departments.GetByIdAsync(deptId, ct);
                if (dept == null) return BadRequest(new { message = $"Department {deptId} not found." });
                if (dept.AreaId != req.AreaId.Value)
                    return BadRequest(new { message = $"Department {deptId} does not belong to Area {req.AreaId}." });
                departments.Add(dept);
            }

            var primaryDeptId = req.PrimaryDepartmentId ?? req.DepartmentIds[0];
            var primaryDept = departments.FirstOrDefault(d => d.Id == primaryDeptId) ?? departments[0];

            employee.AreaId = req.AreaId.Value;
            employee.DepartmentId = primaryDept.Id;
            employee.Area = area.Name;
            employee.Department = primaryDept.Name;
            employee.Departments = departments.Select(d => d.Name).ToList();

            await _uow.Employees.SetDepartmentsAsync(id, req.DepartmentIds, ct);
        }

        await _uow.Employees.UpdateAsync(employee, ct);

        // Real-time sync to devices
        if (departments != null)
            SyncEmployeeToDevices(employee, departments);

        return Ok(MapEmployee(employee));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var employee = await _uow.Employees.GetByIdAsync(id, ct);
        if (employee == null) return NotFound();
        employee.IsActive = false;
        await _uow.Employees.UpdateAsync(employee, ct);
        return Ok(new { message = "Employee deactivated." });
    }

    [HttpPut("{id:guid}/departments")]
    public async Task<IActionResult> AssignDepartments(Guid id, [FromBody] DepartmentAssignRequest req, CancellationToken ct)
    {
        if (req.DepartmentIds == null || req.DepartmentIds.Count == 0)
            return BadRequest(new { message = "At least one DepartmentId is required." });

        var employee = await _uow.Employees.GetByIdAsync(id, ct);
        if (employee == null) return NotFound();

        var departments = new List<Department>();
        int? areaId = null;
        foreach (var deptId in req.DepartmentIds.Distinct())
        {
            var dept = await _uow.Departments.GetByIdAsync(deptId, ct);
            if (dept == null) return BadRequest(new { message = $"Department {deptId} not found." });
            if (areaId.HasValue && dept.AreaId != areaId.Value)
                return BadRequest(new { message = "All departments must belong to the same area." });
            areaId ??= dept.AreaId;
            departments.Add(dept);
        }

        var area = await _uow.Areas.GetByIdAsync(areaId!.Value, ct);
        var primaryDept = departments[0];

        employee.AreaId = areaId.Value;
        employee.DepartmentId = primaryDept.Id;
        employee.Area = area?.Name ?? string.Empty;
        employee.Department = primaryDept.Name;
        employee.Departments = departments.Select(d => d.Name).ToList();

        await _uow.Employees.SetDepartmentsAsync(id, req.DepartmentIds, ct);
        await _uow.Employees.UpdateAsync(employee, ct);

        SyncEmployeeToDevices(employee, departments);

        return Ok(new { employeeId = id, departments = departments.Select(d => new { d.Id, d.Name }) });
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private void SyncEmployeeToDevices(Employee employee, IEnumerable<Department> _departments)
    {
        _svc.SyncEmployeeToDevices(employee);
    }

    private static object MapEmployee(Employee e) => new
    {
        e.Id,
        e.EmployeeCode,
        e.FullName,
        e.Position,
        e.CardNumber,
        e.AreaId,
        e.Area,
        e.DepartmentId,
        e.Department,
        departments = e.Departments,
        e.IsActive,
        e.CreatedAt,
        assignedDepartments = e.EmployeeDepartments.Select(ed => new
        {
            ed.DepartmentId,
            name = ed.Department?.Name
        })
    };
}

public sealed record EmployeeCreateRequest(
    string EmployeeCode,
    string FullName,
    int AreaId,
    List<int> DepartmentIds,
    int? PrimaryDepartmentId = null,
    string? Position = null,
    string? CardNumber = null);

public sealed record EmployeeUpdateRequest(
    string? FullName,
    int? AreaId,
    List<int>? DepartmentIds,
    int? PrimaryDepartmentId = null,
    string? Position = null,
    string? CardNumber = null);

public sealed record DepartmentAssignRequest(List<int> DepartmentIds);
