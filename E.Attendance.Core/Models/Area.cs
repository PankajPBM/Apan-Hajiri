namespace E.Attendance.Core.Models;

public sealed class Area
{
    public int Id { get; init; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; } = false;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation properties
    public ICollection<Department> Departments { get; init; } = new List<Department>();
    public ICollection<Device> Devices { get; init; } = new List<Device>();
    public ICollection<Employee> Employees { get; init; } = new List<Employee>();
    public ICollection<AttendanceRecord> AttendanceRecords { get; init; } = new List<AttendanceRecord>();
}