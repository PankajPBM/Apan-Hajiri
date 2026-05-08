using Microsoft.EntityFrameworkCore;
using E.Attendance.Core.Interfaces;
using E.Attendance.Data;
using E.Attendance.Data.Production;
using E.Attendance.Services;

namespace E.Attendance.Web;

internal static class ServiceExtensions
{
    public static void AddPostgresModules(this IServiceCollection services, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
        });

        services.AddScoped<IEmployeeRepository, EmployeeRepository>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<ISyncLogRepository, SyncLogRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IAttendanceRepository>(_ => new PostgresAttendanceRepository(connectionString));

        services.AddScoped<IDeviceProtocolService, DeviceProtocolService>();
        services.AddScoped<IEmployeeService, EmployeeService>();
        services.AddScoped<ISyncService, SyncService>();
    }
}
