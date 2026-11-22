namespace MediaServer.Core;

public sealed class MetadataMatchReport
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int MediaItemId { get; set; }
    public string Kind { get; set; } = "title";
    public string Note { get; set; } = "";
    public string State { get; set; } = "Open";
    public long Revision { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
