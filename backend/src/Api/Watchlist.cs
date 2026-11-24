using System.Globalization;
using System.Security.Claims;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

public record WatchlistStatus(bool Saved);

public record WatchlistPage(MediaSummary[] Items, string? NextCursor);

public record WatchlistRecommendations(string? BasedOn, MediaSummary[] Items);

public static partial class Operations
{
    private const int WatchlistPageSize = 60;

    private static void MapWatchlist(RouteGroupBuilder group)
    {
        var api = group
            .MapGroup("/watchlist")
            .RequireAuthorization(p => p.RequireRole("Admin", "Viewer"));
        api.MapGet(
                "",
                async (string? cursor, MediaDb db, ClaimsPrincipal user, CancellationToken ct) =>
                {
                    var query = VisibleWatchlist(db, Uid(user));
                    if (cursor is not null)
                    {
                        var parts = cursor.Split('.', 2);
                        if (
                            cursor.Length > 31
                            || parts.Length != 2
                            || !long.TryParse(
                                parts[0],
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out var ticks
                            )
                            || ticks > DateTime.MaxValue.Ticks
                            || !int.TryParse(
                                parts[1],
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out var id
                            )
                            || id < 1
                        )
                            return Results.BadRequest();
                        var addedAt = new DateTime(ticks, DateTimeKind.Utc);
                        query = query.Where(x =>
                            x.AddedAt < addedAt || (x.AddedAt == addedAt && x.Item.Id < id)
                        );
                    }
                    var items = await NewestFirst(query)
                        .Take(WatchlistPageSize + 1)
                        .ToArrayAsync(ct);
                    var page = items.Take(WatchlistPageSize).ToArray();
                    var next =
                        items.Length > WatchlistPageSize
                            ? FormattableString.Invariant(
                                $"{page[^1].AddedAt.Ticks}.{page[^1].Item.Id}"
                            )
                            : null;
                    return Results.Ok(
                        new WatchlistPage(page.Select(x => Summary(x.Item)).ToArray(), next)
                    );
                }
            )
            .Produces<WatchlistPage>();
        api.MapGet(
                "/{mediaId:int}",
                async (int mediaId, MediaDb db, ClaimsPrincipal user, CancellationToken ct) =>
                {
                    if (!await db.Items.AnyAsync(x => x.Id == mediaId, ct))
                        return Results.NotFound();
                    var saved = await Entries(db, Uid(user))
                        .AnyAsync(x => x.MediaItemId == mediaId, ct);
                    return Results.Ok(new WatchlistStatus(saved));
                }
            )
            .Produces<WatchlistStatus>();
        api.MapPut(
                "/{mediaId:int}",
                async (int mediaId, MediaDb db, ClaimsPrincipal user, CancellationToken ct) =>
                {
                    var uid = Uid(user);
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    if (!await db.Items.AnyAsync(x => x.Id == mediaId, ct))
                        return Results.NotFound();
                    if (await Entries(db, uid).AnyAsync(x => x.MediaItemId == mediaId, ct))
                        return Results.NoContent();
                    db.Watchlist.Add(
                        new WatchlistEntry
                        {
                            UserId = uid,
                            MediaItemId = mediaId,
                            AddedAt = DateTime.UtcNow,
                        }
                    );
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return Results.NoContent();
                }
            )
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        api.MapDelete(
                "/{mediaId:int}",
                async (int mediaId, MediaDb db, ClaimsPrincipal user, CancellationToken ct) =>
                {
                    await Entries(db, Uid(user))
                        .Where(x => x.MediaItemId == mediaId)
                        .ExecuteDeleteAsync(ct);
                    return Results.NoContent();
                }
            )
            .Produces(StatusCodes.Status204NoContent);
        api.MapGet(
            "/recommendations",
            async (MediaDb db, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var uid = Uid(user);
                var seed = await NewestFirst(VisibleWatchlist(db, uid))
                    .Select(x => new { x.Item.Title, x.Item.LibraryId })
                    .FirstOrDefaultAsync(ct);
                if (seed is null)
                    return new WatchlistRecommendations(null, []);
                var saved = Entries(db, uid);
                var items = await db
                    .Items.AsNoTracking()
                    .Where(item => item.LibraryId == seed.LibraryId)
                    .Where(item => !saved.Any(entry => entry.MediaItemId == item.Id))
                    .Where(item =>
                        !db.History.Any(watched =>
                            watched.UserId == uid && watched.MediaItemId == item.Id
                        )
                    )
                    .Where(item =>
                        !db.Progress.Any(progress =>
                            progress.UserId == uid
                            && progress.MediaItemId == item.Id
                            && (progress.Seconds > 0 || progress.Completed)
                        )
                    )
                    .OrderBy(item => item.SearchTitle)
                    .ThenBy(item => item.Id)
                    .Take(8)
                    .ToArrayAsync(ct);
                return new WatchlistRecommendations(
                    items.Length > 0 ? seed.Title : null,
                    items.Select(Summary).ToArray()
                );
            }
        );
    }

    private static IQueryable<WatchlistEntry> Entries(MediaDb db, int uid) =>
        db.Watchlist.Where(x => x.UserId == uid);

    private static IQueryable<SavedMedia> VisibleWatchlist(MediaDb db, int uid) =>
        from entry in Entries(db, uid)
        join item in db.Items.AsNoTracking() on entry.MediaItemId equals item.Id
        select new SavedMedia { Item = item, AddedAt = entry.AddedAt };

    private static IOrderedQueryable<SavedMedia> NewestFirst(IQueryable<SavedMedia> items) =>
        items.OrderByDescending(x => x.AddedAt).ThenByDescending(x => x.Item.Id);

    private sealed class SavedMedia
    {
        public required MediaItem Item { get; init; }
        public DateTime AddedAt { get; init; }
    }
}
