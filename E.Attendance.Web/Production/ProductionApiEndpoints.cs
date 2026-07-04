using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using E.Attendance.Core.Interfaces;
using E.Attendance.Core.Models;
using E.Attendance.Data.Production;
using E.Attendance.Services;
using E.Attendance.Services.Production;

namespace E.Attendance.Web.Production;

public static class ProductionApiEndpoints
{
    private static readonly HashSet<string> ValidLeaveStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "pending",
        "approved",
        "rejected",
        "cancelled"
    };

    public static void MapProductionApiEndpoints(this WebApplication app)
    {
        app.MapGet("/api/personnel", async (PbAttendanceDbContext db, string? search, string? status, CancellationToken cancellationToken) =>
        {
            var query = db.Personnel.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var token = search.Trim();
                query = query.Where(x =>
                    x.EmployeeCode.Contains(token) ||
                    x.FirstName.Contains(token) ||
                    x.LastName.Contains(token) ||
                    (x.Email != null && x.Email.Contains(token)));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(x => x.Status == status.Trim().ToLowerInvariant());
            }

            var rows = await query
                .OrderBy(x => x.EmployeeCode)
                .Select(x => new
                {
                    x.Id,
                    x.EmployeeCode,
                    x.FirstName,
                    x.LastName,
                    x.Email,
                    x.Phone,
                    x.BranchId,
                    x.DepartmentId,
                    x.DesignationId,
                    x.ShiftId,
                    x.JoinDate,
                    x.Status
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(rows);
        });

        app.MapPost("/api/personnel", async (PbAttendanceDbContext db, PersonnelUpsertRequest request, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.EmployeeCode))
            {
                return Results.BadRequest(new { message = "personnel_code is required." });
            }

            var personnelCode = request.EmployeeCode.Trim();
            var duplicate = await db.Personnel.AnyAsync(x => x.EmployeeCode == personnelCode, cancellationToken);
            if (duplicate)
            {
                return Results.BadRequest(new { message = "Personnel code already exists." });
            }

            var entity = new PersonnelEntity
            {
                EmployeeCode = personnelCode,
                FirstName = request.FirstName?.Trim() ?? string.Empty,
                LastName = request.LastName?.Trim() ?? string.Empty,
                Email = request.Email?.Trim(),
                Phone = request.Phone?.Trim(),
                BranchId = request.BranchId,
                DepartmentId = request.DepartmentId,
                DesignationId = request.DesignationId,
                ShiftId = request.ShiftId,
                JoinDate = request.JoinDate,
                Status = string.IsNullOrWhiteSpace(request.Status) ? "active" : request.Status.Trim().ToLowerInvariant(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            db.Personnel.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/personnel/{entity.Id}", entity);
        });

        app.MapPut("/api/personnel/{id:guid}", async (PbAttendanceDbContext db, Guid id, PersonnelUpsertRequest request, CancellationToken cancellationToken) =>
        {
            var entity = await db.Personnel.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound(new { message = "Personnel not found." });
            }

            if (!string.IsNullOrWhiteSpace(request.EmployeeCode))
            {
                var personnelCode = request.EmployeeCode.Trim();
                var duplicate = await db.Personnel.AnyAsync(x => x.Id != id && x.EmployeeCode == personnelCode, cancellationToken);
                if (duplicate)
                {
                    return Results.BadRequest(new { message = "Personnel code already exists." });
                }

                entity.EmployeeCode = personnelCode;
            }

            entity.FirstName = request.FirstName?.Trim() ?? entity.FirstName;
            entity.LastName = request.LastName?.Trim() ?? entity.LastName;
            entity.Email = request.Email?.Trim();
            entity.Phone = request.Phone?.Trim();
            entity.BranchId = request.BranchId;
            entity.DepartmentId = request.DepartmentId;
            entity.DesignationId = request.DesignationId;
            entity.ShiftId = request.ShiftId;
            entity.JoinDate = request.JoinDate;
            entity.Status = string.IsNullOrWhiteSpace(request.Status) ? entity.Status : request.Status.Trim().ToLowerInvariant();
            entity.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(entity);
        });

        app.MapDelete("/api/personnel/{id:guid}", async (PbAttendanceDbContext db, Guid id, CancellationToken cancellationToken) =>
        {
            var entity = await db.Personnel.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound(new { message = "Personnel not found." });
            }

            db.Personnel.Remove(entity);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new { success = true });
        });

        app.MapPost("/api/attendance/process", async (AttendanceProcessingEngine engine, CancellationToken cancellationToken) =>
        {
            var result = await engine.ProcessAsync(cancellationToken);
            return Results.Ok(result);
        });

        app.MapGet("/api/attendance/exceptions", async (PbAttendanceDbContext db, bool? resolved, CancellationToken cancellationToken) =>
        {
            var query = db.AttendanceExceptions
                .AsNoTracking()
                .Include(x => x.Attendance)
                .ThenInclude(x => x!.Personnel)
                .AsQueryable();

            if (resolved.HasValue)
            {
                query = query.Where(x => x.Resolved == resolved.Value);
            }

            var rows = await query
                .OrderByDescending(x => x.Id)
                .Select(x => new
                {
                    x.Id,
                    x.AttendanceId,
                    x.ExceptionType,
                    x.Remarks,
                    x.Resolved,
                    personnelCode = x.Attendance!.Personnel!.EmployeeCode,
                    attendanceDate = x.Attendance.AttendanceDate
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(rows);
        });

        app.MapPost("/api/devices/{id:guid}/command", async (PbAttendanceDbContext db, IAttendanceService attendanceService, Guid id, DeviceCommandCreateRequest request, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.CommandString))
            {
                return Results.BadRequest(new { message = "command_string is required." });
            }

            var device = await db.Devices.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (device is null)
            {
                return Results.NotFound(new { message = "Device not found." });
            }

            var cmd = attendanceService.QueueDeviceCommand(device.SerialNumber, request.CommandString.Trim());
            return Results.Ok(new
            {
                id = cmd.Id,
                deviceId = device.Id,
                commandString = cmd.CommandText,
                status = "pending",
                createdAt = cmd.CreatedUtc
            });
        });

        app.MapGet("/api/attendance/policy", async (PbAttendanceDbContext db, CancellationToken cancellationToken) =>
        {
            var policy = await db.AttendancePolicies
                .AsNoTracking()
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync(cancellationToken) ?? new AttendancePolicyEntity();

            return Results.Ok(new
            {
                policy.WeeklyOffWorkAsOvertime,
                policy.HolidayWorkAsOvertime,
                policy.UpdatedAt
            });
        });

        app.MapPut("/api/attendance/policy", async (PbAttendanceDbContext db, AttendancePolicyUpsertRequest request, CancellationToken cancellationToken) =>
        {
            var entity = await db.AttendancePolicies
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (entity is null)
            {
                entity = new AttendancePolicyEntity();
                db.AttendancePolicies.Add(entity);
            }

            entity.WeeklyOffWorkAsOvertime = request.WeeklyOffWorkAsOvertime ?? entity.WeeklyOffWorkAsOvertime;
            entity.HolidayWorkAsOvertime = request.HolidayWorkAsOvertime ?? entity.HolidayWorkAsOvertime;
            entity.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(entity);
        });

        app.MapGet("/api/leave/types", async (PbAttendanceDbContext db, bool? activeOnly, CancellationToken cancellationToken) =>
        {
            try
            {
                var query = db.LeaveTypes.AsNoTracking().AsQueryable();
                if (activeOnly.GetValueOrDefault(true))
                {
                    query = query.Where(x => x.IsActive);
                }

                var rows = await query
                    .OrderBy(x => x.LeaveName)
                    .Select(x => new
                    {
                        x.Id,
                        x.LeaveName,
                        x.IsPaid,
                        x.MaxPerYear,
                        x.CarryForward,
                        x.AllowHalfDay,
                        x.IsActive
                    })
                    .ToListAsync(cancellationToken);

                return Results.Ok(rows);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] Error GET /api/leave/types: {ex}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/leave/types", async (PbAttendanceDbContext db, LeaveTypeUpsertRequest request, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.LeaveName))
            {
                return Results.BadRequest(new { message = "leave_name is required." });
            }

            var leaveName = request.LeaveName.Trim();
            var exists = await db.LeaveTypes.AnyAsync(x => x.LeaveName.ToLower() == leaveName.ToLower(), cancellationToken);
            if (exists)
            {
                return Results.BadRequest(new { message = "Leave type already exists." });
            }

            var entity = new LeaveTypeEntity
            {
                LeaveName = leaveName,
                IsPaid = request.IsPaid ?? true,
                MaxPerYear = request.MaxPerYear,
                CarryForward = request.CarryForward ?? false,
                AllowHalfDay = request.AllowHalfDay ?? false,
                IsActive = request.IsActive ?? true
            };

            db.LeaveTypes.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            await EnsureLeaveBalancesForYearAsync(db, DateTime.UtcNow.Year, cancellationToken);
            // Flat DTO, not the raw entity: EnsureLeaveBalancesForYearAsync just attached new
            // LeaveBalance rows to entity.LeaveBalances, and each of those has its own .LeaveType
            // back-reference — serializing the tracked entity directly throws "object cycle
            // detected" (same issue already documented/avoided on the POST /api/leave endpoint).
            return Results.Created($"/api/leave/types/{entity.Id}", new
            {
                entity.Id,
                entity.LeaveName,
                entity.IsPaid,
                entity.MaxPerYear,
                entity.CarryForward,
                entity.AllowHalfDay,
                entity.IsActive
            });
        });

        app.MapPut("/api/leave/types/{id:int}", async (PbAttendanceDbContext db, int id, LeaveTypeUpsertRequest request, CancellationToken cancellationToken) =>
        {
            var entity = await db.LeaveTypes.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound(new { message = "Leave type not found." });
            }

            if (!string.IsNullOrWhiteSpace(request.LeaveName))
            {
                var leaveName = request.LeaveName.Trim();
                var duplicate = await db.LeaveTypes.AnyAsync(x => x.Id != id && x.LeaveName.ToLower() == leaveName.ToLower(), cancellationToken);
                if (duplicate)
                {
                    return Results.BadRequest(new { message = "Leave type already exists." });
                }

                entity.LeaveName = leaveName;
            }

            entity.IsPaid = request.IsPaid ?? entity.IsPaid;
            entity.MaxPerYear = request.MaxPerYear;
            entity.CarryForward = request.CarryForward ?? entity.CarryForward;
            entity.AllowHalfDay = request.AllowHalfDay ?? entity.AllowHalfDay;
            entity.IsActive = request.IsActive ?? entity.IsActive;

            await db.SaveChangesAsync(cancellationToken);
            // Flat DTO here too — same object-cycle risk as the POST endpoint above once this
            // leave type has any LeaveBalances/LeaveRequests attached.
            return Results.Ok(new
            {
                entity.Id,
                entity.LeaveName,
                entity.IsPaid,
                entity.MaxPerYear,
                entity.CarryForward,
                entity.AllowHalfDay,
                entity.IsActive
            });
        });

        app.MapDelete("/api/leave/types/{id:int}", async (PbAttendanceDbContext db, int id, CancellationToken cancellationToken) =>
        {
            var entity = await db.LeaveTypes.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound(new { message = "Leave type not found." });
            }

            entity.IsActive = false;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new { success = true });
        });

        app.MapGet("/api/leave", async (
            PbAttendanceDbContext db,
            AttendanceService attendanceService,
            string? status,
            Guid? personnelId,
            DateOnly? from,
            DateOnly? to,
            CancellationToken cancellationToken) =>
        {
            try
            {
                // NOTE: do NOT .Include(x => x.Personnel). Employees.Id is TEXT but EF maps it as
                // Guid, so any EF read/join of Personnel throws (uuid = text / "reading Guid for
                // text"). Resolve employee code/name from the raw repository (which handles the
                // text ids) after the query instead. LeaveType (int key) is safe to include.
                var query = db.LeaveRequests
                    .AsNoTracking()
                    .Include(x => x.LeaveType)
                    .AsQueryable();

                if (!string.IsNullOrWhiteSpace(status))
                {
                    var normalized = NormalizeLeaveStatus(status, "all");
                    if (!string.Equals(normalized, "all", StringComparison.Ordinal))
                    {
                        query = query.Where(x => x.Status == normalized);
                    }
                }

                if (personnelId.HasValue)
                {
                    query = query.Where(x => x.EmployeeId == personnelId.Value);
                }

                if (from.HasValue)
                {
                    query = query.Where(x => x.ToDate >= from.Value);
                }

                if (to.HasValue)
                {
                    query = query.Where(x => x.FromDate <= to.Value);
                }

                var raw = await query
                    .OrderByDescending(x => x.AppliedDate)
                    .Select(x => new
                    {
                        x.Id,
                        x.EmployeeId,
                        x.LeaveTypeId,
                        leaveType = x.LeaveType!.LeaveName,
                        x.FromDate,
                        x.ToDate,
                        x.TotalDays,
                        x.IsHalfDay,
                        x.HalfDayType,
                        x.Reason,
                        x.Status,
                        x.AppliedDate,
                        x.ApprovedBy,
                        x.ApprovedDate,
                        x.CancelledDate
                    })
                    .ToListAsync(cancellationToken);

                var empById = attendanceService.ListEmployees()
                    .GroupBy(e => e.Id)
                    .ToDictionary(g => g.Key, g => g.First());

                var rows = raw.Select(x =>
                {
                    empById.TryGetValue(x.EmployeeId, out var emp);
                    return new
                    {
                        x.Id,
                        x.EmployeeId,
                        personnelCode = emp?.EmployeeCode ?? string.Empty,
                        personnelName = emp?.FullName ?? string.Empty,
                        personnelArea = emp?.Area ?? string.Empty,
                        personnelDepartment = emp?.Department ?? string.Empty,
                        x.LeaveTypeId,
                        x.leaveType,
                        x.FromDate,
                        x.ToDate,
                        x.TotalDays,
                        x.IsHalfDay,
                        x.HalfDayType,
                        x.Reason,
                        x.Status,
                        x.AppliedDate,
                        x.ApprovedBy,
                        x.ApprovedDate,
                        x.CancelledDate
                    };
                }).ToList();

                return Results.Ok(rows);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] Error GET /api/leave: {ex}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/leave", async (PbAttendanceDbContext db, AttendanceService attendanceService, LeaveCreateRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                if (request.EmployeeId == Guid.Empty || request.LeaveTypeId <= 0)
                {
                    return Results.BadRequest(new { message = "personnel_id and leave_type_id are required." });
                }

                if (request.ToDate < request.FromDate)
                {
                    return Results.BadRequest(new { message = "to_date cannot be before from_date." });
                }

                // Check via the raw repository (Employees.Id is TEXT; an EF Guid comparison throws).
                var personExists = attendanceService.ListEmployees()
                    .Any(e => e.Id == request.EmployeeId);
                if (!personExists)
                {
                    return Results.NotFound(new { message = "Personnel not found." });
                }

                var leaveType = await db.LeaveTypes.FirstOrDefaultAsync(x => x.Id == request.LeaveTypeId && x.IsActive, cancellationToken);
                if (leaveType is null)
                {
                    return Results.BadRequest(new { message = "Active leave type not found." });
                }

                var isHalfDay = request.IsHalfDay ?? false;
                var halfDayType = NormalizeHalfDayType(request.HalfDayType);
                if (isHalfDay)
                {
                    if (!leaveType.AllowHalfDay)
                    {
                        return Results.BadRequest(new { message = "Selected leave type does not allow half-day leave." });
                    }

                    if (request.FromDate != request.ToDate)
                    {
                        return Results.BadRequest(new { message = "Half-day leave must be a single date request." });
                    }

                    if (halfDayType is null)
                    {
                        return Results.BadRequest(new { message = "half_day_type must be first_half or second_half." });
                    }
                }
                else
                {
                    halfDayType = null;
                }

                // Copy to a local var before use in the LINQ predicate: passing request.EmployeeId
                // (a record property) directly caused Npgsql to parameterize it as "character
                // varying" instead of "uuid" ("operator does not exist: uuid = character varying"),
                // so every leave submission failed at this overlap check. A plain local Guid
                // variable parameterizes correctly — same workaround already used below for
                // employeeId/departmentId in the timetable-assignment queries.
                var employeeIdForOverlapCheck = request.EmployeeId;
                var overlapExists = await db.LeaveRequests.AnyAsync(
                    x => x.EmployeeId == employeeIdForOverlapCheck &&
                         x.Status != "rejected" &&
                         x.Status != "cancelled" &&
                         x.FromDate <= request.ToDate &&
                         x.ToDate >= request.FromDate,
                    cancellationToken);
                if (overlapExists)
                {
                    return Results.BadRequest(new { message = "Overlapping leave request already exists." });
                }

                var entity = new LeaveRequestEntity
                {
                    EmployeeId = request.EmployeeId,
                    LeaveTypeId = request.LeaveTypeId,
                    FromDate = request.FromDate,
                    ToDate = request.ToDate,
                    TotalDays = CalculateLeaveDays(request.FromDate, request.ToDate, isHalfDay),
                    IsHalfDay = isHalfDay,
                    HalfDayType = halfDayType,
                    Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
                    Status = "pending",
                    AppliedDate = DateTime.UtcNow
                };

                db.LeaveRequests.Add(entity);
                await db.SaveChangesAsync(cancellationToken);

                AddLeaveRemark(db, entity.Id, "applied", request.ActorId ?? request.EmployeeId, request.Remarks ?? request.Reason);
                if (!string.IsNullOrWhiteSpace(request.AttachmentUrl))
                {
                    db.LeaveAttachments.Add(new LeaveAttachmentEntity
                    {
                        LeaveRequestId = entity.Id,
                        FileName = string.IsNullOrWhiteSpace(request.AttachmentName) ? "attachment" : request.AttachmentName.Trim(),
                        FileUrl = request.AttachmentUrl.Trim(),
                        UploadedAt = DateTime.UtcNow,
                        UploadedBy = request.ActorId
                    });
                }

                await db.SaveChangesAsync(cancellationToken);
                // Return a flat DTO — serializing the EF entity pulls in LeaveType.LeaveRequests.…
                // navigation cycles and throws "object cycle detected" (500) even though the row saved.
                return Results.Created($"/api/leave/{entity.Id}", new
                {
                    entity.Id,
                    entity.EmployeeId,
                    entity.LeaveTypeId,
                    entity.FromDate,
                    entity.ToDate,
                    entity.TotalDays,
                    entity.IsHalfDay,
                    entity.HalfDayType,
                    entity.Reason,
                    entity.Status,
                    entity.AppliedDate
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] Error applying leave: {ex.ToString()}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        app.MapPut("/api/leave/{id:int}/approve", async (PbAttendanceDbContext db, int id, LeaveDecisionRequest request, CancellationToken cancellationToken) =>
        {
            var entity = await db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound(new { message = "Leave request not found." });
            }

            var targetStatus = NormalizeLeaveStatus(request.Status, "approved");
            if (targetStatus is not ("approved" or "rejected"))
            {
                return Results.BadRequest(new { message = "Status must be approved or rejected." });
            }

            var result = await ApplyLeaveDecisionAsync(
                db,
                entity,
                targetStatus,
                request.ActorId ?? request.ApprovedBy,
                request.Remarks,
                cancellationToken);
            if (!result.Success)
            {
                return Results.BadRequest(new { message = result.ErrorMessage });
            }

            return Results.Ok(new { entity.Id, entity.EmployeeId, entity.LeaveTypeId, entity.FromDate, entity.ToDate, entity.TotalDays, entity.IsHalfDay, entity.HalfDayType, entity.Reason, entity.Status, entity.AppliedDate, entity.ApprovedBy, entity.ApprovedDate, entity.CancelledDate });
        });

        app.MapPut("/api/leave/{id:int}/reject", async (PbAttendanceDbContext db, int id, LeaveDecisionRequest request, CancellationToken cancellationToken) =>
        {
            var entity = await db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound(new { message = "Leave request not found." });
            }

            var result = await ApplyLeaveDecisionAsync(
                db,
                entity,
                "rejected",
                request.ActorId ?? request.ApprovedBy,
                request.Remarks,
                cancellationToken);
            if (!result.Success)
            {
                return Results.BadRequest(new { message = result.ErrorMessage });
            }

            return Results.Ok(new { entity.Id, entity.EmployeeId, entity.LeaveTypeId, entity.FromDate, entity.ToDate, entity.TotalDays, entity.IsHalfDay, entity.HalfDayType, entity.Reason, entity.Status, entity.AppliedDate, entity.ApprovedBy, entity.ApprovedDate, entity.CancelledDate });
        });

        app.MapPut("/api/leave/{id:int}/cancel", async (PbAttendanceDbContext db, int id, LeaveCancelRequest request, CancellationToken cancellationToken) =>
        {
            var entity = await db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound(new { message = "Leave request not found." });
            }

            var result = await ApplyLeaveDecisionAsync(
                db,
                entity,
                "cancelled",
                request.ActorId,
                request.Remarks,
                cancellationToken);
            if (!result.Success)
            {
                return Results.BadRequest(new { message = result.ErrorMessage });
            }

            return Results.Ok(new { entity.Id, entity.EmployeeId, entity.LeaveTypeId, entity.FromDate, entity.ToDate, entity.TotalDays, entity.IsHalfDay, entity.HalfDayType, entity.Reason, entity.Status, entity.AppliedDate, entity.ApprovedBy, entity.ApprovedDate, entity.CancelledDate });
        });

        app.MapPut("/api/leave/bulk-decision", async (PbAttendanceDbContext db, LeaveBulkDecisionRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                if (request.LeaveIds is null || request.LeaveIds.Count == 0)
                {
                    return Results.BadRequest(new { message = "leave_ids is required." });
                }

                var targetStatus = NormalizeLeaveStatus(request.Status, "approved");
                if (targetStatus == "pending")
                {
                    return Results.BadRequest(new { message = "Bulk decision cannot set pending status." });
                }

                var ids = request.LeaveIds.Where(x => x > 0).Distinct().ToArray();
                if (ids.Length == 0)
                {
                    return Results.BadRequest(new { message = "No valid leave ids provided." });
                }

                var entities = await db.LeaveRequests.Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
                var foundIds = entities.Select(x => x.Id).ToHashSet();
                var missingIds = ids.Where(x => !foundIds.Contains(x)).ToArray();

                var updated = 0;
                var skipped = new List<object>();
                foreach (var entity in entities)
                {
                    var result = await ApplyLeaveDecisionAsync(
                        db,
                        entity,
                        targetStatus,
                        request.ActorId ?? request.ApprovedBy,
                        request.Remarks,
                        cancellationToken);
                    if (result.Success)
                    {
                        updated++;
                    }
                    else
                    {
                        skipped.Add(new { id = entity.Id, message = result.ErrorMessage });
                    }
                }

                return Results.Ok(new { requested = ids.Length, updated, skipped, missing = missingIds });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] Error PUT /api/leave/bulk-decision: {ex}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        app.MapGet("/api/leave/{id:int}/remarks", async (PbAttendanceDbContext db, int id, CancellationToken cancellationToken) =>
        {
            try
            {
                var exists = await db.LeaveRequests.AnyAsync(x => x.Id == id, cancellationToken);
                if (!exists)
                {
                    return Results.NotFound(new { message = "Leave request not found." });
                }

                var rows = await db.LeaveRemarks
                    .AsNoTracking()
                    .Where(x => x.LeaveRequestId == id)
                    .OrderByDescending(x => x.CreatedAt)
                    .Select(x => new { x.Id, x.Action, x.Remarks, x.ActorId, x.CreatedAt })
                    .ToListAsync(cancellationToken);

                return Results.Ok(rows);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] Error GET /api/leave/{id}/remarks: {ex}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/leave/{id:int}/remarks", async (PbAttendanceDbContext db, int id, LeaveRemarkCreateRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var exists = await db.LeaveRequests.AnyAsync(x => x.Id == id, cancellationToken);
                if (!exists)
                {
                    return Results.NotFound(new { message = "Leave request not found." });
                }

                AddLeaveRemark(db, id, request.Action ?? "remark", request.ActorId, request.Remarks);
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] Error POST /api/leave/{id}/remarks: {ex}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        app.MapGet("/api/leave/{id:int}/attachments", async (PbAttendanceDbContext db, int id, CancellationToken cancellationToken) =>
        {
            try
            {
                var exists = await db.LeaveRequests.AnyAsync(x => x.Id == id, cancellationToken);
                if (!exists)
                {
                    return Results.NotFound(new { message = "Leave request not found." });
                }

                var rows = await db.LeaveAttachments
                    .AsNoTracking()
                    .Where(x => x.LeaveRequestId == id)
                    .OrderByDescending(x => x.UploadedAt)
                    .Select(x => new { x.Id, x.FileName, x.FileUrl, x.UploadedAt, x.UploadedBy })
                    .ToListAsync(cancellationToken);

                return Results.Ok(rows);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] Error GET /api/leave/{id}/attachments: {ex}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/leave/{id:int}/attachments", async (PbAttendanceDbContext db, int id, LeaveAttachmentCreateRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.FileUrl))
                {
                    return Results.BadRequest(new { message = "file_url is required." });
                }

                var exists = await db.LeaveRequests.AnyAsync(x => x.Id == id, cancellationToken);
                if (!exists)
                {
                    return Results.NotFound(new { message = "Leave request not found." });
                }

                var entity = new LeaveAttachmentEntity
                {
                    LeaveRequestId = id,
                    FileName = string.IsNullOrWhiteSpace(request.FileName) ? "attachment" : request.FileName.Trim(),
                    FileUrl = request.FileUrl.Trim(),
                    UploadedAt = DateTime.UtcNow,
                    UploadedBy = request.UploadedBy
                };
                db.LeaveAttachments.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return Results.Created($"/api/leave/{id}/attachments/{entity.Id}", entity);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] Error POST /api/leave/{id}/attachments: {ex}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        app.MapGet("/api/leave/balance", async (PbAttendanceDbContext db, int? year, Guid? personnelId, int? leaveTypeId, CancellationToken cancellationToken) =>
        {
            try
            {
                var targetYear = year ?? DateTime.UtcNow.Year;
                await EnsureLeaveBalancesForYearAsync(db, targetYear, cancellationToken);

                var query = db.LeaveBalances
                    .AsNoTracking()
                    .Include(x => x.Personnel)
                    .Include(x => x.LeaveType)
                    .Where(x => x.Year == targetYear)
                    .AsQueryable();

                if (personnelId.HasValue)
                {
                    query = query.Where(x => x.EmployeeId == personnelId.Value);
                }

                if (leaveTypeId.HasValue)
                {
                    query = query.Where(x => x.LeaveTypeId == leaveTypeId.Value);
                }

                var rows = await query
                    .OrderBy(x => x.Personnel!.EmployeeCode)
                    .ThenBy(x => x.LeaveType!.LeaveName)
                    .Select(x => new
                    {
                        x.Id,
                        x.EmployeeId,
                        personnelCode = x.Personnel!.EmployeeCode,
                        personnelName = $"{x.Personnel!.FirstName} {x.Personnel.LastName}",
                        x.LeaveTypeId,
                        leaveType = x.LeaveType!.LeaveName,
                        x.Year,
                        x.TotalAllocated,
                        x.Used,
                        x.Remaining,
                        x.UpdatedAt
                    })
                    .ToListAsync(cancellationToken);

                return Results.Ok(rows);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] Error GET /api/leave/balance: {ex}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/leave/balance/reset", async (PbAttendanceDbContext db, LeaveBalanceResetRequest request, CancellationToken cancellationToken) =>
        {
            var year = request.Year ?? DateTime.UtcNow.Year;
            await EnsureLeaveBalancesForYearAsync(db, year, cancellationToken);
            return Results.Ok(new { success = true, year });
        });

        // Disabled: duplicate of the raw handler in Program.cs (caused an AmbiguousMatchException → 500).
        app.MapGet("/api/_ef_disabled/personnel/holidays", async (PbAttendanceDbContext db, DateOnly? from, DateOnly? to, int? departmentId, bool? activeOnly, CancellationToken cancellationToken) =>
        {
            try
            {
                var query = db.Holidays.AsNoTracking().AsQueryable();
                if (activeOnly.GetValueOrDefault(true))
                {
                    query = query.Where(x => x.IsActive);
                }

                if (departmentId.HasValue)
                {
                    query = query.Where(x => x.DepartmentId == null || x.DepartmentId == departmentId.Value);
                }

                if (from.HasValue)
                {
                    query = query.Where(x => x.HolidayDate >= from.Value);
                }

                if (to.HasValue)
                {
                    query = query.Where(x => x.HolidayDate <= to.Value);
                }

                var rows = await query
                    .OrderBy(x => x.HolidayDate)
                    .ThenBy(x => x.HolidayName)
                    .Select(x => new
                    {
                        x.Id,
                        x.HolidayName,
                        x.HolidayDate,
                        x.Description,
                        x.IsRecurring,
                        x.DepartmentId,
                        x.TimeZoneId,
                        x.IsActive
                    })
                    .ToListAsync(cancellationToken);

                return Results.Ok(rows);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] Error GET /api/personnel/holidays: {ex}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        // Disabled: duplicate of the raw handler in Program.cs.
        app.MapPost("/api/_ef_disabled/personnel/holidays", async (PbAttendanceDbContext db, IDeviceProtocolService deviceProtocolService, AttendanceService attendanceService, HolidayUpsertRequest request, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.HolidayName))
            {
                return Results.BadRequest(new { message = "holiday_name is required." });
            }

            var holidayName = request.HolidayName.Trim();
            var duplicate = await db.Holidays.AnyAsync(
                x => x.HolidayDate == request.HolidayDate &&
                     x.DepartmentId == request.DepartmentId &&
                     x.HolidayName.ToLower() == holidayName.ToLower() &&
                     x.IsActive,
                cancellationToken);
            if (duplicate)
            {
                return Results.BadRequest(new { message = "Holiday already exists for selected date and department." });
            }

            var entity = new HolidayEntity
            {
                HolidayName = holidayName,
                HolidayDate = request.HolidayDate,
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                IsRecurring = request.IsRecurring ?? false,
                DepartmentId = request.DepartmentId,
                TimeZoneId = request.TimeZoneId,
                IsActive = request.IsActive ?? true
            };

            db.Holidays.Add(entity);
            await db.SaveChangesAsync(cancellationToken);

            await QueueHolidaySyncCommand(db, entity, deviceProtocolService, attendanceService, cancellationToken);
            
            // Sync holiday to AttendanceRecords and TransactionDetails
            await SyncHolidayToAttendanceAsync(db, entity, cancellationToken);
            
            return Results.Created($"/api/personnel/holidays/{entity.Id}", entity);
        });

        // Disabled: duplicate of the raw handler in Program.cs.
        app.MapPut("/api/_ef_disabled/personnel/holidays/{id:int}", async (PbAttendanceDbContext db, IDeviceProtocolService deviceProtocolService, AttendanceService attendanceService, int id, HolidayUpsertRequest request, CancellationToken cancellationToken) =>
        {
            var entity = await db.Holidays.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound(new { message = "Holiday not found." });
            }

            if (!string.IsNullOrWhiteSpace(request.HolidayName))
            {
                entity.HolidayName = request.HolidayName.Trim();
            }

            entity.HolidayDate = request.HolidayDate;
            entity.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            entity.IsRecurring = request.IsRecurring ?? entity.IsRecurring;
            entity.DepartmentId = request.DepartmentId;
            entity.TimeZoneId = request.TimeZoneId ?? entity.TimeZoneId;
            entity.IsActive = request.IsActive ?? entity.IsActive;

            await db.SaveChangesAsync(cancellationToken);
            await QueueHolidaySyncCommand(db, entity, deviceProtocolService, attendanceService, cancellationToken);
            
            // Sync holiday to AttendanceRecords and TransactionDetails
            await SyncHolidayToAttendanceAsync(db, entity, cancellationToken);
            
            return Results.Ok(entity);
        });

        // Disabled: duplicate of the raw handler in Program.cs.
        app.MapDelete("/api/_ef_disabled/personnel/holidays/{id:int}", async (PbAttendanceDbContext db, IDeviceProtocolService deviceProtocolService, AttendanceService attendanceService, int id, CancellationToken cancellationToken) =>
        {
            var entity = await db.Holidays.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound(new { message = "Holiday not found." });
            }

            entity.IsActive = false;
            await db.SaveChangesAsync(cancellationToken);
            await QueueHolidaySyncCommand(db, entity, deviceProtocolService, attendanceService, cancellationToken);
            return Results.Ok(new { success = true });
        });

        app.MapGet("/api/reports/leave/history", async (PbAttendanceDbContext db, Guid? personnelId, string? status, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
        {
            var query = db.LeaveRequests
                .AsNoTracking()
                .Include(x => x.Personnel)
                .Include(x => x.LeaveType)
                .AsQueryable();

            if (personnelId.HasValue)
            {
                query = query.Where(x => x.EmployeeId == personnelId.Value);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                var normalized = NormalizeLeaveStatus(status, "all");
                if (!string.Equals(normalized, "all", StringComparison.Ordinal))
                {
                    query = query.Where(x => x.Status == normalized);
                }
            }

            if (from.HasValue)
            {
                query = query.Where(x => x.ToDate >= from.Value);
            }

            if (to.HasValue)
            {
                query = query.Where(x => x.FromDate <= to.Value);
            }

            var rows = await query
                .OrderByDescending(x => x.AppliedDate)
                .Select(x => new
                {
                    x.Id,
                    x.EmployeeId,
                    personnelCode = x.Personnel!.EmployeeCode,
                    personnelName = $"{x.Personnel!.FirstName} {x.Personnel.LastName}",
                    departmentId = x.Personnel!.DepartmentId,
                    x.LeaveTypeId,
                    leaveType = x.LeaveType!.LeaveName,
                    x.FromDate,
                    x.ToDate,
                    x.TotalDays,
                    x.IsHalfDay,
                    x.HalfDayType,
                    x.Reason,
                    x.Status,
                    x.AppliedDate,
                    x.ApprovedBy,
                    x.ApprovedDate,
                    x.CancelledDate
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(rows);
        });

        app.MapGet("/api/reports/leave/balance", async (PbAttendanceDbContext db, int? year, CancellationToken cancellationToken) =>
        {
            var targetYear = year ?? DateTime.UtcNow.Year;
            await EnsureLeaveBalancesForYearAsync(db, targetYear, cancellationToken);

            var rows = await db.LeaveBalances
                .AsNoTracking()
                .Include(x => x.Personnel)
                .Include(x => x.LeaveType)
                .Where(x => x.Year == targetYear)
                .OrderBy(x => x.Personnel!.DepartmentId)
                .ThenBy(x => x.Personnel!.EmployeeCode)
                .Select(x => new
                {
                    x.EmployeeId,
                    personnelCode = x.Personnel!.EmployeeCode,
                    personnelName = $"{x.Personnel!.FirstName} {x.Personnel.LastName}",
                    departmentId = x.Personnel!.DepartmentId,
                    x.LeaveTypeId,
                    leaveType = x.LeaveType!.LeaveName,
                    x.Year,
                    x.TotalAllocated,
                    x.Used,
                    x.Remaining
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(rows);
        });

        app.MapGet("/api/reports/leave/summary", async (PbAttendanceDbContext db, int? year, CancellationToken cancellationToken) =>
        {
            var targetYear = year ?? DateTime.UtcNow.Year;
            var start = new DateOnly(targetYear, 1, 1);
            var end = new DateOnly(targetYear, 12, 31);

            var rows = await db.LeaveRequests
                .AsNoTracking()
                .Include(x => x.Personnel)
                .Include(x => x.LeaveType)
                .Where(x => x.Status == "approved" && x.ToDate >= start && x.FromDate <= end)
                .ToListAsync(cancellationToken);

            var summary = rows
                .GroupBy(x => new { x.Personnel?.DepartmentId, LeaveType = x.LeaveType?.LeaveName ?? "Unknown" })
                .Select(g => new
                {
                    departmentId = g.Key.DepartmentId,
                    leaveType = g.Key.LeaveType,
                    requestCount = g.Count(),
                    totalDays = g.Sum(x => CalculateOverlappingLeaveDays(x.FromDate, x.ToDate, x.IsHalfDay, start, end))
                })
                .OrderBy(x => x.departmentId)
                .ThenBy(x => x.leaveType)
                .ToList();

            return Results.Ok(summary);
        });

        app.MapGet("/api/reports/leave/monthly", async (PbAttendanceDbContext db, int? year, int? month, CancellationToken cancellationToken) =>
        {
            var now = DateTime.UtcNow.Date;
            var y = year ?? now.Year;
            var m = month ?? now.Month;
            if (m is < 1 or > 12)
            {
                return Results.BadRequest(new { message = "month must be between 1 and 12." });
            }

            var start = new DateOnly(y, m, 1);
            var end = start.AddMonths(1).AddDays(-1);

            var rows = await db.LeaveRequests
                .AsNoTracking()
                .Include(x => x.Personnel)
                .Include(x => x.LeaveType)
                .Where(x => x.Status == "approved" && x.ToDate >= start && x.FromDate <= end)
                .ToListAsync(cancellationToken);

            var summary = rows
                .GroupBy(x => new
                {
                    x.EmployeeId,
                    EmployeeCode = x.Personnel?.EmployeeCode ?? string.Empty,
                    PersonnelName = $"{x.Personnel?.FirstName} {x.Personnel?.LastName}".Trim(),
                    LeaveType = x.LeaveType?.LeaveName ?? "Unknown"
                })
                .Select(g => new
                {
                    g.Key.EmployeeId,
                    g.Key.EmployeeCode,
                    g.Key.PersonnelName,
                    g.Key.LeaveType,
                    requestCount = g.Count(),
                    totalDays = g.Sum(x => CalculateOverlappingLeaveDays(x.FromDate, x.ToDate, x.IsHalfDay, start, end))
                })
                .OrderBy(x => x.EmployeeCode)
                .ThenBy(x => x.LeaveType)
                .ToList();

            return Results.Ok(new { year = y, month = m, rows = summary });
        });

        app.MapGet("/api/reports/holiday/list", async (PbAttendanceDbContext db, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
        {
            var query = db.Holidays.AsNoTracking().Where(x => x.IsActive).AsQueryable();
            if (from.HasValue)
            {
                query = query.Where(x => x.HolidayDate >= from.Value);
            }

            if (to.HasValue)
            {
                query = query.Where(x => x.HolidayDate <= to.Value);
            }

            var rows = await query
                .OrderBy(x => x.HolidayDate)
                .ThenBy(x => x.HolidayName)
                .Select(x => new
                {
                    x.Id,
                    x.HolidayName,
                    x.HolidayDate,
                    x.Description,
                    x.IsRecurring,
                    x.DepartmentId
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(rows);
        });

        app.MapGet("/api/reports/holiday/work", async (PbAttendanceDbContext db, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
        {
            var start = from ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-30));
            var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);

            var rows = await db.AttendanceRecords
                .AsNoTracking()
                .Include(x => x.Personnel)
                .Where(x => x.AttendanceDate >= start && x.AttendanceDate <= end && x.Status == "Holiday Work")
                .OrderByDescending(x => x.AttendanceDate)
                .Select(x => new
                {
                    x.Id,
                    x.EmployeeId,
                    personnelCode = x.Personnel!.EmployeeCode,
                    personnelName = $"{x.Personnel!.FirstName} {x.Personnel.LastName}",
                    x.AttendanceDate,
                    x.FirstIn,
                    x.LastOut,
                    x.TotalWorkHours,
                    x.OvertimeMinutes
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(rows);
        });

        app.MapGet("/api/reports/daily", async (PbAttendanceDbContext db, DateOnly? date, CancellationToken cancellationToken) =>
        {
            var targetDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);
            var rows = await db.AttendanceRecords
                .AsNoTracking()
                .Where(x => x.AttendanceDate == targetDate)
                .GroupBy(x => x.Status)
                .Select(g => new { status = g.Key, count = g.Count() })
                .ToListAsync(cancellationToken);
            return Results.Ok(new { date = targetDate, stats = rows });
        });

        app.MapGet("/api/reports/monthly", async (PbAttendanceDbContext db, int? year, int? month, CancellationToken cancellationToken) =>
        {
            var now = DateTime.UtcNow.Date;
            var y = year ?? now.Year;
            var m = month ?? now.Month;
            var from = new DateOnly(y, m, 1);
            var to = from.AddMonths(1);

            var rows = await db.AttendanceRecords
                .AsNoTracking()
                .Where(x => x.AttendanceDate >= from && x.AttendanceDate < to)
                .GroupBy(x => x.Status)
                .Select(g => new { status = g.Key, count = g.Count() })
                .ToListAsync(cancellationToken);

            return Results.Ok(new { year = y, month = m, stats = rows });
        });

        app.MapGet("/api/reports/export", async (PbAttendanceDbContext db, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-30));
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);

            var rows = await db.AttendanceRecords
                .AsNoTracking()
                .Include(x => x.Personnel)
                .Where(x => x.AttendanceDate >= fromDate && x.AttendanceDate <= toDate)
                .OrderBy(x => x.AttendanceDate)
                .ToListAsync(cancellationToken);

            var csv = new StringBuilder();
            csv.AppendLine("Date,EmployeeCode,Name,Status,FirstIn,LastOut,WorkHours,LateMinutes,EarlyExitMinutes,OvertimeMinutes");
            foreach (var item in rows)
            {
                var name = $"{item.Personnel?.FirstName} {item.Personnel?.LastName}".Trim();
                csv.Append(item.AttendanceDate).Append(',')
                   .Append(item.Personnel?.EmployeeCode).Append(',')
                   .Append(EscapeCsv(name)).Append(',')
                   .Append(item.Status).Append(',')
                   .Append(item.FirstIn?.ToString("O")).Append(',')
                   .Append(item.LastOut?.ToString("O")).Append(',')
                   .Append(item.TotalWorkHours).Append(',')
                   .Append(item.LateMinutes).Append(',')
                   .Append(item.EarlyExitMinutes).Append(',')
                   .Append(item.OvertimeMinutes)
                   .AppendLine();
            }

            return Results.Text(csv.ToString(), "text/csv");
        });

         // Timetable endpoints 
        app.MapGet("/api/timetables", async (PbAttendanceDbContext db, ITimetableRepository timetableRepo, CancellationToken cancellationToken) =>
         {
             var timetables = await timetableRepo.GetAllAsync(cancellationToken);
             return Results.Ok(timetables);
         });

        // Break time endpoints (ZKBioTime: Attendance > Shift > Break Time)
        app.MapGet("/api/break-times", async (PbAttendanceDbContext db, CancellationToken cancellationToken) =>
        {
            var rows = await db.BreakTimes
                .AsNoTracking()
                .OrderBy(x => x.BreakName)
                .ToListAsync(cancellationToken);
            return Results.Ok(rows);
        });

        app.MapPost("/api/break-times", async (PbAttendanceDbContext db, BreakTimeUpsertRequest request, CancellationToken cancellationToken) =>
        {
            var entity = new BreakTimeEntity
            {
                BreakName = string.IsNullOrWhiteSpace(request.BreakName) ? "New Break" : request.BreakName.Trim(),
                StartTime = TimeOnly.TryParse(request.StartTime, out var start) ? start : new TimeOnly(12, 0),
                EndTime = TimeOnly.TryParse(request.EndTime, out var end) ? end : new TimeOnly(13, 0),
                DurationMinutes = request.DurationMinutes ?? 0,
                CalculateType = string.IsNullOrWhiteSpace(request.CalculateType) ? "auto_deduct" : request.CalculateType.Trim().ToLowerInvariant(),
                MinimumBreakMinutes = request.MinimumBreakMinutes ?? 0,
                IsActive = request.IsActive ?? true
            };

            db.BreakTimes.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/break-times/{entity.Id}", entity);
        });

        app.MapPut("/api/break-times/{id:int}", async (PbAttendanceDbContext db, int id, BreakTimeUpsertRequest request, CancellationToken cancellationToken) =>
        {
            var entity = await db.BreakTimes.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound();
            }

            if (!string.IsNullOrWhiteSpace(request.BreakName))
            {
                entity.BreakName = request.BreakName.Trim();
            }
            if (TimeOnly.TryParse(request.StartTime, out var start))
            {
                entity.StartTime = start;
            }
            if (TimeOnly.TryParse(request.EndTime, out var end))
            {
                entity.EndTime = end;
            }

            entity.DurationMinutes = request.DurationMinutes ?? entity.DurationMinutes;
            if (!string.IsNullOrWhiteSpace(request.CalculateType))
            {
                entity.CalculateType = request.CalculateType.Trim().ToLowerInvariant();
            }
            entity.MinimumBreakMinutes = request.MinimumBreakMinutes ?? entity.MinimumBreakMinutes;
            entity.IsActive = request.IsActive ?? entity.IsActive;

            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(entity);
        });

        app.MapDelete("/api/break-times/{id:int}", async (PbAttendanceDbContext db, int id, CancellationToken cancellationToken) =>
        {
            var entity = await db.BreakTimes.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound();
            }

            db.BreakTimes.Remove(entity);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new { success = true });
        });

         app.MapGet("/api/timetables/{id:int}", async (PbAttendanceDbContext db, ITimetableRepository timetableRepo, int id, CancellationToken cancellationToken) =>
         {
             var timetable = await timetableRepo.GetByIdAsync(id, cancellationToken);
             return timetable is not null ? Results.Ok(timetable) : Results.NotFound();
         });

         app.MapPost("/api/timetables", async (PbAttendanceDbContext db, ITimetableRepository timetableRepo, IDeviceProtocolService deviceProtocolService, AttendanceService attendanceService, TimetableUpsertRequest request, CancellationToken cancellationToken) =>
         {
             var timetable = new TimetableEntity
             {
                 TimetableName = request.TimetableName ?? "New Timetable",
                 IsActive = request.IsActive ?? true
             };

             db.Timetables.Add(timetable);
             await db.SaveChangesAsync(cancellationToken);

             // Queue timetable sync command for all devices
             await QueueTimetableSyncCommand(db, timetable.Id, deviceProtocolService, attendanceService);

             return Results.Created($"/api/timetables/{timetable.Id}", timetable);
         });

         app.MapPut("/api/timetables/{id:int}", async (PbAttendanceDbContext db, ITimetableRepository timetableRepo, IDeviceProtocolService deviceProtocolService, AttendanceService attendanceService, int id, TimetableUpsertRequest request, CancellationToken cancellationToken) =>
         {
             var timetable = await timetableRepo.GetByIdAsync(id, cancellationToken);
             if (timetable == null)
                 return Results.NotFound();

             timetable.TimetableName = request.TimetableName ?? timetable.TimetableName;
             timetable.IsActive = request.IsActive ?? timetable.IsActive;

             await db.SaveChangesAsync(cancellationToken);

             // Queue timetable sync command for all devices
             await QueueTimetableSyncCommand(db, timetable.Id, deviceProtocolService, attendanceService);

             return Results.Ok(timetable);
         });

         app.MapDelete("/api/timetables/{id:int}", async (PbAttendanceDbContext db, ITimetableRepository timetableRepo, IDeviceProtocolService deviceProtocolService, AttendanceService attendanceService, int id, CancellationToken cancellationToken) =>
         {
             var timetable = await timetableRepo.GetByIdAsync(id, cancellationToken);
             if (timetable == null)
                 return Results.NotFound();

             db.Timetables.Remove(timetable);
             await db.SaveChangesAsync(cancellationToken);

             // Queue timetable sync command for all devices (to remove)
             await QueueTimetableSyncCommand(db, timetable.Id, deviceProtocolService, attendanceService);

             return Results.Ok();
         });

         // Timetable day endpoints
         app.MapGet("/api/timetables/{id:int}/days", async (PbAttendanceDbContext db, ITimetableRepository timetableRepo, int id, CancellationToken cancellationToken) =>
         {
             var timetable = await timetableRepo.GetByIdAsync(id, cancellationToken);
             if (timetable == null)
                 return Results.NotFound();

             return Results.Ok(timetable.Days);
         });

         app.MapPost("/api/timetables/{id:int}/days", async (PbAttendanceDbContext db, ITimetableRepository timetableRepo, IDeviceProtocolService deviceProtocolService, AttendanceService attendanceService, int id, TimetableDayUpsertRequest request, CancellationToken cancellationToken) =>
         {
             var timetable = await timetableRepo.GetByIdAsync(id, cancellationToken);
             if (timetable == null)
                 return Results.NotFound();

             var dayOfWeek = request.DayOfWeek ?? 0;
             var existingDay = timetable.Days.FirstOrDefault(d => d.DayOfWeek == dayOfWeek);
             var breakTimeIds = (request.BreakTimeIds ?? [])
                 .Where(x => x > 0)
                 .Distinct()
                 .ToList();
             if (breakTimeIds.Count == 0 && request.BreakTimeId.HasValue && request.BreakTimeId.Value > 0)
             {
                 breakTimeIds.Add(request.BreakTimeId.Value);
             }
             var firstBreakTimeId = breakTimeIds.Count > 0 ? breakTimeIds[0] : request.BreakTimeId;
             if (existingDay != null)
             {
                 // Update existing day
                 existingDay.ShiftId = request.ShiftId;
                 existingDay.IsWeeklyOff = request.IsWeeklyOff ?? existingDay.IsWeeklyOff;
                 existingDay.CheckInTime = TimeOnly.TryParse(request.CheckInTime, out var t1) ? t1 : existingDay.CheckInTime;
                 existingDay.CheckInStartTime = TimeOnly.TryParse(request.CheckInStartTime, out var t2) ? t2 : existingDay.CheckInStartTime;
                 existingDay.CheckInEndTime = TimeOnly.TryParse(request.CheckInEndTime, out var t3) ? t3 : existingDay.CheckInEndTime;
                 existingDay.CheckOutStartTime = TimeOnly.TryParse(request.CheckOutStartTime, out var t4) ? t4 : existingDay.CheckOutStartTime;
                 existingDay.CheckOutTime = TimeOnly.TryParse(request.CheckOutTime, out var t5) ? t5 : existingDay.CheckOutTime;
                 existingDay.CheckOutEndTime = TimeOnly.TryParse(request.CheckOutEndTime, out var t6) ? t6 : existingDay.CheckOutEndTime;
                 existingDay.CrossDay = request.CrossDay ?? existingDay.CrossDay;
                 existingDay.UseBreakTime = request.UseBreakTime ?? (breakTimeIds.Count > 0);
                 existingDay.BreakTimeId = firstBreakTimeId;
             }
             else
             {
                 // Add new day
                 var newDay = new TimetableDayEntity
                 {
                     TimetableId = id,
                     DayOfWeek = dayOfWeek,
                     ShiftId = request.ShiftId,
                     IsWeeklyOff = request.IsWeeklyOff ?? false,
                     CheckInTime = TimeOnly.TryParse(request.CheckInTime, out var t1) ? t1 : new TimeOnly(10, 0),
                     CheckInStartTime = TimeOnly.TryParse(request.CheckInStartTime, out var t2) ? t2 : new TimeOnly(0, 0),
                     CheckInEndTime = TimeOnly.TryParse(request.CheckInEndTime, out var t3) ? t3 : new TimeOnly(10, 30),
                     CheckOutStartTime = TimeOnly.TryParse(request.CheckOutStartTime, out var t4) ? t4 : new TimeOnly(17, 0),
                     CheckOutTime = TimeOnly.TryParse(request.CheckOutTime, out var t5) ? t5 : new TimeOnly(23, 59),
                     CheckOutEndTime = TimeOnly.TryParse(request.CheckOutEndTime, out var t6) ? t6 : new TimeOnly(0, 59),
                     CrossDay = request.CrossDay ?? false,
                     UseBreakTime = request.UseBreakTime ?? (breakTimeIds.Count > 0),
                     BreakTimeId = firstBreakTimeId
                 };

                 timetable.Days.Add(newDay);
             }

             await db.SaveChangesAsync(cancellationToken);

             var dayEntity = timetable.Days.FirstOrDefault(d => d.DayOfWeek == dayOfWeek);
             if (dayEntity is not null)
             {
                 var existingMappings = await db.TimetableDayBreakTimes
                     .Where(x => x.TimetableDayId == dayEntity.Id)
                     .ToListAsync(cancellationToken);
                 db.TimetableDayBreakTimes.RemoveRange(existingMappings);

                 for (var i = 0; i < breakTimeIds.Count; i++)
                 {
                     db.TimetableDayBreakTimes.Add(new TimetableDayBreakTimeEntity
                     {
                         TimetableDayId = dayEntity.Id,
                         BreakTimeId = breakTimeIds[i],
                         SortOrder = i
                     });
                 }

                 await db.SaveChangesAsync(cancellationToken);
             }

             // Queue timetable sync command for all devices
             await QueueTimetableSyncCommand(db, id, deviceProtocolService, attendanceService);

             return Results.Ok();
         });

         app.MapDelete("/api/timetables/{id:int}/days/{dayOfWeek:int}", async (PbAttendanceDbContext db, ITimetableRepository timetableRepo, IDeviceProtocolService deviceProtocolService, AttendanceService attendanceService, int id, int dayOfWeek, CancellationToken cancellationToken) =>
         {
             var timetable = await timetableRepo.GetByIdAsync(id, cancellationToken);
             if (timetable == null)
                 return Results.NotFound();

             var day = timetable.Days.FirstOrDefault(d => d.DayOfWeek == dayOfWeek);
             if (day == null)
                 return Results.NotFound();

             timetable.Days.Remove(day);
             await db.SaveChangesAsync(cancellationToken);

             // Queue timetable sync command for all devices
             await QueueTimetableSyncCommand(db, id, deviceProtocolService, attendanceService);

             return Results.Ok();
         });

         // Timetable assignment endpoints
         app.MapPost("/api/employees/{employeeId:guid}/timetable", async (PbAttendanceDbContext db, ITimetableRepository timetableRepo, IDeviceProtocolService deviceProtocolService, AttendanceService attendanceService, Guid employeeId, EmployeeTimetableUpsertRequest request, CancellationToken cancellationToken) =>
         {
             var employee = await db.Personnel.FindAsync(employeeId);
             if (employee == null)
                 return Results.NotFound();

             var timetable = await timetableRepo.GetByIdAsync(request.TimetableId, cancellationToken);
             if (timetable == null)
                 return Results.NotFound();

             var existingAssignment = await db.EmployeeTimetables
                 .FirstOrDefaultAsync(et => et.EmployeeId == employeeId && et.TimetableId == request.TimetableId);

             if (existingAssignment != null)
             {
                 // Update existing assignment
                 existingAssignment.EffectiveFrom = request.EffectiveFrom;
                 existingAssignment.IsActive = request.IsActive ?? true;
             }
             else
             {
                 // Add new assignment
                 var newAssignment = new EmployeeTimetableEntity
                 {
                     EmployeeId = employeeId,
                     TimetableId = request.TimetableId,
                     EffectiveFrom = request.EffectiveFrom,
                     IsActive = request.IsActive ?? true
                 };

                 db.EmployeeTimetables.Add(newAssignment);
             }

             await db.SaveChangesAsync(cancellationToken);

             // Queue timetable sync command for all devices (since assignment affects which timetable is used)
             await QueueTimetableSyncCommand(db, request.TimetableId, deviceProtocolService, attendanceService);

             return Results.Ok();
         });

         app.MapPost("/api/departments/{departmentId:int}/timetable", async (PbAttendanceDbContext db, ITimetableRepository timetableRepo, IDeviceProtocolService deviceProtocolService, AttendanceService attendanceService, int departmentId, DepartmentTimetableUpsertRequest request, CancellationToken cancellationToken) =>
         {
             var timetable = await timetableRepo.GetByIdAsync(request.TimetableId, cancellationToken);
             if (timetable == null)
                 return Results.NotFound();

             var existingAssignment = await db.DepartmentTimetables
                 .FirstOrDefaultAsync(dt => dt.DepartmentId == departmentId && dt.TimetableId == request.TimetableId);

             if (existingAssignment != null)
             {
                 // Update existing assignment
                 existingAssignment.EffectiveFrom = request.EffectiveFrom;
                 existingAssignment.IsActive = request.IsActive ?? true;
             }
             else
             {
                 // Add new assignment
                 var newAssignment = new DepartmentTimetableEntity
                 {
                     DepartmentId = departmentId,
                     TimetableId = request.TimetableId,
                     EffectiveFrom = request.EffectiveFrom,
                     IsActive = request.IsActive ?? true
                 };

                 db.DepartmentTimetables.Add(newAssignment);
             }

             await db.SaveChangesAsync(cancellationToken);

             // Queue sync commands
             await QueueTimetableSyncCommand(db, request.TimetableId, deviceProtocolService, attendanceService);
             await QueueDepartmentSyncCommand(db, departmentId, request.TimetableId, deviceProtocolService, attendanceService);

             return Results.Ok();
         });

         app.MapDelete("/api/employees/{employeeId:guid}/timetable", async (PbAttendanceDbContext db, ITimetableRepository timetableRepo, Guid employeeId, CancellationToken cancellationToken) =>
         {
             var assignments = await db.EmployeeTimetables
                 .Where(et => et.EmployeeId == employeeId)
                 .ToListAsync(cancellationToken);

             db.EmployeeTimetables.RemoveRange(assignments);
             await db.SaveChangesAsync(cancellationToken);

             // Note: We don't know which timetables were affected, so we can't queue specific sync commands
             // In a real implementation, we might want to fetch the timetables first
             // For now, we'll skip queueing sync commands for deletion of assignments
             return Results.Ok();
         });

         app.MapDelete("/api/departments/{departmentId:int}/timetable", async (PbAttendanceDbContext db, ITimetableRepository timetableRepo, int departmentId, CancellationToken cancellationToken) =>
         {
             var assignments = await db.DepartmentTimetables
                 .Where(dt => dt.DepartmentId == departmentId)
                 .ToListAsync(cancellationToken);

             db.DepartmentTimetables.RemoveRange(assignments);
             await db.SaveChangesAsync(cancellationToken);

             // Note: We don't know which timetables were affected, so we can't queue specific sync commands
             // In a real implementation, we might want to fetch the timetables first
             // For now, we'll skip queueing sync commands for deletion of assignments
             return Results.Ok();
         });
    }

    private static void AddLeaveRemark(PbAttendanceDbContext db, int leaveRequestId, string action, Guid? actorId, string? remarks)
    {
        db.LeaveRemarks.Add(new LeaveRemarkEntity
        {
            LeaveRequestId = leaveRequestId,
            Action = string.IsNullOrWhiteSpace(action) ? "remark" : action.Trim().ToLowerInvariant(),
            ActorId = actorId,
            Remarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim(),
            CreatedAt = DateTime.UtcNow
        });
    }

    private static async Task<(bool Success, string? ErrorMessage)> ApplyLeaveDecisionAsync(
        PbAttendanceDbContext db,
        LeaveRequestEntity entity,
        string targetStatus,
        Guid? actorId,
        string? remarks,
        CancellationToken cancellationToken)
    {
        if (!ValidLeaveStatuses.Contains(targetStatus))
        {
            return (false, "Invalid leave status.");
        }

        if (targetStatus == "pending")
        {
            return (false, "Cannot set pending status as a decision.");
        }

        var years = Enumerable.Range(entity.FromDate.Year, entity.ToDate.Year - entity.FromDate.Year + 1).ToArray();
        foreach (var year in years)
        {
            await EnsureLeaveBalancesForYearAsync(db, year, cancellationToken);
        }

        var currentStatus = NormalizeLeaveStatus(entity.Status, "pending");
        if (targetStatus == "approved" && !string.Equals(currentStatus, "approved", StringComparison.OrdinalIgnoreCase))
        {
            var usageByYear = SplitLeaveDaysByYear(entity.FromDate, entity.ToDate, entity.IsHalfDay);
            foreach (var usage in usageByYear)
            {
                var balance = await db.LeaveBalances.FirstOrDefaultAsync(
                    x => x.EmployeeId == entity.EmployeeId &&
                         x.LeaveTypeId == entity.LeaveTypeId &&
                         x.Year == usage.Key,
                    cancellationToken);

                if (balance is null)
                {
                    return (false, $"Leave balance is not initialized for year {usage.Key}.");
                }

                if (balance.TotalAllocated >= 0 && balance.Remaining < usage.Value)
                {
                    return (false, $"Insufficient leave balance for year {usage.Key}. Remaining={balance.Remaining:0.##}, required={usage.Value:0.##}.");
                }
            }
        }

        entity.Status = targetStatus;
        if (targetStatus == "approved")
        {
            entity.ApprovedBy = actorId ?? entity.ApprovedBy;
            entity.ApprovedDate = DateTime.UtcNow;
            entity.CancelledDate = null;

            // Sync leave dates to AttendanceRecords/TransactionDetails. This is best-effort: it
            // touches the EF/text-id hybrid tables and can throw "text = uuid" on some schemas.
            // It must NOT block the approval itself (the leave still shows in reports via /api/leave).
            try { await SyncLeaveToAttendanceAsync(db, entity, cancellationToken); }
            catch (Exception ex) { Console.Error.WriteLine($"[API] SyncLeaveToAttendance skipped: {ex.Message}"); }
        }
        else if (targetStatus == "rejected")
        {
            entity.ApprovedBy = null;
            entity.ApprovedDate = null;
            entity.CancelledDate = null;
        }
        else if (targetStatus == "cancelled")
        {
            entity.CancelledDate = DateTime.UtcNow;
        }

        AddLeaveRemark(db, entity.Id, targetStatus, actorId, remarks);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var year in years)
        {
            await EnsureLeaveBalancesForYearAsync(db, year, cancellationToken);
        }

        return (true, null);
    }

    private static async Task EnsureLeaveBalancesForYearAsync(
        PbAttendanceDbContext db,
        int year,
        CancellationToken cancellationToken)
    {
        try
        {
            // Read personnel ids via raw SQL: Employees.Id is TEXT but EF maps it as Guid, so
            // db.Personnel.Select(x => x.Id) throws "Reading as Guid ... for text".
            var activePersonnel = new List<Guid>();
            {
                var conn = db.Database.GetDbConnection();
                var opened = false;
                if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(cancellationToken); opened = true; }
                try
                {
                    using var idCmd = conn.CreateCommand();
                    idCmd.CommandText = "SELECT \"Id\" FROM \"E_Attendace\".\"Employees\" WHERE \"IsActive\" = 1";
                    using var reader = await idCmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (!reader.IsDBNull(0) && Guid.TryParse(reader.GetString(0), out var pid))
                        {
                            activePersonnel.Add(pid);
                        }
                    }
                }
                finally { if (opened) await conn.CloseAsync(); }
            }

            var leaveTypes = await db.LeaveTypes
                .AsNoTracking()
                .Where(x => x.IsActive)
                .Select(x => new { x.Id, x.MaxPerYear, x.CarryForward })
                .ToListAsync(cancellationToken);

            if (activePersonnel.Count == 0 || leaveTypes.Count == 0)
            {
                return;
            }

            var existing = await db.LeaveBalances.Where(x => x.Year == year).ToListAsync(cancellationToken);
            var existingByKey = existing.ToDictionary(x => (x.EmployeeId, x.LeaveTypeId));

            var previous = await db.LeaveBalances
                .AsNoTracking()
                .Where(x => x.Year == year - 1)
                .ToListAsync(cancellationToken);
            var previousByKey = previous.ToDictionary(x => (x.EmployeeId, x.LeaveTypeId));

            var inserted = false;
            foreach (var personnelId in activePersonnel)
            {
                foreach (var leaveType in leaveTypes)
                {
                    var key = (personnelId, leaveType.Id);
                    if (existingByKey.ContainsKey(key))
                    {
                        continue;
                    }

                    var carry = 0m;
                    if (leaveType.CarryForward &&
                        previousByKey.TryGetValue(key, out var previousBalance) &&
                        previousBalance.Remaining > 0)
                    {
                        carry = previousBalance.Remaining;
                    }

                    var totalAllocated = leaveType.MaxPerYear.HasValue
                        ? leaveType.MaxPerYear.Value + carry
                        : -1m;

                    var newRow = new LeaveBalanceEntity
                    {
                        EmployeeId = personnelId,
                        LeaveTypeId = leaveType.Id,
                        Year = year,
                        TotalAllocated = totalAllocated,
                        Used = 0,
                        Remaining = totalAllocated < 0 ? -1m : totalAllocated,
                        UpdatedAt = DateTime.UtcNow
                    };

                    db.LeaveBalances.Add(newRow);
                    existingByKey[key] = newRow;
                    inserted = true;
                }
            }

            if (inserted)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            var rangeStart = new DateOnly(year, 1, 1);
            var rangeEnd = new DateOnly(year, 12, 31);
            var approved = await db.LeaveRequests
                .AsNoTracking()
                .Where(x => x.Status == "approved" && x.ToDate >= rangeStart && x.FromDate <= rangeEnd)
                .Select(x => new { x.EmployeeId, x.LeaveTypeId, x.FromDate, x.ToDate, x.IsHalfDay })
                .ToListAsync(cancellationToken);

            var usage = new Dictionary<(Guid EmployeeId, int LeaveTypeId), decimal>();
            foreach (var row in approved)
            {
                var usedDays = CalculateOverlappingLeaveDays(row.FromDate, row.ToDate, row.IsHalfDay, rangeStart, rangeEnd);
                var key = (row.EmployeeId, row.LeaveTypeId);
                usage[key] = usage.TryGetValue(key, out var current) ? current + usedDays : usedDays;
            }

            var yearRows = await db.LeaveBalances.Where(x => x.Year == year).ToListAsync(cancellationToken);
            var changed = false;
            foreach (var row in yearRows)
            {
                var key = (row.EmployeeId, row.LeaveTypeId);
                var used = usage.TryGetValue(key, out var value) ? value : 0m;
                var remaining = row.TotalAllocated < 0 ? -1m : Math.Max(0, row.TotalAllocated - used);
                if (row.Used != used || row.Remaining != remaining)
                {
                    row.Used = used;
                    row.Remaining = remaining;
                    row.UpdatedAt = DateTime.UtcNow;
                    changed = true;
                }
            }

            if (changed)
            {
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[API] Critical error in EnsureLeaveBalancesForYearAsync: {ex}");
            // Re-throw to let the outer MapGet catch it and return 500
            throw;
        }
    }

        private static Dictionary<int, decimal> SplitLeaveDaysByYear(DateOnly fromDate, DateOnly toDate, bool isHalfDay)
        {
        var result = new Dictionary<int, decimal>();
        for (var year = fromDate.Year; year <= toDate.Year; year++)
        {
            var start = new DateOnly(year, 1, 1);
            var end = new DateOnly(year, 12, 31);
            var value = CalculateOverlappingLeaveDays(fromDate, toDate, isHalfDay, start, end);
            if (value > 0)
            {
                result[year] = value;
            }
        }

        return result;
        }

        private static decimal CalculateLeaveDays(DateOnly fromDate, DateOnly toDate, bool isHalfDay)
        {
        if (isHalfDay)
        {
            return 0.5m;
        }

        var total = toDate.DayNumber - fromDate.DayNumber + 1;
        return total <= 0 ? 0 : total;
        }

        private static decimal CalculateOverlappingLeaveDays(
        DateOnly fromDate,
        DateOnly toDate,
        bool isHalfDay,
        DateOnly rangeStart,
        DateOnly rangeEnd)
        {
        if (isHalfDay)
        {
            return fromDate >= rangeStart && fromDate <= rangeEnd ? 0.5m : 0m;
        }

        var overlapStart = fromDate > rangeStart ? fromDate : rangeStart;
        var overlapEnd = toDate < rangeEnd ? toDate : rangeEnd;
        if (overlapEnd < overlapStart)
        {
            return 0m;
        }

        return overlapEnd.DayNumber - overlapStart.DayNumber + 1;
        }

        private static string NormalizeLeaveStatus(string? value, string fallback)
        {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "approve" => "approved",
            "approved" => "approved",
            "reject" => "rejected",
            "rejected" => "rejected",
            "cancel" => "cancelled",
            "cancelled" => "cancelled",
            "pending" => "pending",
            "all" => "all",
            _ => fallback
        };
        }

        private static string? NormalizeHalfDayType(string? value)
        {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "first_half" => "first_half",
            "firsthalf" => "first_half",
            "first-half" => "first_half",
            "second_half" => "second_half",
            "secondhalf" => "second_half",
            "second-half" => "second_half",
            _ => null
        };
        }

        private static string EscapeCsv(string? value)
        {
        var s = value ?? string.Empty;
        if (s.Contains('"'))
        {
            s = s.Replace("\"", "\"\"");
        }

        return (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            ? $"\"{s}\""
            : s;
        }

        private static async Task QueueTimetableSyncCommand(
        PbAttendanceDbContext db, 
        int timetableId, 
        IDeviceProtocolService deviceProtocolService, 
        AttendanceService attendanceService)
        {
        // Get the timetable with its days
        var timetable = await db.Timetables
            .Include(t => t.Days)
            .FirstOrDefaultAsync(t => t.Id == timetableId);

        if (timetable == null)
            return;

        // Convert timetable days to the format expected by CreateAccTimeZoneCommand.
        // PUSH spec for AccTimeZone expects HHmm start/end per weekday.
        var dailySchedules = timetable.Days.Select(d =>
        {
            var start = d.CheckInStartTime;
            var end = d.CheckOutEndTime;
            if (d.CrossDay || end <= start)
            {
                end = new TimeOnly(23, 59);
            }
            return (d.DayOfWeek, start, end);
        });

        // Generate the AccTimeZone command using the existing service method
        var commandString = deviceProtocolService.CreateAccTimeZoneCommand(timetableId, dailySchedules);

        // Get all devices
        var devices = await db.Devices.ToListAsync();

        // Queue timetable sync command for each device
        foreach (var device in devices)
        {
            attendanceService.QueueDeviceCommand(
                device.SerialNumber,
                commandString
            );
        }
        }

        private static async Task QueueDepartmentSyncCommand(
        PbAttendanceDbContext db, 
        int departmentId, 
        int timetableId,
        IDeviceProtocolService deviceProtocolService, 
        AttendanceService attendanceService)
        {
        var commandString = deviceProtocolService.CreateAccGroupCommand(departmentId, timetableId);
        var devices = await db.Devices.ToListAsync();
        foreach (var device in devices)
        {
            attendanceService.QueueDeviceCommand(device.SerialNumber, commandString);
        }
        }

        private static async Task QueueHolidaySyncCommand(
        PbAttendanceDbContext db,
        HolidayEntity holiday,
        IDeviceProtocolService deviceProtocolService,
        AttendanceService attendanceService,
        CancellationToken cancellationToken)
        {
        if (string.IsNullOrWhiteSpace(holiday.HolidayName))
        {
            return;
        }

        var tzId = holiday.TimeZoneId;
        if (!tzId.HasValue || tzId.Value <= 0)
        {
            tzId = await db.Timetables
                .AsNoTracking()
                .Where(x => x.IsActive)
                .OrderBy(x => x.Id)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(cancellationToken) ?? 1;
        }

        var cmd = deviceProtocolService.CreateAccHolidayCommand(
            holiday.Id,
            holiday.HolidayName,
            holiday.HolidayDate,
            holiday.HolidayDate,
            tzId.Value);

        if (string.IsNullOrWhiteSpace(cmd))
        {
            return;
        }

        var devices = await db.Devices.AsNoTracking().ToListAsync(cancellationToken);
        foreach (var device in devices)
        {
            attendanceService.QueueDeviceCommand(device.SerialNumber, cmd);
        }
        }

        private static async Task SyncLeaveToAttendanceAsync(
            PbAttendanceDbContext db,
            LeaveRequestEntity entity,
            CancellationToken cancellationToken)
        {
            var person = await db.Personnel.AsNoTracking().FirstOrDefaultAsync(x => x.Id == entity.EmployeeId, cancellationToken);
            if (person == null) return;

            var leaveType = await db.LeaveTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == entity.LeaveTypeId, cancellationToken);
            var leaveTypeName = leaveType?.LeaveName ?? "Leave";

            var status = entity.IsHalfDay ? "Half Leave" : "Leave";
            
            for (var date = entity.FromDate; date <= entity.ToDate; date = date.AddDays(1))
            {
                // 1. Update AttendanceRecord
                var record = await db.AttendanceRecords.FirstOrDefaultAsync(x => x.EmployeeId == entity.EmployeeId && x.AttendanceDate == date, cancellationToken);
                if (record == null)
                {
                    record = new AttendanceRecordEntity
                    {
                        EmployeeId = entity.EmployeeId,
                        AttendanceDate = date,
                        Status = status,
                        ProcessedAt = DateTime.UtcNow
                    };
                    db.AttendanceRecords.Add(record);
                }
                else
                {
                    // Only overwrite if it's currently Absent or Present (meaning no specific status was set yet)
                    if (record.Status == "Absent" || record.Status == "Present" || string.IsNullOrWhiteSpace(record.Status))
                    {
                        record.Status = status;
                    }
                }

                // 2. Insert into TransactionDetails
                await InsertTransactionDetailAsync(
                    db.Database.GetDbConnection().ConnectionString!,
                    person.EmployeeCode,
                    date.ToDateTime(new TimeOnly(9, 0)),
                    "LEAVE",
                    $"Approved Leave: {leaveTypeName}",
                    cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        private static async Task SyncHolidayToAttendanceAsync(
            PbAttendanceDbContext db,
            HolidayEntity holiday,
            CancellationToken cancellationToken)
        {
            if (!holiday.IsActive) return;

            var query = db.Personnel.AsNoTracking().Where(x => x.Status == "active");
            if (holiday.DepartmentId.HasValue)
            {
                query = query.Where(x => x.DepartmentId == holiday.DepartmentId.Value);
            }

            var personnel = await query.ToListAsync(cancellationToken);
            
            foreach (var person in personnel)
            {
                // 1. Update AttendanceRecord
                var record = await db.AttendanceRecords.FirstOrDefaultAsync(x => x.EmployeeId == person.Id && x.AttendanceDate == holiday.HolidayDate, cancellationToken);
                if (record == null)
                {
                    record = new AttendanceRecordEntity
                    {
                        EmployeeId = person.Id,
                        AttendanceDate = holiday.HolidayDate,
                        Status = "Holiday",
                        ProcessedAt = DateTime.UtcNow
                    };
                    db.AttendanceRecords.Add(record);
                }
                else
                {
                    if (record.Status == "Absent" || string.IsNullOrWhiteSpace(record.Status))
                    {
                        record.Status = "Holiday";
                    }
                }

                // 2. Insert into TransactionDetails
                await InsertTransactionDetailAsync(
                    db.Database.GetDbConnection().ConnectionString!,
                    person.EmployeeCode,
                    holiday.HolidayDate.ToDateTime(new TimeOnly(9, 0)),
                    "HOLIDAY",
                    $"Holiday: {holiday.HolidayName}",
                    cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        private static async Task InsertTransactionDetailAsync(
            string connectionString,
            string employeeCode,
            DateTime punchTime,
            string sourceTable,
            string remarks,
            CancellationToken cancellationToken)
        {
            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(cancellationToken);
                await using var cmd = new NpgsqlCommand(
                    """
                    INSERT INTO "E_Attendace"."TransactionDetails"
                    ("Id","ReceivedUtc","DeviceSerialNumber","SourceTable","RawLine","EmployeeCode","PunchTime","StatusCode","VerifyMode","Workcode","ParsedOk","Remarks")
                    VALUES (@id,@received,@serial,@source,@raw,@employee,@punch,0,1,NULL,1,@remarks);
                    """,
                    conn);
                cmd.Parameters.AddWithValue("id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("received", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("serial", "MANUAL");
                cmd.Parameters.AddWithValue("source", sourceTable);
                cmd.Parameters.AddWithValue("raw", remarks);
                cmd.Parameters.AddWithValue("employee", employeeCode);
                cmd.Parameters.AddWithValue("punch", punchTime);
                cmd.Parameters.AddWithValue("remarks", remarks);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch
            {
                // Non-blocking
            }
        }
    }

public sealed record PersonnelUpsertRequest(
    string? EmployeeCode,
    string? FirstName,
    string? LastName,
    string? Email,
    string? Phone,
    int? BranchId,
    int? DepartmentId,
    int? DesignationId,
    int? ShiftId,
    DateOnly? JoinDate,
    string? Status);

public sealed record DeviceCommandCreateRequest(string? CommandString);

public sealed record AttendancePolicyUpsertRequest(
    bool? WeeklyOffWorkAsOvertime,
    bool? HolidayWorkAsOvertime);

public sealed record LeaveTypeUpsertRequest(
    string? LeaveName,
    bool? IsPaid,
    decimal? MaxPerYear,
    bool? CarryForward,
    bool? AllowHalfDay,
    bool? IsActive);

public sealed record LeaveCreateRequest(
    Guid EmployeeId,
    int LeaveTypeId,
    DateOnly FromDate, DateOnly ToDate,
    bool? IsHalfDay,
    string? HalfDayType,
    string? Reason,
    Guid? ActorId,
    string? Remarks,
    string? AttachmentName,
    string? AttachmentUrl);

public sealed record LeaveDecisionRequest(
    Guid? ApprovedBy,
    Guid? ActorId,
    string? Status,
    string? Remarks);

public sealed record LeaveCancelRequest(
    Guid? ActorId,
    string? Remarks);

public sealed record LeaveBulkDecisionRequest(
    List<int>? LeaveIds,
    Guid? ApprovedBy,
    Guid? ActorId,
    string? Status,
    string? Remarks);

public sealed record LeaveBalanceResetRequest(int? Year);

public sealed record LeaveRemarkCreateRequest(
    string? Action,
    string? Remarks,
    Guid? ActorId);

public sealed record LeaveAttachmentCreateRequest(
    string? FileName,
    string? FileUrl,
    Guid? UploadedBy);

public sealed record TimetableUpsertRequest(
    string? TimetableName,
    bool? IsActive);

public sealed record BreakTimeUpsertRequest(
    string? BreakName,
    string? StartTime,
    string? EndTime,
    int? DurationMinutes,
    string? CalculateType,
    int? MinimumBreakMinutes,
    bool? IsActive);

public sealed record TimetableDayUpsertRequest(
    int? DayOfWeek,
    int? ShiftId,
    bool? IsWeeklyOff,
    string? CheckInTime,
    string? CheckInStartTime,
    string? CheckInEndTime,
    string? CheckOutStartTime,
    string? CheckOutTime,
    string? CheckOutEndTime,
    bool? CrossDay,
    bool? UseBreakTime,
    int? BreakTimeId,
    List<int>? BreakTimeIds);

public sealed record EmployeeTimetableUpsertRequest(
    Guid EmployeeId,
    int TimetableId,
    DateOnly EffectiveFrom,
    bool? IsActive);

public sealed record DepartmentTimetableUpsertRequest(
    int DepartmentId,
    int TimetableId,
    DateOnly EffectiveFrom,
    bool? IsActive);

public sealed record HolidayUpsertRequest(
    string? HolidayName,
    DateOnly HolidayDate,
    string? Description,
    bool? IsRecurring,
    int? DepartmentId,
    int? TimeZoneId,
    bool? IsActive);

