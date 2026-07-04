namespace E.Attendance.Core.Models;

public sealed class EmployeeDepartment
{
    public Guid EmployeeId { get; init; } = Guid.NewGuid();
    public int DepartmentId { get; init; }
    public DateTime AssignedAt { get; init; } = DateTime.UtcNow;
    public Guid? AssignedBy { get; init; }
    
    // Navigation properties
    public Employee? Employee { get; set; }
    public Department? Department { get; set; }
}