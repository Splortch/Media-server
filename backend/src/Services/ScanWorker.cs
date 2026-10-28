using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaServer.Services;

public sealed class ScanQueue
{
    public Channel<int> Channel { get; } =
        System.Threading.Channels.Channel.CreateBounded<int>(
            new BoundedChannelOptions(32)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            }
        );
}

public sealed class ScanWorker(
    IServiceScopeFactory scopes,
    ScanQueue queue,
    IScanEvents events,
    ILogger<ScanWorker> logger
) : BackgroundService
{
    private async Task RecordIssue(
        MediaDb db,
        ScanJob job,
        string name,
        Exception error,
        CancellationToken ct
    )
    {
        var correlation = Guid.NewGuid().ToString("N");
        var code = error switch
        {
            MediaProbeException probe => probe.Code,
            OperationCanceledException => "probe_timeout",
            JsonException => "invalid_media",
            UnauthorizedAccessException => "access_denied",
            FileNotFoundException or DirectoryNotFoundException => "file_missing",
            ArgumentException => "unsafe_path",
            _ => "probe_failed",
        };
        // Names only, no directory paths, exception messages, or raw probe output.
        var safeName = new string(name.Where(c => !char.IsControl(c)).Take(128).ToArray());
        if (await db.ScanIssues.CountAsync(x => x.ScanJobId == job.Id, ct) < 100)
            db.ScanIssues.Add(
                new ScanIssue
                {
                    ScanJobId = job.Id,
                    FileName = safeName,
                    Code = code,
                    CorrelationId = correlation,
                }
            );
        logger.LogWarning(
            "Scan job {JobId}: {Code}; reference {CorrelationId}",
            job.Id,
            code,
            correlation
        );
        await db.SaveChangesAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var startup = scopes.CreateScope())
        {
            var db = startup.ServiceProvider.GetRequiredService<MediaDb>();
            await db
                .Jobs.Where(x => x.State == "queued" || x.State == "running")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "interrupted"), stoppingToken);
        }
        await foreach (var id in queue.Channel.Reader.ReadAllAsync(stoppingToken))
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            var job = await db.Jobs.FindAsync([id], stoppingToken);
            if (job is null)
                continue;
            try
            {
                job.State = "running";
                await db.SaveChangesAsync(stoppingToken);
                await events.PublishAsync(job, stoppingToken);
                var library = await db.Libraries.SingleAsync(
                    x => x.Id == job.LibraryId,
                    stoppingToken
                );
                var policy = scope.ServiceProvider.GetRequiredService<PathPolicy>();
                if (!Directory.Exists(library.Path))
                    throw new DirectoryNotFoundException();
                policy.ValidateDirectory(library.Path);
                var probe = scope.ServiceProvider.GetRequiredService<IMediaProbe>();
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = false,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                var files = Directory
                    .EnumerateFiles(library.Path, "*", options)
                    .Where(path =>
                        new[] { ".mp4", ".m4v", ".webm", ".mkv", ".mov", ".avi" }.Contains(
                            Path.GetExtension(path).ToLowerInvariant()
                        )
                    )
                    .ToArray();
                var seen = files
                    .Select(path => Path.GetRelativePath(library.Path, path))
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var path in files)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    if (
                        !new[] { ".mp4", ".m4v", ".webm", ".mkv", ".mov", ".avi" }.Contains(
                            Path.GetExtension(path).ToLowerInvariant()
                        )
                    )
                        continue;
                    try
                    {
                        var relative = Path.GetRelativePath(library.Path, path);
                        policy.Resolve(library.Path, relative);
                        var fingerprint = "";
                        if (library.ComputeHashes)
                        {
                            using var stream = policy.OpenRead(library.Path, relative);
                            fingerprint = Convert.ToHexString(
                                await SHA256.HashDataAsync(stream, stoppingToken)
                            );
                        }
                        var metadata = await probe.ProbeAsync(path, stoppingToken);
                        var item = await db
                            .Items.IgnoreQueryFilters()
                            .SingleOrDefaultAsync(
                                x => x.LibraryId == library.Id && x.RelativePath == relative,
                                stoppingToken
                            );
                        if (item is null && fingerprint != "")
                        {
                            var moved = await db
                                .Items.IgnoreQueryFilters()
                                .Where(x =>
                                    x.LibraryId == library.Id
                                    && x.Fingerprint == fingerprint
                                    && !seen.Contains(x.RelativePath)
                                )
                                .Take(2)
                                .ToListAsync(stoppingToken);
                            if (moved.Count == 1)
                            {
                                item = moved[0];
                                item.RelativePath = relative;
                            }
                        }
                        if (item is null)
                        {
                            item = new MediaItem
                            {
                                LibraryId = library.Id,
                                RelativePath = relative,
                            };
                            db.Items.Add(item);
                        }
                        item.TracksJson = System.Text.Json.JsonSerializer.Serialize(
                            metadata.Tracks ?? []
                        );
                        item.Title = Path.GetFileNameWithoutExtension(path);
                        item.SearchTitle = item.Title.ToLowerInvariant();
                        item.IsMissing = false;
                        item.Fingerprint = fingerprint;
                        item.ModifiedTicks = File.GetLastWriteTimeUtc(path).Ticks;
                        item.Duration = metadata.Duration;
                        item.Container = metadata.Container;
                        item.VideoCodec = metadata.VideoCodec;
                        item.AudioCodec = metadata.AudioCodec;
                        item.Size = new FileInfo(path).Length;
                        job.Discovered++;
                        await db.SaveChangesAsync(stoppingToken);
                    }
                    catch (Exception ex)
                        when (ex
                                is IOException
                                    or ArgumentException
                                    or System.Text.Json.JsonException
                                    or OperationCanceledException
                            && !stoppingToken.IsCancellationRequested
                        )
                    {
                        job.Failed++;
                        await RecordIssue(db, job, Path.GetFileName(path), ex, stoppingToken);
                    }
                }
                // Only a complete traversal may tombstone missing entries. Never delete original files or history.
                await db
                    .Items.IgnoreQueryFilters()
                    .Where(x => x.LibraryId == library.Id && !seen.Contains(x.RelativePath))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsMissing, true), stoppingToken);
                job.State = "completed";
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                job.State = "failed";
                await RecordIssue(db, job, "(library)", ex, stoppingToken);
            }
            await db.SaveChangesAsync(stoppingToken);
            await events.PublishAsync(job, stoppingToken);
        }
    }
}
