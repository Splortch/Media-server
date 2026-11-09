using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using MediaServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Api.Tests;

public sealed class PlaybackLifecycleTests
{
    private sealed class Attempt
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProbeResult> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GatedProbe : IMediaProbe
    {
        public ConcurrentDictionary<string, Attempt> Attempts { get; } = new();

        public Attempt For(int id) =>
            Attempts.GetOrAdd("synthetic-source-" + id, _ => new Attempt());

        public async Task<ProbeResult> ProbeAsync(string path, CancellationToken ct)
        {
            var bytes = await File.ReadAllTextAsync(path, ct);
            var attempt = Attempts.GetOrAdd(bytes, _ => new Attempt());
            attempt.Started.TrySetResult();
            try
            {
                return await attempt.Result.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                attempt.Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private static Server CreateServer(GatedProbe probe) =>
        new(configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IMediaProbe>(probe))
        );

    private static async Task Seed(Server server)
    {
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
        db.Libraries.Add(
            new Library
            {
                Id = 1,
                Name = "Synthetic",
                Path = server.MediaPath,
            }
        );
        for (var id = 1; id <= 3; id++)
        {
            var bytes = "synthetic-source-" + id;
            var name = id + ".webm";
            await File.WriteAllTextAsync(Path.Combine(server.MediaPath, name), bytes);
            db.Items.Add(
                new MediaItem
                {
                    Id = id,
                    LibraryId = 1,
                    Title = name,
                    RelativePath = name,
                    Size = bytes.Length,
                    Duration = 1,
                }
            );
        }
        await db.SaveChangesAsync();
    }

    private static async Task<ConversionState> Start(HttpClient client, int id)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/media/{id}/playback-sessions",
            new PlaybackSelection()
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversionState>())!;
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition())
            await Task.Delay(20, timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedConversionDropsStagedBytesAndRetryReclaimsOnlyFailedCapacity(
        bool failBoth
    )
    {
        var probe = new GatedProbe();
        using var server = CreateServer(probe);
        using var admin = await server.Admin();
        await Seed(server);
        var first = await Start(admin, 1);
        var second = await Start(admin, 2);
        await Task.WhenAll(probe.For(1).Started.Task, probe.For(2).Started.Task)
            .WaitAsync(TimeSpan.FromSeconds(10));
        var full = await admin.PostAsJsonAsync(
            "/api/v1/media/3/playback-sessions",
            new PlaybackSelection()
        );
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.False(probe.For(3).Started.Task.IsCompleted);
        var cache = Path.Combine(server.DataPath, "playback-cache");
        Assert.True(File.Exists(Path.Combine(cache, first.Id, "source.bin")));

        probe.For(1).Result.SetException(new MediaProbeException("invalid_media"));
        if (failBoth)
            probe.For(2).Result.SetException(new MediaProbeException("invalid_media"));
        await Eventually(async () =>
        {
            var state = await admin.GetFromJsonAsync<ConversionState>(
                "/api/v1/playback-sessions/" + first.Id
            );
            return state?.State == "failed"
                && !Directory.Exists(Path.Combine(cache, first.Id))
                && (!failBoth || !Directory.Exists(Path.Combine(cache, second.Id)));
        });
        var failure = await admin.GetFromJsonAsync<ConversionState>(
            "/api/v1/playback-sessions/" + first.Id
        );
        Assert.Equal("conversion_failed_check_ffmpeg_and_filters", failure!.Error);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(first.Manifest)).StatusCode);

        var retry = await Start(admin, 3);
        await probe.For(3).Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(first.Id, retry.Id);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.GetAsync("/api/v1/playback-sessions/" + first.Id)).StatusCode
        );
        if (failBoth)
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await admin.GetAsync("/api/v1/playback-sessions/" + second.Id)).StatusCode
            );
        else
        {
            var retained = await admin.GetFromJsonAsync<ConversionState>(
                "/api/v1/playback-sessions/" + second.Id
            );
            Assert.Equal("preparing", retained!.State);
            Assert.False(probe.For(2).Cancelled.Task.IsCompleted);
        }
        Assert.Equal(
            failBoth ? 1 : 2,
            server.Services.GetRequiredService<PlaybackSessions>().Snapshot().Preparing
        );
        for (var id = 1; id <= 3; id++)
            Assert.Equal(
                "synthetic-source-" + id,
                await File.ReadAllTextAsync(Path.Combine(server.MediaPath, id + ".webm"))
            );
    }

    [Fact]
    public async Task CancellationWaitsForRealEncoderExitAndReleasesOutput()
    {
        var root = Path.Combine(TestPaths.TempRoot, "encoder-cancel-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var started = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var output = Path.Combine(root, "partial.mkv");
            var work = new FfmpegRunner("ffmpeg").RunAsync(
                [
                    "-re",
                    "-f",
                    "lavfi",
                    "-i",
                    "testsrc2=size=160x90:rate=12",
                    "-t",
                    "60",
                    "-c:v",
                    "libx264",
                    "-preset",
                    "ultrafast",
                    "-threads",
                    "1",
                    output,
                ],
                root,
                stop.Token,
                progress =>
                {
                    if (progress.Frames > 0)
                        started.TrySetResult();
                }
            );
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    work.WaitAsync(TimeSpan.FromSeconds(5))
                );
                // In particular, Windows must be able to take exclusive access
                // immediately after cancellation completes, without a retry delay.
                using (File.Open(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                File.Delete(output);
            }
            finally
            {
                stop.Cancel();
                try
                {
                    await work;
                }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LastLeaseCancelsPreparationAndDeletesCacheWithoutStoppingAnotherOwner()
    {
        var probe = new GatedProbe();
        using var server = CreateServer(probe);
        using var admin = await server.Admin();
        await Seed(server);
        var first = await Start(admin, 1);
        await probe.For(1).Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var other = server.CreateClient();
        await Server.Csrf(other);
        var login = await other.PostAsJsonAsync(
            "/api/auth/login",
            new { name = "admin", password = "twelve-character-password" }
        );
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await Server.Csrf(other);
        // A different login cannot inspect or remove a job before acquiring a lease.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await other.DeleteAsync("/api/v1/playback-sessions/" + first.Id)).StatusCode
        );
        var joined = await Start(other, 1);
        Assert.Equal(first.Id, joined.Id);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await admin.DeleteAsync("/api/v1/playback-sessions/" + first.Id)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.GetAsync("/api/v1/playback-sessions/" + first.Id)).StatusCode
        );
        Assert.False(probe.For(1).Cancelled.Task.IsCompleted);
        Assert.Equal(
            HttpStatusCode.OK,
            (await other.GetAsync("/api/v1/playback-sessions/" + first.Id)).StatusCode
        );
        var directory = Path.Combine(server.DataPath, "playback-cache", first.Id);
        Assert.True(Directory.Exists(directory));

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await other.DeleteAsync("/api/v1/playback-sessions/" + first.Id)).StatusCode
        );
        await probe.For(1).Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists(directory));
        Assert.Empty(server.Services.GetRequiredService<PlaybackSessions>().Snapshot().Jobs);
        var next = await Start(admin, 2);
        Assert.NotEqual(first.Id, next.Id);
        Assert.Equal(
            "synthetic-source-1",
            await File.ReadAllTextAsync(Path.Combine(server.MediaPath, "1.webm"))
        );
    }
}
