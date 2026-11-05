namespace MediaServer.Metadata;

public record MetadataValue(
    string Title,
    int? Year,
    string Synopsis,
    string[] Genres,
    string[] Cast,
    string? Series,
    int? Season,
    int? Episode,
    string Provider,
    string? ProviderId,
    string Attribution
);

public record MetadataView(
    MetadataValue Details,
    string? PosterUrl,
    string? BackdropUrl,
    long Revision
);

public record MetadataSearch(string Query, string Kind);

public record MetadataCandidate(string Id, string Kind, string Title, int? Year);

public record MetadataMatch(
    string Id,
    string Kind,
    long ExpectedRevision,
    int? Season = null,
    int? Episode = null
);

public record MetadataCorrection(MetadataValue Details, long ExpectedRevision);

public record MetadataRevision(long ExpectedRevision);

public record MetadataResult(MetadataValue Details, string? Poster, string? Backdrop);

public interface IMetadataProvider
{
    string Id { get; }
    bool Enabled { get; }
    Task<MetadataCandidate[]> Search(MetadataSearch query, CancellationToken ct);
    Task<MetadataResult> Match(MetadataMatch match, CancellationToken ct);
}

public sealed class MetadataFault(string code) : Exception(code)
{
    public string Code { get; } = code;
}
