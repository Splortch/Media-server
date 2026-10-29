using System.Diagnostics;
using MediaServer.Infrastructure;
using MediaServer.Services;
using Microsoft.EntityFrameworkCore;

public record ResourceSnapshot(
    double? ProcessCpuPercent,
    long ProcessMemoryBytes,
    long ManagedMemoryBytes,
    double UptimeSeconds,
    long? DataDiskAvailableBytes,
    long? DataDiskTotalBytes,
    string GpuStatus
);

public sealed class ResourceMonitor(string dataDirectory)
{
    private readonly Process process = Process.GetCurrentProcess();
    private readonly Stopwatch uptime = Stopwatch.StartNew();
    private readonly object gate = new();
    private TimeSpan cpu;
    private double seconds;
    private double? usage;

    public ResourceSnapshot Read()
    {
        lock (gate)
        {
            process.Refresh();
            var now = uptime.Elapsed.TotalSeconds;
            var used = process.TotalProcessorTime;
            if (now - seconds >= 1)
            {
                usage =
                    seconds == 0
                        ? null
                        : Math.Clamp(
                            (used - cpu).TotalSeconds
                                / (now - seconds)
                                / Environment.ProcessorCount
                                * 100,
                            0,
                            100
                        );
                seconds = now;
                cpu = used;
            }
            long? available = null,
                total = null;
            try
            {
                var drive = new DriveInfo(
                    OperatingSystem.IsWindows() ? Path.GetPathRoot(dataDirectory)! : dataDirectory
                );
                available = drive.AvailableFreeSpace;
                total = drive.TotalSize;
            }
            catch (Exception ex)
                when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            return new ResourceSnapshot(
                usage,
                process.WorkingSet64,
                GC.GetTotalMemory(false),
                now,
                available,
                total,
                "See optional GPU sample; AMD/Intel utilization is unavailable"
            );
        }
    }
}

public static partial class Operations
{
    private static void MapMonitoring(RouteGroupBuilder api)
    {
        api.MapGet(
                "/monitoring",
                async (
                    ResourceMonitor resources,
                    StreamRegistry streams,
                    PlaybackSessions playback,
                    GpuMetrics gpu,
                    MediaDb db,
                    CancellationToken ct
                ) =>
                {
                    var conversion = playback.Snapshot();
                    return Results.Ok(
                        new
                        {
                            resources = resources.Read(),
                            activeHttpStreams = streams.Snapshot(),
                            queuedScans = await db.Jobs.CountAsync(x => x.State == "queued"),
                            runningScans = await db.Jobs.CountAsync(x => x.State == "running"),
                            gpu = await gpu.ReadAsync(ct),
                            transcodingSupported = true,
                            transcodingFramesPerSecond = conversion
                                .Jobs.Where(x =>
                                    x.State == "preparing"
                                    && x.Phase == "encoding"
                                    && x.Progress?.FramesPerSecond is not null
                                )
                                .Select(x => x.Progress!.FramesPerSecond)
                                .DefaultIfEmpty(null)
                                .Aggregate(
                                    (double?)null,
                                    (sum, fps) => fps is null ? sum : (sum ?? 0) + fps
                                ),
                            // Admission rejects excess jobs; there is no unbounded waiting queue.
                            transcodingQueueCount = 0,
                            transcoding = conversion,
                        }
                    );
                }
            )
            .RequireAuthorization("admin");
    }
}
