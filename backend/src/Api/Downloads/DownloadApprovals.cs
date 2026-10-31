using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using MediaServer.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Downloads;

public record DownloadImportPlan(
    int LibraryId,
    string Classification,
    string Title,
    int? Year,
    string? Series,
    int? Season,
    int? Episode,
    int Rating,
    string Extension
);

public record DownloadApprovalInput(
    long RequestRevision,
    DownloadInput Source,
    DownloadImportPlan Plan
);

public record DownloadMatchInput(long Revision, DownloadImportPlan Plan);

public record DownloadImportAction(long Revision, string Action);

public record DownloadDecline(long Revision, string Reason);

public record DownloadImportView(
    int? RequestId,
    int LibraryId,
    string Classification,
    string Title,
    int? Year,
    string? Series,
    int? Season,
    int? Episode,
    int Rating,
    string Extension,
    string State,
    string? Error,
    int? MediaItemId,
    long CopiedBytes
);

public record HouseholdDownloadStatus(int RequestId, string State, int? MediaItemId);

public static class DownloadApprovals
{
    public static DownloadImportView? View(DownloadJob job) =>
        job.TargetLibraryId is int library
            ? new(
                job.RequestId,
                library,
                job.Classification!,
                job.ImportTitle!,
                job.ImportYear,
                job.Series,
                job.Season,
                job.Episode,
                job.ImportRating,
                job.ImportExtension!,
                job.ImportState,
                job.ImportError,
                job.ImportedMediaId,
                job.ImportCopiedBytes
            )
            : null;

    private static string Slug(string value)
    {
        var result = new string(
            value
                .Normalize(NormalizationForm.FormKC)
                .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : ' ')
                .ToArray()
        );
        result = string.Join('-', result.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (result.Length > 60)
            result = result[..60];
        if (
            System.Text.RegularExpressions.Regex.IsMatch(
                result,
                @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.CultureInvariant
            )
        )
            result += "-title";
        return string.IsNullOrWhiteSpace(result) ? "Title" : result;
    }

    private static void Text(string value)
    {
        if (
            string.IsNullOrWhiteSpace(value)
            || value.Length > 120
            || value.Any(char.IsControl)
            || value.IndexOfAny(['/', '\\']) >= 0
            || value.Trim() is "." or ".."
        )
            throw new ArgumentException(
                "Use a title of 1–120 characters without paths or control characters."
            );
    }

    public static async Task Plan(
        MediaDb db,
        DownloadStorage storage,
        IDownloadSpace space,
        PathPolicy paths,
        DownloadJob job,
        DownloadImportPlan input,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        Text(input.Title);
        if (input.Series != null)
            Text(input.Series);
        if (
            input.Classification is not ("movies" or "tv")
            || input.Year is < 1888 or > 2100
            || input.Season is < 0 or > 99
            || input.Episode is < 1 or > 999
            || input.Rating is not (0 or 10 or 13 or 16 or 18)
            || input.Extension is not (".mp4" or ".m4v" or ".mov" or ".webm" or ".mkv" or ".avi")
        )
            throw new ArgumentException(
                "Choose an explicit Movies/TV match, supported extension and valid content rating."
            );
        var library =
            await db.Libraries.SingleOrDefaultAsync(x => x.Id == input.LibraryId, ct)
            ?? throw new DownloadFault("target-library-unavailable");
        if (library.Kind != input.Classification)
            throw new ArgumentException("Classification must match the chosen library.");
        paths.ValidateDirectory(library.Path);
        var data = Path.GetDirectoryName(storage.Root)!;
        if (PathPolicy.Within(data, library.Path) || PathPolicy.Within(library.Path, data))
            throw new ArgumentException(
                "Imports cannot overlap the private server data directory."
            );
        var television = input.Classification == "tv";
        var matched =
            !television || (input.Series != null && input.Season != null && input.Episode != null);
        if (
            television
            && !matched
            && (input.Series != null || input.Season != null || input.Episode != null)
        )
            throw new ArgumentException(
                "Supply series, season and episode together, or leave all three blank for inbox review."
            );
        job.TargetLibraryId = library.Id;
        job.TargetRoot = library.Path;
        job.Classification = input.Classification;
        job.ImportTitle = input.Title.Trim();
        job.ImportYear = input.Year;
        job.Series = television ? input.Series?.Trim() : null;
        job.Season = television ? input.Season : null;
        job.Episode = television ? input.Episode : null;
        job.ImportRating = input.Rating;
        job.ImportExtension = input.Extension;
        job.ImportNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        job.ImportRelativePath = matched
            ? television
                ? Path.Combine(
                    "MediaServer Imports",
                    "TV Shows",
                    Slug(input.Series!),
                    $"Season {input.Season:00}",
                    job.Id,
                    $"{Slug(input.Series!)} - S{input.Season:00}E{input.Episode:00}{input.Extension}"
                )
                : Path.Combine(
                    "MediaServer Imports",
                    "Movies",
                    Slug(input.Title) + (input.Year.HasValue ? $" ({input.Year})" : ""),
                    job.Id,
                    Slug(input.Title) + input.Extension
                )
            : null;
        job.ImportState = matched ? (job.State == "completed" ? "queued" : "waiting") : "inbox";
        job.ImportError = null;
        job.ImportCopiedBytes = 0;
        if (job.ImportRelativePath != null)
            DownloadImporter.ManagedFolder(paths, job, false);
        if (
            job.ImportRelativePath != null
            && Directory.Exists(
                Path.GetDirectoryName(Path.Combine(library.Path, job.ImportRelativePath))
            )
        )
            throw new DownloadFault("import-folder-collision");
        var others = (await DownloadWorker.Budgets(db, ct))
            .Where(x => x.Id != job.Id)
            .Append(job)
            .ToArray();
        storage.Admit(others);
        var reserved =
            others.Sum(DownloadAccess.ImportRemaining)
            + others
                .Where(x => x.State is "queued" or "running" or "paused")
                .Sum(DownloadStorage.Remaining);
        if (space.Available(library.Path) - DownloadStorage.Margin < reserved)
            throw new DownloadFault("destination-storage-insufficient");
    }

    private static async Task<bool> CanReceive(
        MediaDb db,
        MovieRequest request,
        int rating,
        CancellationToken ct
    ) =>
        await db.Users.AnyAsync(
            u =>
                u.Id == request.UserId
                && !u.Disabled
                && u.Role != "Guest"
                && (
                    u.Role == "Admin"
                    || u.AllLibraries
                    || db.Grants.Any(g => g.UserId == u.Id && g.LibraryId == request.LibraryId)
                )
                && (u.MaximumRating == null || (rating > 0 && rating <= u.MaximumRating)),
            ct
        );

    public static void MapDownloadApprovals(this WebApplication app)
    {
        foreach (var prefix in new[] { "/api", "/api/v1" })
        {
            var api = app.MapGroup(prefix).RequireAuthorization();
            if (prefix.EndsWith("v1", StringComparison.Ordinal))
                api.WithGroupName("client-v1");
            api.AddEndpointFilter(
                async (context, next) =>
                {
                    try
                    {
                        return await next(context);
                    }
                    catch (DownloadFault e)
                    {
                        return Results.BadRequest(new { message = e.Code });
                    }
                    catch (IOException)
                    {
                        return Results.BadRequest(
                            new
                            {
                                message = "Managed import storage is unavailable. Check permissions and free space.",
                            }
                        );
                    }
                }
            );
            api.MapGet(
                "/requests/download-status",
                async (MediaDb db, ClaimsPrincipal user, CancellationToken ct) =>
                {
                    if (
                        user.IsInRole("Guest")
                        || !await DownloadWorker.Enabled(db, ct)
                        || !await Features.Enabled(db, "movie-requests")
                    )
                        return Array.Empty<HouseholdDownloadStatus>();
                    var uid = Operations.Uid(user);
                    return await (
                        from job in db.Downloads
                        from request in db.MovieRequests
                        where
                            job.RequestId == request.Id
                            && request.UserId == uid
                            && db.Libraries.Any(l => l.Id == request.LibraryId)
                            && (
                                job.ImportState != "imported"
                                || (
                                    job.ImportedMediaId != null
                                    && db.Items.Any(x => x.Id == job.ImportedMediaId)
                                )
                            )
                            && db.Users.Any(u =>
                                u.Id == uid
                                && !u.Disabled
                                && (
                                    u.MaximumRating == null
                                    || (job.ImportRating > 0 && job.ImportRating <= u.MaximumRating)
                                )
                            )
                        select new HouseholdDownloadStatus(
                            request.Id,
                            job.ImportState == "imported" ? "Available"
                                : job.State == "completed"
                                    ? job.ImportState == "inbox" ? "Awaiting administrator match"
                                        : job.ImportState == "held" ? "Administrator review needed"
                                        : "Organizing"
                                : job.State == "running" ? "Downloading"
                                : job.State == "queued" ? "Approved"
                                : job.State == "paused" ? "Paused"
                                : "Administrator review needed",
                            job.ImportedMediaId != null
                            && db.Items.Any(x => x.Id == job.ImportedMediaId)
                                ? job.ImportedMediaId
                                : null
                        )
                    ).ToArrayAsync(ct);
                }
            );
            api.MapPost(
                    "/requests/{id:int}/download",
                    async (
                        int id,
                        DownloadApprovalInput input,
                        MediaDb db,
                        DownloadControl control,
                        DownloadStorage storage,
                        IDownloadSpace space,
                        PathPolicy paths,
                        IDataProtectionProvider protection,
                        ClaimsPrincipal user,
                        CancellationToken ct
                    ) =>
                    {
                        DownloadEndpoints.Validate(input.Source);
                        ArgumentNullException.ThrowIfNull(input.Plan);
                        await control.Gate.WaitAsync(ct);
                        try
                        {
                            if (
                                !await DownloadWorker.Enabled(db, ct)
                                || !await Features.Enabled(db, "movie-requests")
                            )
                                return Results.Forbid();
                            var request = await db.MovieRequests.SingleOrDefaultAsync(
                                x => x.Id == id,
                                ct
                            );
                            if (request == null)
                                return Results.NotFound();
                            if (
                                request.Revision != input.RequestRevision
                                || request.State is "Added" or "Declined"
                                || await db.Downloads.AnyAsync(x => x.RequestId == id, ct)
                            )
                                return Results.Conflict(
                                    new
                                    {
                                        message = "This request was already reviewed or approved. Refresh before continuing.",
                                    }
                                );
                            if (
                                input.Plan.LibraryId != request.LibraryId
                                || !await CanReceive(db, request, input.Plan.Rating, ct)
                            )
                                return Results.BadRequest(
                                    new
                                    {
                                        message = "The requester must retain access to the chosen library and approved content rating.",
                                    }
                                );
                            if (await db.Downloads.CountAsync(ct) >= 100)
                                throw new ArgumentException(
                                    "Remove finished downloads before adding more than 100 jobs."
                                );
                            var policy = await db.DownloadPolicies.SingleAsync(ct);
                            var source = DownloadNetwork.Url(
                                input.Source.Url,
                                JsonSerializer.Deserialize<string[]>(policy.HostsJson) ?? []
                            );
                            var job = new DownloadJob
                            {
                                UserId = Operations.Uid(user),
                                Name = input.Source.Name.Trim(),
                                ProtectedUrl = protection
                                    .CreateProtector(DownloadWorker.SecretPurpose)
                                    .Protect(source.AbsoluteUri),
                                SourceHost = source.IdnHost,
                                MaxBytes = input.Source.BudgetBytes,
                                ExpectedHash = input.Source.Sha256?.ToLowerInvariant(),
                                Priority = input.Source.Priority,
                                RequestId = id,
                                RequestOwnerId = request.UserId,
                                RequestRevision = request.Revision + 1,
                            };
                            await Plan(db, storage, space, paths, job, input.Plan, ct);
                            request.State = "Considering";
                            request.Reply =
                                "Download approved; delivery is subject to media validation and current permissions.";
                            request.Revision++;
                            request.UpdatedAt = DateTime.UtcNow;
                            db.Downloads.Add(job);
                            try
                            {
                                await db.SaveChangesAsync(ct);
                            }
                            catch (DbUpdateException)
                            {
                                return Results.Conflict(
                                    new
                                    {
                                        message = "Approval changed. Refresh the request before retrying.",
                                    }
                                );
                            }
                            control.Signal();
                            return Results.Ok(DownloadEndpoints.View(job));
                        }
                        finally
                        {
                            control.Gate.Release();
                        }
                    }
                )
                .RequireAuthorization("admin")
                .Produces<DownloadView>();
            api.MapPost(
                    "/requests/{id:int}/download/decline",
                    async (
                        int id,
                        DownloadDecline input,
                        MediaDb db,
                        DownloadControl control,
                        CancellationToken ct
                    ) =>
                    {
                        if (
                            string.IsNullOrWhiteSpace(input.Reason)
                            || input.Reason.Length > 500
                            || input.Reason.Any(c => char.IsControl(c) && c != '\n')
                        )
                            throw new ArgumentException(
                                "Give a decline reason of 1–500 characters."
                            );
                        await control.Gate.WaitAsync(ct);
                        try
                        {
                            var request = await db.MovieRequests.SingleOrDefaultAsync(
                                x => x.Id == id,
                                ct
                            );
                            if (request == null)
                                return Results.NotFound();
                            if (request.Revision != input.Revision || request.State == "Added")
                                return Results.Conflict(
                                    new { message = "Request changed. Refresh before declining." }
                                );
                            request.State = "Declined";
                            request.Reply = input.Reason.Trim();
                            request.Revision++;
                            request.UpdatedAt = DateTime.UtcNow;
                            var job = await db.Downloads.SingleOrDefaultAsync(
                                x => x.RequestId == id,
                                ct
                            );
                            if (job != null)
                            {
                                job.Desired = "pause";
                                if (job.State == "queued")
                                    job.State = "paused";
                                if (DownloadImporter.Busy(job.ImportState))
                                    job.ImportState = "held";
                                job.Revision++;
                                control.Cancel(job.Id);
                            }
                            try
                            {
                                await db.SaveChangesAsync(ct);
                            }
                            catch (DbUpdateConcurrencyException)
                            {
                                return Results.Conflict(
                                    new { message = "Request changed. Refresh before declining." }
                                );
                            }
                            return Results.NoContent();
                        }
                        finally
                        {
                            control.Gate.Release();
                        }
                    }
                )
                .RequireAuthorization("admin");
            api.MapPut(
                    "/downloads/{id}/import-plan",
                    async (
                        string id,
                        DownloadMatchInput input,
                        MediaDb db,
                        DownloadStorage storage,
                        IDownloadSpace space,
                        PathPolicy paths,
                        DownloadControl control,
                        CancellationToken ct
                    ) =>
                    {
                        ArgumentNullException.ThrowIfNull(input.Plan);
                        await control.Gate.WaitAsync(ct);
                        try
                        {
                            if (!await DownloadWorker.Enabled(db, ct))
                                return Results.Forbid();
                            var job = await db.Downloads.SingleOrDefaultAsync(x => x.Id == id, ct);
                            if (job == null)
                                return Results.NotFound();
                            if (
                                control.IsActive(id)
                                || job.Revision != input.Revision
                                || job.State == "cancelled"
                                || DownloadImporter.Busy(job.ImportState)
                                || job.ImportState == "imported"
                                || job.ImportCopiedBytes > 0
                            )
                                return Results.Conflict(
                                    new
                                    {
                                        message = "Stop and discard the previous import before changing its match.",
                                    }
                                );
                            if (job.RequestId is int requestId)
                            {
                                var request = await db.MovieRequests.SingleOrDefaultAsync(
                                    x => x.Id == requestId,
                                    ct
                                );
                                if (
                                    request == null
                                    || request.State != "Considering"
                                    || request.Revision != job.RequestRevision
                                    || request.LibraryId != input.Plan.LibraryId
                                    || !await CanReceive(db, request, input.Plan.Rating, ct)
                                )
                                    return Results.Forbid();
                            }
                            // An existing ownership marker must be explicitly discarded before replanning.
                            if (
                                job.TargetRoot != null
                                && job.ImportRelativePath != null
                                && Directory.Exists(
                                    Path.GetDirectoryName(
                                        Path.Combine(job.TargetRoot, job.ImportRelativePath)
                                    )
                                )
                            )
                                return Results.Conflict(
                                    new
                                    {
                                        message = "Discard the held import before replanning; staged media will be retained.",
                                    }
                                );
                            await Plan(db, storage, space, paths, job, input.Plan, ct);
                            job.Revision++;
                            await db.SaveChangesAsync(ct);
                            control.Signal();
                            return Results.Ok(DownloadEndpoints.View(job));
                        }
                        finally
                        {
                            control.Gate.Release();
                        }
                    }
                )
                .RequireAuthorization("admin")
                .Produces<DownloadView>();
            api.MapPut(
                    "/downloads/{id}/import",
                    async (
                        string id,
                        DownloadImportAction input,
                        MediaDb db,
                        PathPolicy paths,
                        DownloadControl control,
                        ScanScheduler scheduler,
                        CancellationToken ct
                    ) =>
                    {
                        await control.Gate.WaitAsync(ct);
                        try
                        {
                            var job = await db.Downloads.SingleOrDefaultAsync(x => x.Id == id, ct);
                            if (job == null)
                                return Results.NotFound();
                            if (
                                (input.Action != "hold" && control.IsActive(id))
                                || job.Revision != input.Revision
                                || job.ImportState == "imported"
                            )
                                return Results.Conflict(
                                    new { message = "Import changed. Refresh before continuing." }
                                );
                            if (input.Action == "hold")
                            {
                                if (!DownloadImporter.Busy(job.ImportState))
                                    throw new ArgumentException(
                                        "Only pending imports can be held."
                                    );
                                job.ImportState = "held";
                                control.Cancel(id);
                            }
                            else if (input.Action == "retry")
                            {
                                if (
                                    !await DownloadWorker.Enabled(db, ct)
                                    || !await DownloadAccess.Allowed(db, job, ct)
                                )
                                    return Results.Forbid();
                                if (
                                    job.State != "completed"
                                    || job.ImportState != "held"
                                    || job.ImportRelativePath == null
                                )
                                    throw new ArgumentException(
                                        "Only matched held imports can retry."
                                    );
                                job.ImportState = "queued";
                                job.ImportError = null;
                            }
                            else if (input.Action == "discard")
                            {
                                if (DownloadImporter.Busy(job.ImportState))
                                    return Results.Conflict(
                                        new
                                        {
                                            message = "Hold the import, then wait for the worker to stop before discarding.",
                                        }
                                    );
                                // Worker cleanup and this operation share the control gate; Idle is checked outside below via an explicit held state.
                                if (
                                    !await scheduler.WhileIdleAsync(
                                        job.TargetLibraryId ?? 0,
                                        () => DownloadImporter.Discard(db, paths, job, ct),
                                        ct
                                    )
                                )
                                    return Results.Conflict(
                                        new
                                        {
                                            message = "Wait for the active library scan before discarding import files.",
                                        }
                                    );
                            }
                            else
                                throw new ArgumentException("Choose hold, retry or discard.");
                            job.Revision++;
                            await db.SaveChangesAsync(ct);
                            control.Signal();
                            return Results.Ok(DownloadEndpoints.View(job));
                        }
                        finally
                        {
                            control.Gate.Release();
                        }
                    }
                )
                .RequireAuthorization("admin")
                .Produces<DownloadView>();
        }
    }
}
