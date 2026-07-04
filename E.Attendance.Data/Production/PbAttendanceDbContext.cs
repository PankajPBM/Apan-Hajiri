using Microsoft.EntityFrameworkCore;
using E.Attendance.Core.Models;

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
    public DbSet<TimetableDayBreakTimeEntity> TimetableDayBreakTimes => Set<TimetableDayBreakTimeEntity>();
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
    public DbSet<AccCombinationEntity> AccCombinations => Set<AccCombinationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("E_Attendace");

        // Convert Guid to string for all Guid properties to match the TEXT columns in database
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(Guid))
                {
                    property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.GuidToStringConverter());
                }
                else if (property.ClrType == typeof(Guid?))
                {
                    property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.GuidToStringConverter());
                }
            }
        }

        modelBuilder.Entity<PersonnelEntity>().ToTable("Employees");
        modelBuilder.Entity<PersonnelEntity>().HasIndex(x => x.EmployeeCode).IsUnique();
        modelBuilder.Entity<DeviceEntity>().HasIndex(x => x.SerialNumber).IsUnique();
        modelBuilder.Entity<UserEntity>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<RawPushLogEntity>().HasIndex(x => new { x.Processed, x.ReceivedAt });
        modelBuilder.Entity<AttendanceRecordEntity>().ToTable("AttendanceRecords");
        modelBuilder.Entity<AttendanceRecordEntity>().Property(x => x.EmployeeId).HasColumnName("PersonnelId");
        modelBuilder.Entity<AttendanceRecordEntity>().HasIndex(x => new { x.EmployeeId, x.AttendanceDate }).IsUnique();
        
        modelBuilder.Entity<TimetableEntity>().ToTable("Timetables");
        modelBuilder.Entity<TimetableEntity>().HasIndex(x => x.TimetableName).IsUnique();
        
        modelBuilder.Entity<TimetableDayEntity>().ToTable("TimetableDays");
        modelBuilder.Entity<TimetableDayEntity>().HasIndex(x => new { x.TimetableId, x.DayOfWeek }).IsUnique();
        
        modelBuilder.Entity<BreakTimeEntity>().ToTable("BreakTimes");
        modelBuilder.Entity<BreakTimeEntity>().HasIndex(x => x.BreakName).IsUnique();
        
        modelBuilder.Entity<TimetableDayBreakTimeEntity>().ToTable("TimetableDayBreakTimes");
        modelBuilder.Entity<TimetableDayBreakTimeEntity>().HasIndex(x => new { x.TimetableDayId, x.BreakTimeId }).IsUnique();
        
        modelBuilder.Entity<EmployeeTimetableEntity>().ToTable("EmployeeTimetables");
        modelBuilder.Entity<EmployeeTimetableEntity>().Property(x => x.EmployeeId).HasColumnName("PersonnelId");
        modelBuilder.Entity<EmployeeTimetableEntity>().HasIndex(x => new { x.EmployeeId, x.EffectiveFrom });
        
        modelBuilder.Entity<DepartmentTimetableEntity>().ToTable("DepartmentTimetables");
        modelBuilder.Entity<DepartmentTimetableEntity>().HasIndex(x => new { x.DepartmentId, x.EffectiveFrom });
        
        modelBuilder.Entity<HolidayEntity>().ToTable("Holidays");
        modelBuilder.Entity<HolidayEntity>().HasIndex(x => new { x.HolidayDate, x.DepartmentId });
        
        modelBuilder.Entity<LeaveTypeEntity>().ToTable("LeaveTypes");
        modelBuilder.Entity<LeaveTypeEntity>().HasIndex(x => x.LeaveName).IsUnique();
        
        modelBuilder.Entity<LeaveRequestEntity>().ToTable("LeaveRequests");
        modelBuilder.Entity<LeaveRequestEntity>().Property(x => x.EmployeeId).HasColumnName("PersonnelId");
        modelBuilder.Entity<LeaveRequestEntity>().Property(x => x.ApprovedBy).HasColumnName("ApprovedBy");
        modelBuilder.Entity<LeaveRequestEntity>().HasIndex(x => new { x.EmployeeId, x.FromDate, x.ToDate, x.Status });
        
        modelBuilder.Entity<LeaveBalanceEntity>().ToTable("LeaveBalances");
        modelBuilder.Entity<LeaveBalanceEntity>().Property(x => x.EmployeeId).HasColumnName("PersonnelId");
        modelBuilder.Entity<LeaveBalanceEntity>().HasIndex(x => new { x.EmployeeId, x.LeaveTypeId, x.Year }).IsUnique();
        
        modelBuilder.Entity<LeaveAttachmentEntity>().ToTable("LeaveAttachments");
        modelBuilder.Entity<LeaveAttachmentEntity>().HasIndex(x => x.LeaveRequestId);
        
        modelBuilder.Entity<LeaveRemarkEntity>().ToTable("LeaveRemarks");
        modelBuilder.Entity<LeaveRemarkEntity>().Property(x => x.ActorId).HasColumnName("ActorId");
        modelBuilder.Entity<LeaveRemarkEntity>().HasIndex(x => new { x.LeaveRequestId, x.CreatedAt });

        modelBuilder.Entity<DeviceHeartbeatEntity>()
            .HasOne(x => x.Device).WithMany(x => x.Heartbeats).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RawPushLogEntity>()
            .HasOne(x => x.Device).WithMany(x => x.RawPushLogs).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<AttendanceRecordEntity>()
            .HasOne(x => x.Personnel).WithMany(x => x.AttendanceRecords).HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AttendanceExceptionEntity>()
            .HasOne(x => x.Attendance).WithMany(x => x.Exceptions).HasForeignKey(x => x.AttendanceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TimetableDayEntity>()
            .HasOne(x => x.Timetable).WithMany(x => x.Days).HasForeignKey(x => x.TimetableId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TimetableDayEntity>()
            .HasOne(x => x.Shift).WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<TimetableDayEntity>()
            .HasOne(x => x.BreakTime).WithMany().HasForeignKey(x => x.BreakTimeId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<TimetableDayBreakTimeEntity>()
            .HasOne(x => x.TimetableDay).WithMany(x => x.BreakTimes).HasForeignKey(x => x.TimetableDayId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TimetableDayBreakTimeEntity>()
            .HasOne(x => x.BreakTime).WithMany().HasForeignKey(x => x.BreakTimeId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<EmployeeTimetableEntity>()
            .HasOne(x => x.Personnel).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<EmployeeTimetableEntity>()
            .HasOne(x => x.Timetable).WithMany().HasForeignKey(x => x.TimetableId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DepartmentTimetableEntity>()
            .HasOne(x => x.Timetable).WithMany().HasForeignKey(x => x.TimetableId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LeaveRequestEntity>()
            .HasOne(x => x.Personnel).WithMany(x => x.LeaveRequests).HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LeaveRequestEntity>()
            .HasOne(x => x.LeaveType).WithMany(x => x.LeaveRequests).HasForeignKey(x => x.LeaveTypeId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LeaveBalanceEntity>()
            .HasOne(x => x.Personnel).WithMany(x => x.LeaveBalances).HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Cascade);
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
