using MediaServer.Core;
using MediaServer.Media;

namespace MediaServer.Downloads;

public interface IDownloadSpace
{
    long Available(string directory);
}

public sealed class DownloadSpace : IDownloadSpace
{
    public long Available(string directory) => new DriveInfo(directory).AvailableFreeSpace;
}

public sealed class DownloadFault(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class DownloadStorage(string data, IDownloadSpace space)
{
    public const long Margin = 512L * 1024 * 1024,
        TotalLimit = 100L * 1024 * 1024 * 1024;
    public string Root { get; } = Path.Combine(Path.GetFullPath(data), "downloads");
    private PathPolicy Policy => new([Root]);

    private static string Name(string id, bool complete)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Invalid download identifier.");
        return id + (complete ? ".download" : ".part");
    }

    public void Ensure()
    {
        Directory.CreateDirectory(Root);
        PathPolicy.RejectLinks(Root);
    }

    public long Free() =>
        space.Available(Directory.Exists(Root) ? Root : Path.GetDirectoryName(Root)!);

    public bool Exists(string id, bool complete) =>
        File.Exists(Path.Combine(Root, Name(id, complete)));

    public FileStream OpenWrite(string id)
    {
        Ensure();
        return Policy.OpenWrite(Root, Name(id, false), !Exists(id, false));
    }

    public FileStream OpenRead(string id) => Policy.OpenRead(Root, Name(id, true));

    public long PartialLength(string id)
    {
        if (!Exists(id, false))
            return 0;
        using var f = Policy.OpenRead(Root, Name(id, false));
        return f.Length;
    }

    public void Complete(string id)
    {
        var part = Policy.Resolve(Root, Name(id, false));
        PathPolicy.RejectLinks(Root);
        File.Move(part, Path.Combine(Root, Name(id, true)), false);
    }

    public void Discard(string id)
    {
        if (!Directory.Exists(Root))
            return;
        foreach (var complete in new[] { false, true })
            if (Exists(id, complete))
                File.Delete(Policy.Resolve(Root, Name(id, complete)));
    }

    public static long Remaining(DownloadJob job) =>
        Math.Max(0, (job.TotalBytes ?? job.MaxBytes) - job.ReceivedBytes);

    private long ExternalRetained(IEnumerable<DownloadJob> jobs)
    {
        var root = Path.Combine(Path.GetDirectoryName(Root)!, "external-downloads");
        if (!Directory.Exists(root))
            return 0;
        PathPolicy.RejectLinks(root);
        var reserved = jobs.Where(x =>
                x.TransferClient == "aria2" && x.State is "queued" or "running" or "paused"
            )
            .Select(x => x.Id)
            .ToHashSet(StringComparer.Ordinal);
        long total = 0;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _))
                throw new DownloadFault("external-staging-cleanup-required");
            PathPolicy.RejectLinks(directory);
            if (reserved.Contains(id) || !File.Exists(Path.Combine(directory, "payload")))
                continue;
            using var file = new PathPolicy([root]).OpenRead(directory, "payload");
            total = checked(total + file.Length);
        }
        return total;
    }

    public void Admit(IEnumerable<DownloadJob> jobs)
    {
        var rows = jobs.ToArray();
        var reserved = rows.Where(x => x.State is "queued" or "running" or "paused").Sum(Remaining);
        var importReserved = rows.Sum(DownloadAccess.ImportRemaining);
        reserved += rows.Where(x =>
                x.TransferClient == "aria2" && x.State is "queued" or "running" or "paused"
            )
            .Sum(x => x.MaxBytes);
        if (
            Free() - Margin < reserved + importReserved
            || rows.Sum(x => x.ReceivedBytes) + reserved + ExternalRetained(rows) > TotalLimit
        )
            throw new DownloadFault("insufficient-storage");
    }
}

public sealed class DownloadControl
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly object sync = new();
    private string? active;
    private CancellationTokenSource? cancellation;
    private TaskCompletionSource? stopped;

    public bool IsActive(string id)
    {
        lock (sync)
            return active == id;
    }

    public void Signal()
    {
        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException) { }
    }

    public Task<bool> Wait(CancellationToken ct) => wake.WaitAsync(TimeSpan.FromSeconds(2), ct);

    public CancellationToken Begin(string id, CancellationToken ct)
    {
        lock (sync)
        {
            active = id;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cancellation.CancelAfter(TimeSpan.FromHours(6));
            stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return cancellation.Token;
        }
    }

    public void Cancel(string? id = null)
    {
        lock (sync)
            if (active != null && (id == null || id == active))
                cancellation?.Cancel();
        Signal();
    }

    public void End()
    {
        lock (sync)
        {
            active = null;
            cancellation?.Dispose();
            cancellation = null;
            stopped?.TrySetResult();
        }
    }

    public Task Idle(CancellationToken ct)
    {
        lock (sync)
            return (active == null ? Task.CompletedTask : stopped!.Task).WaitAsync(
                TimeSpan.FromSeconds(15),
                ct
            );
    }
}
