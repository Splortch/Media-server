using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using MediaServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public sealed class RemuxPlaybackTests
{
    [Fact]
    public async Task SelectedAacRemuxPublishesSeekablePrivateSegmentsAndRechecksTheSource()
    {
        using var server = new Server(realProbe: true);
        using var admin = await server.Admin();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var runner = new FfmpegRunner("ffmpeg");
        var probe = new Ffprobe("ffprobe");
        var path = Path.Combine(server.MediaPath, "source.mkv");
        await runner.RunAsync(
            [
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=192x108:rate=12",
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=440",
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=660",
                "-t",
                "9",
                "-map",
                "0:v:0",
                "-map",
                "1:a:0",
                "-map",
                "2:a:0",
                "-c:v",
                "libx264",
                "-preset",
                "ultrafast",
                "-threads",
                "1",
                "-pix_fmt",
                "yuv420p",
                "-g",
                "48",
                "-sc_threshold",
                "0",
                "-c:a:0",
                "pcm_s16le",
                "-c:a:1",
                "aac",
                path,
            ],
            server.Root,
            timeout.Token
        );
        var source = await probe.ProbeAsync(path, timeout.Token);
        var aac = Assert.Single(source.Tracks!, x => x.Type == "audio" && x.Codec == "aac").Index;
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Libraries.Add(new Library { Id = 1, Path = server.MediaPath });
            db.Items.Add(
                new MediaItem
                {
                    Id = 1,
                    LibraryId = 1,
                    Duration = source.Duration,
                    RelativePath = "source.mkv",
                    Size = new FileInfo(path).Length,
                    TracksJson = JsonSerializer.Serialize(source.Tracks),
                    VideoCodec = source.VideoCodec,
                    AudioCodec = source.AudioCodec,
                    ColorJson = source.ColorJson,
                }
            );
            await db.SaveChangesAsync(timeout.Token);
        }
        const string endpoint = "/api/v1/media/1/playback-sessions";
        var options = await admin.GetFromJsonAsync<JsonElement>(
            "/api/v1/media/1/playback-options",
            timeout.Token
        );
        Assert.False(options.GetProperty("remuxAvailable").GetBoolean());
        Assert.Equal(
            aac,
            Assert.Single(options.GetProperty("remuxAudioTracks").EnumerateArray()).GetInt32()
        );
        using var unsupported = await admin.PostAsJsonAsync(
            endpoint,
            new PlaybackSelection(Remux: true),
            timeout.Token
        );
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);

        var selection = new PlaybackSelection(AudioTrack: aac, Remux: true);
        var state = await Start(admin, endpoint, selection, timeout.Token);
        Assert.True(state.State == "ready", state.Error);
        Assert.Equal("copy", state.Encoder);
        var playlist = await admin.GetStringAsync(state.Manifest, timeout.Token);
        Assert.Contains("#EXT-X-ENDLIST", playlist);
        var segments = playlist
            .Split('\n')
            .Where(line => line.StartsWith("original_", StringComparison.Ordinal))
            .ToArray();
        Assert.True(segments.Length > 1);
        var later = state.Manifest.Replace("master.m3u8", segments[1], StringComparison.Ordinal);
        using var anonymous = server.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync(later, timeout.Token)).StatusCode
        );
        using var range = new HttpRequestMessage(HttpMethod.Get, later);
        range.Headers.Range = new RangeHeaderValue(0, 187);
        using var packet = await admin.SendAsync(range, timeout.Token);
        Assert.Equal(HttpStatusCode.PartialContent, packet.StatusCode);
        Assert.Equal(188, (await packet.Content.ReadAsByteArrayAsync(timeout.Token)).Length);
        var segmentPath = Path.Combine(server.Root, "later.ts");
        await File.WriteAllBytesAsync(
            segmentPath,
            await admin.GetByteArrayAsync(later, timeout.Token),
            timeout.Token
        );
        await runner.RunAsync(
            ["-i", segmentPath, "-frames:v", "1", "-f", "null", "-"],
            server.Root,
            timeout.Token
        );
        var privateManifest = Path.Combine(
            server.DataPath,
            "playback-cache",
            state.Id,
            "master.m3u8"
        );
        var output = await probe.ProbeGeneratedHlsAsync(privateManifest, timeout.Token);
        Assert.Equal("aac", output.AudioCodec);
        Assert.Equal(192, TranscodeProfile.Color(output.ColorJson)!.Width);

        using var removed = await admin.DeleteAsync(
            "/api/v1/playback-sessions/" + state.Id,
            timeout.Token
        );
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.False(File.Exists(privateManifest));
        await runner.RunAsync(
            ["-f", "lavfi", "-i", "testsrc2=size=160x90:rate=12", "-t", "1", "-c:v", "mpeg4", path],
            server.Root,
            timeout.Token
        );
        var stale = await Start(admin, endpoint, selection, timeout.Token);
        Assert.Equal("failed", stale.State);
        Assert.Equal("remux_requires_conversion", stale.Error);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.GetAsync(stale.Manifest, timeout.Token)).StatusCode
        );
    }

    private static async Task<ConversionState> Start(
        HttpClient client,
        string endpoint,
        PlaybackSelection selection,
        CancellationToken ct
    )
    {
        using var created = await client.PostAsJsonAsync(endpoint, selection, ct);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var state = (await created.Content.ReadFromJsonAsync<ConversionState>(ct))!;
        var status = "/api/v1/playback-sessions/" + state.Id;
        while (state.State == "preparing")
        {
            await Task.Delay(100, ct);
            state = (await client.GetFromJsonAsync<ConversionState>(status, ct))!;
        }
        return state;
    }
}
