namespace MediaServer.Core;

public class PlaybackBookmark
{
    public int UserId { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int MediaItemId { get; set; }
    public double Seconds { get; set; }
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    public long Revision { get; set; }
}
