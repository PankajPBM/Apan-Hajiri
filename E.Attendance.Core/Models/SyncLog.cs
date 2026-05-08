namespace E.Attendance.Core.Models;

public sealed class SyncLog
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string DeviceSerialNumber { get; set; } = string.Empty;
    public SyncType Type { get; set; } = SyncType.Configuration;
    public SyncStatus Status { get; set; } = SyncStatus.Pending;
    public string CommandText { get; set; } = string.Empty;
    public string ResponseText { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
}

public enum SyncType
{
    UserInfo,
    FaceTemplate,
    ProfilePhoto,
    Fingerprint,
    Attendance,
    Configuration
}

public enum SyncStatus
{
    Pending,
    Sent,
    Completed,
    Failed
}
