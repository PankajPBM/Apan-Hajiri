using Microsoft.EntityFrameworkCore;

namespace E.Attendance.Data.Production;

public sealed class PbAttendanceDbContext(DbContextOptions<PbAttendanceDbContext> options) : DbContext(options)
{
    public DbSet<PersonnelEntity> Personnel => Set<PersonnelEntity>();
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<DeviceHeartbeatEntity> DeviceHeartbeats => Set<DeviceHeartbeatEntity>();
    public DbSet<RawPushLogEntity> RawPushLogs => Set<RawPushLogEntity>();
    public DbSet<AttendanceRecordEntity> AttendanceRecords => Set<AttendanceRecordEntity>();
    public DbSet<AttendanceExceptionEntity> AttendanceExceptions => Set<AttendanceExceptionEntity>();
    public DbSet<ShiftEntity> Shifts => Set<ShiftEntity>();
    public DbSet<TimetableEntity> Timetables => Set<TimetableEntity>();
    public DbSet<TimetableDayEntity> TimetableDays => Set<TimetableDayEntity>();
    public DbSet<BreakTimeEntity> BreakTimes => Set<BreakTimeEntity>();
    public DbSet<EmployeeTimetableEntity> EmployeeTimetables => Set<EmployeeTimetableEntity>();
    public DbSet<DepartmentTimetableEntity> DepartmentTimetables => Set<DepartmentTimetableEntity>();
    public DbSet<HolidayEntity> Holidays => Set<HolidayEntity>();
    public DbSet<AttendancePolicyEntity> AttendancePolicies => Set<AttendancePolicyEntity>();
    public DbSet<LeaveTypeEntity> LeaveTypes => Set<LeaveTypeEntity>();
    public DbSet<LeaveRequestEntity> LeaveRequests => Set<LeaveRequestEntity>();
    public DbSet<LeaveBalanceEntity> LeaveBalances => Set<LeaveBalanceEntity>();
    public DbSet<LeaveAttachmentEntity> LeaveAttachments => Set<LeaveAttachmentEntity>();
    public DbSet<LeaveRemarkEntity> LeaveRemarks => Set<LeaveRemarkEntity>();
    public DbSet<DeviceCommandEntity> DeviceCommands => Set<DeviceCommandEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<RoleEntity> Roles => Set<RoleEntity>();
    public DbSet<PermissionEntity> Permissions => Set<PermissionEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<AuditLogEntity> AuditLogs => Set<AuditLogEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PersonnelEntity>().HasIndex(x => x.PersonnelCode).IsUnique();
        modelBuilder.Entity<DeviceEntity>().HasIndex(x => x.SerialNumber).IsUnique();
        modelBuilder.Entity<UserEntity>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<RawPushLogEntity>().HasIndex(x => new { x.Processed, x.ReceivedAt });
        modelBuilder.Entity<AttendanceRecordEntity>().HasIndex(x => new { x.PersonnelId, x.AttendanceDate }).IsUnique();
        modelBuilder.Entity<TimetableEntity>().HasIndex(x => x.TimetableName).IsUnique();
        modelBuilder.Entity<TimetableDayEntity>().HasIndex(x => new { x.TimetableId, x.DayOfWeek }).IsUnique();
        modelBuilder.Entity<BreakTimeEntity>().HasIndex(x => x.BreakName).IsUnique();
        modelBuilder.Entity<EmployeeTimetableEntity>().HasIndex(x => new { x.PersonnelId, x.EffectiveFrom });
        modelBuilder.Entity<DepartmentTimetableEntity>().HasIndex(x => new { x.DepartmentId, x.EffectiveFrom });
        modelBuilder.Entity<HolidayEntity>().HasIndex(x => new { x.HolidayDate, x.DepartmentId });
        modelBuilder.Entity<LeaveTypeEntity>().HasIndex(x => x.LeaveName).IsUnique();
        modelBuilder.Entity<LeaveRequestEntity>().HasIndex(x => new { x.PersonnelId, x.FromDate, x.ToDate, x.Status });
        modelBuilder.Entity<LeaveBalanceEntity>().HasIndex(x => new { x.PersonnelId, x.LeaveTypeId, x.Year }).IsUnique();
        modelBuilder.Entity<LeaveAttachmentEntity>().HasIndex(x => x.LeaveRequestId);
        modelBuilder.Entity<LeaveRemarkEntity>().HasIndex(x => new { x.LeaveRequestId, x.CreatedAt });

        modelBuilder.Entity<DeviceHeartbeatEntity>()
            .HasOne(x => x.Device).WithMany(x => x.Heartbeats).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RawPushLogEntity>()
            .HasOne(x => x.Device).WithMany(x => x.RawPushLogs).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<AttendanceRecordEntity>()
            .HasOne(x => x.Personnel).WithMany(x => x.AttendanceRecords).HasForeignKey(x => x.PersonnelId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AttendanceExceptionEntity>()
            .HasOne(x => x.Attendance).WithMany(x => x.Exceptions).HasForeignKey(x => x.AttendanceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TimetableDayEntity>()
            .HasOne(x => x.Timetable).WithMany(x => x.Days).HasForeignKey(x => x.TimetableId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TimetableDayEntity>()
            .HasOne(x => x.Shift).WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<TimetableDayEntity>()
            .HasOne(x => x.BreakTime).WithMany().HasForeignKey(x => x.BreakTimeId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<EmployeeTimetableEntity>()
            .HasOne(x => x.Personnel).WithMany().HasForeignKey(x => x.PersonnelId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<EmployeeTimetableEntity>()
            .HasOne(x => x.Timetable).WithMany().HasForeignKey(x => x.TimetableId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DepartmentTimetableEntity>()
            .HasOne(x => x.Timetable).WithMany().HasForeignKey(x => x.TimetableId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LeaveRequestEntity>()
            .HasOne(x => x.Personnel).WithMany(x => x.LeaveRequests).HasForeignKey(x => x.PersonnelId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LeaveRequestEntity>()
            .HasOne(x => x.LeaveType).WithMany(x => x.LeaveRequests).HasForeignKey(x => x.LeaveTypeId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LeaveBalanceEntity>()
            .HasOne(x => x.Personnel).WithMany(x => x.LeaveBalances).HasForeignKey(x => x.PersonnelId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LeaveBalanceEntity>()
            .HasOne(x => x.LeaveType).WithMany(x => x.LeaveBalances).HasForeignKey(x => x.LeaveTypeId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LeaveAttachmentEntity>()
            .HasOne(x => x.LeaveRequest).WithMany(x => x.Attachments).HasForeignKey(x => x.LeaveRequestId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LeaveRemarkEntity>()
            .HasOne(x => x.LeaveRequest).WithMany(x => x.Remarks).HasForeignKey(x => x.LeaveRequestId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DeviceCommandEntity>()
            .HasOne(x => x.Device).WithMany(x => x.Commands).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserEntity>()
            .HasOne(x => x.Role).WithMany(x => x.Users).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<PermissionEntity>()
            .HasOne(x => x.Role).WithMany(x => x.Permissions).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NotificationEntity>()
            .HasOne(x => x.User).WithMany(x => x.Notifications).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PersonnelEntity
{
    public int Id { get; set; }
    public string PersonnelCode { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public int? BranchId { get; set; }
    public int? DepartmentId { get; set; }
    public int? DesignationId { get; set; }
    public int? ShiftId { get; set; }
    public DateOnly? JoinDate { get; set; }
    public string Status { get; set; } = "active";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<AttendanceRecordEntity> AttendanceRecords { get; set; } = [];
    public List<LeaveRequestEntity> LeaveRequests { get; set; } = [];
    public List<LeaveBalanceEntity> LeaveBalances { get; set; } = [];
}

public sealed class DeviceEntity
{
    public int Id { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? IpAddress { get; set; }
    public string? Location { get; set; }
    public string Status { get; set; } = "offline";
    public DateTime? LastHeartbeat { get; set; }
    public DateTime? LastPushTime { get; set; }
    public string? SecretKey { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<DeviceHeartbeatEntity> Heartbeats { get; set; } = [];
    public List<RawPushLogEntity> RawPushLogs { get; set; } = [];
    public List<DeviceCommandEntity> Commands { get; set; } = [];
}

public sealed class DeviceHeartbeatEntity
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public DateTime HeartbeatTime { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }
    public DeviceEntity? Device { get; set; }
}

public sealed class RawPushLogEntity
{
    public int Id { get; set; }
    public int? DeviceId { get; set; }
    public string PersonnelCode { get; set; } = string.Empty;
    public DateTime PunchTime { get; set; }
    public string PunchState { get; set; } = "In";
    public int? VerifyMode { get; set; }
    public string? WorkCode { get; set; }
    public decimal? Temperature { get; set; }
    public string RawData { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public bool Processed { get; set; }
    public DeviceEntity? Device { get; set; }
}

public sealed class AttendanceRecordEntity
{
    public int Id { get; set; }
    public int PersonnelId { get; set; }
    public DateOnly AttendanceDate { get; set; }
    public DateTime? FirstIn { get; set; }
    public DateTime? LastOut { get; set; }
    public decimal TotalWorkHours { get; set; }
    public int LateMinutes { get; set; }
    public int EarlyExitMinutes { get; set; }
    public int OvertimeMinutes { get; set; }
    public string Status { get; set; } = "Present";
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
    public PersonnelEntity? Personnel { get; set; }
    public List<AttendanceExceptionEntity> Exceptions { get; set; } = [];
}

public sealed class AttendanceExceptionEntity
{
    public int Id { get; set; }
    public int AttendanceId { get; set; }
    public string ExceptionType { get; set; } = string.Empty;
    public string? Remarks { get; set; }
    public bool Resolved { get; set; }
    public AttendanceRecordEntity? Attendance { get; set; }
}

public sealed class ShiftEntity
{
    public int Id { get; set; }
    public string ShiftName { get; set; } = string.Empty;
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int GraceMinutes { get; set; }
    public int HalfdayThreshold { get; set; }
    public int OvertimeAfterMinutes { get; set; }
    public bool NightShift { get; set; }
}

public sealed class TimetableEntity
{
    public int Id { get; set; }
    public string TimetableName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<TimetableDayEntity> Days { get; set; } = [];
}

public sealed class TimetableDayEntity
{
    public int Id { get; set; }
    public int TimetableId { get; set; }
    public int DayOfWeek { get; set; } // 0 = Sunday ... 6 = Saturday
    public int? ShiftId { get; set; }
    public bool IsWeeklyOff { get; set; }
    public TimeOnly CheckInTime { get; set; } = new(10, 0);
    public TimeOnly CheckInStartTime { get; set; } = new(0, 0);
    public TimeOnly CheckInEndTime { get; set; } = new(10, 30);
    public TimeOnly CheckOutStartTime { get; set; } = new(17, 0);
    public TimeOnly CheckOutTime { get; set; } = new(23, 59);
    public TimeOnly CheckOutEndTime { get; set; } = new(0, 59);
    public bool CrossDay { get; set; }
    public bool UseBreakTime { get; set; }
    public int? BreakTimeId { get; set; }
    public TimetableEntity? Timetable { get; set; }
    public ShiftEntity? Shift { get; set; }
    public BreakTimeEntity? BreakTime { get; set; }
}

public sealed class BreakTimeEntity
{
    public int Id { get; set; }
    public string BreakName { get; set; } = string.Empty;
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int DurationMinutes { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class EmployeeTimetableEntity
{
    public int Id { get; set; }
    public int PersonnelId { get; set; }
    public int TimetableId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public bool IsActive { get; set; } = true;
    public PersonnelEntity? Personnel { get; set; }
    public TimetableEntity? Timetable { get; set; }
}

public sealed class DepartmentTimetableEntity
{
    public int Id { get; set; }
    public int DepartmentId { get; set; }
    public int TimetableId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public bool IsActive { get; set; } = true;
    public TimetableEntity? Timetable { get; set; }
}

public sealed class HolidayEntity
{
    public int Id { get; set; }
    public DateOnly HolidayDate { get; set; }
    public string HolidayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsRecurring { get; set; }
    public int? DepartmentId { get; set; } // null = company-wide holiday
    public bool IsActive { get; set; } = true;
}

public sealed class AttendancePolicyEntity
{
    public int Id { get; set; }
    public bool WeeklyOffWorkAsOvertime { get; set; } = true;
    public bool HolidayWorkAsOvertime { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class LeaveTypeEntity
{
    public int Id { get; set; }
    public string LeaveName { get; set; } = string.Empty;
    public bool IsPaid { get; set; } = true;
    public decimal? MaxPerYear { get; set; } = 0;
    public bool CarryForward { get; set; }
    public bool AllowHalfDay { get; set; }
    public bool IsActive { get; set; } = true;
    public List<LeaveRequestEntity> LeaveRequests { get; set; } = [];
    public List<LeaveBalanceEntity> LeaveBalances { get; set; } = [];
}

public sealed class LeaveRequestEntity
{
    public int Id { get; set; }
    public int PersonnelId { get; set; }
    public int LeaveTypeId { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public decimal TotalDays { get; set; }
    public bool IsHalfDay { get; set; }
    public string? HalfDayType { get; set; } // first_half | second_half
    public string? Reason { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime AppliedDate { get; set; } = DateTime.UtcNow;
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedDate { get; set; }
    public DateTime? CancelledDate { get; set; }
    public PersonnelEntity? Personnel { get; set; }
    public LeaveTypeEntity? LeaveType { get; set; }
    public List<LeaveAttachmentEntity> Attachments { get; set; } = [];
    public List<LeaveRemarkEntity> Remarks { get; set; } = [];
}

public sealed class LeaveBalanceEntity
{
    public int Id { get; set; }
    public int PersonnelId { get; set; }
    public int LeaveTypeId { get; set; }
    public int Year { get; set; }
    public decimal TotalAllocated { get; set; }
    public decimal Used { get; set; }
    public decimal Remaining { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public PersonnelEntity? Personnel { get; set; }
    public LeaveTypeEntity? LeaveType { get; set; }
}

public sealed class LeaveAttachmentEntity
{
    public int Id { get; set; }
    public int LeaveRequestId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public int? UploadedBy { get; set; }
    public LeaveRequestEntity? LeaveRequest { get; set; }
}

public sealed class LeaveRemarkEntity
{
    public int Id { get; set; }
    public int LeaveRequestId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Remarks { get; set; }
    public int? ActorId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public LeaveRequestEntity? LeaveRequest { get; set; }
}

public sealed class DeviceCommandEntity
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public string CommandString { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExecutedAt { get; set; }
    public string? ResponseMessage { get; set; }
    public DeviceEntity? Device { get; set; }
}

public sealed class UserEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public int? RoleId { get; set; }
    public int? BranchId { get; set; }
    public string Status { get; set; } = "active";
    public RoleEntity? Role { get; set; }
    public List<NotificationEntity> Notifications { get; set; } = [];
}

public sealed class RoleEntity
{
    public int Id { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public List<UserEntity> Users { get; set; } = [];
    public List<PermissionEntity> Permissions { get; set; } = [];
}

public sealed class PermissionEntity
{
    public int Id { get; set; }
    public int RoleId { get; set; }
    public string Module { get; set; } = string.Empty;
    public bool CanView { get; set; }
    public bool CanCreate { get; set; }
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
    public RoleEntity? Role { get; set; }
}

public sealed class NotificationEntity
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool ReadStatus { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public UserEntity? User { get; set; }
}

public sealed class AuditLogEntity
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string? RecordId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }
}
