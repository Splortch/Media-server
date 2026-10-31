namespace MediaServer.Core;

public sealed class DownloadJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public string SourceHost { get; set; } = "";
    public string ProtectedUrl { get; set; } = "";
    public string State { get; set; } = "queued";
    public string Desired { get; set; } = "run";
    public int Priority { get; set; }
    public long MaxBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public long? TotalBytes { get; set; }
    public string? SourceIdentity { get; set; }
    public string? ETag { get; set; }
    public string? ExpectedHash { get; set; }
    public string? ContentHash { get; set; }
    public string? Error { get; set; }
    public long Revision { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class DownloadPolicy
{
    public int Id { get; set; } = 1;
    public string HostsJson { get; set; } = "[]";
}
