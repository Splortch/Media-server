using System.Security.Claims;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

public record WatchSnapshot(double Seconds, bool Completed, bool Hidden);

public record WatchChange(string Action, long ExpectedRevision, WatchSnapshot? Restore = null);

public record WatchState(ProgressState Progress, bool Hidden, WatchSnapshot Previous);

public static class WatchActions
{
    public static void MapWatchActions(this WebApplication app)
    {
        foreach (var prefix in new[] { "/api", "/api/v1" })
        {
            var group = app.MapGroup(prefix).RequireAuthorization();
            if (prefix.EndsWith("v1"))
                group.WithGroupName("client-v1");
            group
                .MapGet(
                    "/media/{id:int}/watch-state",
                    async (int id, MediaDb db, ClaimsPrincipal user, CancellationToken ct) =>
                    {
                        if (user.IsInRole("Guest"))
                            return Results.Forbid();
                        if (!await db.Items.AnyAsync(x => x.Id == id, ct))
                            return Results.NotFound();
                        var uid = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
                        var p = await db
                            .Progress.AsNoTracking()
                            .SingleOrDefaultAsync(x => x.MediaItemId == id && x.UserId == uid, ct);
                        var snapshot = new WatchSnapshot(
                            p?.Seconds ?? 0,
                            p?.Completed ?? false,
                            p?.HiddenFromContinueWatching ?? false
                        );
                        return Results.Ok(
                            new WatchState(
                                new ProgressState(
                                    id,
                                    snapshot.Seconds,
                                    snapshot.Completed,
                                    p?.Revision ?? 0,
                                    p?.UpdatedAt
                                ),
                                snapshot.Hidden,
                                snapshot
                            )
                        );
                    }
                )
                .Produces<WatchState>();
            group
                .MapPost(
                    "/media/{id:int}/watch-state",
                    async (
                        int id,
                        WatchChange input,
                        MediaDb db,
                        ClaimsPrincipal user,
                        CancellationToken ct
                    ) =>
                    {
                        if (user.IsInRole("Guest"))
                            return Results.Forbid();
                        if (
                            input.Action is not ("watched" or "unwatched" or "hide" or "restore")
                            || input.ExpectedRevision < 0
                        )
                            return Results.BadRequest();
                        var item = await db.Items.SingleOrDefaultAsync(x => x.Id == id, ct);
                        if (item is null)
                            return Results.NotFound();
                        var uid = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
                        await using var tx = await db.Database.BeginTransactionAsync(ct);
                        var old = await db
                            .Progress.AsNoTracking()
                            .SingleOrDefaultAsync(x => x.MediaItemId == id && x.UserId == uid, ct);
                        if ((old?.Revision ?? 0) != input.ExpectedRevision)
                            return Results.Conflict();
                        var previous = new WatchSnapshot(
                            old?.Seconds ?? 0,
                            old?.Completed ?? false,
                            old?.HiddenFromContinueWatching ?? false
                        );
                        var next = input.Action switch
                        {
                            "watched" => new WatchSnapshot(item.Duration, true, false),
                            "unwatched" => new WatchSnapshot(0, false, false),
                            "hide" => previous with { Hidden = true },
                            _ => input.Restore,
                        };
                        if (
                            next is null
                            || !double.IsFinite(next.Seconds)
                            || next.Seconds < 0
                            || (item.Duration > 0 && next.Seconds > item.Duration + 2)
                        )
                            return Results.BadRequest();
                        var at = DateTime.UtcNow;
                        var changed = await db.Database.ExecuteSqlInterpolatedAsync(
                            $"INSERT INTO Progress (UserId,MediaItemId,Seconds,UpdatedAt,Revision,Completed,HiddenFromContinueWatching) VALUES ({uid},{id},{next.Seconds},{at},1,{next.Completed},{next.Hidden}) ON CONFLICT(UserId,MediaItemId) DO UPDATE SET Seconds=excluded.Seconds,UpdatedAt=excluded.UpdatedAt,Completed=excluded.Completed,HiddenFromContinueWatching=excluded.HiddenFromContinueWatching,Revision=Progress.Revision+1 WHERE Progress.Revision={input.ExpectedRevision}",
                            ct
                        );
                        if (changed == 0)
                            return Results.Conflict();
                        if (input.Action != "hide")
                        {
                            await HistoryWriter.RecordAsync(
                                db,
                                uid,
                                id,
                                next.Seconds,
                                next.Completed,
                                at
                            );
                            if (!next.Completed)
                                await db
                                    .History.Where(x => x.UserId == uid && x.MediaItemId == id)
                                    .ExecuteUpdateAsync(
                                        s => s.SetProperty(x => x.CompletedAt, (DateTime?)null),
                                        ct
                                    );
                        }
                        var revision = (old?.Revision ?? 0) + 1;
                        await tx.CommitAsync(ct);
                        return Results.Ok(
                            new WatchState(
                                new ProgressState(id, next.Seconds, next.Completed, revision, at),
                                next.Hidden,
                                previous
                            )
                        );
                    }
                )
                .Produces<WatchState>();
        }
    }
}
