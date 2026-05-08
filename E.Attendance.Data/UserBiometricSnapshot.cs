namespace E.Attendance.Data;

public sealed record UnifiedUserSnapshot
{
    public string Pin { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int? Privilege { get; init; }
    public string Password { get; init; } = string.Empty;
    public string PrimaryCard { get; init; } = string.Empty;
    public string SecondaryCard { get; init; } = string.Empty;
    public int? GroupId { get; init; }
    public string TimezoneProfile { get; init; } = string.Empty;
    public int? VerifyMode { get; init; }
    public DateTime? ValidFrom { get; init; }
    public DateTime? ValidTo { get; init; }
    public bool? ExpiresEnabled { get; init; }
    public string Phone { get; init; } = string.Empty;
    public int? Gender { get; init; }
    public int? Nation { get; init; }
    public string IdNumber { get; init; } = string.Empty;
    public DateTime? UpdatedAt { get; init; }
}

public sealed record BiometricTemplateSnapshot
{
    public string UserPin { get; init; } = string.Empty;
    public int Type { get; init; }
    public int No { get; init; }
    public int Index { get; init; }
    public bool Valid { get; init; }
    public bool Duress { get; init; }
    public int? MajorVersion { get; init; }
    public int? MinorVersion { get; init; }
    public int? Format { get; init; }
    public string TemplateData { get; init; } = string.Empty;
    public DateTime? UpdatedAt { get; init; }
}

public sealed record BiometricPhotoSnapshot
{
    public string UserPin { get; init; } = string.Empty;
    public int Type { get; init; }
    public int No { get; init; }
    public int Index { get; init; }
    public int? Format { get; init; }
    public string Content { get; init; } = string.Empty;
    public DateTime? UpdatedAt { get; init; }
}
