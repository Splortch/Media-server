using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Metadata;

// Generated derivatives are separate from curated artwork. One bounded generation at a time.
public sealed class PreviewImages(IConfiguration config, FfmpegRunner runner)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string Root =>
        Path.Combine(
            Path.GetFullPath(config["MediaServer:DataDirectory"] ?? "data"),
            "preview-cache"
        );

    public static double Sample(double duration, double? position)
    {
        var interval = Math.Max(10, Math.Ceiling(duration / 120));
        return position is null
            ? Math.Min(60, Math.Max(0, duration * .1))
            : Math.Min(
                Math.Max(0, duration - .1),
                Math.Floor(position.Value / interval) * interval
            );
    }

    public async Task<FileStream> Get(
        MediaItem item,
        Library library,
        PathPolicy paths,
        double? position,
        CancellationToken ct
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        await gate.WaitAsync(deadline.Token);
        string? work = null;
        try
        {
            Directory.CreateDirectory(Root);
            PathPolicy.RejectLinks(Root);
            var safe = new PathPolicy([Root]);
            foreach (var stale in Directory.EnumerateDirectories(Root, "job-*"))
            {
                if (Guid.TryParseExact(Path.GetFileName(stale)[4..], "N", out _))
                {
                    PathPolicy.RejectLinks(stale);
                    Directory.Delete(stale, true);
                }
            }
            var second = Sample(item.Duration, position);
            var key =
                Convert
                    .ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                $"v1|{item.Id}|{item.LibraryId}|{item.Size}|{item.ModifiedTicks}|{item.Fingerprint}|{item.ColorJson}|{second.ToString(CultureInfo.InvariantCulture)}"
                            )
                        )
                    )
                    .ToLowerInvariant() + ".jpg";
            if (File.Exists(Path.Combine(Root, key)))
                return safe.OpenRead(Root, key);
            using var source = paths.OpenRead(library.Path, item.RelativePath);
            work = Path.Combine(Root, "job-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string input;
            if (OperatingSystem.IsLinux())
            {
                // The parent owns this descriptor until FFmpeg has exited, including cancellation.
                input =
                    $"/proc/{Environment.ProcessId}/fd/{source.SafeFileHandle.DangerousGetHandle().ToInt32()}";
            }
            else
            {
                if (source.Length > 8L * 1024 * 1024 * 1024)
                    throw new IOException("Preview source exceeds staging quota.");
                input = Path.Combine(work, "source.bin");
                await using var staged = File.Create(input);
                var buffer = new byte[65536];
                long bytes = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    bytes += read;
                    if (bytes > 8L * 1024 * 1024 * 1024)
                        throw new IOException("Preview source exceeds staging quota.");
                    await staged.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
                }
            }
            var observed = await new Ffprobe(
                config["MediaServer:FfprobePath"] ?? "ffprobe"
            ).ProbeAsync(input, deadline.Token);
            var actualColor = System.Text.Json.JsonSerializer.Deserialize<VideoColor>(
                observed.ColorJson
            );
            var filter =
                TranscodeProfile.Filter(new Rendition("preview", 180, 0), actualColor?.Hdr == true)
                + ",scale=320:180:force_original_aspect_ratio=decrease,pad=320:180:(ow-iw)/2:(oh-ih)/2";
            await runner.RunAsync(
                [
                    "-filter_threads",
                    "1",
                    "-ss",
                    second.ToString(CultureInfo.InvariantCulture),
                    "-protocol_whitelist",
                    "file",
                    "-format_whitelist",
                    "mov,matroska,webm,avi",
                    "-i",
                    input,
                    "-map",
                    "0:v:0",
                    "-an",
                    "-sn",
                    "-frames:v",
                    "1",
                    "-vf",
                    filter,
                    "-threads",
                    "1",
                    "-q:v",
                    "3",
                    "frame.jpg",
                ],
                work,
                deadline.Token
            );
            var generated = Path.Combine(work, "frame.jpg");
            using (var image = new PathPolicy([work]).OpenRead(work, "frame.jpg"))
            {
                if (image.Length is <= 0 or > 1024 * 1024)
                    throw new IOException("Preview output exceeds quota.");
                var header = new byte[3];
                image.ReadExactly(header);
                if (!header.AsSpan().SequenceEqual(new byte[] { 255, 216, 255 }))
                    throw new IOException("Invalid preview image.");
            }
            // FIFO eviction bounds generated data independently of poster retention.
            var files = Directory
                .EnumerateFiles(Root, "*.jpg")
                .Select(x => new FileInfo(x))
                .OrderBy(x => x.LastWriteTimeUtc)
                .ToArray();
            var total = files.Sum(x => x.Length);
            var remaining = files.Length;
            foreach (var file in files)
            {
                if (
                    remaining < 4096
                    && total + new FileInfo(generated).Length <= 256L * 1024 * 1024
                )
                    break;
                try
                {
                    using var check = safe.OpenRead(Root, file.Name);
                }
                catch (IOException)
                {
                    continue;
                }
                try
                {
                    File.Delete(safe.Resolve(Root, file.Name));
                    total -= file.Length;
                    remaining--;
                }
                catch (IOException) { }
            }
            if (remaining >= 4096 || total + new FileInfo(generated).Length > 256L * 1024 * 1024)
                throw new IOException("Preview cache full.");
            File.Move(generated, Path.Combine(Root, key));
            return safe.OpenRead(Root, key);
        }
        finally
        {
            try
            {
                if (work is not null && Directory.Exists(work))
                    Directory.Delete(work, true);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}

public sealed record ArtworkUploadLimit;

public record ImageOptions(string? PosterUrl, string ThumbnailUrl);

public record ArtworkUpload(string Kind, string Data, long ExpectedRevision);

public record ArtworkChoice(string Kind, string Source, long ExpectedRevision, string? Key = null);

public record ArtworkCandidate(string Key, string Url);

public static class ImageEndpoints
{
    public static void MapImages(this RouteGroupBuilder group, string prefix)
    {
        group
            .MapGet(
                "/media/{id:int}/images",
                async (int id, MediaDb db, CancellationToken ct) =>
                {
                    if (!await db.Items.AnyAsync(x => x.Id == id, ct))
                        return Results.NotFound();
                    var row = await db.Metadata.SingleOrDefaultAsync(x => x.MediaItemId == id, ct);
                    return Results.Ok(
                        new ImageOptions(
                            row?.PosterKey is null
                                ? null
                                : $"{prefix}/media/{id}/artwork/poster?revision={row.Revision}",
                            $"{prefix}/media/{id}/thumbnail"
                        )
                    );
                }
            )
            .Produces<ImageOptions>();
        group
            .MapGet(
                "/media/{id:int}/thumbnail",
                async (
                    int id,
                    double? position,
                    MediaDb db,
                    PathPolicy paths,
                    PreviewImages previews,
                    HttpContext context,
                    CancellationToken ct
                ) =>
                {
                    if (
                        position is not null
                        && (!double.IsFinite(position.Value) || position < 0 || position > 21600)
                    )
                        return Results.BadRequest();
                    var item = await db.Items.SingleOrDefaultAsync(x => x.Id == id, ct);
                    if (item is null)
                        return Results.NotFound();
                    var library = await db.Libraries.SingleOrDefaultAsync(
                        x => x.Id == item.LibraryId,
                        ct
                    );
                    if (library is null)
                        return Results.NotFound();
                    try
                    {
                        var image = await previews.Get(item, library, paths, position, ct);
                        context.Response.Headers.CacheControl = "private, no-store";
                        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                        return Results.File(image, "image/jpeg");
                    }
                    catch (Exception e)
                        when (e
                                is IOException
                                    or UnauthorizedAccessException
                                    or ArgumentException
                                    or OperationCanceledException
                                    or System.ComponentModel.Win32Exception
                        )
                    {
                        context.Response.Headers.RetryAfter = "5";
                        return Results.StatusCode(503);
                    }
                }
            )
            .RequireRateLimiting("previews");
        group
            .MapPost(
                "/media/{id:int}/artwork/upload",
                async (
                    int id,
                    ArtworkUpload input,
                    MediaDb db,
                    ArtworkCache cache,
                    MetadataService service,
                    CancellationToken ct
                ) =>
                {
                    if (
                        input.Kind is not ("poster" or "backdrop")
                        || input.Data is null
                        || input.Data.Length > 5592408
                    )
                        return Results.BadRequest();
                    await service.Gate.WaitAsync(ct);
                    try
                    {
                        var item = await db.Items.SingleOrDefaultAsync(x => x.Id == id, ct);
                        if (item is null)
                            return Results.NotFound();
                        var row = await db.Metadata.SingleOrDefaultAsync(
                            x => x.MediaItemId == id,
                            ct
                        );
                        if ((row?.Revision ?? 0) != input.ExpectedRevision)
                            return Results.Conflict();
                        byte[] bytes;
                        try
                        {
                            bytes = Convert.FromBase64String(input.Data);
                        }
                        catch (FormatException)
                        {
                            return Results.BadRequest();
                        }
                        var key = await cache.Store(new MemoryStream(bytes), null, ct);
                        var value = row is null
                            ? Empty(item)
                            : MetadataService.View(row, id, prefix).Details;
                        row = await MetadataService.Save(
                            db,
                            item,
                            value,
                            input.Kind == "poster" ? key : row?.PosterKey,
                            input.Kind == "backdrop" ? key : row?.BackdropKey,
                            input.ExpectedRevision,
                            ct
                        );
                        return Results.Ok(MetadataService.View(row, id, prefix));
                    }
                    catch (MetadataFault)
                    {
                        return Results.BadRequest();
                    }
                    finally
                    {
                        service.Gate.Release();
                    }
                }
            )
            .RequireAuthorization("admin")
            .WithMetadata(new ArtworkUploadLimit())
            .Produces<MetadataView>();
        group
            .MapGet(
                "/media/{id:int}/artwork/candidates",
                async (int id, MediaDb db, PathPolicy paths, CancellationToken ct) =>
                {
                    var item = await db.Items.SingleOrDefaultAsync(x => x.Id == id, ct);
                    if (item is null)
                        return Results.NotFound();
                    var library = await db.Libraries.SingleAsync(x => x.Id == item.LibraryId, ct);
                    var rows = await db
                        .Metadata.AsNoTracking()
                        .Where(x => x.MediaItemId == id)
                        .ToArrayAsync(ct);
                    var row = rows.SingleOrDefault();
                    var saved = await db
                        .ArtworkOptions.Where(x => x.MediaItemId == id)
                        .OrderByDescending(x => x.CreatedAt)
                        .Select(x => x.Key)
                        .ToArrayAsync(ct);
                    var candidates = new[] { row?.PosterKey, row?.BackdropKey }
                        .Concat(saved)
                        .Where(x => x is not null)
                        .Distinct()
                        .Select(x => new ArtworkCandidate(
                            x!,
                            $"{prefix}/media/{id}/artwork/candidate/{x}"
                        ))
                        .ToList();
                    var local = LocalNames(item);
                    for (var index = 0; index < local.Length; index++)
                    {
                        try
                        {
                            using var file = paths.OpenRead(library.Path, local[index]);
                            candidates.Add(
                                new ArtworkCandidate(
                                    "local-" + index,
                                    $"{prefix}/media/{id}/artwork/candidate/local-{index}"
                                )
                            );
                        }
                        catch (Exception e)
                            when (e
                                    is IOException
                                        or ArgumentException
                                        or UnauthorizedAccessException
                            ) { }
                    }
                    return Results.Ok(candidates);
                }
            )
            .RequireAuthorization("admin")
            .Produces<ArtworkCandidate[]>();
        group
            .MapGet(
                "/media/{id:int}/artwork/candidate/{key}",
                async (
                    int id,
                    string key,
                    MediaDb db,
                    ArtworkCache cache,
                    PathPolicy paths,
                    CancellationToken ct
                ) =>
                {
                    if (!await db.Items.AnyAsync(x => x.Id == id, ct))
                        return Results.NotFound();
                    var row = await db.Metadata.SingleOrDefaultAsync(x => x.MediaItemId == id, ct);
                    if (key.StartsWith("local-", StringComparison.Ordinal))
                    {
                        try
                        {
                            var item = await db.Items.SingleAsync(x => x.Id == id, ct);
                            var library = await db.Libraries.SingleAsync(
                                x => x.Id == item.LibraryId,
                                ct
                            );
                            using var input = LocalInput(key, item, library, paths);
                            var stored = await cache.Store(input, null, ct);
                            return Results.File(
                                cache.Open(stored),
                                stored.EndsWith(".png") ? "image/png" : "image/jpeg"
                            );
                        }
                        catch (Exception e)
                            when (e
                                    is IOException
                                        or MetadataFault
                                        or ArgumentException
                                        or UnauthorizedAccessException
                            )
                        {
                            return Results.NotFound();
                        }
                    }
                    if (
                        (row is null || (key != row.PosterKey && key != row.BackdropKey))
                        && !await db.ArtworkOptions.AnyAsync(
                            x => x.MediaItemId == id && x.Key == key,
                            ct
                        )
                    )
                        return Results.NotFound();
                    try
                    {
                        return Results.File(
                            cache.Open(key),
                            key.EndsWith(".png") ? "image/png"
                                : key.EndsWith(".webp") ? "image/webp"
                                : "image/jpeg"
                        );
                    }
                    catch (IOException)
                    {
                        return Results.NotFound();
                    }
                }
            )
            .RequireAuthorization("admin");
        group
            .MapPost(
                "/media/{id:int}/artwork/choice",
                async (
                    int id,
                    ArtworkChoice input,
                    MediaDb db,
                    ArtworkCache cache,
                    MetadataService service,
                    PreviewImages previews,
                    PathPolicy paths,
                    CancellationToken ct
                ) =>
                {
                    if (
                        input.Kind is not ("poster" or "backdrop")
                        || input.Source is not ("thumbnail" or "remove" or "existing")
                    )
                        return Results.BadRequest();
                    await service.Gate.WaitAsync(ct);
                    try
                    {
                        var item = await db.Items.SingleOrDefaultAsync(x => x.Id == id, ct);
                        if (item is null)
                            return Results.NotFound();
                        var row = await db.Metadata.SingleOrDefaultAsync(
                            x => x.MediaItemId == id,
                            ct
                        );
                        if ((row?.Revision ?? 0) != input.ExpectedRevision)
                            return Results.Conflict();
                        string? key = null;
                        if (input.Source == "existing")
                        {
                            if (input.Key?.StartsWith("local-", StringComparison.Ordinal) == true)
                            {
                                var library = await db.Libraries.SingleAsync(
                                    x => x.Id == item.LibraryId,
                                    ct
                                );
                                using var image = LocalInput(input.Key, item, library, paths);
                                key = await cache.Store(image, null, ct);
                            }
                            else
                            {
                                if (
                                    input.Key is null
                                    || (
                                        (
                                            input.Key != row?.PosterKey
                                            && input.Key != row?.BackdropKey
                                        )
                                        && !await db.ArtworkOptions.AnyAsync(
                                            x => x.MediaItemId == id && x.Key == input.Key,
                                            ct
                                        )
                                    )
                                )
                                    return Results.BadRequest();
                                using var image = cache.Open(input.Key);
                                key = input.Key;
                            }
                        }
                        else if (input.Source == "thumbnail")
                        {
                            var library = await db.Libraries.SingleAsync(
                                x => x.Id == item.LibraryId,
                                ct
                            );
                            using var image = await previews.Get(item, library, paths, null, ct);
                            key = await cache.Store(image, "image/jpeg", ct);
                        }
                        row = await MetadataService.Save(
                            db,
                            item,
                            row is null
                                ? Empty(item)
                                : MetadataService.View(row, id, prefix).Details,
                            input.Kind == "poster" ? key : row?.PosterKey,
                            input.Kind == "backdrop" ? key : row?.BackdropKey,
                            input.ExpectedRevision,
                            ct
                        );
                        return Results.Ok(MetadataService.View(row, id, prefix));
                    }
                    catch (Exception e)
                        when (e
                                is MetadataFault
                                    or IOException
                                    or OperationCanceledException
                                    or ArgumentException
                                    or System.ComponentModel.Win32Exception
                        )
                    {
                        return Results.BadRequest();
                    }
                    finally
                    {
                        service.Gate.Release();
                    }
                }
            )
            .RequireAuthorization("admin")
            .Produces<MetadataView>();
    }

    private static string[] LocalNames(MediaItem item)
    {
        var folder = Path.GetDirectoryName(item.RelativePath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(item.RelativePath);
        return new[]
        {
            stem + "-poster.jpg",
            stem + "-poster.png",
            "poster.jpg",
            "poster.png",
            stem + "-fanart.jpg",
            stem + "-fanart.png",
            "fanart.jpg",
            "fanart.png",
        }
            .Select(x => Path.Combine(folder, x))
            .ToArray();
    }

    private static FileStream LocalInput(
        string key,
        MediaItem item,
        Library library,
        PathPolicy paths
    )
    {
        if (!int.TryParse(key.AsSpan(6), out var index) || index is < 0 or > 7)
            throw new ArgumentException();
        return paths.OpenRead(library.Path, LocalNames(item)[index]);
    }

    private static MetadataValue Empty(MediaItem item) =>
        new(
            item.Title,
            null,
            "",
            [],
            [],
            null,
            null,
            null,
            "manual",
            null,
            "Manual library artwork"
        );
}
