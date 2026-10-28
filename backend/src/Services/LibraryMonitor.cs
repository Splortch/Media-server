using System.Collections.Concurrent;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaServer.Services;

public sealed class ScanScheduler(IServiceScopeFactory scopes, ScanQueue queue)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<ScanJob?> EnqueueAsync(int libraryId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            if (!await db.Libraries.AnyAsync(x => x.Id == libraryId, ct))
                return null;
            if (
                await db.Jobs.AnyAsync(
                    x => x.LibraryId == libraryId && (x.State == "queued" || x.State == "running"),
                    ct
                )
            )
                return null;
            var job = new ScanJob { LibraryId = libraryId };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct);
            if (!queue.Channel.Writer.TryWrite(job.Id))
            {
                job.State = "rejected";
                await db.SaveChangesAsync(ct);
            }
            return job;
        }
        finally
        {
            gate.Release();
        }
    }
}

public sealed class LibraryMonitor(
    IServiceScopeFactory scopes,
    ScanScheduler scheduler,
    ILogger<LibraryMonitor> logger
) : BackgroundService
{
    private readonly ConcurrentDictionary<int, DateTime> dirty = new();
    private readonly ConcurrentDictionary<int, bool> faulted = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var watchers = new Dictionary<int, FileSystemWatcher>();
        var reconciled = new Dictionary<int, DateTime>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
                    var libraries = await db
                        .Libraries.Where(x => x.WatchEnabled)
                        .ToListAsync(stoppingToken);
                    foreach (
                        var id in watchers
                            .Keys.Where(id => !libraries.Any(l => l.Id == id))
                            .ToArray()
                    )
                    {
                        watchers[id].Dispose();
                        watchers.Remove(id);
                        dirty.TryRemove(id, out _);
                        reconciled.Remove(id);
                    }
                    foreach (var library in libraries)
                    {
                        var id = library.Id;
                        if (
                            faulted.TryRemove(id, out _)
                            && watchers.Remove(id, out var failedWatcher)
                        )
                            failedWatcher.Dispose();
                        var now = DateTime.UtcNow;
                        if (!reconciled.ContainsKey(id))
                        {
                            reconciled[id] = now;
                            dirty.TryAdd(id, now);
                        }
                        if (!watchers.ContainsKey(id))
                        {
                            try
                            {
                                scope
                                    .ServiceProvider.GetRequiredService<PathPolicy>()
                                    .ValidateDirectory(library.Path);
                                var watcher = new FileSystemWatcher(library.Path)
                                {
                                    IncludeSubdirectories = true,
                                    NotifyFilter =
                                        NotifyFilters.FileName
                                        | NotifyFilters.DirectoryName
                                        | NotifyFilters.Size
                                        | NotifyFilters.LastWrite,
                                };
                                FileSystemEventHandler changed = (_, _) =>
                                    dirty[id] = DateTime.UtcNow;
                                watcher.Changed += changed;
                                watcher.Created += changed;
                                watcher.Deleted += changed;
                                watcher.Renamed += (_, _) => dirty[id] = DateTime.UtcNow;
                                watcher.Error += (_, _) =>
                                {
                                    dirty[id] = DateTime.UtcNow;
                                    faulted[id] = true;
                                    logger.LogWarning(
                                        "Library {LibraryId} watcher requires reconciliation",
                                        id
                                    );
                                };
                                watcher.EnableRaisingEvents = true;
                                watchers[id] = watcher;
                            }
                            catch (Exception ex)
                                when (ex
                                        is IOException
                                            or UnauthorizedAccessException
                                            or ArgumentException
                                ) { }
                        }
                        if (now - reconciled[id] >= TimeSpan.FromMinutes(10))
                            dirty.TryAdd(id, now.AddSeconds(-3));
                        if (
                            dirty.TryGetValue(id, out var changedAt)
                            && now - changedAt >= TimeSpan.FromSeconds(2)
                        )
                        {
                            var job = await scheduler.EnqueueAsync(id, stoppingToken);
                            if (job is { State: "queued" })
                            {
                                // Do not erase events arriving during queue insertion.
                                ((ICollection<KeyValuePair<int, DateTime>>)dirty).Remove(
                                    new(id, changedAt)
                                );
                                reconciled[id] = now;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Library monitor retry ({ErrorType})", ex.GetType().Name);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        finally
        {
            foreach (var watcher in watchers.Values)
                watcher.Dispose();
        }
    }
}
