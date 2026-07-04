namespace E.Attendance.Core.Models;

public sealed class Device
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string SerialNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "+5:45";
    public string FirmwareVersion { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string PushVer { get; set; } = string.Empty;
    public int Language { get; set; }
    public string MultiBioDataSupport { get; set; } = string.Empty;
    public string MultiBioPhotoSupport { get; set; } = string.Empty;
    public string MultiBioVersion { get; set; } = string.Empty;
    public string MaxMultiBioDataCount { get; set; } = string.Empty;
    public string MaxMultiBioPhotoCount { get; set; } = string.Empty;
    public bool FingerFunOn { get; set; }
    public bool FaceFunOn { get; set; }
    public bool VisiLightFunOn { get; set; }
    public bool BioDataFun { get; set; }
    public bool BioPhotoFun { get; set; }
    public bool SupportsVisibleLightFace { get; set; }
    public bool SupportsInfraredFace { get; set; }
    public bool SupportsFingerprint { get; set; }
    public bool SupportsPalm { get; set; }
    public bool SupportsFingerVein { get; set; }
    public string CapabilitiesJson { get; set; } = "{}";
    public string FaceAlgorithmVersion { get; set; } = string.Empty;
    public string FingerprintAlgorithmVersion { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DeviceStatus Status { get; set; } = DeviceStatus.Offline;
    public bool IsRegistrationDevice { get; set; }
    public bool IsOnline { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public bool IsRegistered { get; set; }
    
    // Hierarchy - mandatory per requirements
    public int AreaId { get; set; } = 1;
    public int DepartmentId { get; set; } = 1;
    
    // Navigation properties
    public Area? Area { get; set; }
    public Department? Department { get; set; }
    public ICollection<AttendanceRecord> AttendanceRecords { get; init; } = new List<AttendanceRecord>();
    public ICollection<DeviceCommand> Commands { get; init; } = new List<DeviceCommand>();
    public ICollection<SyncLog> SyncLogs { get; init; } = new List<SyncLog>();
}

public enum DeviceStatus
{
    Offline,
    Online,
    Busy
}