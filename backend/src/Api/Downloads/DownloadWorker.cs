using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Downloads;

public sealed class DownloadWorker(
    IServiceScopeFactory scopes,
    DownloadControl control,
    DownloadStorage storage,
    IDownloadTransport transport,
    IDataProtectionProvider protection,
    DownloadImporter importer,
    ILogger<DownloadWorker> logger
) : BackgroundService
{
    public const string SecretPurpose = "MediaServer.Download.Source.v1";
    private readonly IDataProtector secrets = protection.CreateProtector(SecretPurpose);

    public static Task<bool> Enabled(MediaDb db, CancellationToken ct = default) =>
        db.FeaturePolicies.AnyAsync(x => x.Key == "downloads" && x.Enabled, ct);

    public static Task<DownloadJob[]> Budgets(MediaDb db, CancellationToken ct) =>
        db
            .Downloads.AsNoTracking()
            .Select(x => new DownloadJob
            {
                Id = x.Id,
                State = x.State,
                MaxBytes = x.MaxBytes,
                ReceivedBytes = x.ReceivedBytes,
                TotalBytes = x.TotalBytes,
                TargetLibraryId = x.TargetLibraryId,
                ImportState = x.ImportState,
                ImportCopiedBytes = x.ImportCopiedBytes,
            })
            .ToArrayAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            await db
                .Downloads.Where(x => x.State == "running" || x.State == "finalizing")
                .ExecuteUpdateAsync(
                    s =>
                        s.SetProperty(x => x.State, "paused")
                            .SetProperty(x => x.Desired, "pause")
                            .SetProperty(x => x.Error, "server-restarted")
                            .SetProperty(x => x.Revision, x => x.Revision + 1),
                    stoppingToken
                );
        }
        using (var recovery = scopes.CreateScope())
        {
            var db = recovery.ServiceProvider.GetRequiredService<MediaDb>();
            await db
                .Downloads.Where(x => x.ImportState == "copying" || x.ImportState == "scanning")
                .ExecuteUpdateAsync(
                    s =>
                        s.SetProperty(x => x.ImportState, "held")
                            .SetProperty(x => x.ImportError, "server-restarted"),
                    stoppingToken
                );
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            string? id = null;
            var isImport = false;
            CancellationToken ct = stoppingToken;
            await control.Gate.WaitAsync(stoppingToken);
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
                if (await Enabled(db, stoppingToken))
                {
                    var row = await db
                        .Downloads.Where(x =>
                            (x.State == "queued" && x.Desired == "run")
                            || (x.State == "completed" && x.ImportState == "queued")
                        )
                        .OrderByDescending(x => x.Priority)
                        .ThenBy(x => x.CreatedAt)
                        .FirstOrDefaultAsync(stoppingToken);
                    if (row != null)
                    {
                        isImport = row.State == "completed";
                        if (isImport)
                        {
                            row.ImportState = "copying";
                            row.ImportError = null;
                        }
                        else
                        {
                            row.State = "running";
                            row.Error = null;
                        }
                        await db.SaveChangesAsync(stoppingToken);
                        id = row.Id;
                        ct = control.Begin(id, stoppingToken);
                    }
                }
            }
            finally
            {
                control.Gate.Release();
            }
            if (id == null)
            {
                await control.Wait(stoppingToken);
                continue;
            }
            try
            {
                if (isImport)
                    await importer.Run(id, ct);
                else
                    await Transfer(id, ct);
            }
            catch (Exception ex)
            {
                // Source URLs, query tokens, response text and exception messages must never enter logs.
                var code = ex switch
                {
                    DownloadFault fault => fault.Code,
                    OperationCanceledException => "interrupted",
                    CryptographicException => "source-secret-unavailable",
                    IOException => "storage-or-connection-error",
                    _ => "source-unavailable",
                };
                logger.LogWarning("Download {DownloadId} stopped with {ErrorCode}", id, code);
                if (isImport)
                    await importer.Interrupted(id, code);
                else
                    await Interrupted(id, code);
            }
            finally
            {
                control.End();
            }
        }
    }

    private async Task Transfer(string id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
        var job = await db.Downloads.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var policy = await db.DownloadPolicies.AsNoTracking().SingleAsync(ct);
        var hosts = DownloadNetwork.Hosts(
            JsonSerializer.Deserialize<string[]>(policy.HostsJson) ?? []
        );
        if (!await Enabled(db, ct) || !await DownloadAccess.Allowed(db, job, ct))
            throw new DownloadFault("request-access-revoked");
        if (storage.Exists(id, true))
        {
            using var existing = storage.OpenRead(id);
            var hash = Convert
                .ToHexString(await SHA256.HashDataAsync(existing, ct))
                .ToLowerInvariant();
            if (existing.Length != job.ReceivedBytes || hash != job.ContentHash)
                throw new DownloadFault("staged-file-changed");
            await db
                .Downloads.Where(x => x.Id == id && x.Desired == "run")
                .ExecuteUpdateAsync(
                    s =>
                        s.SetProperty(x => x.State, "completed")
                            .SetProperty(
                                x => x.ImportState,
                                x =>
                                    x.TargetLibraryId != null
                                    && x.ImportRelativePath != null
                                    && x.ImportState == "waiting"
                                        ? "queued"
                                        : x.ImportState
                            )
                            .SetProperty(x => x.ProtectedUrl, "")
                            .SetProperty(x => x.Error, (string?)null),
                    ct
                );
            return;
        }
        using var client = transport.Create(hosts);
        await using (var file = storage.OpenWrite(id))
        {
            var offset = file.Length;
            if (offset > job.MaxBytes || (job.TotalBytes != null && offset > job.TotalBytes))
                throw new DownloadFault("partial-file-changed");
            await db
                .Downloads.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReceivedBytes, offset), ct);
            if (offset == 0 || job.TotalBytes != offset)
            {
                if (
                    offset > 0
                    && (
                        job.ETag == null
                        || !EntityTagHeaderValue.TryParse(job.ETag, out var tag)
                        || tag.IsWeak
                    )
                )
                    throw new DownloadFault("safe-resume-unavailable");
                var result = await Response(
                    client,
                    DownloadNetwork.Url(secrets.Unprotect(job.ProtectedUrl), hosts),
                    hosts,
                    offset,
                    job.ETag,
                    ct
                );
                using var response = result.Response;
                if (offset > 0 && job.SourceIdentity != result.Identity)
                    throw new DownloadFault("redirect-target-changed");
                if (
                    response.Content.Headers.ContentEncoding.Any(x =>
                        !x.Equals("identity", StringComparison.OrdinalIgnoreCase)
                    )
                )
                    throw new DownloadFault("encoded-content-unsupported");
                var etag = response.Headers.ETag is { IsWeak: false } strong
                    ? strong.ToString()
                    : null;
                long? total;
                if (offset > 0)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (
                        response.StatusCode != HttpStatusCode.PartialContent
                        || range?.Unit != "bytes"
                        || range.From != offset
                        || range.To == null
                        || range.Length == null
                        || range.To != range.Length - 1
                        || (
                            response.Content.Headers.ContentLength != null
                            && response.Content.Headers.ContentLength != range.Length - offset
                        )
                        || etag != job.ETag
                        || (job.TotalBytes != null && range.Length != job.TotalBytes)
                    )
                        throw new DownloadFault("source-changed-or-range-ignored");
                    total = range.Length;
                }
                else
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new DownloadFault("source-http-error");
                    total = response.Content.Headers.ContentLength;
                }
                if (total > job.MaxBytes || total < 0)
                    throw new DownloadFault("source-exceeds-budget");
                await db
                    .Downloads.Where(x => x.Id == id)
                    .ExecuteUpdateAsync(
                        s =>
                            s.SetProperty(x => x.TotalBytes, total)
                                .SetProperty(x => x.ETag, etag)
                                .SetProperty(x => x.SourceIdentity, result.Identity),
                        ct
                    );
                file.Position = offset;
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[65536];
                var lastCheck = DateTime.MinValue;
                long lastPersist = offset;
                while (true)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stall.CancelAfter(TimeSpan.FromSeconds(30));
                    var count = await body.ReadAsync(buffer, stall.Token);
                    if (count == 0)
                        break;
                    if (
                        file.Position > job.MaxBytes - count
                        || (total != null && file.Position + count > total)
                    )
                        throw new DownloadFault("source-exceeds-budget");
                    if (
                        file.Position - lastPersist >= 1024 * 1024
                        || DateTime.UtcNow - lastCheck > TimeSpan.FromSeconds(1)
                    )
                    {
                        await Check(db, job, file.Position, ct);
                        lastPersist = file.Position;
                        lastCheck = DateTime.UtcNow;
                    }
                    await file.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                await file.FlushAsync(ct);
                if (total != null && file.Position != total)
                    throw new DownloadFault("source-truncated");
            }
            await db
                .Downloads.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TotalBytes, (long?)file.Length), ct);
            await Check(db, job, file.Length, ct);
            file.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
            if (job.ExpectedHash != null && hash != job.ExpectedHash)
                throw new DownloadFault("content-hash-mismatch");
            await control.Gate.WaitAsync(ct);
            try
            {
                if (
                    await db
                        .Downloads.Where(x => x.Id == id && x.Desired == "run")
                        .ExecuteUpdateAsync(
                            s =>
                                s.SetProperty(x => x.State, "finalizing")
                                    .SetProperty(x => x.ContentHash, hash)
                                    .SetProperty(x => x.TotalBytes, (long?)file.Length)
                                    .SetProperty(x => x.ReceivedBytes, file.Length),
                            ct
                        ) == 0
                )
                    throw new OperationCanceledException(ct);
            }
            finally
            {
                control.Gate.Release();
            }
        }
        // Once the transfer is closed, finish only local atomic staging; there is no network activity.
        storage.Complete(id);
        await db
            .Downloads.Where(x => x.Id == id && x.State == "finalizing")
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(x => x.State, "completed")
                        .SetProperty(
                            x => x.ImportState,
                            x =>
                                x.TargetLibraryId != null
                                && x.ImportRelativePath != null
                                && x.ImportState == "waiting"
                                    ? "queued"
                                    : x.ImportState
                        )
                        .SetProperty(x => x.ProtectedUrl, "")
                        .SetProperty(x => x.Error, (string?)null)
                        .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
                CancellationToken.None
            );
    }

    private async Task Check(MediaDb db, DownloadJob job, long received, CancellationToken ct)
    {
        await control.Gate.WaitAsync(ct);
        try
        {
            await db
                .Downloads.Where(x => x.Id == job.Id)
                .ExecuteUpdateAsync(
                    s =>
                        s.SetProperty(x => x.ReceivedBytes, received)
                            .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
                    ct
                );
            var desired = await db
                .Downloads.Where(x => x.Id == job.Id)
                .Select(x => x.Desired)
                .SingleAsync(ct);
            if (!await Enabled(db, ct) || desired != "run")
                throw new OperationCanceledException(ct);
            var access = await db.Downloads.AsNoTracking().SingleAsync(x => x.Id == job.Id, ct);
            if (!await DownloadAccess.Allowed(db, access, ct))
                throw new DownloadFault("request-access-revoked");
            storage.Admit(await Budgets(db, ct));
        }
        finally
        {
            control.Gate.Release();
        }
    }

    private static async Task<(HttpResponseMessage Response, string Identity)> Response(
        HttpClient client,
        Uri uri,
        string[] hosts,
        long offset,
        string? etag,
        CancellationToken ct
    )
    {
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            DownloadNetwork.Url(uri.AbsoluteUri, hosts);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("MediaServer-DownloadCenter/0.1");
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                request.Headers.IfRange = new RangeConditionHeaderValue(
                    EntityTagHeaderValue.Parse(etag!)
                );
            }
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headers.CancelAfter(TimeSpan.FromSeconds(30));
            var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                headers.Token
            );
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null)
                    throw new DownloadFault("invalid-redirect");
                uri = new Uri(uri, location);
                continue;
            }
            return (
                response,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))
            );
        }
        throw new DownloadFault("too-many-redirects");
    }

    private async Task Interrupted(string id, string code)
    {
        await control.Gate.WaitAsync();
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            var row = await db.Downloads.SingleOrDefaultAsync(x => x.Id == id);
            if (row == null || row.State == "completed")
                return;
            try
            {
                if (row.Desired == "cancel")
                {
                    storage.Discard(id);
                    row.State = "cancelled";
                    row.ImportState = "discarded";
                    row.ProtectedUrl = "";
                    row.ReceivedBytes = 0;
                    row.TotalBytes = null;
                    row.ContentHash = null;
                }
                else
                {
                    if (!storage.Exists(id, true))
                        row.ReceivedBytes = storage.PartialLength(id);
                    if (row.Desired == "pause" || !await Enabled(db) || code == "interrupted")
                    {
                        row.State = "paused";
                        row.Desired = "pause";
                    }
                    else
                        row.State = "failed";
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or ArgumentException)
            {
                row.State = "failed";
                row.Desired = "pause";
                code = "staging-cleanup-required";
            }
            row.Error = code;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception)
        {
            logger.LogWarning("Download {DownloadId} needs recovery", id);
        }
        finally
        {
            control.Gate.Release();
        }
    }
}
