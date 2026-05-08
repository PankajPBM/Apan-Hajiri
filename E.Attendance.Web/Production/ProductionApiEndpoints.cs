using System.Text;
using Microsoft.EntityFrameworkCore;
using E.Attendance.Data.Production;
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
                    x.PersonnelCode.Contains(token) ||
                    x.FirstName.Contains(token) ||
                    x.LastName.Contains(token) ||
                    (x.Email != null && x.Email.Contains(token)));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(x => x.Status == status.Trim().ToLowerInvariant());
            }

            var rows = await query
                .OrderBy(x => x.PersonnelCode)
                .Select(x => new
                {
                    x.Id,
                    x.PersonnelCode,
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
            if (string.IsNullOrWhiteSpace(request.PersonnelCode))
            {
                return Results.BadRequest(new { message = "personnel_code is required." });
            }

            var personnelCode = request.PersonnelCode.Trim();
            var duplicate = await db.Personnel.AnyAsync(x => x.PersonnelCode == personnelCode, cancellationToken);
            if (duplicate)
            {
                return Results.BadRequest(new { message = "Personnel code already exists." });
            }

            var entity = new PersonnelEntity
            {
                PersonnelCode = personnelCode,
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

        app.MapPut("/api/personnel/{id:int}", async (PbAttendanceDbContext db, int id, PersonnelUpsertRequest request, CancellationToken cancellationToken) =>
        {
            var entity = await db.Personnel.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound(new { message = "Personnel not found." });
            }

            if (!string.IsNullOrWhiteSpace(request.PersonnelCode))
            {
                var personnelCode = request.PersonnelCode.Trim();
                var duplicate = await db.Personnel.AnyAsync(x => x.Id != id && x.PersonnelCode == personnelCode, cancellationToken);
                if (duplicate)
                {
                    return Results.BadRequest(new { message = "Personnel code already exists." });
                }

                entity.PersonnelCode = personnelCode;
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

        app.MapDelete("/api/personnel/{id:int}", async (PbAttendanceDbContext db, int id, CancellationToken cancellationToken) =>
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
                    personnelCode = x.Attendance!.Personnel!.PersonnelCode,
                    attendanceDate = x.Attendance.AttendanceDate
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(rows);
        });

        app.MapPost("/api/devices/{id:int}/command", async (PbAttendanceDbContext db, int id, DeviceCommandCreateRequest request, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.CommandString))
            {
                return Results.BadRequest(new { message = "command_string is required." });
            }

            var exists = await db.Devices.AnyAsync(x => x.Id == id, cancellationToken);
            if (!exists)
            {
                return Results.NotFound(new { message = "Device not found." });
            }

            var cmd = new DeviceCommandEntity
            {
                DeviceId = id,
                CommandString = request.CommandString.Trim(),
                Status = "pending",
                CreatedAt = DateTime.UtcNow
            };

            db.DeviceCommands.Add(cmd);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(cmd);
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
            return Results.Created($"/api/leave/types/{entity.Id}", entity);
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
            return Results.Ok(entity);
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
            string? status,
            int? personnelId,
            DateOnly? from,
            DateOnly? to,
            CancellationToken cancellationToken) =>
        {
            var query = db.LeaveRequests
                .AsNoTracking()
                .Include(x => x.Personnel)
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
                query = query.Where(x => x.PersonnelId == personnelId.Value);
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
                    x.PersonnelId,
                    personnelCode = x.Personnel!.PersonnelCode,
                    personnelName = $"{x.Personnel!.FirstName} {x.Personnel.LastName}",
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

        app.MapPost("/api/leave", async (PbAttendanceDbContext db, LeaveCreateRequest request, CancellationToken cancellationToken) =>
        {
            if (request.PersonnelId <= 0 || request.LeaveTypeId <= 0)
            {
                return Results.BadRequest(new { message = "personnel_id and leave_type_id are required." });
            }

            if (request.ToDate < request.FromDate)
            {
                return Results.BadRequest(new { message = "to_date cannot be before from_date." });
            }

            var personExists = await db.Personnel.AnyAsync(x => x.Id == request.PersonnelId, cancellationToken);
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

            var overlapExists = await db.LeaveRequests.AnyAsync(
                x => x.PersonnelId == request.PersonnelId &&
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
                PersonnelId = request.PersonnelId,
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

            AddLeaveRemark(db, entity.Id, "applied", request.ActorId ?? request.PersonnelId, request.Remarks ?? request.Reason);
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
            return Results.Created($"/api/leave/{entity.Id}", entity);
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

            return Results.Ok(entity);
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

            return Results.Ok(entity);
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

            return Results.Ok(entity);
        });

        app.MapPut("/api/leave/bulk-decision", async (PbAttendanceDbContext db, LeaveBulkDecisionRequest request, CancellationToken cancellationToken) =>
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
        });

        app.MapGet("/api/leave/{id:int}/remarks", async (PbAttendanceDbContext db, int id, CancellationToken cancellationToken) =>
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
        });

        app.MapPost("/api/leave/{id:int}/remarks", async (PbAttendanceDbContext db, int id, LeaveRemarkCreateRequest request, CancellationToken cancellationToken) =>
        {
            var exists = await db.LeaveRequests.AnyAsync(x => x.Id == id, cancellationToken);
            if (!exists)
            {
                return Results.NotFound(new { message = "Leave request not found." });
            }

            AddLeaveRemark(db, id, request.Action ?? "remark", request.ActorId, request.Remarks);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new { success = true });
        });

        app.MapGet("/api/leave/{id:int}/attachments", async (PbAttendanceDbContext db, int id, CancellationToken cancellationToken) =>
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
        });

        app.MapPost("/api/leave/{id:int}/attachments", async (PbAttendanceDbContext db, int id, LeaveAttachmentCreateRequest request, CancellationToken cancellationToken) =>
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
        });

        app.MapGet("/api/leave/balance", async (PbAttendanceDbContext db, int? year, int? personnelId, int? leaveTypeId, CancellationToken cancellationToken) =>
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
                query = query.Where(x => x.PersonnelId == personnelId.Value);
            }

            if (leaveTypeId.HasValue)
            {
                query = query.Where(x => x.LeaveTypeId == leaveTypeId.Value);
            }

            var rows = await query
                .OrderBy(x => x.Personnel!.PersonnelCode)
                .ThenBy(x => x.LeaveType!.LeaveName)
                .Select(x => new
                {
                    x.Id,
                    x.PersonnelId,
                    personnelCode = x.Personnel!.PersonnelCode,
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
        });

        app.MapPost("/api/leave/balance/reset", async (PbAttendanceDbContext db, LeaveBalanceResetRequest request, CancellationToken cancellationToken) =>
        {
            var year = request.Year ?? DateTime.UtcNow.Year;
            await EnsureLeaveBalancesForYearAsync(db, year, cancellationToken);
            return Results.Ok(new { success = true, year });
        });

        app.MapGet("/api/holidays", async (PbAttendanceDbContext db, DateOnly? from, DateOnly? to, int? departmentId, bool? activeOnly, CancellationToken cancellationToken) =>
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
                    x.IsActive
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(rows);
        });

        app.MapPost("/api/holidays", async (PbAttendanceDbContext db, HolidayUpsertRequest request, CancellationToken cancellationToken) =>
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
                IsActive = request.IsActive ?? true
            };

            db.Holidays.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/holidays/{entity.Id}", entity);
        });

        app.MapPut("/api/holidays/{id:int}", async (PbAttendanceDbContext db, int id, HolidayUpsertRequest request, CancellationToken cancellationToken) =>
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
            entity.IsActive = request.IsActive ?? entity.IsActive;

            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(entity);
        });

        app.MapDelete("/api/holidays/{id:int}", async (PbAttendanceDbContext db, int id, CancellationToken cancellationToken) =>
        {
            var entity = await db.Holidays.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (entity is null)
            {
                return Results.NotFound(new { message = "Holiday not found." });
            }

            entity.IsActive = false;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new { success = true });
        });

        app.MapGet("/api/reports/leave/history", async (PbAttendanceDbContext db, int? personnelId, string? status, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
        {
            var query = db.LeaveRequests
                .AsNoTracking()
                .Include(x => x.Personnel)
                .Include(x => x.LeaveType)
                .AsQueryable();

            if (personnelId.HasValue)
            {
                query = query.Where(x => x.PersonnelId == personnelId.Value);
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
                    x.PersonnelId,
                    personnelCode = x.Personnel!.PersonnelCode,
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
                .ThenBy(x => x.Personnel!.PersonnelCode)
                .Select(x => new
                {
                    x.PersonnelId,
                    personnelCode = x.Personnel!.PersonnelCode,
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
                    x.PersonnelId,
                    PersonnelCode = x.Personnel?.PersonnelCode ?? string.Empty,
                    PersonnelName = $"{x.Personnel?.FirstName} {x.Personnel?.LastName}".Trim(),
                    LeaveType = x.LeaveType?.LeaveName ?? "Unknown"
                })
                .Select(g => new
                {
                    g.Key.PersonnelId,
                    g.Key.PersonnelCode,
                    g.Key.PersonnelName,
                    g.Key.LeaveType,
                    requestCount = g.Count(),
                    totalDays = g.Sum(x => CalculateOverlappingLeaveDays(x.FromDate, x.ToDate, x.IsHalfDay, start, end))
                })
                .OrderBy(x => x.PersonnelCode)
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
                    x.PersonnelId,
                    personnelCode = x.Personnel!.PersonnelCode,
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
            csv.AppendLine("Date,PersonnelCode,Name,Status,FirstIn,LastOut,WorkHours,LateMinutes,EarlyExitMinutes,OvertimeMinutes");
            foreach (var item in rows)
            {
                var name = $"{item.Personnel?.FirstName} {item.Personnel?.LastName}".Trim();
                csv.Append(item.AttendanceDate).Append(',')
                   .Append(item.Personnel?.PersonnelCode).Append(',')
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
    }

    private static void AddLeaveRemark(PbAttendanceDbContext db, int leaveRequestId, string action, int? actorId, string? remarks)
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
        int? actorId,
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
                    x => x.PersonnelId == entity.PersonnelId &&
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
        var activePersonnel = await db.Personnel
            .AsNoTracking()
            .Where(x => x.Status == "active")
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

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
        var existingByKey = existing.ToDictionary(x => (x.PersonnelId, x.LeaveTypeId));

        var previous = await db.LeaveBalances
            .AsNoTracking()
            .Where(x => x.Year == year - 1)
            .ToListAsync(cancellationToken);
        var previousByKey = previous.ToDictionary(x => (x.PersonnelId, x.LeaveTypeId));

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
                    PersonnelId = personnelId,
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
            .Select(x => new { x.PersonnelId, x.LeaveTypeId, x.FromDate, x.ToDate, x.IsHalfDay })
            .ToListAsync(cancellationToken);

        var usage = new Dictionary<(int PersonnelId, int LeaveTypeId), decimal>();
        foreach (var row in approved)
        {
            var usedDays = CalculateOverlappingLeaveDays(row.FromDate, row.ToDate, row.IsHalfDay, rangeStart, rangeEnd);
            var key = (row.PersonnelId, row.LeaveTypeId);
            usage[key] = usage.TryGetValue(key, out var current) ? current + usedDays : usedDays;
        }

        var yearRows = await db.LeaveBalances.Where(x => x.Year == year).ToListAsync(cancellationToken);
        var changed = false;
        foreach (var row in yearRows)
        {
            var key = (row.PersonnelId, row.LeaveTypeId);
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
}

public sealed record PersonnelUpsertRequest(
    string? PersonnelCode,
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
    int PersonnelId,
    int LeaveTypeId,
    DateOnly FromDate,
    DateOnly ToDate,
    bool? IsHalfDay,
    string? HalfDayType,
    string? Reason,
    int? ActorId,
    string? Remarks,
    string? AttachmentName,
    string? AttachmentUrl);

public sealed record LeaveDecisionRequest(
    int? ApprovedBy,
    int? ActorId,
    string? Status,
    string? Remarks);

public sealed record LeaveCancelRequest(
    int? ActorId,
    string? Remarks);

public sealed record LeaveBulkDecisionRequest(
    List<int>? LeaveIds,
    int? ApprovedBy,
    int? ActorId,
    string? Status,
    string? Remarks);

public sealed record LeaveBalanceResetRequest(int? Year);

public sealed record LeaveRemarkCreateRequest(
    string? Action,
    string? Remarks,
    int? ActorId);

public sealed record LeaveAttachmentCreateRequest(
    string? FileName,
    string? FileUrl,
    int? UploadedBy);

public sealed record HolidayUpsertRequest(
    string? HolidayName,
    DateOnly HolidayDate,
    string? Description,
    bool? IsRecurring,
    int? DepartmentId,
    bool? IsActive);
