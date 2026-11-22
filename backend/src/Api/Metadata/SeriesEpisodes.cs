using System.Security.Claims;
using System.Text.Json;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Metadata;

public record SeriesEpisode(
    int Id,
    string Title,
    int Season,
    int Episode,
    double Duration,
    double Seconds,
    bool Completed
);

public record SeriesNavigation(
    string? Series,
    int[] Seasons,
    int? Season,
    SeriesEpisode[] Episodes,
    int? NextOffset,
    SeriesEpisode? NextEpisode,
    SeriesEpisode? NextUnwatched = null,
    SeasonWatchView? SeasonProgress = null
);

public static class SeriesEpisodes
{
    public static void MapSeriesEpisodes(this RouteGroupBuilder group)
    {
        group
            .MapGet(
                "/media/{id:int}/episodes",
                async (
                    int id,
                    int? season,
                    int? offset,
                    MediaDb db,
                    ClaimsPrincipal user,
                    CancellationToken ct
                ) =>
                {
                    if (season is < 0 or > 99 || offset is < 0 or > 100000)
                        return Results.BadRequest();
                    var item = await db
                        .Items.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == id, ct);
                    if (item is null)
                        return Results.NotFound();
                    var metadata = await db
                        .Metadata.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.MediaItemId == id, ct);
                    var value = metadata is null
                        ? null
                        : JsonSerializer.Deserialize<MetadataValue>(metadata.DocumentJson);
                    if (
                        string.IsNullOrWhiteSpace(value?.Series)
                        || value.Season is null
                        || value.Episode is null
                    )
                        return Results.Ok(new SeriesNavigation(null, [], null, [], null, null));

                    // Join through Items so library grants, ratings, missing files and pending imports
                    // are filtered before season discovery, progress or next-episode selection.
                    var entries =
                        from media in db.Items.AsNoTracking()
                        join details in db.Metadata.AsNoTracking()
                            on media.Id equals details.MediaItemId
                        where
                            media.LibraryId == item.LibraryId
                            && MediaDb.MetadataText(details.DocumentJson, "$.Series")
                                == value.Series
                        let s = MediaDb.MetadataNumber(details.DocumentJson, "$.Season")
                        let e = MediaDb.MetadataNumber(details.DocumentJson, "$.Episode")
                        where s >= 0 && s <= 99 && e >= 1 && e <= 999
                        select new
                        {
                            media.Id,
                            media.Title,
                            media.Duration,
                            Season = s!.Value,
                            Episode = e!.Value,
                        };
                    var seasons = await entries
                        .Select(x => x.Season)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToArrayAsync(ct);
                    var selected = season ?? value.Season.Value;
                    var uid = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
                    var episodes =
                        from entry in entries
                        join progress in db.Progress.Where(x => x.UserId == uid)
                            on entry.Id equals progress.MediaItemId
                            into positions
                        from position in positions.DefaultIfEmpty()
                        select new
                        {
                            entry.Id,
                            entry.Title,
                            entry.Season,
                            entry.Episode,
                            entry.Duration,
                            Seconds = position == null ? 0 : position.Seconds,
                            Completed = position != null && position.Completed,
                        };
                    var page = await episodes
                        .Where(x => x.Season == selected)
                        .OrderBy(x => x.Episode)
                        .ThenBy(x => x.Id)
                        .Skip(offset ?? 0)
                        .Take(201)
                        .ToArrayAsync(ct);
                    var next = await episodes
                        .Where(x =>
                            x.Season > value.Season
                            || (x.Season == value.Season && x.Episode > value.Episode)
                        )
                        .OrderBy(x => x.Season)
                        .ThenBy(x => x.Episode)
                        .ThenBy(x => x.Id)
                        .Select(x => new SeriesEpisode(
                            x.Id,
                            x.Title,
                            x.Season,
                            x.Episode,
                            x.Duration,
                            x.Seconds,
                            x.Completed
                        ))
                        .FirstOrDefaultAsync(ct);
                    // Specials are deliberately separate: their numeric zero does not
                    // make them a prerequisite to a regular season.
                    var nextUnwatched = await episodes
                        .Where(x => !x.Completed && (selected == 0 ? x.Season == 0 : x.Season > 0))
                        .OrderBy(x => x.Season)
                        .ThenBy(x => x.Episode)
                        .ThenBy(x => x.Id)
                        .Select(x => new SeriesEpisode(
                            x.Id,
                            x.Title,
                            x.Season,
                            x.Episode,
                            x.Duration,
                            x.Seconds,
                            x.Completed
                        ))
                        .FirstOrDefaultAsync(ct);
                    var seasonPositions = (
                        await SeasonWatchActions.Read(db, id, selected, uid, ct)
                    )!;
                    var summary = SeasonWatchActions.View(selected, seasonPositions) with
                    {
                        Total = await episodes.CountAsync(x => x.Season == selected, ct),
                        Watched = await episodes.CountAsync(
                            x => x.Season == selected && x.Completed,
                            ct
                        ),
                    };
                    return Results.Ok(
                        new SeriesNavigation(
                            value.Series,
                            seasons,
                            selected,
                            page.Take(200)
                                .Select(x => new SeriesEpisode(
                                    x.Id,
                                    x.Title,
                                    x.Season,
                                    x.Episode,
                                    x.Duration,
                                    x.Seconds,
                                    x.Completed
                                ))
                                .ToArray(),
                            page.Length > 200 ? (offset ?? 0) + 200 : null,
                            next,
                            nextUnwatched,
                            summary
                        )
                    );
                }
            )
            .Produces<SeriesNavigation>();
    }
}
