using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Metadata;

public record CatalogEntry(
    int Id,
    string Title,
    double Duration,
    string VideoCodec,
    string AudioCodec,
    string Container,
    long Size,
    int LibraryId,
    string? Series,
    int EpisodeCount
);

public record CatalogPage(CatalogEntry[] Items, string? NextCursor);

public record CatalogCursor(int Offset, string Filter);

public static class LibraryCatalog
{
    public static void MapLibraryCatalog(this RouteGroupBuilder group)
    {
        group
            .MapGet(
                "/catalog",
                async (
                    string? prefix,
                    string? cursor,
                    int? offset,
                    int? limit,
                    int? libraryId,
                    string? videoCodec,
                    int? collectionId,
                    MediaDb db,
                    ClaimsPrincipal user,
                    CancellationToken ct
                ) =>
                {
                    prefix = (prefix ?? "").Trim().ToLowerInvariant();
                    if (
                        prefix.Length > 128
                        || prefix.Any(char.IsControl)
                        || videoCodec is { Length: > 32 }
                        || offset is < 0 or > 100000
                    )
                        return Results.BadRequest();
                    var uid = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
                    if (collectionId is not null)
                    {
                        if (!await Features.Enabled(db, "smart-collections"))
                            return Results.Forbid();
                        var collection = await db.Collections.SingleOrDefaultAsync(
                            x => x.Id == collectionId && x.UserId == uid,
                            ct
                        );
                        if (collection is null)
                            return Results.NotFound();
                        if (prefix.Length == 0)
                            prefix = collection.Prefix;
                        else if (!prefix.StartsWith(collection.Prefix, StringComparison.Ordinal))
                            return Results.Ok(new CatalogPage([], null));
                        libraryId = collection.LibraryId ?? libraryId;
                        videoCodec = collection.VideoCodec ?? videoCodec;
                    }
                    var filter = Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                $"{uid}|{prefix}|{libraryId}|{videoCodec}|{collectionId}"
                            )
                        )
                    );
                    var start = offset ?? 0;
                    if (cursor is not null)
                    {
                        try
                        {
                            if (cursor.Length > 1024 || offset is not null)
                                return Results.BadRequest();
                            var decoded = JsonSerializer.Deserialize<CatalogCursor>(
                                Convert.FromBase64String(cursor)
                            );
                            if (
                                decoded is null
                                || decoded.Filter != filter
                                || decoded.Offset is < 0 or > 100000
                            )
                                return Results.BadRequest();
                            start = decoded.Offset;
                        }
                        catch (Exception e) when (e is FormatException or JsonException)
                        {
                            return Results.BadRequest();
                        }
                    }
                    var source =
                        from item in db.Items.AsNoTracking()
                        join metadata in db.Metadata.AsNoTracking()
                            on item.Id equals metadata.MediaItemId
                            into details
                        from metadata in details.DefaultIfEmpty()
                        let name = metadata == null
                            ? null
                            : MediaDb.MetadataText(metadata.DocumentJson, "$.Series")
                        let season = metadata == null
                            ? null
                            : MediaDb.MetadataNumber(metadata.DocumentJson, "$.Season")
                        let episode = metadata == null
                            ? null
                            : MediaDb.MetadataNumber(metadata.DocumentJson, "$.Episode")
                        let series = name != null
                        && name != ""
                        && season >= 0
                        && season <= 99
                        && episode >= 1
                        && episode <= 999
                            ? name
                            : null
                        where
                            (!libraryId.HasValue || item.LibraryId == libraryId.Value)
                            && (
                                videoCodec == null
                                || videoCodec == ""
                                || item.VideoCodec == videoCodec
                            )
                            && (
                                item.SearchTitle.StartsWith(prefix)
                                || (series != null && MediaDb.Fold(series).StartsWith(prefix))
                            )
                        select new
                        {
                            item.Id,
                            item.LibraryId,
                            item.Title,
                            Series = series,
                        };
                    // Group BEFORE pagination, and only after the per-user Items filters.
                    var groups = source
                        .GroupBy(x => new
                        {
                            x.LibraryId,
                            Key = x.Series == null ? "m:" + x.Id : "s:" + x.Series,
                        })
                        .Select(g => new
                        {
                            Id = g.Min(x => x.Id),
                            Title = g.Max(x => x.Series) ?? g.Max(x => x.Title),
                            Series = g.Max(x => x.Series),
                            Count = g.Count(),
                        });
                    var size = Math.Clamp(limit ?? 60, 1, 200);
                    var page = await groups
                        .OrderBy(x => MediaDb.Fold(x.Title!))
                        .ThenBy(x => x.Id)
                        .Skip(start)
                        .Take(size + 1)
                        .ToArrayAsync(ct);
                    var rows = page.Take(size).ToArray();
                    var ids = rows.Select(x => x.Id).ToArray();
                    var media = await db
                        .Items.AsNoTracking()
                        .Where(x => ids.Contains(x.Id))
                        .ToDictionaryAsync(x => x.Id, ct);
                    var items = rows.Where(x => media.ContainsKey(x.Id))
                        .Select(x =>
                        {
                            var m = media[x.Id];
                            return new CatalogEntry(
                                m.Id,
                                x.Title!,
                                m.Duration,
                                m.VideoCodec,
                                m.AudioCodec,
                                m.Container,
                                m.Size,
                                m.LibraryId,
                                x.Series,
                                x.Series is null ? 0 : x.Count
                            );
                        })
                        .ToArray();
                    var next =
                        page.Length > size
                            ? Convert.ToBase64String(
                                JsonSerializer.SerializeToUtf8Bytes(
                                    new CatalogCursor(start + size, filter)
                                )
                            )
                            : null;
                    return Results.Ok(new CatalogPage(items, next));
                }
            )
            .Produces<CatalogPage>();
    }
}
