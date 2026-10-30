using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

public record ServerInfo(
    string Name,
    string ServerVersion,
    int ApiVersion,
    int MinimumClientApiVersion,
    bool SetupRequired,
    string[] Features
);

public record ProgressState(
    int MediaItemId,
    double Seconds,
    bool Completed,
    long Revision,
    DateTime? UpdatedAt
);

public record ProgressUpdate(double Seconds, long ExpectedRevision, bool Completed = false);

public record ClientCapabilities(
    string Platform,
    string Player,
    string[] Containers,
    string[] Codecs
);

public record PlaybackError(string Code, string Client);

public record SessionInfo(
    string Id,
    string DeviceName,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    bool Current
);

public record TrackCatalog(
    IReadOnlyList<MediaTrack> Tracks,
    bool CanSelectAudio,
    bool CanSelectSubtitles
);

public static class ClientV1
{
    public static string ServerVersion =>
        typeof(ClientV1)
            .Assembly.GetCustomAttributes(
                typeof(System.Reflection.AssemblyInformationalVersionAttribute),
                false
            )
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()
            ?.InformationalVersion.Split('+')[0]
        ?? "0.2.0-alpha.1";

    public static void MapClientV1(this WebApplication app)
    {
        var root = app.MapGroup("/api/v1").WithGroupName("client-v1");
        root.MapGet(
                "/server-info",
                async (MediaDb db) =>
                    new ServerInfo(
                        "Media Server",
                        ServerVersion,
                        1,
                        1,
                        !await db.Users.AnyAsync(),
                        [
                            "direct-play",
                            "hls-conversion",
                            "track-selection",
                            "library-removal",
                            "progress-revisions",
                            "track-metadata",
                            "sessions",
                            "accounts",
                            "prefix-search",
                            "smart-collections",
                            "optional-features",
                            "library-reconciliation",
                            "monitoring",
                            "maintenance-v1",
                        ]
                    )
            )
            .WithName("ServerInfo");
        var api = root.MapGroup("").RequireAuthorization();
        api.MapGet(
                "/media/{id:int}/progress",
                async Task<Results<Ok<ProgressState>, NotFound>> (
                    int id,
                    ClaimsPrincipal user,
                    MediaDb db
                ) =>
                {
                    if (!await db.Items.AnyAsync(x => x.Id == id))
                        return TypedResults.NotFound();
                    return TypedResults.Ok(State(id, await db.Progress.FindAsync(Uid(user), id)));
                }
            )
            .WithName("GetProgress");
        api.MapPut(
                "/media/{id:int}/progress-sync",
                async Task<Results<Ok<ProgressState>, Conflict<ProgressState>, NotFound>> (
                    int id,
                    ProgressUpdate input,
                    MediaDb db,
                    ClaimsPrincipal user
                ) =>
                {
                    var item = await db.Items.FindAsync(id);
                    if (item is null)
                        return TypedResults.NotFound();
                    if (
                        !double.IsFinite(input.Seconds)
                        || input.Seconds < 0
                        || input.ExpectedRevision < 0
                        || (item.Duration > 0 && input.Seconds > item.Duration + 2)
                    )
                        throw new ArgumentException("Invalid playback position or revision.");
                    var uid = Uid(user);
                    var updated = DateTime.UtcNow;
                    var completed =
                        input.Completed
                        || (item.Duration > 0 && input.Seconds >= item.Duration * .95);
                    await using var transaction = await db.Database.BeginTransactionAsync();
                    var count = await db.Database.ExecuteSqlInterpolatedAsync(
                        $"INSERT INTO Progress (UserId, MediaItemId, Seconds, UpdatedAt, Revision, Completed) SELECT {uid}, {id}, {input.Seconds}, {updated}, 1, {completed} WHERE {input.ExpectedRevision} = 0 OR EXISTS (SELECT 1 FROM Progress WHERE UserId={uid} AND MediaItemId={id} AND Revision={input.ExpectedRevision}) ON CONFLICT(UserId, MediaItemId) DO UPDATE SET Seconds=excluded.Seconds, UpdatedAt=excluded.UpdatedAt, Completed=excluded.Completed, Revision=Progress.Revision+1 WHERE Progress.Revision={input.ExpectedRevision}"
                    );
                    if (count > 0)
                        await HistoryWriter.RecordAsync(
                            db,
                            uid,
                            id,
                            input.Seconds,
                            completed,
                            updated
                        );
                    var current = State(
                        id,
                        await db
                            .Progress.AsNoTracking()
                            .SingleOrDefaultAsync(x => x.UserId == uid && x.MediaItemId == id)
                    );
                    await transaction.CommitAsync();
                    // Lost-response retries are idempotent; stale, different positions are never silently replayed.
                    return
                        count > 0
                        || (
                            current.Revision > 0
                            && input.ExpectedRevision < current.Revision
                            && current.Seconds == input.Seconds
                            && current.Completed == completed
                        )
                        ? TypedResults.Ok(current)
                        : TypedResults.Conflict(current);
                }
            )
            .WithName("SyncProgress");
        api.MapGet(
                "/continue-watching",
                async (MediaDb db, ClaimsPrincipal user) =>
                {
                    var uid = Uid(user);
                    return await db
                        .Progress.Where(p =>
                            p.UserId == uid
                            && !p.Completed
                            && p.Seconds > 0
                            && db.Items.Any(item => item.Id == p.MediaItemId)
                        )
                        .OrderByDescending(p => p.UpdatedAt)
                        .Take(100)
                        .Select(p => new ProgressState(
                            p.MediaItemId,
                            p.Seconds,
                            p.Completed,
                            p.Revision,
                            p.UpdatedAt
                        ))
                        .ToListAsync();
                }
            )
            .WithName("ContinueWatching");
        api.MapGet(
                "/media/{id:int}/tracks",
                async Task<Results<Ok<TrackCatalog>, NotFound>> (
                    int id,
                    MediaDb db,
                    PathPolicy policy
                ) =>
                {
                    var item = await db.Items.FindAsync(id);
                    if (item is null)
                        return TypedResults.NotFound();
                    var tracks = JsonSerializer.Deserialize<MediaTrack[]>(item.TracksJson) ?? [];
                    var library = await db.Libraries.FindAsync(item.LibraryId);
                    try
                    {
                        if (library is not null)
                            tracks =
                            [
                                .. tracks,
                                .. ExternalCaptions
                                    .Discover(policy, library, item)
                                    .Select(x => x.Track),
                            ];
                    }
                    catch (Exception e)
                        when (e is IOException or ArgumentException or UnauthorizedAccessException)
                    { }
                    return TypedResults.Ok(
                        new TrackCatalog(
                            tracks,
                            tracks.Any(x => x.Type == "audio"),
                            tracks.Any(x =>
                                x.Type == "subtitle"
                                && MediaServer.Services.PlaybackSessions.TextSubtitle(x.Codec)
                            )
                        )
                    );
                }
            )
            .WithName("MediaTracks");
        api.MapGet(
                "/sessions",
                async (MediaDb db, ClaimsPrincipal user) =>
                {
                    var uid = Uid(user);
                    var sid = user.FindFirstValue("sid");
                    var sessions = await db
                        .Sessions.Where(x => x.UserId == uid && x.ExpiresAt > DateTime.UtcNow)
                        .OrderByDescending(x => x.CreatedAt)
                        .ToListAsync();
                    return sessions
                        .Select(s => new SessionInfo(
                            Handle(s.Id),
                            s.DeviceName,
                            s.CreatedAt,
                            s.ExpiresAt,
                            s.Id == sid
                        ))
                        .ToArray();
                }
            )
            .WithName("Sessions");
        api.MapDelete(
                "/sessions/{handle}",
                async (string handle, MediaDb db, ClaimsPrincipal user) =>
                {
                    var uid = Uid(user);
                    var sessions = await db.Sessions.Where(x => x.UserId == uid).ToListAsync();
                    var session = sessions.Find(x => Handle(x.Id) == handle);
                    if (session is null)
                        return Results.NotFound();
                    db.Sessions.Remove(session);
                    await db.SaveChangesAsync();
                    return Results.NoContent();
                }
            )
            .WithName("RevokeSession");
        api.MapPut(
                "/capabilities",
                async (ClientCapabilities input, MediaDb db, ClaimsPrincipal user) =>
                {
                    if (
                        string.IsNullOrWhiteSpace(input.Platform)
                        || input.Platform.Length > 32
                        || string.IsNullOrWhiteSpace(input.Player)
                        || input.Player.Length > 64
                        || input.Containers is null
                        || input.Codecs is null
                        || input.Containers.Length > 16
                        || input.Codecs.Length > 32
                        || input
                            .Containers.Concat(input.Codecs)
                            .Any(x => x is null || x.Length > 64)
                    )
                        throw new ArgumentException("Invalid capability report.");
                    var session = await db.Sessions.FindAsync(user.FindFirstValue("sid"));
                    if (session is null)
                        return Results.Unauthorized();
                    session.CapabilitiesJson = JsonSerializer.Serialize(input);
                    await db.SaveChangesAsync();
                    return Results.NoContent();
                }
            )
            .WithName("ReportCapabilities");
        api.MapPost(
                "/media/{id:int}/playback-errors",
                async (int id, PlaybackError input, MediaDb db, ClaimsPrincipal user) =>
                {
                    if (
                        !new[] { "network", "decode", "unsupported", "unknown" }.Contains(
                            input.Code
                        )
                        || string.IsNullOrWhiteSpace(input.Client)
                        || input.Client.Length > 64
                    )
                        throw new ArgumentException("Invalid error category.");
                    if (!await db.Items.AnyAsync(x => x.Id == id))
                        return Results.NotFound();
                    var uid = Uid(user);
                    db.Reports.Add(
                        new PlaybackReport
                        {
                            UserId = uid,
                            MediaItemId = id,
                            Code = input.Code,
                            Client = input.Client,
                        }
                    );
                    await db.SaveChangesAsync();
                    var remove = await db
                        .Reports.Where(x => x.UserId == uid)
                        .OrderByDescending(x => x.Id)
                        .Skip(100)
                        .ToListAsync();
                    db.Reports.RemoveRange(remove);
                    await db.SaveChangesAsync();
                    return Results.NoContent();
                }
            )
            .WithName("ReportPlaybackError");
    }

    private static int Uid(ClaimsPrincipal user) =>
        int.Parse(
            user.FindFirstValue(ClaimTypes.NameIdentifier)!,
            System.Globalization.CultureInfo.InvariantCulture
        );

    private static string Handle(string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..32];

    private static ProgressState State(int id, PlaybackProgress? p) =>
        new(id, p?.Seconds ?? 0, p?.Completed ?? false, p?.Revision ?? 0, p?.UpdatedAt);
}

public record CsrfToken(string Token);

public record UserIdentity(string Name, string Role);

public record MediaSummary(
    int Id,
    string Title,
    double Duration,
    string VideoCodec,
    string AudioCodec,
    string Container,
    long Size,
    int LibraryId
);

public record MediaDetails(
    int Id,
    string Title,
    double Duration,
    string VideoCodec,
    string AudioCodec,
    string Container,
    long Size,
    PlaybackPlan Playback,
    int ContentRating = 0
);
