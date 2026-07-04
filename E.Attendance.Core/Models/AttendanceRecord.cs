namespace E.Attendance.Core.Models;

public sealed class AttendanceRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid EmployeeId { get; init; }
    public string EmployeeCode { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public AttendanceStatus Status { get; init; } = AttendanceStatus.Unknown;
    public int? StatusCode { get; init; }
    public int? VerifyMode { get; init; }
    public string? Workcode { get; init; }
    public Guid? DeviceId { get; init; }
    public string? DeviceSerialNumber { get; init; }
    
    // Hierarchy - inherited from Device/Employee at punch time
    public int AreaId { get; init; } = 1;
    public int DepartmentId { get; init; } = 1;
    
    // Navigation properties
    public Area? Area { get; set; }
    public Department? Department { get; set; }
    public Employee? Employee { get; set; }
    public Device? Device { get; set; }
}