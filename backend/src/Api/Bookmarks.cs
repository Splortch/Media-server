using System.Security.Claims;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

public record BookmarkInput(double Seconds, string Name, string? Note, long ExpectedRevision = 0);

public record BookmarkView(string Id, double Seconds, string Name, string Note, long Revision);

public record BookmarkTransfer(string Id, double Seconds, string Name, string? Note);

public record BookmarkArchive(int Version, string MediaKey, BookmarkTransfer[] Bookmarks);

public static class Bookmarks
{
    private static BookmarkView View(PlaybackBookmark b) =>
        new(b.Id, b.Seconds, b.Name, b.Note, b.Revision);

    private static void Validate(BookmarkInput input, MediaItem item)
    {
        if (
            !double.IsFinite(item.Duration)
            || item.Duration <= 0
            || !double.IsFinite(input.Seconds)
            || input.Seconds < 0
            || input.Seconds > item.Duration
        )
            throw new ArgumentException("Choose a timestamp within this title's known duration.");
        if (
            string.IsNullOrWhiteSpace(input.Name)
            || input.Name.Length > 80
            || (input.Note?.Length ?? 0) > 500
            || (input.Name + input.Note).Any(c => char.IsControl(c) && c != '\n' && c != '\t')
        )
            throw new ArgumentException(
                "Use a name of 1–80 characters and a note of at most 500 characters, without control characters."
            );
    }

    private static string MediaKey(MediaItem item)
    {
        if (item.Fingerprint.Length != 64 || !item.Fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException(
                "Portable transfer requires a full SHA-256 content hash. Ask an administrator to enable library hashing and rescan first."
            );
        return "sha256:" + item.Fingerprint.ToLowerInvariant();
    }

    public static void MapBookmarks(this WebApplication app)
    {
        var gate = new SemaphoreSlim(1, 1);
        foreach (var prefix in new[] { "/api", "/api/v1" })
        {
            var api = app.MapGroup(prefix + "/media/{id:int}/bookmarks").RequireAuthorization();
            if (prefix.EndsWith("v1", StringComparison.Ordinal))
                api.WithGroupName("client-v1");
            api.AddEndpointFilter(
                async (context, next) =>
                {
                    if (context.HttpContext.User.IsInRole("Guest"))
                        return Results.Forbid();
                    if (HttpMethods.IsGet(context.HttpContext.Request.Method))
                        return await next(context);
                    await gate.WaitAsync(context.HttpContext.RequestAborted);
                    try
                    {
                        return await next(context);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
            );
            api.MapGet(
                "",
                async (int id, MediaDb db, ClaimsPrincipal user) =>
                {
                    if (!await db.Items.AnyAsync(x => x.Id == id))
                        return Results.NotFound();
                    var uid = Operations.Uid(user);
                    var rows = await db
                        .Bookmarks.Where(x => x.UserId == uid && x.MediaItemId == id)
                        .OrderBy(x => x.Seconds)
                        .ThenBy(x => x.Id)
                        .ToArrayAsync();
                    return Results.Ok(rows.Select(View).ToArray());
                }
            );
            api.MapPost(
                "",
                async (int id, BookmarkInput input, MediaDb db, ClaimsPrincipal user) =>
                {
                    var item = await db.Items.SingleOrDefaultAsync(x => x.Id == id);
                    if (item is null)
                        return Results.NotFound();
                    Validate(input, item);
                    var uid = Operations.Uid(user);
                    if (
                        await db.Bookmarks.CountAsync(x => x.UserId == uid && x.MediaItemId == id)
                            >= 15
                        || await db.Bookmarks.CountAsync(x => x.UserId == uid) >= 1500
                    )
                        throw new ArgumentException(
                            "Bookmark limit reached (15 per title, 1500 per account). Delete one before adding another."
                        );
                    var row = new PlaybackBookmark
                    {
                        UserId = uid,
                        MediaItemId = id,
                        Seconds = input.Seconds,
                        Name = input.Name.Trim(),
                        Note = input.Note?.Trim() ?? "",
                        Revision = 1,
                    };
                    db.Bookmarks.Add(row);
                    await db.SaveChangesAsync();
                    return Results.Ok(View(row));
                }
            );
            api.MapPut(
                "/{bookmark}",
                async (
                    int id,
                    string bookmark,
                    BookmarkInput input,
                    MediaDb db,
                    ClaimsPrincipal user
                ) =>
                {
                    var item = await db.Items.SingleOrDefaultAsync(x => x.Id == id);
                    if (item is null)
                        return Results.NotFound();
                    var uid = Operations.Uid(user);
                    var row = await db.Bookmarks.SingleOrDefaultAsync(x =>
                        x.UserId == uid && x.MediaItemId == id && x.Id == bookmark
                    );
                    if (row is null)
                        return Results.NotFound();
                    Validate(input, item);
                    if (row.Revision != input.ExpectedRevision)
                        return Results.Conflict(
                            new
                            {
                                message = "Bookmark changed on another device. Reload before editing.",
                            }
                        );
                    row.Seconds = input.Seconds;
                    row.Name = input.Name.Trim();
                    row.Note = input.Note?.Trim() ?? "";
                    row.Revision++;
                    await db.SaveChangesAsync();
                    return Results.Ok(View(row));
                }
            );
            api.MapDelete(
                "/{bookmark}/{revision:long}",
                async (int id, string bookmark, long revision, MediaDb db, ClaimsPrincipal user) =>
                {
                    if (!await db.Items.AnyAsync(x => x.Id == id))
                        return Results.NotFound();
                    var uid = Operations.Uid(user);
                    var row = await db.Bookmarks.SingleOrDefaultAsync(x =>
                        x.UserId == uid && x.MediaItemId == id && x.Id == bookmark
                    );
                    if (row is null)
                        return Results.NotFound();
                    if (row.Revision != revision)
                        return Results.Conflict();
                    db.Bookmarks.Remove(row);
                    await db.SaveChangesAsync();
                    return Results.NoContent();
                }
            );
            api.MapGet(
                "/export",
                async (int id, MediaDb db, ClaimsPrincipal user) =>
                {
                    var item = await db.Items.SingleOrDefaultAsync(x => x.Id == id);
                    if (item is null)
                        return Results.NotFound();
                    var key = MediaKey(item);
                    var uid = Operations.Uid(user);
                    var rows = await db
                        .Bookmarks.Where(x => x.UserId == uid && x.MediaItemId == id)
                        .OrderBy(x => x.Seconds)
                        .Select(x => new BookmarkTransfer(x.Id, x.Seconds, x.Name, x.Note))
                        .ToArrayAsync();
                    return Results.Ok(new BookmarkArchive(1, key, rows));
                }
            );
            api.MapPost(
                "/import",
                async (int id, BookmarkArchive archive, MediaDb db, ClaimsPrincipal user) =>
                {
                    var item = await db.Items.SingleOrDefaultAsync(x => x.Id == id);
                    if (item is null)
                        return Results.NotFound();
                    if (
                        archive.Version != 1
                        || archive.MediaKey != MediaKey(item)
                        || archive.Bookmarks is null
                        || archive.Bookmarks.Length > 15
                    )
                        throw new ArgumentException(
                            "Unsupported bookmark archive, wrong media content hash, or too many bookmarks."
                        );
                    var uid = Operations.Uid(user);
                    var additions = new List<PlaybackBookmark>();
                    var ids = new HashSet<string>();
                    foreach (var entry in archive.Bookmarks)
                    {
                        if (
                            entry is null
                            || !Guid.TryParseExact(entry.Id, "N", out var parsedId)
                            || !ids.Add(parsedId.ToString("N"))
                        )
                            throw new ArgumentException("Bookmark IDs must be unique UUIDs.");
                        var canonicalId = parsedId.ToString("N");
                        Validate(new(entry.Seconds, entry.Name, entry.Note), item);
                        var existing = await db.Bookmarks.SingleOrDefaultAsync(x =>
                            x.UserId == uid && x.Id == canonicalId
                        );
                        if (existing is not null)
                        {
                            if (
                                existing.MediaItemId != id
                                || existing.Seconds != entry.Seconds
                                || existing.Name != entry.Name.Trim()
                                || existing.Note != (entry.Note?.Trim() ?? "")
                            )
                                return Results.Conflict(
                                    new
                                    {
                                        message = "An imported bookmark conflicts with an existing bookmark. Nothing was imported.",
                                    }
                                );
                            continue;
                        }
                        additions.Add(
                            new()
                            {
                                UserId = uid,
                                Id = canonicalId,
                                MediaItemId = id,
                                Seconds = entry.Seconds,
                                Name = entry.Name.Trim(),
                                Note = entry.Note?.Trim() ?? "",
                                Revision = 1,
                            }
                        );
                    }
                    if (
                        await db.Bookmarks.CountAsync(x => x.UserId == uid && x.MediaItemId == id)
                            + additions.Count
                            > 15
                        || await db.Bookmarks.CountAsync(x => x.UserId == uid) + additions.Count
                            > 1500
                    )
                        throw new ArgumentException(
                            "Import exceeds bookmark limits. Nothing was imported."
                        );
                    db.Bookmarks.AddRange(additions);
                    await db.SaveChangesAsync();
                    return Results.Ok(new { imported = additions.Count });
                }
            );
        }
    }
}
