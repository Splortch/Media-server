using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Api.Tests;

public sealed class ScanProbeHandleTests
{
    private sealed class ReplacingProbe(Action replace, Func<Ffprobe>? probe = null) : IMediaProbe
    {
        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct)
        {
            replace();
            return new Ffprobe("ffprobe").ProbeAsync(path, ct);
        }

        public Task<ProbeResult> ProbeFileAsync(FileStream source, CancellationToken ct)
        {
            replace();
            return probe is null
                ? Task.FromResult(new ProbeResult(1, "mp4", "h264", ""))
                : probe().ProbeFileAsync(source, ct);
        }
    }

    // A pathname-only third-party probe must not silently bypass pinned scanning.
    private sealed class PathnameOnlyProbe : IMediaProbe
    {
        public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct) =>
            throw new InvalidOperationException("The pathname fallback must not run.");
    }

    [Theory]
    [InlineData("file-link", false)]
    [InlineData("directory-link", false)]
    [InlineData("replacement", false)]
    [InlineData("file-link", true)]
    [InlineData("directory-link", true)]
    [InlineData("replacement", true)]
    public async Task ScanProbesTheOpenedInodeAfterItsPathIsReplaced(string replacement, bool stage)
    {
        // Windows' validated handle denies rename/delete sharing on the source.
        if (!(OperatingSystem.IsLinux() || (stage && OperatingSystem.IsMacOS())))
            return;
        string path = "",
            directory = "",
            outside = "",
            parked = "";
        string? stagingPath = null;
        var probe = new ReplacingProbe(
            () =>
            {
                if (replacement == "directory-link")
                {
                    Directory.Move(directory, parked);
                    Directory.CreateSymbolicLink(directory, Path.GetDirectoryName(outside)!);
                }
                else
                {
                    File.Move(path, parked);
                    if (replacement == "file-link")
                        File.CreateSymbolicLink(path, outside);
                    else
                        File.Copy(outside, path);
                }
            },
            () => new Ffprobe("ffprobe", stagingPath)
        );
        using var server = new Server(configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IMediaProbe>(probe))
        );
        stagingPath = stage ? Path.Combine(server.DataPath, "scan-probe.tmp") : null;
        directory = Path.Combine(server.MediaPath, "folder");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "movie.mp4");
        var privateDirectory = Path.Combine(server.Root, "private");
        Directory.CreateDirectory(privateDirectory);
        outside = Path.Combine(privateDirectory, "movie.mp4");
        parked = Path.Combine(server.Root, "pinned-original");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await CreateVideo(path, "Original title", timeout.Token);
        await CreateVideo(outside, "Private title must not be read", timeout.Token);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var bytes = await File.ReadAllBytesAsync(path, timeout.Token);
        var modified = File.GetLastWriteTimeUtc(path).Ticks;
        using var admin = await server.Admin();
        var job = await Scan(server, admin);
        Assert.Equal(0, job.GetProperty("failed").GetInt32());
        using var scope = server.Services.CreateScope();
        var item = Assert.Single(
            await scope
                .ServiceProvider.GetRequiredService<MediaDb>()
                .Items.IgnoreQueryFilters()
                .ToListAsync()
        );
        Assert.Equal("Original title", item.Title);
        Assert.Equal("folder/movie.mp4", item.RelativePath);
        Assert.Equal(bytes.Length, item.Size);
        Assert.Equal(modified, item.ModifiedTicks);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), item.Fingerprint);
        Assert.Contains("h264", item.VideoCodec);
        if (stage)
            Assert.False(File.Exists(stagingPath));
    }

    [Fact]
    public async Task ScanRejectsAFileChangedThroughTheOpenedInode()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string path = "";
        var probe = new ReplacingProbe(() => File.AppendAllText(path, "changed"));
        using var server = new Server(configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IMediaProbe>(probe))
        );
        path = Path.Combine(server.MediaPath, "movie.mp4");
        await File.WriteAllTextAsync(path, "original");
        using var admin = await server.Admin();
        var job = await Scan(server, admin);
        Assert.Equal(1, job.GetProperty("failed").GetInt32());
        using var scope = server.Services.CreateScope();
        Assert.Empty(
            await scope
                .ServiceProvider.GetRequiredService<MediaDb>()
                .Items.IgnoreQueryFilters()
                .ToListAsync()
        );
    }

    [Fact]
    public async Task ScanFailsClosedForAPathnameOnlyProbe()
    {
        using var server = new Server(configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IMediaProbe>(new PathnameOnlyProbe()))
        );
        await File.WriteAllTextAsync(Path.Combine(server.MediaPath, "movie.mp4"), "original");
        using var admin = await server.Admin();
        var job = await Scan(server, admin);
        Assert.Equal(1, job.GetProperty("failed").GetInt32());
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
        Assert.Empty(await db.Items.IgnoreQueryFilters().ToListAsync());
        Assert.Equal("pinned_probe_unavailable", (await db.ScanIssues.SingleAsync()).Code);
    }

    [Fact]
    public async Task StagedProbeUsesTheOpenFileAndCleansUpAfterSuccessAndFailure()
    {
        using var server = new Server();
        Directory.CreateDirectory(server.DataPath);
        var path = Path.Combine(server.MediaPath, "movie.mp4");
        var stagedPath = Path.Combine(server.DataPath, "scan-probe.tmp");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await CreateVideo(path, "Original title", timeout.Token);
        // Allow rename on Windows to exercise the probe independently of the
        // sharing protections of PathPolicy.OpenRead.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
        source.Position = source.Length;
        File.Move(path, Path.Combine(server.MediaPath, "original.mp4"));
        await File.WriteAllTextAsync(path, "invalid replacement", timeout.Token);
        var probe = new Ffprobe("ffprobe", stagedPath);
        var result = await probe.ProbeFileAsync(source, timeout.Token);
        Assert.Equal("Original title", result.Metadata?.Title);
        Assert.False(File.Exists(stagedPath));

        using var invalid = new PathPolicy([server.MediaPath]).OpenRead(
            server.MediaPath,
            "movie.mp4"
        );
        var error = await Assert.ThrowsAsync<MediaProbeException>(() =>
            probe.ProbeFileAsync(invalid, timeout.Token)
        );
        Assert.Equal("invalid_media", error.Code);
        Assert.False(File.Exists(stagedPath));
    }

    [Fact]
    public async Task NativeStagedScanReclaimsAnInterruptedCopyOnStartup()
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()))
            return;
        using var server = new Server(realProbe: true);
        Directory.CreateDirectory(server.DataPath);
        var stagedPath = Path.Combine(server.DataPath, "scan-probe.tmp");
        await File.WriteAllTextAsync(stagedPath, "interrupted scan");
        using var admin = await server.Admin();
        Assert.False(File.Exists(stagedPath));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await CreateVideo(Path.Combine(server.MediaPath, "movie.mp4"), "Movie", timeout.Token);
        var job = await Scan(server, admin);
        Assert.Equal(1, job.GetProperty("discovered").GetInt32());
        Assert.Equal(0, job.GetProperty("failed").GetInt32());
        Assert.False(File.Exists(stagedPath));
    }

    private static async Task<JsonElement> Scan(Server server, HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/libraries",
            new
            {
                name = "Movies",
                path = server.MediaPath,
                computeHashes = true,
            }
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var library = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
        response = await admin.PostAsync($"/api/libraries/{library}/scan", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var job = await admin.GetFromJsonAsync<JsonElement>($"/api/scans/{id}", timeout.Token);
            if (job.GetProperty("state").GetString() == "completed")
                return job;
            Assert.NotEqual("failed", job.GetProperty("state").GetString());
            await Task.Delay(25, timeout.Token);
        }
    }

    private static Task CreateVideo(string path, string title, CancellationToken ct) =>
        new FfmpegRunner("ffmpeg").RunAsync(
            [
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=160x90:rate=12",
                "-t",
                "0.25",
                "-c:v",
                "libx264",
                "-threads",
                "1",
                "-metadata",
                "title=" + title,
                "-an",
                path,
            ],
            Path.GetDirectoryName(path)!,
            ct
        );
}
