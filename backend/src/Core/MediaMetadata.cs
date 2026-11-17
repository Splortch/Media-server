namespace MediaServer.Core;

public sealed class MediaMetadata
{
    public int MediaItemId { get; set; }
    public string DocumentJson { get; set; } = "{}";
    public string? PosterKey { get; set; }
    public string? BackdropKey { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class MediaArtworkOption
{
    public int MediaItemId { get; set; }
    public string Key { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
