namespace MediaServer.Core;

public class User
{
    public int Id { get; set; } = 1;
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Admin";
    public bool Disabled { get; set; }
    public bool AllLibraries { get; set; } = true;
    public int? MaximumRating { get; set; }
}

public class Library
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "movies";
    public bool WatchEnabled { get; set; }
    public bool ComputeHashes { get; set; }
}

public class MediaItem
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public string RelativePath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Container { get; set; } = "";
    public string VideoCodec { get; set; } = "";
    public string AudioCodec { get; set; } = "";
    public double Duration { get; set; }
    public long Size { get; set; }
    public string ColorJson { get; set; } = "null";
    public string TracksJson { get; set; } = "[]";
    public int ContentRating { get; set; }
    public bool IsMissing { get; set; }
    public string Fingerprint { get; set; } = "";
    public long ModifiedTicks { get; set; }
    public string SearchTitle { get; set; } = "";
}

public class PlaybackProgress
{
    public int UserId { get; set; }
    public int MediaItemId { get; set; }
    public double Seconds { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long Revision { get; set; }
    public bool Completed { get; set; }
}

public class Session
{
    public string Id { get; set; } = "";
    public int UserId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string DeviceName { get; set; } = "Web browser";
    public string CapabilitiesJson { get; set; } = "{}";
}

public class ScanJob
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public string State { get; set; } = "queued";
    public int Discovered { get; set; }
    public int Failed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public record MediaTrack(
    int Index,
    string Type,
    string Codec,
    string? Language,
    string? Title,
    int? Channels = null,
    string? ChannelLayout = null,
    bool Default = false,
    bool Forced = false
);

public record ProbeResult(
    double Duration,
    string Container,
    string VideoCodec,
    string AudioCodec,
    IReadOnlyList<MediaTrack>? Tracks = null,
    string ColorJson = "null"
);

public interface IMediaProbe
{
    Task<ProbeResult> ProbeAsync(string path, CancellationToken ct);
}

public record PlaybackPlan(string Mode, string MimeType, string? Warning);

public interface IPlaybackPlanner
{
    PlaybackPlan Plan(MediaItem item);
}

public interface IScanEvents
{
    Task PublishAsync(ScanJob job, CancellationToken ct);
}

public class PlaybackReport
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int MediaItemId { get; set; }
    public string Code { get; set; } = "unknown";
    public string Client { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ScanIssue
{
    public int Id { get; set; }
    public int ScanJobId { get; set; }
    public string FileName { get; set; } = "";
    public string Code { get; set; } = "probe_failed";
    public string CorrelationId { get; set; } = "";
}

public class MediaProbeException(string code) : IOException("Media identification failed.")
{
    public string Code { get; } = code;
}

public interface IUserContext
{
    int? UserId { get; }
}

public class LibraryGrant
{
    public int UserId { get; set; }
    public int LibraryId { get; set; }
}

public class WatchHistory
{
    public int UserId { get; set; }
    public int MediaItemId { get; set; }
    public double Seconds { get; set; }
    public DateTime LastPlayedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class SmartCollection
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public int? LibraryId { get; set; }
    public string? VideoCodec { get; set; }
}

public class FeaturePolicy
{
    public string Key { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public class FeatureVisibility
{
    public int UserId { get; set; }
    public string Key { get; set; } = "";
}
