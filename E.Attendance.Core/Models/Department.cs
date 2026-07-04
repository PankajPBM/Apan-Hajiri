namespace E.Attendance.Core.Models;

public sealed class Department
{
    public int Id { get; init; }
    public string Name { get; set; } = string.Empty;
    public int AreaId { get; set; }
    public bool IsDefault { get; set; } = false;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation properties
    public Area? Area { get; set; }
    public ICollection<EmployeeDepartment> EmployeeDepartments { get; init; } = new List<EmployeeDepartment>();
    public ICollection<Device> Devices { get; init; } = new List<Device>();
    public ICollection<AttendanceRecord> AttendanceRecords { get; init; } = new List<AttendanceRecord>();
}