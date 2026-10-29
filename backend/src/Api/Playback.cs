using System.Security.Claims;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using MediaServer.Services;
using Microsoft.EntityFrameworkCore;

public record PlaybackOptions(
    VideoColor? Color,
    IReadOnlyList<MediaTrack> Tracks,
    bool ExternalWebVtt,
    Rendition[] Qualities,
    string[] Encoders,
    string ConversionNotice
);

public static class PlaybackApi
{
    public static void MapPlayback(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").WithGroupName("client-v1").RequireAuthorization();
        api.MapGet(
                "/transcoding/encoders",
                async (EncoderDiscovery discovery, CancellationToken ct) =>
                    await discovery.GetAsync(ct)
            )
            .RequireAuthorization("admin");
        api.MapGet(
                "/media/{id:int}/playback-options",
                async (int id, MediaDb db, PathPolicy policy, TranscodeSettings settings) =>
                {
                    var item = await db.Items.FindAsync(id);
                    if (item is null)
                        return Results.NotFound();
                    var library = await db.Libraries.FindAsync(item.LibraryId);
                    IReadOnlyList<ExternalCaption> captions = [];
                    try
                    {
                        if (library is not null)
                            captions = ExternalCaptions.Discover(policy, library, item);
                    }
                    catch (Exception ex)
                        when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                    { }
                    return Results.Ok(
                        new PlaybackOptions(
                            TranscodeProfile.Color(item),
                            [
                                .. JsonSerializer.Deserialize<MediaTrack[]>(item.TracksJson) ?? [],
                                .. captions.Select(x => x.Track),
                            ],
                            captions.Any(x => x.Index == -1),
                            TranscodeProfile.Ladder,
                            settings.AllowedEncoders,
                            "Alpha HLS prepares the whole video before playback. Up to 8 GiB source, six hours, two jobs and 30 minutes conversion. HDR is explicitly tone-mapped to SDR. Direct Play remains available."
                        )
                    );
                }
            )
            .Produces<PlaybackOptions>();
        api.MapPost(
                "/media/{id:int}/playback-sessions",
                async (
                    int id,
                    PlaybackSelection selection,
                    MediaDb db,
                    PathPolicy policy,
                    PlaybackSessions sessions,
                    ClaimsPrincipal user,
                    CancellationToken ct
                ) =>
                {
                    var item = await db.Items.FindAsync(id);
                    if (item is null)
                        return Results.NotFound();
                    var library = await db.Libraries.FindAsync(item.LibraryId);
                    if (library is null)
                        return Results.NotFound();
                    var state = await sessions.StartAsync(
                        item,
                        library,
                        policy,
                        Operations.Uid(user),
                        user.FindFirstValue("sid")!,
                        selection,
                        ct
                    );
                    return state is null
                        ? Results.Conflict(
                            new
                            {
                                message = "Two conversion jobs are already active. Close an HLS session or wait for cache expiry.",
                            }
                        )
                        : Results.Accepted($"/api/v1/playback-sessions/{state.Id}", state);
                }
            )
            .Produces<ConversionState>(202)
            .RequireRateLimiting("auth");
        api.MapGet(
                "/playback-sessions/{session}",
                async (
                    string session,
                    MediaDb db,
                    PlaybackSessions sessions,
                    ClaimsPrincipal user
                ) =>
                {
                    var uid = Operations.Uid(user);
                    var sid = user.FindFirstValue("sid")!;
                    var media = sessions.MediaId(session, uid, sid);
                    if (media is null || !await db.Items.AnyAsync(x => x.Id == media))
                        return Results.NotFound();
                    var state = sessions.Get(session, media.Value, uid, sid);
                    return state is null ? Results.NotFound() : Results.Ok(state);
                }
            )
            .Produces<ConversionState>();
        api.MapDelete(
            "/playback-sessions/{session}",
            async (string session, PlaybackSessions sessions, ClaimsPrincipal user) =>
                await sessions.RemoveAsync(
                    session,
                    Operations.Uid(user),
                    user.FindFirstValue("sid")!
                )
                    ? Results.NoContent()
                    : Results.NotFound()
        );
        api.MapMethods(
            "/playback-sessions/{session}/files/{file}",
            ["GET", "HEAD"],
            async (
                string session,
                string file,
                MediaDb db,
                PlaybackSessions sessions,
                ClaimsPrincipal user,
                HttpContext context
            ) =>
            {
                var uid = Operations.Uid(user);
                var sid = user.FindFirstValue("sid")!;
                var media = sessions.MediaId(session, uid, sid);
                // Recheck grants, rating, missing status and session authentication on EVERY request.
                if (media is null || !await db.Items.AnyAsync(x => x.Id == media))
                    return Results.NotFound();
                var path = sessions.FilePath(session, media.Value, uid, sid, file);
                if (path is null)
                    return Results.NotFound();
                context.Response.Headers.CacheControl = "private, no-store";
                var mime =
                    file.EndsWith(".m3u8", StringComparison.Ordinal)
                        ? "application/vnd.apple.mpegurl"
                    : file.EndsWith(".vtt", StringComparison.Ordinal) ? "text/vtt; charset=utf-8"
                    : "video/mp2t";
                // Open now rather than let FileResult reopen after cleanup.
                try
                {
                    return Results.File(
                        new FileStream(
                            path,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read | FileShare.Delete
                        ),
                        mime,
                        enableRangeProcessing: true
                    );
                }
                catch (IOException)
                {
                    return Results.NotFound();
                }
            }
        );
    }
}
