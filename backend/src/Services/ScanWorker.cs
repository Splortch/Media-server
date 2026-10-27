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
                policy.ValidateDirectory(library.Path);
                var probe = scope.ServiceProvider.GetRequiredService<IMediaProbe>();
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = false,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (var path in Directory.EnumerateFiles(library.Path, "*", options))
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
                        var metadata = await probe.ProbeAsync(path, stoppingToken);
                        var item = await db.Items.SingleOrDefaultAsync(
                            x => x.LibraryId == library.Id && x.RelativePath == relative,
                            stoppingToken
                        );
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
                        logger.LogWarning(
                            "Scan job {JobId}: a file could not be identified ({ErrorType})",
                            id,
                            ex.GetType().Name
                        );
                    }
                }
                job.State = "completed";
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                job.State = "failed";
                logger.LogError("Scan job {JobId} failed ({ErrorType})", id, ex.GetType().Name);
            }
            await db.SaveChangesAsync(stoppingToken);
            await events.PublishAsync(job, stoppingToken);
        }
    }
}
