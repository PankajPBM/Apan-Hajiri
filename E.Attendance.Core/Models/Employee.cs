namespace E.Attendance.Core.Models;

public sealed class Employee
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string EmployeeCode { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public int? DepartmentId { get; set; }
    public List<string> Departments { get; set; } = [];
    public string Position { get; set; } = string.Empty;
    public string CardNumber { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string MobileNumber { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public UserPrivilege Privilege { get; set; } = UserPrivilege.User;
    public int VerifyMode { get; set; } = 0;
    public string? FaceTemplateVisible { get; set; }
    public string? FaceTemplateInfrared { get; set; }
    public string? ProfilePhoto { get; set; }
    public string? FingerprintTemplates { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SyncedAt { get; set; }
    public bool IsActive { get; set; } = true;

    public IReadOnlyList<string> GetDepartments()
    {
        if (Departments.Count > 0)
        {
            return Departments;
        }

        if (string.IsNullOrWhiteSpace(Department))
        {
            return [];
        }

        return [Department];
    }

    public void SetAssignments(string? area, IEnumerable<string>? departments, string? primaryDepartment = null)
    {
        Area = area?.Trim() ?? string.Empty;

        var normalizedDepartments = (departments ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var preferredPrimary = primaryDepartment?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(preferredPrimary))
        {
            normalizedDepartments.RemoveAll(value => string.Equals(value, preferredPrimary, StringComparison.OrdinalIgnoreCase));
            normalizedDepartments.Insert(0, preferredPrimary);
        }

        Department = normalizedDepartments.FirstOrDefault() ?? string.Empty;
        Departments = normalizedDepartments;
    }
}

public enum UserPrivilege
{
    User = 0,
    Registrar = 2,
    Administrator = 6,
    SuperAdministrator = 14
}
