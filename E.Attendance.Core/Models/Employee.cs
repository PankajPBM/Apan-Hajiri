namespace E.Attendance.Core.Models;

public sealed class Employee
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string EmployeeCode { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
    public string CardNumber { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string MobileNumber { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public UserPrivilege Privilege { get; set; } = UserPrivilege.User;
    public int VerifyMode { get; set; } = -1;
    public string? FaceTemplateVisible { get; set; }
    public string? FaceTemplateInfrared { get; set; }
    public string? ProfilePhoto { get; set; }
    public string? FingerprintTemplates { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SyncedAt { get; set; }
    public bool IsActive { get; set; } = true;

    // Mandatory area/department assignment (integer FK, default = Default Area/Dept)
    public int AreaId { get; set; } = 1;
    public int DepartmentId { get; set; } = 1;   // primary department FK

    // Legacy string fields — kept for raw-SQL repository compatibility and ZKTeco protocol
    public string Area { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public List<string> Departments { get; set; } = [];

    // Navigation property for many-to-many Employee ↔ Department mapping
    public ICollection<EmployeeDepartment> EmployeeDepartments { get; init; } = new List<EmployeeDepartment>();

    // -----------------------------------------------------------------------
    // Assignment helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sets area + department string fields and the primary department id.
    /// Used by AttendanceService and the ZKTeco push-flow to assign an employee
    /// to a logical grouping before persisting via the raw-SQL repository.
    /// </summary>
    public void SetAssignments(string area, IEnumerable<string> departments, string? primaryDepartment = null)
    {
        var deptList = departments
            .Where(static d => !string.IsNullOrWhiteSpace(d))
            .Select(static d => d.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Area = area?.Trim() ?? string.Empty;
        Departments = deptList;
        Department = primaryDepartment?.Trim()
            ?? deptList.FirstOrDefault()
            ?? string.Empty;
    }

    // -----------------------------------------------------------------------
    // EmployeeDepartments helpers (new EF Core path)
    // -----------------------------------------------------------------------

    public IReadOnlyList<int> GetDepartmentIds()
        => EmployeeDepartments.Select(ed => ed.DepartmentId).Distinct().ToList();

    public IReadOnlyList<Department> GetDepartments()
        => EmployeeDepartments.Select(ed => ed.Department).Where(d => d != null).Distinct().ToList()!;

    public Department? GetPrimaryDepartment()
        => EmployeeDepartments.OrderBy(ed => ed.AssignedAt).FirstOrDefault()?.Department;

    /// <summary>
    /// Returns the single AreaId shared by all assigned departments, or null if
    /// none are assigned or they span multiple areas (constraint violation).
    /// </summary>
    public int? GetAreaId()
    {
        var depts = GetDepartments();
        if (depts.Count == 0) return null;
        var areaIds = depts.Select(d => d.AreaId).Distinct().ToList();
        return areaIds.Count == 1 ? areaIds[0] : null;
    }

    public bool IsValidDepartmentAssignment() => GetAreaId().HasValue;

    public bool AddDepartment(int departmentId, int areaId, Guid? assignedBy = null)
    {
        if (EmployeeDepartments.Any(ed => ed.DepartmentId == departmentId))
            return true;

        var currentAreaId = GetAreaId();
        if (currentAreaId.HasValue && currentAreaId.Value != areaId)
            return false;

        EmployeeDepartments.Add(new EmployeeDepartment
        {
            EmployeeId = Id,
            DepartmentId = departmentId,
            AssignedAt = DateTime.UtcNow,
            AssignedBy = assignedBy
        });
        return true;
    }

    public bool RemoveDepartment(int departmentId)
    {
        var ed = EmployeeDepartments.FirstOrDefault(e => e.DepartmentId == departmentId);
        if (ed == null) return false;
        EmployeeDepartments.Remove(ed);
        return true;
    }

    public bool SetDepartments(IEnumerable<(int DepartmentId, int AreaId)> departments, Guid? assignedBy = null)
    {
        var deptList = departments.ToList();
        if (deptList.Count == 0) { EmployeeDepartments.Clear(); return true; }

        var areaIds = deptList.Select(d => d.AreaId).Distinct().ToList();
        if (areaIds.Count != 1) return false;

        EmployeeDepartments.Clear();
        foreach (var (deptId, areaId) in deptList)
        {
            EmployeeDepartments.Add(new EmployeeDepartment
            {
                EmployeeId = Id,
                DepartmentId = deptId,
                AssignedAt = DateTime.UtcNow,
                AssignedBy = assignedBy
            });
        }
        return true;
    }
}

public enum UserPrivilege
{
    User = 0,
    Registrar = 2,
    Administrator = 6,
    SuperAdministrator = 14
}
