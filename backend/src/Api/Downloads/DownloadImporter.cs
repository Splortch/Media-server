using System.Security.Cryptography;
using System.Text;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using MediaServer.Services;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Downloads;

public sealed class DownloadImporter(
    IServiceScopeFactory scopes,
    DownloadControl control,
    DownloadStorage storage,
    IDownloadSpace space,
    ScanScheduler scheduler
)
{
    public static bool Busy(string state) =>
        state is "waiting" or "queued" or "copying" or "scanning";

    private static string Folder(DownloadJob job) =>
        Path.GetDirectoryName(job.ImportRelativePath!)!;

    private static string Temporary(DownloadJob job) =>
        Path.Combine(Folder(job), "payload.ingesting");

    private static string Marker(DownloadJob job) =>
        Path.Combine(Folder(job), ".mediaserver-owner");

    private static string Proof(DownloadJob job) => job.Id + ":" + job.ImportNonce;

    private static string Location(PathPolicy paths, DownloadJob job, string relative)
    {
        paths.ValidateDirectory(job.TargetRoot!);
        if (Path.IsPathFullyQualified(relative))
            throw new ArgumentException("Relative import paths required.");
        var full = Path.GetFullPath(Path.Combine(job.TargetRoot!, relative));
        if (!PathPolicy.Within(job.TargetRoot!, full))
            throw new ArgumentException("Import path escapes its library.");
        PathPolicy.RejectLinks(
            File.Exists(full) || Directory.Exists(full) ? full : Path.GetDirectoryName(full)!
        );
        return full;
    }

    public static string ManagedFolder(PathPolicy paths, DownloadJob job, bool create)
    {
        paths.ValidateDirectory(job.TargetRoot!);
        if (
            !Guid.TryParseExact(job.Id, "N", out _)
            || job.ImportRelativePath == null
            || Path.IsPathFullyQualified(job.ImportRelativePath)
            || !job.ImportRelativePath.StartsWith(
                "MediaServer Imports" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal
            )
        )
            throw new ArgumentException("Invalid managed import layout.");
        var full = Path.GetFullPath(Path.Combine(job.TargetRoot!, Folder(job)));
        if (!PathPolicy.Within(job.TargetRoot!, full))
            throw new ArgumentException("Import folder escapes its library.");
        var current = job.TargetRoot!;
        foreach (
            var segment in Path.GetRelativePath(job.TargetRoot!, full)
                .Split(Path.DirectorySeparatorChar)
        )
        {
            current = Path.Combine(current, segment);
            var exists = true;
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Linked import directories are not allowed.");
                if ((attributes & FileAttributes.Directory) == 0)
                    throw new IOException("Import folder collides with an existing file.");
            }
            catch (FileNotFoundException)
            {
                exists = false;
            }
            catch (DirectoryNotFoundException)
            {
                exists = false;
            }
            if (!exists && create)
            {
                Directory.CreateDirectory(current);
                PathPolicy.RejectLinks(current);
            }
        }
        return full;
    }

    private static bool Owns(PathPolicy paths, DownloadJob job)
    {
        var marker = Location(paths, job, Marker(job));
        if (!File.Exists(marker))
            return false;
        using var file = paths.OpenRead(job.TargetRoot!, Marker(job));
        if (file.Length > 256)
            return false;
        using var reader = new StreamReader(file, Encoding.UTF8);
        return reader.ReadToEnd() == Proof(job);
    }

    private async Task Check(MediaDb db, DownloadJob job, long copied, CancellationToken ct)
    {
        await control.Gate.WaitAsync(ct);
        try
        {
            var current = await db.Downloads.AsNoTracking().SingleAsync(x => x.Id == job.Id, ct);
            if (
                !await DownloadWorker.Enabled(db, ct)
                || current.ImportState is not ("copying" or "scanning")
            )
                throw new OperationCanceledException(ct);
            if (!await DownloadAccess.Allowed(db, current, ct))
                throw new DownloadFault("request-access-revoked");
            await db
                .Downloads.Where(x => x.Id == job.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ImportCopiedBytes, copied), ct);
            var budgets = await DownloadWorker.Budgets(db, ct);
            storage.Admit(budgets);
            var reserved =
                budgets.Sum(DownloadAccess.ImportRemaining)
                + budgets
                    .Where(x => x.State is "queued" or "running" or "paused")
                    .Sum(DownloadStorage.Remaining);
            if (space.Available(job.TargetRoot!) - DownloadStorage.Margin < reserved)
                throw new DownloadFault("destination-storage-insufficient");
        }
        finally
        {
            control.Gate.Release();
        }
    }

    private static async Task HashMatches(
        PathPolicy paths,
        DownloadJob job,
        string relative,
        CancellationToken ct
    )
    {
        using var file = paths.OpenRead(job.TargetRoot!, relative);
        if (
            file.Length != job.TotalBytes
            || Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant()
                != job.ContentHash
        )
            throw new DownloadFault("import-content-changed");
    }

    public async Task Run(string id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
        var paths = scope.ServiceProvider.GetRequiredService<PathPolicy>();
        var job = await db.Downloads.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        if (
            job.TargetRoot == null
            || job.ImportRelativePath == null
            || job.ImportNonce == null
            || job.ContentHash == null
            || job.TotalBytes == null
        )
            throw new DownloadFault("import-match-required");
        paths.ValidateDirectory(job.TargetRoot);
        await Check(db, job, job.ImportCopiedBytes, ct);
        var folder = ManagedFolder(paths, job, false);
        if (!Directory.Exists(folder))
        {
            ManagedFolder(paths, job, true);
            using var marker = paths.OpenWrite(job.TargetRoot, Marker(job), true);
            marker.Write(Encoding.UTF8.GetBytes(Proof(job)));
            marker.Flush(true);
        }
        if (!Owns(paths, job))
            throw new DownloadFault("import-folder-collision");
        var final = Location(paths, job, job.ImportRelativePath);
        var temp = Location(paths, job, Temporary(job));
        if (!File.Exists(final))
        {
            // Only our claimed temporary file may be restarted. The original staged source is retained.
            if (File.Exists(temp))
                File.Delete(temp);
            await db
                .Downloads.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ImportCopiedBytes, 0), ct);
            await Check(db, job, 0, ct);
            await using (var source = storage.OpenRead(id))
            await using (var destination = paths.OpenWrite(job.TargetRoot, Temporary(job), true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536];
                long checkedAt = 0;
                var lastCheck = DateTime.MinValue;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, ct);
                    if (count == 0)
                        break;
                    if (destination.Position > job.TotalBytes - count)
                        throw new DownloadFault("import-content-changed");
                    if (
                        destination.Position - checkedAt >= 1024 * 1024
                        || DateTime.UtcNow - lastCheck > TimeSpan.FromSeconds(1)
                    )
                    {
                        await Check(db, job, destination.Position, ct);
                        checkedAt = destination.Position;
                        lastCheck = DateTime.UtcNow;
                    }
                    hash.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                await destination.FlushAsync(ct);
                destination.Flush(true);
                if (
                    destination.Length != job.TotalBytes
                    || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()
                        != job.ContentHash
                )
                    throw new DownloadFault("import-content-changed");
            }
            // Probe before exposing even the final filename to the normal scanner.
            var probe = await scope
                .ServiceProvider.GetRequiredService<IMediaProbe>()
                .ProbeImportAsync(temp, ct);
            if (
                !double.IsFinite(probe.Duration)
                || probe.Duration <= 0
                || string.IsNullOrWhiteSpace(probe.VideoCodec)
            )
                throw new DownloadFault("import-not-video");
            if (!ContainerMatches(job.ImportExtension!, probe.Container))
                throw new DownloadFault("import-container-mismatch");
            await Check(db, job, job.TotalBytes.Value, ct);
            await PrepareCatalog(db, job, ct);
            Location(paths, job, Temporary(job));
            PathPolicy.RejectLinks(folder);
            File.Move(temp, final, false);
        }
        else
        {
            await HashMatches(paths, job, job.ImportRelativePath, ct);
            await PrepareCatalog(db, job, ct);
        }
        await db
            .Downloads.Where(x => x.Id == id)
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(x => x.ImportState, "scanning")
                        .SetProperty(x => x.ImportCopiedBytes, job.TotalBytes.Value),
                ct
            );
        await Check(db, job, job.TotalBytes.Value, ct);
        ScanJob? scan = null;
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (scan == null)
                scan = await scheduler.EnqueueAsync(job.TargetLibraryId!.Value, ct);
            if (scan != null)
            {
                var state = await db
                    .Jobs.AsNoTracking()
                    .Where(x => x.Id == scan.Id)
                    .Select(x => x.State)
                    .SingleOrDefaultAsync(ct);
                if (state == "completed")
                    break;
                if (state is "failed" or "interrupted" or "rejected" || state == null)
                    throw new DownloadFault("import-scan-failed");
            }
            await Check(db, job, job.TotalBytes.Value, ct);
            await Task.Delay(250, ct);
        }
        if (
            scan == null
            || await db.Jobs.Where(x => x.Id == scan.Id).Select(x => x.State).SingleAsync(ct)
                != "completed"
        )
            throw new DownloadFault("import-scan-timeout");
        await Publish(db, job, scan.Id, ct);
    }

    public static bool ContainerMatches(string extension, string container) =>
        extension switch
        {
            ".webm" => container.Split(',').Contains("webm"),
            ".mkv" => container.Split(',').Contains("matroska"),
            ".mp4" or ".m4v" or ".mov" => container.Split(',').Any(x => x is "mp4" or "mov"),
            ".avi" => container.Split(',').Contains("avi"),
            _ => false,
        };

    private async Task PrepareCatalog(MediaDb db, DownloadJob job, CancellationToken ct)
    {
        await control.Gate.WaitAsync(ct);
        try
        {
            if (!await DownloadAccess.Allowed(db, job, ct))
                throw new DownloadFault("request-access-revoked");
            var item = await db
                .Items.IgnoreQueryFilters()
                .SingleOrDefaultAsync(
                    x =>
                        x.LibraryId == job.TargetLibraryId
                        && x.RelativePath == job.ImportRelativePath,
                    ct
                );
            if (item != null && item.ImportJobId != job.Id)
                throw new DownloadFault("import-catalog-collision");
            if (item == null)
            {
                db.Items.Add(
                    new MediaItem
                    {
                        LibraryId = job.TargetLibraryId!.Value,
                        RelativePath = job.ImportRelativePath!,
                        PendingImport = true,
                        IsMissing = true,
                        ImportJobId = job.Id,
                        Title = job.ImportTitle!,
                        SearchTitle = job.ImportTitle!.ToLowerInvariant(),
                        ContentRating = job.ImportRating,
                    }
                );
                await db.SaveChangesAsync(ct);
            }
        }
        finally
        {
            control.Gate.Release();
        }
    }

    private async Task Publish(MediaDb db, DownloadJob job, int scanId, CancellationToken ct)
    {
        await control.Gate.WaitAsync(ct);
        try
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var current = await db.Downloads.SingleAsync(x => x.Id == job.Id, ct);
            if (
                current.ImportState != "scanning"
                || !await DownloadWorker.Enabled(db, ct)
                || !await DownloadAccess.Allowed(db, current, ct)
            )
                throw new DownloadFault("request-access-revoked");
            var item = await db
                .Items.IgnoreQueryFilters()
                .SingleAsync(
                    x => x.ImportJobId == job.Id && x.LibraryId == job.TargetLibraryId,
                    ct
                );
            if (
                item.IsMissing
                || !item.PendingImport
                || item.Duration <= 0
                || item.Size != job.TotalBytes
                || string.IsNullOrWhiteSpace(item.VideoCodec)
            )
                throw new DownloadFault("import-scan-failed");
            item.PendingImport = false;
            item.ContentRating = job.ImportRating;
            if (job.RequestId is int requestId)
            {
                var request = await db.MovieRequests.SingleAsync(x => x.Id == requestId, ct);
                request.State = "Added";
                request.MediaItemId = item.Id;
                request.Reply = "Imported and available in your library.";
                request.Revision++;
                request.UpdatedAt = DateTime.UtcNow;
            }
            current.ImportState = "imported";
            current.ImportError = null;
            current.ImportScanId = scanId;
            current.ImportedMediaId = item.Id;
            current.Revision++;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            control.Gate.Release();
        }
    }

    public async Task Interrupted(string id, string code)
    {
        await control.Gate.WaitAsync();
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            await db
                .Downloads.Where(x => x.Id == id && x.ImportState != "imported")
                .ExecuteUpdateAsync(s =>
                    s.SetProperty(x => x.ImportState, "held").SetProperty(x => x.ImportError, code)
                );
        }
        finally
        {
            control.Gate.Release();
        }
    }

    public static async Task Discard(
        MediaDb db,
        PathPolicy paths,
        DownloadJob job,
        CancellationToken ct
    )
    {
        if (
            job.TargetRoot != null
            && job.ImportRelativePath != null
            && Directory.Exists(Path.Combine(job.TargetRoot, Folder(job)))
        )
        {
            if (!Owns(paths, job))
                throw new DownloadFault("import-folder-needs-review");
            var final = Location(paths, job, job.ImportRelativePath);
            if (File.Exists(final))
                await HashMatches(paths, job, job.ImportRelativePath, ct);
            foreach (var relative in new[] { Temporary(job), job.ImportRelativePath })
            {
                var path = Location(paths, job, relative);
                if (File.Exists(path))
                    File.Delete(path);
            }
            File.Delete(Location(paths, job, Marker(job)));
            // Never recursively delete folders containing unrelated content.
            if (
                !Directory
                    .EnumerateFileSystemEntries(Path.Combine(job.TargetRoot, Folder(job)))
                    .Any()
            )
                Directory.Delete(Path.Combine(job.TargetRoot, Folder(job)));
        }
        await db
            .Items.IgnoreQueryFilters()
            .Where(x => x.ImportJobId == job.Id && x.PendingImport)
            .ExecuteDeleteAsync(ct);
        job.ImportState = "discarded";
        job.ImportError = null;
        job.ImportCopiedBytes = 0;
        job.Revision++;
    }
}
