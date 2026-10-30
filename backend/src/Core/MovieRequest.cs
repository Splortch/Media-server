namespace MediaServer.Core;

public sealed class MovieRequest
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int LibraryId { get; set; }
    public string Title { get; set; } = "";
    public string SearchTitle { get; set; } = "";
    public int? Year { get; set; }
    public string Note { get; set; } = "";
    public string State { get; set; } = "Requested";
    public string Reply { get; set; } = "";
    public int? MediaItemId { get; set; }
    public long Revision { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class RequestSupport
{
    public int RequestId { get; set; }
    public int UserId { get; set; }
}
