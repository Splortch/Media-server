using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaServer.Core;
using MediaServer.Media;
using Microsoft.Extensions.Hosting;

namespace MediaServer.Services;

public record PlaybackSelection(
    int? AudioTrack = null,
    int? SubtitleTrack = null,
    string Encoder = "libx264"
);

public record ConversionState(
    string Id,
    string State,
    string Encoder,
    string? Error,
    double EncodeSeconds,
    string Manifest
);

// Conversion cache is private and ephemeral; access is bound to authenticated session leases.
// Encodes may be reused, but every authenticated session needs its own lease and media ACL.
// No shared public files or bearer tokens in URLs. At most two staged/encoded sources.
public sealed class PlaybackSessions(
    string dataDirectory,
    FfmpegRunner runner,
    EncoderDiscovery discovery,
    IHostApplicationLifetime lifetime,
    TranscodeSettings settings,
    IMediaProbe probe
) : BackgroundService
{
    public const long MaxSourceBytes = 8L * 1024 * 1024 * 1024;
    public const long MaxOutputBytes = 4L * 1024 * 1024 * 1024;
    private readonly string root = Path.Combine(dataDirectory, "playback-cache");
    private readonly ConcurrentDictionary<string, Job> jobs = new();
    private readonly SemaphoreSlim admission = new(1, 1);

    private sealed record Lease(int User, DateTime Touched);

    private sealed class Job(
        string id,
        int media,
        string key,
        string path,
        PlaybackSelection selection
    )
    {
        public string Id = id,
            Key = key,
            Directory = path;
        public int Media = media;
        public ConcurrentDictionary<string, Lease> Leases { get; } = new();
        public PlaybackSelection Selection = selection;
        public volatile string State = "preparing";
        public string Encoder = "libx264";
        public string? Error;
        public double Seconds;
        public CancellationTokenSource Stop = new();
        public Task Work = Task.CompletedTask;

        public ConversionState Info() =>
            new(
                Id,
                State,
                Encoder,
                Error,
                Seconds,
                $"/api/v1/playback-sessions/{Id}/files/master.m3u8"
            );
    }

    public async Task<ConversionState?> StartAsync(
        MediaItem item,
        Library library,
        PathPolicy policy,
        int user,
        string session,
        PlaybackSelection selection,
        CancellationToken ct
    )
    {
        if (!settings.AllowedEncoders.Contains(selection.Encoder))
            throw new ArgumentException("Unknown encoder.");
        var tracks = JsonSerializer.Deserialize<MediaTrack[]>(item.TracksJson) ?? [];
        if (
            selection.AudioTrack is int audio
            && !tracks.Any(x => x.Index == audio && x.Type == "audio")
        )
            throw new ArgumentException("Unknown audio track.");
        if (
            selection.SubtitleTrack is int subtitle
            && subtitle >= 0
            && !tracks.Any(x =>
                x.Index == subtitle && x.Type == "subtitle" && TextSubtitle(x.Codec)
            )
        )
            throw new ArgumentException("Only text subtitles can be converted to WebVTT.");
        byte[]? captionBytes = null;
        string? captionCodec = null;
        if (selection.SubtitleTrack is < 0)
        {
            var caption =
                ExternalCaptions
                    .Discover(policy, library, item)
                    .FirstOrDefault(x => x.Index == selection.SubtitleTrack)
                ?? throw new ArgumentException("Unknown external subtitle.");
            captionBytes = await ExternalCaptions.ReadAsync(policy, library, caption, ct);
            captionCodec = caption.Codec;
        }
        if (item.Size > MaxSourceBytes || item.Duration > 21600)
            throw new ArgumentException(
                "Alpha conversion limit: 8 GiB source and six hours. Use Direct Play for larger files."
            );
        await admission.WaitAsync(ct);
        try
        {
            var key =
                $"{item.Id}:{item.ModifiedTicks}:{item.Size}:{JsonSerializer.Serialize(selection)}:{(captionBytes is null ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(captionBytes)))}";
            var existing = jobs.Values.FirstOrDefault(x =>
                x.Key == key && x.State is "preparing" or "ready"
            );
            if (existing is not null)
            {
                if (existing.Leases.Count >= 32 && !existing.Leases.ContainsKey(session))
                    return null;
                existing.Leases[session] = new Lease(user, DateTime.UtcNow);
                return existing.Info();
            }
            if (jobs.Count >= 2)
                return null;
            var source = policy.OpenRead(library.Path, item.RelativePath);
            try
            {
                if (source.Length > MaxSourceBytes)
                    throw new ArgumentException("Source exceeds conversion limit.");
                var id = Guid.NewGuid().ToString("N");
                var path = Path.Combine(root, id);
                Directory.CreateDirectory(path);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(
                        path,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    );
                var job = new Job(id, item.Id, key, path, selection);
                job.Leases[session] = new Lease(user, DateTime.UtcNow);
                jobs[id] = job;
                // Own already pinned handles; never reopen a client/source path in FFmpeg.
                job.Work = ConvertAsync(job, source, captionBytes, captionCodec, item);
                return job.Info();
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }
        finally
        {
            admission.Release();
        }
    }

    public static bool TextSubtitle(string codec) =>
        codec is "subrip" or "ass" or "ssa" or "webvtt" or "mov_text" or "text";

    public ConversionState? Get(string id, int media, int user, string session) =>
        Find(id, media, user, session)?.Info();

    private Job? Find(string id, int media, int user, string session)
    {
        if (
            !jobs.TryGetValue(id, out var job)
            || job.Media != media
            || !job.Leases.TryGetValue(session, out var lease)
            || lease.User != user
        )
            return null;
        if (!job.Leases.TryUpdate(session, new Lease(user, DateTime.UtcNow), lease))
            return null;
        return job;
    }

    public int? MediaId(string id, int user, string session) =>
        jobs.TryGetValue(id, out var job)
        && job.Leases.TryGetValue(session, out var lease)
        && lease.User == user
            ? job.Media
            : null;

    public string? FilePath(string id, int media, int user, string session, string file)
    {
        var job = Find(id, media, user, session);
        // Flat server-generated filenames only; no URL rewriting or arbitrary file access.
        if (
            job?.State != "ready"
            || !Regex.IsMatch(
                file,
                @"\A(?:master\.m3u8|(?:360p|480p|720p)\.m3u8|(?:360p|480p|720p)_\d{5,}\.ts|captions\.vtt)\z"
            )
        )
            return null;
        var path = Path.Combine(job.Directory, file);
        return File.Exists(path) ? path : null;
    }

    public async Task<bool> RemoveAsync(string id, int user, string session)
    {
        await admission.WaitAsync();
        try
        {
            if (
                !jobs.TryGetValue(id, out var job)
                || !job.Leases.TryGetValue(session, out var lease)
                || lease.User != user
            )
                return false;
            job.Leases.TryRemove(session, out _);
            if (job.Leases.IsEmpty)
                await StopAsync(job);
            return true;
        }
        finally
        {
            admission.Release();
        }
    }

    private async Task StopAsync(Job job)
    {
        job.State = "cancelled";
        job.Stop.Cancel();
        try
        {
            await job.Work;
        }
        catch (OperationCanceledException) { }
        // Retain admission slot until cleanup completes. Never delete another job's cache.
        try
        {
            Directory.Delete(job.Directory, true);
        }
        catch (DirectoryNotFoundException) { }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        jobs.TryRemove(job.Id, out _);
    }

    private async Task ConvertAsync(
        Job job,
        Stream source,
        byte[]? captions,
        string? captionCodec,
        MediaItem item
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            job.Stop.Token,
            lifetime.ApplicationStopping
        );
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var ct = timeout.Token;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var quota = MonitorQuota(job.Directory, timeout, monitorStop.Token);
        try
        {
            await using (source)
            await using (var staged = File.Create(Path.Combine(job.Directory, "source.bin")))
            {
                // The handle length and byte counter both enforce the cap if a source grows.
                var buffer = new byte[128 * 1024];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxSourceBytes)
                        throw new IOException("Source quota exceeded.");
                    await staged.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            // Reprobe the pinned staged bytes, so old catalogs cannot silently misclassify HDR.
            var metadata = await probe.ProbeAsync(Path.Combine(job.Directory, "source.bin"), ct);
            item.ColorJson = metadata.ColorJson;
            if (captions is not null)
            {
                var captionPath = Path.Combine(job.Directory, "captions.vtt");
                if (captionCodec == "webvtt")
                    await File.WriteAllTextAsync(
                        captionPath,
                        SanitizeVtt(Encoding.UTF8.GetString(captions)),
                        ct
                    );
                else
                {
                    var input = "captions-source." + (captionCodec == "subrip" ? "srt" : "ass");
                    await File.WriteAllBytesAsync(Path.Combine(job.Directory, input), captions, ct);
                    await runner.RunAsync(
                        [
                            "-protocol_whitelist",
                            "file",
                            "-format_whitelist",
                            "srt,ass,webvtt",
                            "-i",
                            input,
                            "-map",
                            "0:s:0",
                            "-c:s",
                            "webvtt",
                            "captions.vtt",
                        ],
                        job.Directory,
                        ct
                    );
                    if (new FileInfo(captionPath).Length > ExternalCaptions.MaxBytes)
                        throw new IOException("Caption quota exceeded.");
                    await File.WriteAllTextAsync(
                        captionPath,
                        SanitizeVtt(await File.ReadAllTextAsync(captionPath, ct)),
                        ct
                    );
                    File.Delete(Path.Combine(job.Directory, input));
                }
            }
            else if (job.Selection.SubtitleTrack is int subtitle)
            {
                await runner.RunAsync(
                    [
                        "-protocol_whitelist",
                        "file",
                        "-format_whitelist",
                        "mov,matroska,webm,avi",
                        "-i",
                        "source.bin",
                        "-map",
                        $"0:{subtitle}",
                        "-c:s",
                        "webvtt",
                        "captions.vtt",
                    ],
                    job.Directory,
                    ct
                );
                var captionPath = Path.Combine(job.Directory, "captions.vtt");
                if (new FileInfo(captionPath).Length > 1024 * 1024)
                    throw new IOException("Caption quota exceeded.");
                await File.WriteAllTextAsync(
                    captionPath,
                    SanitizeVtt(await File.ReadAllTextAsync(captionPath, ct)),
                    ct
                );
            }
            var available = await discovery.GetAsync(ct);
            var encoder = available.Any(x => x.Encoder == job.Selection.Encoder && x.Available)
                ? job.Selection.Encoder
                : "libx264";
            try
            {
                await Encode(job, item, encoder, ct);
            }
            catch (IOException) when (encoder != "libx264" && !ct.IsCancellationRequested)
            {
                // Never publish partial playlists after hardware failure.
                foreach (
                    var file in Directory
                        .EnumerateFiles(job.Directory)
                        .Where(x =>
                            x.EndsWith(".ts", StringComparison.Ordinal)
                            || x.EndsWith(".m3u8", StringComparison.Ordinal)
                        )
                )
                    File.Delete(file);
                await Encode(job, item, "libx264", ct);
            }
            File.Delete(Path.Combine(job.Directory, "source.bin"));
            job.Seconds = watch.Elapsed.TotalSeconds;
            job.State = "ready";
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or OperationCanceledException
                        or System.ComponentModel.Win32Exception
                        or ArgumentException
                        or JsonException
                        or UnauthorizedAccessException
                        or RegexMatchTimeoutException
            )
        {
            job.Error = ct.IsCancellationRequested
                ? "conversion_cancelled_or_quota_exceeded"
                : "conversion_failed_check_ffmpeg_and_filters";
            job.State = "failed";
        }
        finally
        {
            source.Dispose();
            monitorStop.Cancel();
            await quota;
        }
    }

    private async Task Encode(Job job, MediaItem item, string encoder, CancellationToken ct)
    {
        job.Encoder = encoder;
        var master = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n");
        var hdr = TranscodeProfile.Color(item)?.Hdr == true;
        foreach (var level in TranscodeProfile.Ladder)
        {
            var args = new List<string> { "-filter_threads", hdr ? "1" : "2" };
            args.AddRange(TranscodeProfile.DeviceArguments(encoder, settings));
            args.AddRange([
                "-protocol_whitelist",
                "file",
                "-format_whitelist",
                "mov,matroska,webm,avi",
                "-i",
                "source.bin",
                "-map",
                "0:v:0",
            ]);
            args.AddRange([
                "-map",
                job.Selection.AudioTrack is int audio ? $"0:{audio}" : "0:a:0?",
            ]);
            var filter = TranscodeProfile.Filter(level, hdr);
            if (encoder == "h264_vaapi")
                filter += ",format=nv12,hwupload";
            args.AddRange(["-vf", filter]);
            args.AddRange(TranscodeProfile.EncoderArguments(encoder, settings));
            args.AddRange([
                "-threads",
                "2",
                "-b:v",
                $"{level.Kilobits}k",
                "-maxrate",
                $"{level.Kilobits}k",
                "-bufsize",
                $"{level.Kilobits * 2}k",
                "-force_key_frames",
                "expr:gte(t,n_forced*4)",
                "-sc_threshold",
                "0",
                "-c:a",
                "aac",
                "-b:a",
                "128k",
                "-ac",
                "2",
                "-sn",
                "-map_metadata",
                "-1",
                "-f",
                "hls",
                "-hls_time",
                "4",
                "-hls_playlist_type",
                "vod",
                "-hls_flags",
                "independent_segments",
                "-hls_segment_filename",
                level.Name + "_%05d.ts",
                level.Name + ".m3u8",
            ]);
            if (hdr)
                args.InsertRange(
                    args.Count - 1,
                    ["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]
                );
            await runner.RunAsync(args, job.Directory, ct);
            master.Append(
                $"#EXT-X-STREAM-INF:BANDWIDTH={(level.Kilobits + 160) * 1000},NAME=\"{level.Name}\"\n{level.Name}.m3u8\n"
            );
        }
        await File.WriteAllTextAsync(
            Path.Combine(job.Directory, "master.m3u8"),
            master.ToString(),
            ct
        );
    }

    public static string SanitizeVtt(string value)
    {
        if (!value.StartsWith("WEBVTT", StringComparison.Ordinal))
            throw new IOException("Invalid WebVTT captions.");
        // Captions are text, never HTML. Strip markup/control characters and metadata blocks.
        var blocks = value.Replace("\r", "").Split("\n\n").Skip(1);
        return "WEBVTT\n\n"
            + string.Join(
                "\n\n",
                blocks
                    .Where(x => x.Contains(" --> ", StringComparison.Ordinal))
                    .Select(x =>
                        Regex.Replace(
                            x,
                            "<[^>]*>",
                            "",
                            RegexOptions.CultureInvariant,
                            TimeSpan.FromSeconds(1)
                        )
                    )
            )
            + "\n";
    }

    private static async Task MonitorQuota(
        string path,
        CancellationTokenSource stop,
        CancellationToken ct
    )
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                if (
                    Directory
                        .EnumerateFiles(path)
                        .Where(x => Path.GetFileName(x) != "source.bin")
                        .Sum(x => new FileInfo(x).Length) > MaxOutputBytes
                )
                {
                    stop.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException)
        {
            stop.Cancel();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        // Stale private cache only; no library paths ever passed to cleanup.
        foreach (var directory in Directory.EnumerateDirectories(root))
            Directory.Delete(directory, true);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                await admission.WaitAsync(stoppingToken);
                try
                {
                    foreach (var job in jobs.Values)
                    {
                        foreach (
                            var lease in job
                                .Leases.Where(x =>
                                    DateTime.UtcNow - x.Value.Touched > TimeSpan.FromMinutes(30)
                                )
                                .ToArray()
                        )
                            ((ICollection<KeyValuePair<string, Lease>>)job.Leases).Remove(lease);
                        if (job.Leases.IsEmpty)
                            await StopAsync(job);
                    }
                }
                finally
                {
                    admission.Release();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var job in jobs.Values)
                await StopAsync(job);
        }
    }
}
