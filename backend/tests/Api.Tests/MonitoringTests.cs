using System.Net;
using System.Net.Http.Json;
using MediaServer.Media;
using MediaServer.Services;
using Xunit;

namespace Api.Tests;

public sealed class MonitoringTests
{
    [Fact]
    public async Task ProgressParsingDropsRawOutputOversizedLinesAndNonfiniteNumbers()
    {
        var samples = new List<FfmpegProgress>();
        var raw =
            "filename=/private/secret.mkv\n"
            + new string('x', 50_000)
            + "\n"
            + "frame=12\nfps=24.5\nout_time_us=500000\nspeed=1.5x\nprogress=continue\n"
            + "frame=-2\nfps=NaN\nout_time_us=-1\nspeed=Infinity\nprogress=end\n";
        await FfmpegProgressReader.ReadAsync(
            new StringReader(raw),
            samples.Add,
            CancellationToken.None
        );
        Assert.Equal(2, samples.Count);
        Assert.Equal(new FfmpegProgress(12, 24.5, 0.5, 1.5), samples[0]);
        Assert.Equal(new FfmpegProgress(0, null, null, null), samples[1]);
    }

    [Fact]
    public async Task RealEncoderPublishesActualFrameCounts()
    {
        var samples = new List<FfmpegProgress>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await new FfmpegRunner("ffmpeg").RunAsync(
            [
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=64x64:rate=24",
                "-frames:v",
                "12",
                "-c:v",
                "libx264",
                "-threads",
                "1",
                "-f",
                "null",
                "-",
            ],
            TestPaths.TempRoot,
            timeout.Token,
            samples.Add
        );
        Assert.NotEmpty(samples);
        Assert.Equal(12, samples[^1].Frames);
        Assert.All(samples, x => Assert.True(x.FramesPerSecond is null or >= 0));
    }

    [Fact]
    public void NvidiaMetricsAreNumericOnlyAndMissingCountersRemainUnavailable()
    {
        var snapshot = GpuMetrics.Parse("0, 25, 1024, 12288\n1, N/A, N/A, N/A\n");
        Assert.Equal(2, snapshot.Devices.Length);
        Assert.Equal(25, snapshot.Devices[0].UtilizationPercent);
        Assert.Equal(1024L * 1024 * 1024, snapshot.Devices[0].MemoryUsedBytes);
        Assert.Null(snapshot.Devices[1].UtilizationPercent);
        Assert.Null(snapshot.Devices[1].MemoryTotalBytes);
        Assert.NotNull(snapshot.SampledAt);
    }

    [Theory]
    [InlineData("0, NaN, 1, 2")]
    [InlineData("0, 101, 1, 2")]
    [InlineData("0, 1, 3, 2")]
    [InlineData("32, 1, 1, 2")]
    [InlineData("0, 1, 1, 2\n0, 2, 1, 2")]
    [InlineData("/private/path, 1, 1, 2")]
    [InlineData("0, 1, -1, 2")]
    public void InvalidVendorOutputFailsClosed(string csv) =>
        Assert.Throws<IOException>(() => GpuMetrics.Parse(csv));

    [Fact]
    public async Task DisabledAndMissingToolsReturnSafeStatuses()
    {
        var disabled = await new GpuMetrics(false, "not-a-real-executable").ReadAsync(
            CancellationToken.None
        );
        Assert.Equal("Disabled by operator", disabled.Status);
        Assert.Empty(disabled.Devices);
        var unavailable = await new GpuMetrics(true, "missing-nvidia-smi-fixture").ReadAsync(
            CancellationToken.None
        );
        Assert.StartsWith("Unavailable:", unavailable.Status);
        Assert.DoesNotContain("missing-nvidia", unavailable.Status);
        Assert.Empty(unavailable.Devices);
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1")]
    public async Task MonitoringRequiresAdministratorForEveryAlias(string prefix)
    {
        using var server = new Server();
        using var anonymous = server.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync(prefix + "/monitoring")).StatusCode
        );
        using var admin = await server.Admin();
        var created = await admin.PostAsJsonAsync(
            "/api/users",
            new
            {
                name = "observer",
                password = "observer-long-password",
                libraries = Array.Empty<int>(),
            }
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var viewer = server.CreateClient();
        await Server.Csrf(viewer);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await viewer.PostAsJsonAsync(
                    "/api/auth/login",
                    new { name = "observer", password = "observer-long-password" }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await viewer.GetAsync(prefix + "/monitoring")).StatusCode
        );
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(prefix + "/monitoring")).StatusCode);
    }
}
