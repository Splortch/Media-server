using System.Security.Claims;
using System.Text.Json;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Metadata;

public record EpisodePreferences(bool RevealSpoilers);

public static class EpisodePrivacy
{
    // Conservative default: an identified episode stays spoiler-safe until this
    // account starts/watches it or explicitly opts in. Specials follow the same rule.
    public static async Task<bool> Hidden(
        MediaDb db,
        int id,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var uid = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (await db.Users.AnyAsync(x => x.Id == uid && x.RevealEpisodeSpoilers, ct))
            return false;
        var row = await db
            .Metadata.AsNoTracking()
            .SingleOrDefaultAsync(x => x.MediaItemId == id, ct);
        var value = row is null
            ? null
            : JsonSerializer.Deserialize<MetadataValue>(row.DocumentJson);
        if (
            string.IsNullOrWhiteSpace(value?.Series)
            || value.Season is null
            || value.Episode is null
        )
            return false;
        return !await db.Progress.AnyAsync(
            x => x.UserId == uid && x.MediaItemId == id && (x.Completed || x.Seconds > 0),
            ct
        );
    }

    public static void MapEpisodePreferences(this RouteGroupBuilder group)
    {
        group
            .MapGet(
                "/preferences/episodes",
                async (MediaDb db, ClaimsPrincipal user, CancellationToken ct) =>
                {
                    if (user.IsInRole("Guest"))
                        return Results.Forbid();
                    var uid = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
                    return Results.Ok(
                        new EpisodePreferences(
                            await db
                                .Users.Where(x => x.Id == uid)
                                .Select(x => x.RevealEpisodeSpoilers)
                                .SingleAsync(ct)
                        )
                    );
                }
            )
            .Produces<EpisodePreferences>();
        group
            .MapPut(
                "/preferences/episodes",
                async (
                    EpisodePreferences input,
                    MediaDb db,
                    ClaimsPrincipal user,
                    CancellationToken ct
                ) =>
                {
                    if (user.IsInRole("Guest"))
                        return Results.Forbid();
                    var uid = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
                    await db
                        .Users.Where(x => x.Id == uid)
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(x => x.RevealEpisodeSpoilers, input.RevealSpoilers),
                            ct
                        );
                    return Results.Ok(input);
                }
            )
            .Produces<EpisodePreferences>();
    }
}
