namespace E.Attendance.Core.Models;

public sealed class DeviceCommand
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string DeviceSerialNumber { get; set; } = string.Empty;
    public string CommandText { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentUtc { get; set; }
    public string? ReplyText { get; set; }
}
