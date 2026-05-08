using Microsoft.EntityFrameworkCore;
using E.Attendance.Core.Models;

namespace E.Attendance.Data.Production;

public sealed class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<SyncLog> SyncLogs => Set<SyncLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Employee>(entity =>
        {
            entity.ToTable("Employees");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeCode).IsRequired().HasMaxLength(64);
            entity.Property(x => x.FullName).IsRequired().HasMaxLength(256);
            entity.Property(x => x.Department).HasMaxLength(128);
            entity.HasIndex(x => x.EmployeeCode).IsUnique();
            entity.HasIndex(x => x.IsActive);
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.ToTable("Devices");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SerialNumber).IsRequired().HasMaxLength(128);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(256);
            entity.Property(x => x.Model).HasMaxLength(128);
            entity.Property(x => x.IpAddress).HasMaxLength(64);
            entity.Property(x => x.Location).HasMaxLength(128);
            entity.Property(x => x.TimeZone).HasMaxLength(16);
            entity.HasIndex(x => x.SerialNumber).IsUnique();
            entity.HasIndex(x => x.IsOnline);
        });

        modelBuilder.Entity<SyncLog>(entity =>
        {
            entity.ToTable("SyncLogs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DeviceSerialNumber).IsRequired().HasMaxLength(128);
            entity.Property(x => x.CommandText).IsRequired();
            entity.HasIndex(x => new { x.DeviceSerialNumber, x.Status, x.CreatedUtc });
        });
    }
}
