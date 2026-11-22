using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Metadata;

public record SeasonWatchView(int Season, int Total, int Watched, string? Revision);

public record SeasonWatchChange(
    int Season,
    string Action,
    string? ExpectedRevision = null,
    string? UndoToken = null
);

public record SeasonWatchResult(SeasonWatchView Progress, string? UndoToken);

public record SeasonPosition(
    int Id,
    double Duration,
    long MetadataRevision,
    long Revision,
    WatchSnapshot State
);

public static class SeasonWatchActions
{
    public const int MaximumEpisodes = 2000;

    public static async Task<SeasonPosition[]?> Read(
        MediaDb db,
        int id,
        int season,
        int uid,
        CancellationToken ct
    )
    {
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (item is null)
            return null;
        var details = await db
            .Metadata.AsNoTracking()
            .SingleOrDefaultAsync(x => x.MediaItemId == id, ct);
        var value = details is null
            ? null
            : JsonSerializer.Deserialize<MetadataValue>(details.DocumentJson);
        if (
            string.IsNullOrWhiteSpace(value?.Series)
            || value.Season is null
            || value.Episode is null
        )
            return null;
        var query =
            from media in db.Items.AsNoTracking()
            join metadata in db.Metadata.AsNoTracking() on media.Id equals metadata.MediaItemId
            join progress in db.Progress.Where(x => x.UserId == uid)
                on media.Id equals progress.MediaItemId
                into positions
            from progress in positions.DefaultIfEmpty()
            where
                media.LibraryId == item.LibraryId
                && MediaDb.MetadataText(metadata.DocumentJson, "$.Series") == value.Series
                && MediaDb.MetadataNumber(metadata.DocumentJson, "$.Season") == season
                && MediaDb.MetadataNumber(metadata.DocumentJson, "$.Episode") >= 1
                && MediaDb.MetadataNumber(metadata.DocumentJson, "$.Episode") <= 999
            orderby media.Id
            select new SeasonPosition(
                media.Id,
                media.Duration,
                metadata.Revision,
                progress == null ? 0 : progress.Revision,
                new WatchSnapshot(
                    progress == null ? 0 : progress.Seconds,
                    progress != null && progress.Completed,
                    progress != null && progress.HiddenFromContinueWatching
                )
            );
        return await query.Take(MaximumEpisodes + 1).ToArrayAsync(ct);
    }

    public static string Revision(SeasonPosition[] entries) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entries)))
        );

    public static SeasonWatchView View(int season, SeasonPosition[] entries) =>
        new(
            season,
            entries.Length,
            entries.Count(x => x.State.Completed),
            entries.Length > MaximumEpisodes ? null : Revision(entries)
        );

    public static void MapSeasonWatchActions(this RouteGroupBuilder group)
    {
        group
            .MapPost(
                "/media/{id:int}/season-watch-state",
                async (
                    int id,
                    SeasonWatchChange input,
                    MediaDb db,
                    ClaimsPrincipal user,
                    CancellationToken ct
                ) =>
                {
                    if (user.IsInRole("Guest"))
                        return Results.Forbid();
                    if (
                        input.Season is < 0 or > 99
                        || input.Action is not ("watched" or "unwatched" or "undo")
                    )
                        return Results.BadRequest();
                    var uid = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    var entries = await Read(db, id, input.Season, uid, ct);
                    if (entries is null)
                        return Results.NotFound();
                    if (entries.Length == 0 || entries.Length > MaximumEpisodes)
                        return Results.BadRequest();
                    SeasonPosition[]? restore = null;
                    if (input.Action == "undo")
                    {
                        var undo = await db.SeasonWatchUndos.SingleOrDefaultAsync(
                            x => x.UserId == uid,
                            ct
                        );
                        if (
                            undo is null
                            || undo.Token != input.UndoToken
                            || undo.MediaItemId != id
                            || undo.Season != input.Season
                            || undo.ExpiresAt <= DateTime.UtcNow
                            || undo.Revision != Revision(entries)
                        )
                            return Results.Conflict();
                        restore = JsonSerializer.Deserialize<SeasonPosition[]>(undo.SnapshotJson)!;
                        if (!restore.Select(x => x.Id).SequenceEqual(entries.Select(x => x.Id)))
                            return Results.Conflict();
                    }
                    else if (input.ExpectedRevision != Revision(entries))
                        return Results.Conflict();
                    var at = DateTime.UtcNow;
                    for (var index = 0; index < entries.Length; index++)
                    {
                        var entry = entries[index];
                        var next = restore is not null
                            ? restore[index].State
                            : new WatchSnapshot(
                                input.Action == "watched" ? entry.Duration : 0,
                                input.Action == "watched",
                                false
                            );
                        await db.Database.ExecuteSqlInterpolatedAsync(
                            $"INSERT INTO Progress (UserId,MediaItemId,Seconds,UpdatedAt,Revision,Completed,HiddenFromContinueWatching) VALUES ({uid},{entry.Id},{next.Seconds},{at},1,{next.Completed},{next.Hidden}) ON CONFLICT(UserId,MediaItemId) DO UPDATE SET Seconds=excluded.Seconds,UpdatedAt=excluded.UpdatedAt,Completed=excluded.Completed,HiddenFromContinueWatching=excluded.HiddenFromContinueWatching,Revision=Progress.Revision+1",
                            ct
                        );
                        await HistoryWriter.RecordAsync(
                            db,
                            uid,
                            entry.Id,
                            next.Seconds,
                            next.Completed,
                            at
                        );
                        if (!next.Completed)
                            await db
                                .History.Where(x => x.UserId == uid && x.MediaItemId == entry.Id)
                                .ExecuteUpdateAsync(
                                    s => s.SetProperty(x => x.CompletedAt, (DateTime?)null),
                                    ct
                                );
                    }
                    var updated = (await Read(db, id, input.Season, uid, ct))!;
                    await db
                        .SeasonWatchUndos.Where(x => x.UserId == uid || x.ExpiresAt <= at)
                        .ExecuteDeleteAsync(ct);
                    var token = restore is null
                        ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
                        : null;
                    if (token is not null)
                    {
                        db.SeasonWatchUndos.Add(
                            new SeasonWatchUndo
                            {
                                UserId = uid,
                                MediaItemId = id,
                                Season = input.Season,
                                Token = token,
                                Revision = Revision(updated),
                                SnapshotJson = JsonSerializer.Serialize(entries),
                                ExpiresAt = at.AddMinutes(10),
                            }
                        );
                        await db.SaveChangesAsync(ct);
                    }
                    await tx.CommitAsync(ct);
                    return Results.Ok(new SeasonWatchResult(View(input.Season, updated), token));
                }
            )
            .Produces<SeasonWatchResult>()
            .RequireRateLimiting("season-watch");
    }
}
