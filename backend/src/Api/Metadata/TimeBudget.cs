using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Metadata;

public record TimeBudgetMatch(
    int Id,
    string Title,
    string? Series,
    int? Season,
    int? Episode,
    double RemainingSeconds,
    bool Started
);

public record TimeBudgetPage(TimeBudgetMatch[] Items, bool HasMore);

public static class TimeBudget
{
    private const int PageSize = 30;

    public static void MapTimeBudget(this RouteGroupBuilder group)
    {
        group
            .MapGet(
                "/time-budget",
                async (
                    int minutes,
                    double? speed,
                    bool? includeStarted,
                    MediaDb db,
                    System.Security.Claims.ClaimsPrincipal user,
                    CancellationToken ct
                ) =>
                {
                    if (
                        minutes is < 1 or > 720
                        || speed is not (null or 0.5 or 0.75 or 1 or 1.25 or 1.5 or 1.75 or 2)
                    )
                        return Results.BadRequest();
                    var uid = Operations.Uid(user);
                    var rate = speed ?? 1;
                    var source =
                        from entry in LibraryCatalog.Identify(db)
                        join progress in db.Progress.AsNoTracking().Where(x => x.UserId == uid)
                            on entry.Media.Id equals progress.MediaItemId
                            into positions
                        from position in positions.DefaultIfEmpty()
                        where position == null || !position.Completed
                        select new
                        {
                            entry.Media.Id,
                            entry.Media.LibraryId,
                            entry.Media.Title,
                            entry.Media.Duration,
                            entry.Series,
                            entry.Season,
                            entry.Episode,
                            Seconds = position == null ? 0 : position.Seconds,
                        };

                    // Pick series order before applying the budget. A later short episode
                    // must not replace the next episode when the latter won't fit.
                    var nextEpisodes = source
                        .Where(x => x.Series != null && x.Season > 0)
                        .GroupBy(x => new { x.LibraryId, x.Series })
                        .Select(series =>
                            series
                                .OrderBy(x => x.Season)
                                .ThenBy(x => x.Episode)
                                .ThenBy(x => x.Id)
                                .Select(x => x.Id)
                                .First()
                        );
                    var rowFilter =
                        from entry in source
                        where
                            (entry.Series == null || nextEpisodes.Contains(entry.Id))
                            && entry.Duration <= double.MaxValue
                            && entry.Seconds >= 0
                            && entry.Seconds < entry.Duration
                            && (includeStarted != false || entry.Seconds == 0)
                        let remaining = (entry.Duration - entry.Seconds) / rate
                        where remaining <= minutes * 60.0
                        orderby remaining descending, entry.Id
                        select new TimeBudgetMatch(
                            entry.Id,
                            entry.Title,
                            entry.Series,
                            entry.Series == null ? null : entry.Season,
                            entry.Series == null ? null : entry.Episode,
                            remaining,
                            entry.Seconds > 0
                        );
                    var rows = await rowFilter.Take(PageSize + 1).ToArrayAsync(ct);
                    return Results.Ok(
                        new TimeBudgetPage(rows.Take(PageSize).ToArray(), rows.Length > PageSize)
                    );
                }
            )
            .Produces<TimeBudgetPage>();
    }
}
