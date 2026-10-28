using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

public record CollectionInput(
    string Name,
    string Prefix = "",
    int? LibraryId = null,
    string? VideoCodec = null
);

public record MediaPage(MediaSummary[] Items, string? NextCursor);

public record MediaCursor(string Title, int Id, string Filter);

public static partial class Operations
{
    private static string GlobPrefix(string text) =>
        string.Concat(
            text.Select(c =>
                c switch
                {
                    '[' => "[[]",
                    '*' => "[*]",
                    '?' => "[?]",
                    _ => c.ToString(),
                }
            )
        ) + "*";

    private static void ValidateSearch(string prefix, string? codec)
    {
        if (
            prefix.Length > 128
            || prefix.Any(char.IsControl)
            || codec is { Length: > 32 }
            || codec?.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') == true
        )
            throw new ArgumentException("Invalid search prefix or codec.");
    }

    private static void MapSearch(RouteGroupBuilder api)
    {
        api.MapGet(
            "/collections",
            async (MediaDb db, ClaimsPrincipal user) =>
                await db
                    .Collections.Where(x => x.UserId == Uid(user))
                    .OrderBy(x => x.Name)
                    .ToListAsync()
        );
        api.MapPost(
            "/collections",
            async (CollectionInput input, MediaDb db, ClaimsPrincipal user) =>
            {
                if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80)
                    throw new ArgumentException("Collection name must contain 1–80 characters.");
                ValidateSearch(input.Prefix, input.VideoCodec);
                var uid = Uid(user);
                await using var transaction = await db.Database.BeginTransactionAsync();
                if (await db.Collections.CountAsync(x => x.UserId == uid) >= 50)
                    return Results.Conflict();
                if (
                    input.LibraryId is not null
                    && !await db.Libraries.AnyAsync(x => x.Id == input.LibraryId)
                )
                    return Results.NotFound();
                var collection = new SmartCollection
                {
                    UserId = uid,
                    Name = input.Name.Trim(),
                    Prefix = input.Prefix.Trim().ToLowerInvariant(),
                    LibraryId = input.LibraryId,
                    VideoCodec = input.VideoCodec,
                };
                db.Collections.Add(collection);
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                return Results.Created($"/api/collections/{collection.Id}", collection);
            }
        );
        api.MapDelete(
            "/collections/{id:int}",
            async (int id, MediaDb db, ClaimsPrincipal user) =>
            {
                var uid = Uid(user);
                var count = await db
                    .Collections.Where(x => x.Id == id && x.UserId == uid)
                    .ExecuteDeleteAsync();
                return count == 0 ? Results.NotFound() : Results.NoContent();
            }
        );
        api.MapGet(
                "/media-page",
                async (
                    string? prefix,
                    string? cursor,
                    int? limit,
                    int? libraryId,
                    string? videoCodec,
                    int? collectionId,
                    MediaDb db,
                    ClaimsPrincipal user
                ) =>
                {
                    prefix = (prefix ?? "").Trim().ToLowerInvariant();
                    ValidateSearch(prefix, videoCodec);
                    var uid = Uid(user);
                    if (collectionId is not null)
                    {
                        var collection = await db.Collections.SingleOrDefaultAsync(x =>
                            x.Id == collectionId && x.UserId == uid
                        );
                        if (collection is null)
                            return Results.NotFound();
                        // An explicit search can narrow a collection prefix, never broaden it.
                        if (prefix.Length == 0)
                            prefix = collection.Prefix;
                        else if (!prefix.StartsWith(collection.Prefix, StringComparison.Ordinal))
                            return Results.Ok(new MediaPage([], null));
                        libraryId = collection.LibraryId ?? libraryId;
                        videoCodec = collection.VideoCodec ?? videoCodec;
                    }
                    var filter = Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                $"{uid}|{prefix}|{libraryId}|{videoCodec}|{collectionId}"
                            )
                        )
                    )[..16];
                    var after = new MediaCursor("", 0, filter);
                    if (cursor is not null)
                    {
                        try
                        {
                            if (cursor.Length > 4096)
                                throw new FormatException();
                            after =
                                JsonSerializer.Deserialize<MediaCursor>(
                                    Convert.FromBase64String(cursor)
                                ) ?? throw new FormatException();
                            if (
                                after.Title is null
                                || after.Title.Length > 512
                                || after.Id < 1
                                || after.Filter != filter
                            )
                                throw new FormatException();
                        }
                        catch (Exception ex) when (ex is FormatException or JsonException)
                        {
                            throw new ArgumentException("Invalid cursor for this search.");
                        }
                    }
                    var pattern = GlobPrefix(prefix);
                    var size = Math.Clamp(limit ?? 60, 1, 200);
                    // All values are bound parameters; the global per-user/rating filter also applies to FromSql.
                    var query = db
                        .Items.FromSqlInterpolated(
                            $"SELECT * FROM Items WHERE SearchTitle GLOB {pattern} AND SearchTitle >= {after.Title} AND (SearchTitle > {after.Title} OR (SearchTitle = {after.Title} AND Id > {after.Id}))"
                        )
                        .AsNoTracking();
                    if (libraryId.HasValue)
                        query = query.Where(x => x.LibraryId == libraryId.Value);
                    if (!string.IsNullOrEmpty(videoCodec))
                        query = query.Where(x => x.VideoCodec == videoCodec);
                    var page = await query
                        .OrderBy(x => x.SearchTitle)
                        .ThenBy(x => x.Id)
                        .Take(size + 1)
                        .ToListAsync();
                    var more = page.Count > size;
                    var values = page.Take(size).ToArray();
                    var next = more
                        ? Convert.ToBase64String(
                            JsonSerializer.SerializeToUtf8Bytes(
                                new MediaCursor(values[^1].SearchTitle, values[^1].Id, filter)
                            )
                        )
                        : null;
                    return Results.Ok(new MediaPage(values.Select(Summary).ToArray(), next));
                }
            )
            .Produces<MediaPage>();
        api.MapGet(
            "/continue-watching-media",
            async (MediaDb db, ClaimsPrincipal user) =>
            {
                var uid = Uid(user);
                return await (
                    from p in db.Progress
                    join item in db.Items on p.MediaItemId equals item.Id
                    where p.UserId == uid && !p.Completed && p.Seconds > 0
                    orderby p.UpdatedAt descending
                    select new MediaSummary(
                        item.Id,
                        item.Title,
                        item.Duration,
                        item.VideoCodec,
                        item.AudioCodec,
                        item.Container,
                        item.Size,
                        item.LibraryId
                    )
                )
                    .Take(100)
                    .ToListAsync();
            }
        );
    }

    private static MediaSummary Summary(MediaItem item) =>
        new(
            item.Id,
            item.Title,
            item.Duration,
            item.VideoCodec,
            item.AudioCodec,
            item.Container,
            item.Size,
            item.LibraryId
        );
}
