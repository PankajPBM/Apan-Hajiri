using Microsoft.AspNetCore.Mvc;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;
using E.Attendance.Services;

namespace E.Attendance.Web;

[ApiController]
[Route("api/v2/device")]
public sealed class DeviceController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly ISyncService _syncService;
    private readonly IAttendanceService _attendanceService;

    public DeviceController(IUnitOfWork uow, ISyncService syncService, IAttendanceService attendanceService)
    {
        _uow = uow;
        _syncService = syncService;
        _attendanceService = attendanceService;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? areaId, [FromQuery] int? departmentId, CancellationToken ct)
    {
        IReadOnlyList<Device> rows;
        if (areaId.HasValue && departmentId.HasValue)
            rows = await _uow.Devices.GetByAreaDepartmentAsync(areaId.Value, departmentId.Value, ct);
        else
            rows = await _uow.Devices.GetAllAsync(ct);

        return Ok(rows.Select(d => new
        {
            d.Id,
            serialNumber = d.SerialNumber,
            deviceName = d.Name,
            d.Model,
            d.IpAddress,
            status = d.IsOnline ? "Online" : "Offline",
            d.IsRegistrationDevice,
            d.IsRegistered,
            d.AreaId,
            d.DepartmentId,
            d.SupportsVisibleLightFace,
            d.SupportsInfraredFace,
            d.SupportsFingerprint,
            d.TimeZone,
            d.LastSeenUtc
        }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var device = await _uow.Devices.GetByIdAsync(id, ct);
        if (device == null) return NotFound();
        return Ok(device);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DeviceCreateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.SerialNumber))
            return BadRequest(new { message = "SerialNumber is required." });
        if (req.AreaId <= 0)
            return BadRequest(new { message = "AreaId is required." });
        if (req.DepartmentId <= 0)
            return BadRequest(new { message = "DepartmentId is required." });

        // Validate area/department exist and department belongs to area
        var area = await _uow.Areas.GetByIdAsync(req.AreaId, ct);
        if (area == null) return BadRequest(new { message = $"Area {req.AreaId} not found." });

        var dept = await _uow.Departments.GetByIdAsync(req.DepartmentId, ct);
        if (dept == null) return BadRequest(new { message = $"Department {req.DepartmentId} not found." });
        if (dept.AreaId != req.AreaId)
            return BadRequest(new { message = "Department does not belong to the selected Area." });

        var existing = await _uow.Devices.GetBySerialAsync(req.SerialNumber.Trim(), ct);
        if (existing != null)
            return Conflict(new { message = $"Device with serial '{req.SerialNumber}' already exists." });

        var device = new Device
        {
            SerialNumber = req.SerialNumber.Trim(),
            Name = string.IsNullOrWhiteSpace(req.Name) ? req.SerialNumber.Trim() : req.Name.Trim(),
            Model = req.Model ?? string.Empty,
            IpAddress = req.IpAddress ?? string.Empty,
            AreaId = req.AreaId,
            DepartmentId = req.DepartmentId,
            IsRegistrationDevice = req.IsRegistrationDevice,
            IsOnline = false,
            Status = DeviceStatus.Offline
        };

        await _uow.Devices.AddAsync(device, ct);
        return CreatedAtAction(nameof(Get), new { id = device.Id }, new { device.Id, device.SerialNumber, device.AreaId, device.DepartmentId });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] DeviceUpdateRequest req, CancellationToken ct)
    {
        var device = await _uow.Devices.GetByIdAsync(id, ct);
        if (device == null) return NotFound();

        if (req.AreaId.HasValue && req.DepartmentId.HasValue)
        {
            var dept = await _uow.Departments.GetByIdAsync(req.DepartmentId.Value, ct);
            if (dept == null) return BadRequest(new { message = $"Department {req.DepartmentId} not found." });
            if (dept.AreaId != req.AreaId.Value)
                return BadRequest(new { message = "Department does not belong to the selected Area." });
            device.AreaId = req.AreaId.Value;
            device.DepartmentId = req.DepartmentId.Value;
        }

        if (!string.IsNullOrWhiteSpace(req.Name)) device.Name = req.Name.Trim();
        if (!string.IsNullOrWhiteSpace(req.Model)) device.Model = req.Model.Trim();
        if (!string.IsNullOrWhiteSpace(req.IpAddress)) device.IpAddress = req.IpAddress.Trim();
        if (req.IsRegistrationDevice.HasValue) device.IsRegistrationDevice = req.IsRegistrationDevice.Value;

        device.UpdatedAtUtc = DateTime.UtcNow;
        await _uow.Devices.UpdateAsync(device, ct);
        return Ok(new { device.Id, device.SerialNumber, device.AreaId, device.DepartmentId, device.IsRegistrationDevice });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await _uow.Devices.DeleteAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    /// <summary>
    /// Queue sync of all employees in the device's area+department to that device.
    /// </summary>
    [HttpPost("sync/{deviceSerial}")]
    public async Task<IActionResult> QueueSync(string deviceSerial, CancellationToken ct)
    {
        var count = await _syncService.QueueDeviceSyncAsync(deviceSerial, ct);
        if (count == 0)
            return NotFound(new { message = "Device not found or no active employees in its area/department." });

        return Ok(new { message = "Sync commands queued", commandCount = count });
    }

    /// <summary>
    /// Manual fetch for non-registration devices: pull users, attendance, or both from the device.
    /// </summary>
    [HttpPost("{id:guid}/fetch")]
    public async Task<IActionResult> ManualFetch(Guid id, [FromBody] ManualFetchRequest req, CancellationToken ct)
    {
        var device = await _uow.Devices.GetByIdAsync(id, ct);
        if (device == null) return NotFound();

        // For registration devices this happens automatically on connect;
        // for non-registration devices the caller must explicitly trigger it here.
        var queued = await _syncService.QueueDeviceSyncAsync(device.SerialNumber, ct);
        return Ok(new { message = "Manual fetch queued", commandCount = queued, fetchUsers = req.FetchUsers, fetchAttendance = req.FetchAttendance });
    }
}

public sealed record DeviceCreateRequest(
    string SerialNumber,
    string? Name,
    string? Model,
    string? IpAddress,
    int AreaId,
    int DepartmentId,
    bool IsRegistrationDevice = false);

public sealed record DeviceUpdateRequest(
    string? Name,
    string? Model,
    string? IpAddress,
    int? AreaId,
    int? DepartmentId,
    bool? IsRegistrationDevice);

public sealed record ManualFetchRequest(bool FetchUsers = true, bool FetchAttendance = true);
