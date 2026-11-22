namespace MediaServer.Core;

// Only the most recent season operation per account can be undone, for ten minutes.
public class SeasonWatchUndo
{
    public int UserId { get; set; }
    public string Token { get; set; } = "";
    public int MediaItemId { get; set; }
    public int Season { get; set; }
    public string Revision { get; set; } = "";
    public string SnapshotJson { get; set; } = "[]";
    public DateTime ExpiresAt { get; set; }
}
