namespace MediaServer.Core;

public class User
{
    public int Id { get; set; } = 1;
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Admin";
}

public class Library
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
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
}

public class PlaybackProgress
{
    public int UserId { get; set; }
    public int MediaItemId { get; set; }
    public double Seconds { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class Session
{
    public string Id { get; set; } = "";
    public int UserId { get; set; }
    public DateTime ExpiresAt { get; set; }
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

public record ProbeResult(double Duration, string Container, string VideoCodec, string AudioCodec);

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
