using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Downloads;

public record DownloadInput(
    string Name,
    string Url,
    long BudgetBytes,
    string? Sha256,
    int Priority = 0
);

public record DownloadAction(string Action, long ExpectedRevision, int? Priority = null);

public record DownloadSettingsInput(bool? Enabled, string[]? Hosts);

public record DownloadView(
    string Id,
    string Name,
    string SourceHost,
    string State,
    string Desired,
    int Priority,
    long ReceivedBytes,
    long? TotalBytes,
    long BudgetBytes,
    string? ContentHash,
    string? Error,
    long Revision,
    bool CanResume
);

public record DownloadSettingsView(
    bool Enabled,
    string[] Hosts,
    long AvailableBytes,
    long ReservedBytes,
    long SafetyMarginBytes,
    long StagingLimitBytes
);

public static class DownloadEndpoints
{
    public static DownloadView View(DownloadJob x) =>
        new(
            x.Id,
            x.Name,
            x.SourceHost,
            x.State,
            x.Desired,
            x.Priority,
            x.ReceivedBytes,
            x.TotalBytes,
            x.MaxBytes,
            x.ContentHash,
            x.Error,
            x.Revision,
            x.ReceivedBytes == 0 || x.ETag != null || x.TotalBytes == x.ReceivedBytes
        );

    private static readonly Expression<Func<DownloadJob, DownloadView>> Projection = x =>
        new(
            x.Id,
            x.Name,
            x.SourceHost,
            x.State,
            x.Desired,
            x.Priority,
            x.ReceivedBytes,
            x.TotalBytes,
            x.MaxBytes,
            x.ContentHash,
            x.Error,
            x.Revision,
            x.ReceivedBytes == 0 || x.ETag != null || x.TotalBytes == x.ReceivedBytes
        );

    public static async Task<DownloadSettingsView> Settings(
        MediaDb db,
        DownloadStorage storage,
        CancellationToken ct
    )
    {
        var policy = await db.DownloadPolicies.AsNoTracking().SingleOrDefaultAsync(ct);
        var jobs = await DownloadWorker.Budgets(db, ct);
        long free;
        try
        {
            free = storage.Free();
        }
        catch (IOException)
        {
            free = 0;
        }
        return new(
            await DownloadWorker.Enabled(db, ct),
            JsonSerializer.Deserialize<string[]>(policy?.HostsJson ?? "[]") ?? [],
            free,
            jobs.Where(x => x.State is "queued" or "running" or "paused")
                .Sum(DownloadStorage.Remaining),
            DownloadStorage.Margin,
            DownloadStorage.TotalLimit
        );
    }

    public static void MapDownloads(this WebApplication app)
    {
        foreach (var prefix in new[] { "/api", "/api/v1" })
        {
            var api = app.MapGroup(prefix + "/downloads").RequireAuthorization("admin");
            if (prefix.EndsWith("v1", StringComparison.Ordinal))
                api.WithGroupName("client-v1");
            api.AddEndpointFilter(
                async (context, next) =>
                {
                    try
                    {
                        return await next(context);
                    }
                    catch (DownloadFault fault)
                    {
                        return Results.BadRequest(new { message = fault.Code });
                    }
                    catch (IOException)
                    {
                        return Results.BadRequest(
                            new
                            {
                                message = "Download storage is unavailable. Check data-directory permissions and free space.",
                            }
                        );
                    }
                }
            );
            api.MapGet("/settings", Settings);
            api.MapPut(
                    "/settings",
                    async (
                        DownloadSettingsInput input,
                        MediaDb db,
                        DownloadStorage storage,
                        DownloadControl control,
                        CancellationToken ct
                    ) =>
                    {
                        if (input.Enabled == null)
                            throw new ArgumentException("Choose whether downloads are enabled.");
                        var enabled = input.Enabled.Value;
                        var hosts = enabled ? DownloadNetwork.Hosts(input.Hosts ?? []) : null;
                        if (enabled && hosts!.Length == 0)
                            throw new ArgumentException(
                                "Approve at least one exact public source host before enabling downloads."
                            );
                        await control.Gate.WaitAsync(ct);
                        try
                        {
                            var policy = await db.DownloadPolicies.SingleOrDefaultAsync(ct);
                            if (policy == null)
                            {
                                policy = new();
                                db.DownloadPolicies.Add(policy);
                            }
                            if (hosts != null)
                                policy.HostsJson = JsonSerializer.Serialize(hosts);
                            var flag = await db.FeaturePolicies.SingleOrDefaultAsync(
                                x => x.Key == "downloads",
                                ct
                            );
                            if (flag == null)
                            {
                                flag = new() { Key = "downloads" };
                                db.FeaturePolicies.Add(flag);
                            }
                            flag.Enabled = enabled;
                            await db.SaveChangesAsync(ct);
                            // A policy/host change must stop the old transport, including existing pooled connections.
                            await db
                                .Downloads.Where(x => x.State == "queued" || x.State == "running")
                                .ExecuteUpdateAsync(
                                    s =>
                                        s.SetProperty(x => x.Desired, "pause")
                                            .SetProperty(
                                                x => x.State,
                                                x => x.State == "queued" ? "paused" : x.State
                                            )
                                            .SetProperty(x => x.Revision, x => x.Revision + 1),
                                    ct
                                );
                            control.Cancel();
                        }
                        finally
                        {
                            control.Gate.Release();
                        }
                        try
                        {
                            await control.Idle(ct);
                        }
                        catch (TimeoutException)
                        {
                            return Results.Accepted(
                                value: new
                                {
                                    message = "Settings saved; the worker is still stopping. Check the queue before changing storage.",
                                }
                            );
                        }
                        return Results.Ok(await Settings(db, storage, ct));
                    }
                )
                .Produces<DownloadSettingsView>();
            api.MapGet(
                    "",
                    async (MediaDb db, CancellationToken ct) =>
                    {
                        if (!await DownloadWorker.Enabled(db, ct))
                            return Results.Forbid();
                        return Results.Ok(
                            await db
                                .Downloads.AsNoTracking()
                                .OrderByDescending(x => x.Priority)
                                .ThenBy(x => x.CreatedAt)
                                .Take(100)
                                .Select(Projection)
                                .ToArrayAsync(ct)
                        );
                    }
                )
                .Produces<DownloadView[]>();
            api.MapPost(
                    "",
                    async (
                        DownloadInput input,
                        MediaDb db,
                        DownloadStorage storage,
                        DownloadControl control,
                        IDataProtectionProvider protection,
                        ClaimsPrincipal user,
                        CancellationToken ct
                    ) =>
                    {
                        if (
                            string.IsNullOrWhiteSpace(input.Name)
                            || input.Name.Length > 80
                            || input.Name.Any(char.IsControl)
                            || input.BudgetBytes is < 1 or > DownloadStorage.TotalLimit
                            || input.Priority is < -10 or > 10
                            || (
                                input.Sha256 != null
                                && (input.Sha256.Length != 64 || !input.Sha256.All(Uri.IsHexDigit))
                            )
                        )
                            throw new ArgumentException(
                                "Use a name of 1–80 characters, positive budget up to 100 GiB, priority -10…10 and an optional 64-digit SHA-256."
                            );
                        await control.Gate.WaitAsync(ct);
                        try
                        {
                            if (!await DownloadWorker.Enabled(db, ct))
                                return Results.Forbid();
                            if (await db.Downloads.CountAsync(ct) >= 100)
                                throw new ArgumentException(
                                    "Remove finished jobs before adding more than 100 downloads."
                                );
                            var policy = await db.DownloadPolicies.SingleAsync(ct);
                            var uri = DownloadNetwork.Url(
                                input.Url,
                                JsonSerializer.Deserialize<string[]>(policy.HostsJson) ?? []
                            );
                            var job = new DownloadJob
                            {
                                UserId = Operations.Uid(user),
                                Name = input.Name.Trim(),
                                SourceHost = uri.IdnHost,
                                ProtectedUrl = protection
                                    .CreateProtector(DownloadWorker.SecretPurpose)
                                    .Protect(uri.AbsoluteUri),
                                MaxBytes = input.BudgetBytes,
                                ExpectedHash = input.Sha256?.ToLowerInvariant(),
                                Priority = input.Priority,
                            };
                            storage.Admit((await DownloadWorker.Budgets(db, ct)).Append(job));
                            db.Downloads.Add(job);
                            await db.SaveChangesAsync(ct);
                            control.Signal();
                            return Results.Ok(View(job));
                        }
                        finally
                        {
                            control.Gate.Release();
                        }
                    }
                )
                .Produces<DownloadView>();
            api.MapPut(
                    "/{id}",
                    async (
                        string id,
                        DownloadAction input,
                        MediaDb db,
                        DownloadControl control,
                        DownloadStorage storage,
                        CancellationToken ct
                    ) =>
                    {
                        await control.Gate.WaitAsync(ct);
                        try
                        {
                            var row = await db.Downloads.SingleOrDefaultAsync(x => x.Id == id, ct);
                            if (row == null)
                                return Results.NotFound();
                            if (row.Revision != input.ExpectedRevision || row.State == "finalizing")
                                return Results.Conflict(
                                    new
                                    {
                                        message = "Download changed or is finalizing. Refresh the queue.",
                                    }
                                );
                            switch (input.Action)
                            {
                                case "priority":
                                    if (
                                        input.Priority is null or < -10 or > 10
                                        || row.State is "completed" or "cancelled"
                                    )
                                        throw new ArgumentException(
                                            "Set pending download priority from -10 to 10."
                                        );
                                    row.Priority = input.Priority.Value;
                                    break;
                                case "pause":
                                    if (row.State is not ("queued" or "running"))
                                        throw new ArgumentException(
                                            "Only queued/running jobs can pause."
                                        );
                                    row.Desired = "pause";
                                    if (row.State == "queued")
                                        row.State = "paused";
                                    control.Cancel(id);
                                    break;
                                case "resume":
                                case "restart":
                                    if (!await DownloadWorker.Enabled(db, ct))
                                        return Results.Forbid();
                                    if (row.State is not ("paused" or "failed"))
                                        throw new ArgumentException(
                                            "Only stopped jobs can resume/restart."
                                        );
                                    if (input.Action == "resume" && !View(row).CanResume)
                                        throw new ArgumentException(
                                            "No strong resume validator. Restart from zero to avoid combining different content."
                                        );
                                    // Admission precedes discarding the partial: a failed quota check must not mutate it.
                                    var others = (await DownloadWorker.Budgets(db, ct)).Where(x =>
                                        x.Id != id
                                    );
                                    var budget = new DownloadJob
                                    {
                                        State = "queued",
                                        MaxBytes = row.MaxBytes,
                                        ReceivedBytes =
                                            input.Action == "restart" ? 0 : row.ReceivedBytes,
                                        TotalBytes =
                                            input.Action == "restart" ? null : row.TotalBytes,
                                    };
                                    storage.Admit(others.Append(budget));
                                    if (input.Action == "restart")
                                    {
                                        storage.Discard(id);
                                        row.ReceivedBytes = 0;
                                        row.TotalBytes = null;
                                        row.ETag = null;
                                        row.SourceIdentity = null;
                                        row.ContentHash = null;
                                    }
                                    row.State = "queued";
                                    row.Desired = "run";
                                    row.Error = null;
                                    break;
                                case "cancel":
                                    if (row.State is "completed" or "cancelled")
                                        throw new ArgumentException(
                                            "Remove finished jobs explicitly."
                                        );
                                    row.Desired = "cancel";
                                    if (row.State != "running")
                                    {
                                        storage.Discard(id);
                                        row.State = "cancelled";
                                        row.ProtectedUrl = "";
                                        row.ReceivedBytes = 0;
                                        row.TotalBytes = null;
                                        row.ContentHash = null;
                                    }
                                    control.Cancel(id);
                                    break;
                                default:
                                    throw new ArgumentException("Unknown download operation.");
                            }
                            row.Revision++;
                            row.UpdatedAt = DateTime.UtcNow;
                            await db.SaveChangesAsync(ct);
                            control.Signal();
                            return Results.Ok(View(row));
                        }
                        finally
                        {
                            control.Gate.Release();
                        }
                    }
                )
                .Produces<DownloadView>();
            api.MapDelete(
                "/{id}/{revision:long}",
                async (
                    string id,
                    long revision,
                    MediaDb db,
                    DownloadStorage storage,
                    DownloadControl control,
                    CancellationToken ct
                ) =>
                {
                    await control.Gate.WaitAsync(ct);
                    try
                    {
                        var row = await db.Downloads.SingleOrDefaultAsync(x => x.Id == id, ct);
                        if (row == null)
                            return Results.NotFound();
                        if (
                            row.Revision != revision
                            || row.State is not ("completed" or "cancelled" or "failed" or "paused")
                        )
                            return Results.Conflict(
                                new
                                {
                                    message = "Stop the job and refresh before removing its staged files.",
                                }
                            );
                        storage.Discard(id);
                        db.Downloads.Remove(row);
                        await db.SaveChangesAsync(ct);
                        return Results.NoContent();
                    }
                    finally
                    {
                        control.Gate.Release();
                    }
                }
            );
            api.MapGet(
                "/{id}/file",
                async (string id, MediaDb db, DownloadStorage storage, CancellationToken ct) =>
                {
                    if (!await db.Downloads.AnyAsync(x => x.Id == id && x.State == "completed", ct))
                        return Results.NotFound();
                    return Results.File(
                        storage.OpenRead(id),
                        "application/octet-stream",
                        "download-" + id + ".bin",
                        enableRangeProcessing: true
                    );
                }
            );
        }
    }
}
