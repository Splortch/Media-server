namespace MediaServer.Core;

public sealed class DownloadJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int? RequestId { get; set; }
    public int? RequestOwnerId { get; set; }
    public long? RequestRevision { get; set; }
    public int? TargetLibraryId { get; set; }
    public string? TargetRoot { get; set; }
    public string? Classification { get; set; }
    public string? ImportTitle { get; set; }
    public int? ImportYear { get; set; }
    public string? Series { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public int ImportRating { get; set; }
    public string? ImportExtension { get; set; }
    public string? ImportRelativePath { get; set; }
    public string? ImportNonce { get; set; }
    public string ImportState { get; set; } = "none";
    public string? ImportError { get; set; }
    public long ImportCopiedBytes { get; set; }
    public int? ImportedMediaId { get; set; }
    public int? ImportScanId { get; set; }
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
