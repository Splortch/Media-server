using System.Security.Claims;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Metadata;

public record MatchReportInput(string Kind, string? Note);

public record MatchReportReview(string State, long ExpectedRevision);

public record MatchReportView(
    int Id,
    string Kind,
    string Note,
    string State,
    long Revision,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record LibraryProvenanceView(
    string Library,
    DateTime? AddedAt,
    DateTime? LastScannedAt,
    string Identification,
    bool CanReport,
    MatchReportView? Report
);

public record AdminMatchReport(
    int Id,
    int MediaId,
    string Title,
    string Reporter,
    string Kind,
    string Note,
    string State,
    long Revision
);

public record MatchReportPage(AdminMatchReport[] Items, int? NextOffset);

public record ProvenanceDiagnostics(
    int MediaId,
    string Title,
    string RelativePath,
    string? FilePath,
    bool Missing,
    bool PendingImport,
    long Size,
    string Fingerprint,
    string Identification,
    long? MetadataRevision,
    string? ProviderId,
    string? LastLibraryScanState,
    DateTime? LastLibraryScanAt
);

public static class LibraryProvenance
{
    private const string Feature = "library-provenance";

    private static int Uid(ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static MatchReportView View(MetadataMatchReport report) =>
        new(
            report.Id,
            report.Kind,
            report.Note,
            report.State,
            report.Revision,
            report.CreatedAt,
            report.UpdatedAt
        );

    private static string Source(MediaItem item, MediaMetadata? row)
    {
        if (row is not null)
        {
            var value = JsonSerializer.Deserialize<MetadataValue>(row.DocumentJson);
            return value?.Provider switch
            {
                "local-nfo" => "Local NFO metadata",
                "manual" => "Manual metadata correction",
                "tmdb" => "TMDB metadata match",
                _ => "Saved metadata match",
            };
        }
        if (item.ImportJobId is not null)
            return "Administrator's import selection";
        try
        {
            var embedded = JsonSerializer.Deserialize<MovieMetadata>(item.MetadataJson);
            if (!string.IsNullOrWhiteSpace(embedded?.Title) && item.Title == embedded.Title)
                return "Metadata embedded in the media file";
        }
        catch (JsonException) { }
        return "Filename-derived title";
    }

    private static async Task<bool> Visible(
        MediaDb db,
        ClaimsPrincipal user,
        CancellationToken ct
    ) =>
        await Features.Enabled(db, Feature)
        && !await db.FeatureVisibility.AnyAsync(x => x.UserId == Uid(user) && x.Key == Feature, ct);

    public static void MapLibraryProvenance(this RouteGroupBuilder group)
    {
        group
            .MapGet(
                "/media/{id:int}/provenance",
                async (int id, MediaDb db, ClaimsPrincipal user, CancellationToken ct) =>
                {
                    var item = await db
                        .Items.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == id, ct);
                    if (item is null)
                        return Results.NotFound();
                    if (!await Visible(db, user, ct))
                        return Results.NoContent();
                    var library = await db
                        .Libraries.Where(x => x.Id == item.LibraryId)
                        .Select(x => x.Name)
                        .SingleAsync(ct);
                    var metadata = await db
                        .Metadata.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.MediaItemId == id, ct);
                    var report = await db
                        .MatchReports.AsNoTracking()
                        .SingleOrDefaultAsync(
                            x => x.MediaItemId == id && x.UserId == Uid(user),
                            ct
                        );
                    return Results.Ok(
                        new LibraryProvenanceView(
                            library,
                            item.FirstSeenAt,
                            item.LastScannedAt,
                            Source(item, metadata),
                            !user.IsInRole("Guest"),
                            report is null ? null : View(report)
                        )
                    );
                }
            )
            .Produces<LibraryProvenanceView>();
        group
            .MapPost(
                "/media/{id:int}/match-report",
                async (
                    int id,
                    MatchReportInput input,
                    MediaDb db,
                    ClaimsPrincipal user,
                    CancellationToken ct
                ) =>
                {
                    if (user.IsInRole("Guest"))
                        return Results.Forbid();
                    if (
                        input.Kind is not ("title" or "artwork" or "episode")
                        || input.Note is { Length: > 500 }
                        || input.Note?.Any(c =>
                            char.IsControl(c) && c is not ('\n' or '\r' or '\t')
                        ) == true
                    )
                        return Results.BadRequest();
                    var uid = Uid(user);
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    if (!await Visible(db, user, ct))
                        return Results.Forbid();
                    if (!await db.Items.AnyAsync(x => x.Id == id, ct))
                        return Results.NotFound();
                    var report = await db.MatchReports.SingleOrDefaultAsync(
                        x => x.MediaItemId == id && x.UserId == uid,
                        ct
                    );
                    if (report?.State == "Open")
                        return Results.Ok(View(report));
                    var at = DateTime.UtcNow;
                    if (report is not null && report.UpdatedAt > at.AddMinutes(-5))
                        return Results.StatusCode(429);
                    if (
                        await db.MatchReports.CountAsync(
                            x => x.UserId == uid && x.State == "Open",
                            ct
                        ) >= 200
                    )
                        return Results.StatusCode(429);
                    if (report is null)
                    {
                        report = new MetadataMatchReport { UserId = uid, MediaItemId = id };
                        db.MatchReports.Add(report);
                    }
                    else
                        report.Revision++;
                    report.Kind = input.Kind;
                    report.Note = (input.Note ?? "").Trim();
                    report.State = "Open";
                    report.UpdatedAt = at;
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return Results.Ok(View(report));
                }
            )
            .Produces<MatchReportView>()
            .RequireRateLimiting("match-reports");

        var admin = group.MapGroup("/admin").RequireAuthorization("admin");
        admin
            .MapGet(
                "/media/{id:int}/provenance",
                async (int id, MediaDb db, PathPolicy paths, CancellationToken ct) =>
                {
                    if (!await Features.Enabled(db, Feature))
                        return Results.Forbid();
                    var item = await db
                        .Items.IgnoreQueryFilters()
                        .AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == id, ct);
                    if (item is null)
                        return Results.NotFound();
                    var library = await db.Libraries.SingleAsync(x => x.Id == item.LibraryId, ct);
                    string? file = null;
                    try
                    {
                        file = paths.Resolve(library.Path, item.RelativePath);
                    }
                    catch (Exception e)
                        when (e is IOException or UnauthorizedAccessException or ArgumentException)
                    { }
                    var row = await db
                        .Metadata.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.MediaItemId == id, ct);
                    var details = row is null
                        ? null
                        : JsonSerializer.Deserialize<MetadataValue>(row.DocumentJson);
                    var scan = await db
                        .Jobs.Where(x => x.LibraryId == library.Id)
                        .OrderByDescending(x => x.Id)
                        .FirstOrDefaultAsync(ct);
                    return Results.Ok(
                        new ProvenanceDiagnostics(
                            id,
                            item.Title,
                            item.RelativePath,
                            file,
                            item.IsMissing,
                            item.PendingImport,
                            item.Size,
                            item.Fingerprint,
                            Source(item, row),
                            row?.Revision,
                            details?.ProviderId,
                            scan?.State,
                            scan?.CreatedAt
                        )
                    );
                }
            )
            .Produces<ProvenanceDiagnostics>();
        admin
            .MapGet(
                "/match-reports",
                async (int? mediaId, int? offset, bool? closed, MediaDb db, CancellationToken ct) =>
                {
                    if (!await Features.Enabled(db, Feature))
                        return Results.Forbid();
                    if (mediaId is < 1 || offset is < 0 or > 100000)
                        return Results.BadRequest();
                    var query =
                        from report in db.MatchReports.AsNoTracking()
                        join media in db.Items.IgnoreQueryFilters().AsNoTracking()
                            on report.MediaItemId equals media.Id
                        join reporter in db.Users.AsNoTracking() on report.UserId equals reporter.Id
                        where
                            (mediaId == null || report.MediaItemId == mediaId)
                            && (closed == true || report.State == "Open")
                        orderby report.Id
                        select new AdminMatchReport(
                            report.Id,
                            media.Id,
                            media.Title,
                            reporter.Name,
                            report.Kind,
                            report.Note,
                            report.State,
                            report.Revision
                        );
                    var rows = await query.Skip(offset ?? 0).Take(51).ToArrayAsync(ct);
                    return Results.Ok(
                        new MatchReportPage(
                            rows.Take(50).ToArray(),
                            rows.Length > 50 ? (offset ?? 0) + 50 : null
                        )
                    );
                }
            )
            .Produces<MatchReportPage>();
        admin
            .MapPost(
                "/match-reports/{id:int}/review",
                async (int id, MatchReportReview input, MediaDb db, CancellationToken ct) =>
                {
                    if (!await Features.Enabled(db, Feature))
                        return Results.Forbid();
                    if (
                        input.State is not ("Resolved" or "Dismissed")
                        || input.ExpectedRevision < 1
                    )
                        return Results.BadRequest();
                    var report = await db.MatchReports.SingleOrDefaultAsync(x => x.Id == id, ct);
                    if (report is null)
                        return Results.NotFound();
                    if (report.Revision != input.ExpectedRevision || report.State != "Open")
                        return Results.Conflict();
                    report.State = input.State;
                    report.Revision++;
                    report.UpdatedAt = DateTime.UtcNow;
                    try
                    {
                        await db.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        return Results.Conflict();
                    }
                    return Results.Ok(View(report));
                }
            )
            .Produces<MatchReportView>();
    }
}
