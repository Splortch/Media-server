using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using MediaServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public sealed class PlaybackPipelineTests
{
    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1")]
    public async Task RemoveLibraryRequiresAdminAndPreservesOriginals(string prefix)
    {
        using var server = new Server();
        using var admin = await server.Admin();
        var path = Path.Combine(server.MediaPath, "original.webm");
        await File.WriteAllTextAsync(path, "original bytes must survive");
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Libraries.Add(
                new Library
                {
                    Id = 1,
                    Name = "Films",
                    Path = server.MediaPath,
                }
            );
            db.Items.Add(
                new MediaItem
                {
                    Id = 1,
                    LibraryId = 1,
                    RelativePath = "original.webm",
                }
            );
            db.Progress.Add(
                new PlaybackProgress
                {
                    UserId = 1,
                    MediaItemId = 1,
                    Seconds = 10,
                }
            );
            db.History.Add(new WatchHistory { UserId = 1, MediaItemId = 1 });
            db.Collections.Add(
                new SmartCollection
                {
                    UserId = 1,
                    LibraryId = 1,
                    Name = "Films",
                }
            );
            db.Jobs.Add(new ScanJob { LibraryId = 1, State = "completed" });
            await db.SaveChangesAsync();
        }
        using var anonymous = server.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.DeleteAsync(prefix + "/libraries/1")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await admin.DeleteAsync(prefix + "/libraries/1")).StatusCode
        );
        Assert.Equal("original bytes must survive", await File.ReadAllTextAsync(path));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.GetAsync("/api/media/1/stream")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.DeleteAsync(prefix + "/libraries/1")).StatusCode
        );
        using var verify = server.Services.CreateScope();
        var check = verify.ServiceProvider.GetRequiredService<MediaDb>();
        Assert.Empty(await check.Items.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await check.Progress.ToListAsync());
        Assert.Empty(await check.History.ToListAsync());
        Assert.Empty(await check.Collections.ToListAsync());
        Assert.Empty(await check.Jobs.ToListAsync());
    }

    [Fact]
    public async Task RemovalCannotRaceQueuedScans()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
        db.Libraries.Add(new Library { Id = 1, Path = server.MediaPath });
        db.Jobs.Add(new ScanJob { LibraryId = 1, State = "running" });
        await db.SaveChangesAsync();
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await admin.DeleteAsync("/api/libraries/1")).StatusCode
        );
        Assert.Single(await db.Libraries.ToListAsync());
    }

    [Fact]
    public void ProfileIsExplicitAboutHdrAndSanitizesCaptions()
    {
        Assert.Contains(
            "tonemap=tonemap=hable",
            TranscodeProfile.Filter(TranscodeProfile.Ladder[0], true)
        );
        Assert.DoesNotContain(
            "tonemap",
            TranscodeProfile.Filter(TranscodeProfile.Ladder[0], false)
        );
        Assert.True(
            new VideoColor(
                1920,
                1080,
                10,
                "yuv420p10le",
                "bt2020",
                "smpte2084",
                "bt2020nc",
                true
            ).Hdr
        );
        Assert.Throws<ArgumentException>(() => TranscodeProfile.EncoderArguments("-malicious"));
        var caption = PlaybackSessions.SanitizeVtt(
            "WEBVTT\n\nSTYLE\n::cue { color:red }\n\n00:00:00.000 --> 00:00:01.000\n<script>alert(1)</script><b>hello</b>\n"
        );
        Assert.DoesNotContain("<", caption);
        Assert.DoesNotContain("STYLE", caption);
        Assert.Contains("hello", caption);
    }

    [Fact]
    public async Task WidescreenRenditionsHaveEvenDimensions()
    {
        var root = Path.Combine(TestPaths.TempRoot, "scaling-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var runner = new FfmpegRunner("ffmpeg");
            var probe = new Ffprobe("ffprobe");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            foreach (var level in TranscodeProfile.Ladder)
            {
                var output = level.Name + ".mp4";
                await runner.RunAsync(
                    [
                        "-filter_threads",
                        "1",
                        "-f",
                        "lavfi",
                        "-i",
                        "testsrc2=size=1280x720:rate=24",
                        "-frames:v",
                        "1",
                        "-vf",
                        TranscodeProfile.Filter(level, false),
                        "-c:v",
                        "libx264",
                        "-preset",
                        "ultrafast",
                        "-threads",
                        "1",
                        output,
                    ],
                    root,
                    timeout.Token
                );
                var result = await probe.ProbeAsync(Path.Combine(root, output), timeout.Token);
                var color = JsonSerializer.Deserialize<VideoColor>(result.ColorJson)!;
                Assert.Equal(level.Height, color.Height);
                Assert.Equal(0, color.Width % 2);
                Assert.Equal(0, color.Height % 2);
                Assert.Equal("h264", result.VideoCodec);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("libx265", "hevc")]
    [InlineData("libaom-av1", "av1")]
    public async Task ProbeModernHdrFormatsAndToneMapSyntheticFrames(string encoder, string codec)
    {
        var root = Path.Combine(TestPaths.TempRoot, "color-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var runner = new FfmpegRunner("ffmpeg");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var args = new List<string>
            {
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=64x64:rate=24",
                "-t",
                "0.25",
                "-vf",
                "format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc:range=limited",
                "-c:v",
                encoder,
                "-threads",
                "1",
                "-color_primaries",
                "bt2020",
                "-color_trc",
                "smpte2084",
                "-colorspace",
                "bt2020nc",
            };
            if (encoder == "libx265")
                args.AddRange([
                    "-x265-params",
                    "pools=none:frame-threads=1:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400",
                ]);
            else
                args.AddRange(["-cpu-used", "8"]);
            args.Add("hdr.mkv");
            await runner.RunAsync(args, root, timeout.Token);
            var probe = new Ffprobe("ffprobe");
            var result = await probe.ProbeAsync(Path.Combine(root, "hdr.mkv"), timeout.Token);
            Assert.Equal(codec, result.VideoCodec);
            var color = JsonSerializer.Deserialize<VideoColor>(result.ColorJson)!;
            Assert.True(color.Hdr, result.ColorJson);
            Assert.Equal(10, color.BitDepth);
            Assert.Equal("bt2020", color.Primaries);
            if (encoder == "libx265")
                Assert.True(color.HasMasteringMetadata, result.ColorJson);
            var watch = Stopwatch.StartNew();
            await runner.RunAsync(
                [
                    "-filter_threads",
                    "1",
                    "-i",
                    "hdr.mkv",
                    "-vf",
                    TranscodeProfile.Filter(TranscodeProfile.Ladder[0], true),
                    "-c:v",
                    "libx264",
                    "-threads",
                    "1",
                    "-color_primaries",
                    "bt709",
                    "-color_trc",
                    "bt709",
                    "-colorspace",
                    "bt709",
                    "sdr.mkv",
                ],
                root,
                timeout.Token
            );
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
            var sdr = await probe.ProbeAsync(Path.Combine(root, "sdr.mkv"), timeout.Token);
            var output = JsonSerializer.Deserialize<VideoColor>(sdr.ColorJson)!;
            Assert.False(output.Hdr);
            Assert.False(output.HasMasteringMetadata);
            Assert.Equal(8, output.BitDepth);
            Assert.Equal("bt709", output.Primaries);
            var inspect = new ProcessStartInfo("ffprobe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (
                var arg in new[]
                {
                    "-v",
                    "error",
                    "-select_streams",
                    "v:0",
                    "-read_intervals",
                    "%+#8",
                    "-show_entries",
                    "frame_side_data",
                    "-of",
                    "json",
                    Path.Combine(root, "sdr.mkv"),
                }
            )
                inspect.ArgumentList.Add(arg);
            using var inspection = Process.Start(inspect)!;
            var frameData = inspection.StandardOutput.ReadToEndAsync(timeout.Token);
            var inspectionError = inspection.StandardError.ReadToEndAsync(timeout.Token);
            await inspection.WaitForExitAsync(timeout.Token);
            Assert.True(inspection.ExitCode == 0, await inspectionError);
            var inspectedFrames = await frameData;
            Assert.DoesNotContain("Mastering display metadata", inspectedFrames);
            Assert.DoesNotContain("Content light level metadata", inspectedFrames);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // Runs actual FFmpeg, HTTP authentication, media ACL, HLS and SQLite together.
    [Fact]
    public async Task HlsConversionAndEveryAssetRequireOwningSession()
    {
        using var server = new Server(["h264_nvenc"]);
        using var admin = await server.Admin();
        var path = Path.Combine(server.MediaPath, "synthetic.mkv");
        var start = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        foreach (
            var arg in new[]
            {
                "-v",
                "error",
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=160x90:rate=24",
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=440",
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=880",
                "-map",
                "0:v",
                "-map",
                "1:a",
                "-map",
                "2:a",
                "-metadata:s:a:0",
                "language=eng",
                "-metadata:s:a:1",
                "language=swe",
                "-t",
                "2",
                "-c:v",
                "libx264",
                "-threads",
                "1",
                "-c:a",
                "aac",
                "-y",
                path,
            }
        )
            start.ArgumentList.Add(arg);
        using (var process = Process.Start(start)!)
        {
            var err = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, err);
        }
        await File.WriteAllTextAsync(
            Path.ChangeExtension(path, ".vtt"),
            "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\n<b>Accessible caption</b>\n"
        );
        var externalSrt = Path.Combine(server.MediaPath, "synthetic.swe.srt");
        await File.WriteAllTextAsync(
            externalSrt,
            "1\n00:00:00,000 --> 00:00:01,000\n<b>Swedish caption</b>\n"
        );
        var observed = await new Ffprobe("ffprobe").ProbeAsync(path, CancellationToken.None);
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Libraries.Add(new Library { Id = 1, Path = server.MediaPath });
            db.Items.Add(
                new MediaItem
                {
                    Id = 1,
                    LibraryId = 1,
                    TracksJson = JsonSerializer.Serialize(observed.Tracks),
                    RelativePath = "synthetic.mkv",
                    Duration = 2,
                    Size = new FileInfo(path).Length,
                }
            );
            await db.SaveChangesAsync();
        }
        var denied = await admin.PostAsJsonAsync(
            "/api/v1/media/1/playback-sessions",
            new PlaybackSelection(Encoder: "h264_amf")
        );
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        var options = await admin.GetFromJsonAsync<JsonElement>("/api/v1/media/1/playback-options");
        Assert.Contains(
            options.GetProperty("tracks").EnumerateArray(),
            x => x.GetProperty("language").GetString() == "swe"
        );
        var created = await admin.PostAsJsonAsync(
            "/api/v1/media/1/playback-sessions",
            new PlaybackSelection(AudioTrack: 2, SubtitleTrack: -1, Encoder: "h264_nvenc")
        );
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var state = (await created.Content.ReadFromJsonAsync<ConversionState>())!;
        var repeated = await admin.PostAsJsonAsync(
            "/api/v1/media/1/playback-sessions",
            new PlaybackSelection(AudioTrack: 2, SubtitleTrack: -1, Encoder: "h264_nvenc")
        );
        Assert.Equal(state.Id, (await repeated.Content.ReadFromJsonAsync<ConversionState>())!.Id);
        for (var i = 0; i < 200 && state.State == "preparing"; i++)
        {
            await Task.Delay(150);
            state = (
                await admin.GetFromJsonAsync<ConversionState>(
                    "/api/v1/playback-sessions/" + state.Id
                )
            )!;
        }
        Assert.True(state.State == "ready", state.Error);
        var manifest = await admin.GetStringAsync(state.Manifest);
        Assert.Contains("360p.m3u8", manifest);
        Assert.Contains("720p.m3u8", manifest);
        var baseUrl = state.Manifest[..state.Manifest.LastIndexOf('/')];
        foreach (var level in TranscodeProfile.Ladder)
        {
            var playlist = await admin.GetStringAsync(baseUrl + "/" + level.Name + ".m3u8");
            Assert.Contains("#EXT-X-ENDLIST", playlist);
            var segment = playlist
                .Split('\n')
                .First(x => x.EndsWith(".ts", StringComparison.Ordinal));
            var bytes = await admin.GetByteArrayAsync(baseUrl + "/" + segment);
            Assert.True(bytes.Length > 188);
            Assert.Equal(0x47, bytes[0]);
        }
        Assert.DoesNotContain("<b>", await admin.GetStringAsync(baseUrl + "/captions.vtt"));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.GetAsync(baseUrl + "/source.bin")).StatusCode
        );
        using var anonymous = server.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync(state.Manifest)).StatusCode
        );
        using var other = server.CreateClient();
        await Server.Csrf(other);
        await other.PostAsJsonAsync(
            "/api/auth/login",
            new { name = "admin", password = "twelve-character-password" }
        );
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(state.Manifest)).StatusCode);
        await Server.Csrf(other);
        var joined = await other.PostAsJsonAsync(
            "/api/v1/media/1/playback-sessions",
            new PlaybackSelection(AudioTrack: 2, SubtitleTrack: -1, Encoder: "h264_nvenc")
        );
        Assert.Equal(HttpStatusCode.Accepted, joined.StatusCode);
        Assert.Equal(state.Id, (await joined.Content.ReadFromJsonAsync<ConversionState>())!.Id);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync(state.Manifest)).StatusCode);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await admin.DeleteAsync("/api/v1/playback-sessions/" + state.Id)).StatusCode
        );
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(state.Manifest)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync(state.Manifest)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await other.DeleteAsync("/api/v1/playback-sessions/" + state.Id)).StatusCode
        );
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(state.Manifest)).StatusCode);
        var externalTrack = options
            .GetProperty("tracks")
            .EnumerateArray()
            .Single(x =>
                x.GetProperty("codec").GetString() == "subrip"
                && x.GetProperty("language").GetString() == "swe"
            );
        var selected = new PlaybackSelection(
            SubtitleTrack: externalTrack.GetProperty("index").GetInt32()
        );
        Assert.True(selected.SubtitleTrack < -1);
        async Task<ConversionState> PrepareCaption()
        {
            var response = await admin.PostAsJsonAsync(
                "/api/v1/media/1/playback-sessions",
                selected
            );
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var value = (await response.Content.ReadFromJsonAsync<ConversionState>())!;
            for (var i = 0; i < 200 && value.State == "preparing"; i++)
            {
                await Task.Delay(100);
                value = (
                    await admin.GetFromJsonAsync<ConversionState>(
                        "/api/v1/playback-sessions/" + value.Id
                    )
                )!;
            }
            Assert.True(value.State == "ready", value.Error);
            return value;
        }
        var firstCaption = await PrepareCaption();
        var firstCaptionUrl = firstCaption.Manifest.Replace("master.m3u8", "captions.vtt");
        var rendered = await admin.GetStringAsync(firstCaptionUrl);
        Assert.Contains("Swedish caption", rendered);
        Assert.DoesNotContain("<b>", rendered);
        await File.WriteAllTextAsync(
            externalSrt,
            "1\n00:00:00,000 --> 00:00:01,000\nUpdated caption\n"
        );
        var updatedCaption = await PrepareCaption();
        Assert.NotEqual(firstCaption.Id, updatedCaption.Id);
        Assert.Contains(
            "Updated caption",
            await admin.GetStringAsync(
                updatedCaption.Manifest.Replace("master.m3u8", "captions.vtt")
            )
        );
        Assert.Contains("Swedish caption", await admin.GetStringAsync(firstCaptionUrl));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync(firstCaptionUrl)).StatusCode
        );
        await admin.DeleteAsync("/api/v1/playback-sessions/" + firstCaption.Id);
        await admin.DeleteAsync("/api/v1/playback-sessions/" + updatedCaption.Id);
        Assert.True(File.Exists(path));
    }
}
