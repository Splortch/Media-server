using MediaServer.Core;
using MediaServer.Media;
using Xunit;

namespace Api.Tests;

public sealed class ProbeBoundaryTests
{
    [Theory]
    [InlineData("mp4", "libx264")]
    [InlineData("m4v", "libx264")]
    [InlineData("mov", "libx264")]
    [InlineData("mkv", "libx264")]
    [InlineData("webm", "libvpx-vp9")]
    [InlineData("avi", "mpeg4")]
    public async Task SupportedContainersStillProbeForScansAndImports(
        string extension,
        string codec
    )
    {
        var root = Path.Combine(TestPaths.TempRoot, "probe-boundary-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var path = Path.Combine(root, "movie." + extension);
            await CreateVideo(root, path, codec, timeout.Token);
            var probe = new Ffprobe("ffprobe");
            var scan = await probe.ProbeAsync(path, timeout.Token);
            var import = await probe.ProbeImportAsync(path, timeout.Token);
            Assert.NotEmpty(scan.VideoCodec);
            Assert.Equal(scan.Container, import.Container);
            Assert.Equal(scan.VideoCodec, import.VideoCodec);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RenamedIndirectInputsAreRejected(bool imports, bool hls)
    {
        var root = Path.Combine(TestPaths.TempRoot, "probe-playlist-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // A real local video makes this a readable indirect input without the
            // demuxer allowlist, rather than a rejection of a broken media fixture.
            await CreateVideo(root, Path.Combine(root, "private.ts"), "mpeg2video", timeout.Token);
            var path = Path.Combine(root, "movie.mp4");
            await File.WriteAllTextAsync(
                path,
                hls
                    ? "#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\nprivate.ts\n#EXT-X-ENDLIST\n"
                    : "ffconcat version 1.0\nfile 'private.ts'\n",
                timeout.Token
            );
            var probe = new Ffprobe("ffprobe");
            var error = await Assert.ThrowsAsync<MediaProbeException>(() =>
                imports
                    ? probe.ProbeImportAsync(path, timeout.Token)
                    : probe.ProbeAsync(path, timeout.Token)
            );
            Assert.Equal("invalid_media", error.Code);
            Assert.DoesNotContain(root, error.Message);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static Task CreateVideo(string root, string path, string codec, CancellationToken ct) =>
        new FfmpegRunner("ffmpeg").RunAsync(
            [
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=160x90:rate=12",
                "-t",
                "1",
                "-c:v",
                codec,
                "-threads",
                "1",
                "-an",
                path,
            ],
            root,
            ct
        );
}
