using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Media;
using MediaServer.Services;
using Microsoft.Extensions.Hosting;

return await Benchmark.RunAsync(args);

static class Benchmark
{
    public static async Task<int> RunAsync(string[] args)
    {
        string Option(string key, string fallback)
        {
            var i = Array.IndexOf(args, key);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
        var output = Path.GetFullPath(Option("--output", "artifacts/playback-benchmark.json"));
        // Canonicalize only the system temp directory, never a user media path.
        var temp = Path.GetFullPath(Path.GetTempPath());
        var canonical = Path.GetPathRoot(temp)!;
        foreach (
            var part in temp[canonical.Length..]
                .Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries
                )
        )
        {
            var directory = new DirectoryInfo(Path.Combine(canonical, part));
            canonical = directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName;
        }
        var root = Path.Combine(canonical, "media-benchmark-" + Guid.NewGuid().ToString("N"));
        var report = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["platform"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["logicalProcessors"] = Environment.ProcessorCount,
            ["commit"] = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            ["startedAt"] = DateTime.UtcNow,
        };
        var lifetime = new BenchmarkLifetime();
        try
        {
            var encoder = Option("--encoder", "libx264");
            var scenario = Option("--scenario", "sdr");
            var seconds = int.Parse(
                Option("--seconds", "6"),
                System.Globalization.CultureInfo.InvariantCulture
            );
            var height = int.Parse(
                Option("--height", "720"),
                System.Globalization.CultureInfo.InvariantCulture
            );
            if (
                !EncoderDiscovery.Encoders.Contains(encoder)
                || scenario is not ("sdr" or "hevc-hdr" or "av1-hdr")
                || seconds is < 2 or > 60
                || height is not (360 or 720 or 1080)
            )
                throw new ArgumentException();
            var settings = new TranscodeSettings(
                new[] { "libx264", encoder }.Distinct().ToArray(),
                Environment.GetEnvironmentVariable("MediaServer__VaapiDevice")
                    ?? "/dev/dri/renderD128",
                int.Parse(
                    Environment.GetEnvironmentVariable("MediaServer__NvencGpu") ?? "-1",
                    System.Globalization.CultureInfo.InvariantCulture
                ),
                Environment.GetEnvironmentVariable("MediaServer__QsvDevice")
            );
            settings.Validate();
            report["requestedEncoder"] = encoder;
            report["scenario"] = scenario;
            report["device"] = encoder switch
            {
                "h264_vaapi" => settings.VaapiDevice,
                "h264_qsv" => settings.QsvDevice ?? "automatic",
                "h264_nvenc" => settings.NvencGpu.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                ),
                _ => "default",
            };
            var runner = new FfmpegRunner(
                Environment.GetEnvironmentVariable("MediaServer__FfmpegPath") ?? "ffmpeg"
            );
            var probe = new Ffprobe(
                Environment.GetEnvironmentVariable("MediaServer__FfprobePath") ?? "ffprobe"
            );
            var discovery = new EncoderDiscovery(runner, settings);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var capabilities = await discovery.GetAsync(deadline.Token);
            report["capabilities"] = capabilities;
            if (
                args.Contains("--require-hardware")
                && (
                    encoder == "libx264"
                    || !capabilities.Any(x => x.Encoder == encoder && x.Available)
                )
            )
            {
                report["status"] = "hardware-unavailable";
                await Write(output, report);
                return 2;
            }
            Directory.CreateDirectory(Path.Combine(root, "media"));
            Directory.CreateDirectory(Path.Combine(root, "data"));
            var input = new List<string>
            {
                "-filter_threads",
                "1",
                "-f",
                "lavfi",
                "-i",
                $"testsrc2=size={height * 16 / 9}x{height}:rate=24",
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=440",
                "-t",
                seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-threads",
                "2",
            };
            if (scenario == "sdr")
                input.AddRange(["-c:v", "libx264", "-preset", "ultrafast"]);
            else
            {
                input.AddRange([
                    "-vf",
                    "format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc:range=limited",
                    "-color_primaries",
                    "bt2020",
                    "-color_trc",
                    "smpte2084",
                    "-colorspace",
                    "bt2020nc",
                ]);
                if (scenario == "hevc-hdr")
                    input.AddRange([
                        "-c:v",
                        "libx265",
                        "-preset",
                        "ultrafast",
                        "-x265-params",
                        "pools=none:frame-threads=1:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400",
                    ]);
                else
                    input.AddRange(["-c:v", "libaom-av1", "-cpu-used", "8", "-row-mt", "1"]);
            }
            input.AddRange(["-c:a", "aac", "source.mkv"]);
            await runner.RunAsync(input, Path.Combine(root, "media"), deadline.Token);
            var path = Path.Combine(root, "media", "source.mkv");
            var metadata = await probe.ProbeAsync(path, deadline.Token);
            var item = new MediaItem
            {
                Id = 1,
                LibraryId = 1,
                RelativePath = "source.mkv",
                Duration = metadata.Duration,
                AudioCodec = metadata.AudioCodec,
                VideoCodec = metadata.VideoCodec,
                TracksJson = JsonSerializer.Serialize(metadata.Tracks),
                ColorJson = metadata.ColorJson,
                Size = new FileInfo(path).Length,
                ModifiedTicks = File.GetLastWriteTimeUtc(path).Ticks,
            };
            if (
                (scenario != "sdr" && TranscodeProfile.Color(item)?.Hdr != true)
                || (scenario == "hevc-hdr" && metadata.VideoCodec != "hevc")
                || (scenario == "av1-hdr" && metadata.VideoCodec != "av1")
            )
                throw new IOException("Synthetic input does not match the requested scenario.");
            report["source"] = new
            {
                metadata.Duration,
                nominalSeconds = seconds,
                width = height * 16 / 9,
                height,
                hdr = TranscodeProfile.Color(item)?.Hdr == true,
            };
            using var sessions = new PlaybackSessions(
                Path.Combine(root, "data"),
                runner,
                discovery,
                lifetime,
                settings,
                probe
            );
            await sessions.StartAsync(deadline.Token);
            try
            {
                var policy = new PathPolicy([Path.Combine(root, "media")]);
                var library = new Library { Id = 1, Path = Path.Combine(root, "media") };
                var selection = new PlaybackSelection(Encoder: encoder);
                var watch = Stopwatch.StartNew();
                var state =
                    await sessions.StartAsync(
                        item,
                        library,
                        policy,
                        1,
                        "benchmark-a",
                        selection,
                        deadline.Token
                    ) ?? throw new IOException();
                while (state.State == "preparing")
                {
                    await Task.Delay(25, deadline.Token);
                    state = sessions.Get(state.Id, 1, 1, "benchmark-a") ?? throw new IOException();
                }
                watch.Stop();
                if (state.State != "ready")
                    throw new IOException();
                report["actualEncoder"] = state.Encoder;
                report["elapsedSeconds"] = watch.Elapsed.TotalSeconds;
                report["preparationMode"] = "complete-vod";
                report["nominalAggregateFramesPerSecond"] =
                    seconds * 24 * TranscodeProfile.Ladder.Length / watch.Elapsed.TotalSeconds;
                var outputs = new List<object>();
                long bytes = 0;
                foreach (var level in TranscodeProfile.Ladder)
                {
                    var manifest =
                        sessions.FilePath(state.Id, 1, 1, "benchmark-a", level.Name + ".m3u8")
                        ?? throw new IOException();
                    var decoded = await probe.ProbeAsync(manifest, deadline.Token);
                    var color = JsonSerializer.Deserialize<VideoColor>(decoded.ColorJson);
                    if (
                        decoded.VideoCodec != "h264"
                        || decoded.AudioCodec != "aac"
                        || color?.Width % 2 != 0
                        || color?.Height % 2 != 0
                        || color?.Hdr == true
                        || (scenario != "sdr" && color?.Primaries != "bt709")
                    )
                        throw new IOException();
                    foreach (
                        var file in Directory.EnumerateFiles(
                            Path.GetDirectoryName(manifest)!,
                            level.Name + "_*.ts"
                        )
                    )
                        bytes += new FileInfo(file).Length;
                    outputs.Add(
                        new
                        {
                            level.Name,
                            decoded.Duration,
                            decoded.VideoCodec,
                            decoded.AudioCodec,
                            color,
                        }
                    );
                }
                report["outputs"] = outputs;
                report["segmentBytes"] = bytes;
                var reuse = Stopwatch.StartNew();
                var shared =
                    await sessions.StartAsync(
                        item,
                        library,
                        policy,
                        2,
                        "benchmark-b",
                        selection,
                        deadline.Token
                    ) ?? throw new IOException();
                reuse.Stop();
                if (shared.Id != state.Id || shared.State != "ready")
                    throw new IOException();
                report["authorizedReuseMilliseconds"] = reuse.Elapsed.TotalMilliseconds;
                await sessions.RemoveAsync(state.Id, 1, "benchmark-a");
                if (sessions.Get(shared.Id, 1, 2, "benchmark-b")?.State != "ready")
                    throw new IOException();
                await sessions.RemoveAsync(state.Id, 2, "benchmark-b");
                var fallback = state.Encoder != encoder;
                report["softwareFallback"] = fallback;
                report["status"] =
                    fallback && args.Contains("--require-hardware")
                        ? "unexpected-fallback"
                        : "passed";
                await Write(output, report);
                return fallback && args.Contains("--require-hardware") ? 2 : 0;
            }
            finally
            {
                await sessions.StopAsync(CancellationToken.None);
            }
        }
        catch (Exception e)
            when (e
                    is IOException
                        or ArgumentException
                        or OperationCanceledException
                        or System.ComponentModel.Win32Exception
                        or FormatException
                        or OverflowException
            )
        {
            report["status"] = "failed";
            report["errorType"] = e.GetType().Name;
            await Write(output, report);
            return 1;
        }
        finally
        {
            lifetime.StopApplication();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static async Task Write(string output, Dictionary<string, object?> report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(
            output,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }
            ) + "\n"
        );
        Console.WriteLine($"Playback benchmark: {report["status"]}");
    }
}

sealed class BenchmarkLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource stopping = new();
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => stopping.Token;
    public CancellationToken ApplicationStopped => stopping.Token;

    public void StopApplication() => stopping.Cancel();
}
