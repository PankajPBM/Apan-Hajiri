using Microsoft.EntityFrameworkCore;
using E.Attendance.Core.Models;

namespace E.Attendance.Data.Production;

public sealed class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Area> Areas => Set<Area>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<EmployeeDepartment> EmployeeDepartments => Set<EmployeeDepartment>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<SyncLog> SyncLogs => Set<SyncLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("E_Attendace");
        base.OnModelCreating(modelBuilder);

        // ── Areas ─────────────────────────────────────────────────────────────
        modelBuilder.Entity<Area>(entity =>
        {
            entity.ToTable("Areas");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).UseIdentityAlwaysColumn();
            entity.Property(x => x.Name).IsRequired().HasMaxLength(128);
            entity.HasIndex(x => x.Name).IsUnique();
        });

        // ── Departments ───────────────────────────────────────────────────────
        modelBuilder.Entity<Department>(entity =>
        {
            entity.ToTable("Departments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).UseIdentityAlwaysColumn();
            entity.Property(x => x.Name).IsRequired().HasMaxLength(128);
            // (AreaId, Name) must be unique — no duplicate dept name within same area
            entity.HasIndex(x => new { x.AreaId, x.Name }).IsUnique();

            entity.HasOne(d => d.Area)
                .WithMany(a => a.Departments)
                .HasForeignKey(d => d.AreaId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Employees ─────────────────────────────────────────────────────────
        modelBuilder.Entity<Employee>(entity =>
        {
            entity.ToTable("Employees");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeCode).IsRequired().HasMaxLength(64);
            entity.Property(x => x.FullName).IsRequired().HasMaxLength(256);
            entity.Property(x => x.Department).HasMaxLength(128);
            entity.Property(x => x.Area).HasMaxLength(128);
            entity.HasIndex(x => x.EmployeeCode).IsUnique();
            entity.HasIndex(x => x.IsActive);
            entity.HasIndex(x => x.AreaId);
            entity.HasIndex(x => x.DepartmentId);

            // Ignore the List<string> Departments — raw-SQL path only (stored as DepartmentsJson)
            entity.Ignore(e => e.Departments);
        });

        // ── EmployeeDepartments (junction) ────────────────────────────────────
        modelBuilder.Entity<EmployeeDepartment>(entity =>
        {
            entity.ToTable("EmployeeDepartments");
            entity.HasKey(x => new { x.EmployeeId, x.DepartmentId });

            entity.HasOne(ed => ed.Employee)
                .WithMany(e => e.EmployeeDepartments)
                .HasForeignKey(ed => ed.EmployeeId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(ed => ed.Department)
                .WithMany(d => d.EmployeeDepartments)
                .HasForeignKey(ed => ed.DepartmentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Devices ───────────────────────────────────────────────────────────
        modelBuilder.Entity<Device>(entity =>
        {
            entity.ToTable("Devices");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SerialNumber).IsRequired().HasMaxLength(128);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(256);
            entity.Property(x => x.Model).HasMaxLength(128);
            entity.Property(x => x.IpAddress).HasMaxLength(64);
            entity.Property(x => x.TimeZone).HasMaxLength(16);
            entity.HasIndex(x => x.SerialNumber).IsUnique();
            entity.HasIndex(x => x.IsOnline);
            entity.HasIndex(x => new { x.AreaId, x.DepartmentId });

            entity.Ignore(d => d.Area);
            entity.Ignore(d => d.Department);
            entity.Ignore(d => d.AttendanceRecords);
            entity.Ignore(d => d.Commands);
            entity.Ignore(d => d.SyncLogs);
        });

        // ── SyncLog ───────────────────────────────────────────────────────────
        modelBuilder.Entity<SyncLog>(entity =>
        {
            entity.ToTable("DeviceSyncLogs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DeviceSerialNumber).IsRequired().HasMaxLength(128);
            entity.Property(x => x.CommandText).IsRequired();
            entity.HasIndex(x => new { x.DeviceSerialNumber, x.Status, x.CreatedUtc });
        });
    }
}
