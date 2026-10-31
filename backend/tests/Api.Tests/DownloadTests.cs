using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using MediaServer.Core;
using MediaServer.Downloads;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Api.Tests;

public sealed class DownloadTests
{
    private sealed class Space : IDownloadSpace
    {
        public long Free = DownloadStorage.TotalLimit * 2;

        public long Available(string directory) => Free;
    }

    // Only the remote HTTP source is controlled. Real API, SQLite, worker, protection and staging run unchanged.
    private sealed class Source : HttpMessageHandler, IDownloadTransport
    {
        public byte[] Bytes = Enumerable
            .Range(0, 2 * 1024 * 1024)
            .Select(i => (byte)(i % 251))
            .ToArray();
        public bool Slow,
            IgnoreRange,
            ChangeTag,
            Redirect,
            UnknownLength,
            Disconnect;
        public int Calls;
        public readonly List<long> Offsets = [];
        public readonly List<string> Paths = [];

        public HttpClient Create(string[] hosts) =>
            new(this, false) { Timeout = Timeout.InfiniteTimeSpan };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Interlocked.Increment(ref Calls);
            var offset = request.Headers.Range?.Ranges.Single().From ?? 0;
            lock (Offsets)
                Offsets.Add(offset);
            lock (Paths)
                Paths.Add(request.RequestUri!.AbsolutePath);
            if (Redirect)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://unapproved.example/secret");
                return Task.FromResult(redirect);
            }
            var start = IgnoreRange ? 0 : offset;
            var response = new HttpResponseMessage(
                start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK
            );
            response.Headers.ETag = new EntityTagHeaderValue(
                ChangeTag && offset > 0 ? "\"changed\"" : "\"fixture-v1\""
            );
            response.Content = new StreamContent(new Body(Bytes, (int)start, Slow, Disconnect));
            if (!UnknownLength)
                response.Content.Headers.ContentLength = Bytes.Length - start;
            if (start > 0)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    start,
                    Bytes.Length - 1,
                    Bytes.Length
                );
            if (offset > 0)
                Assert.Equal("\"fixture-v1\"", request.Headers.IfRange!.EntityTag!.ToString());
            return Task.FromResult(response);
        }
    }

    private sealed class Body(byte[] bytes, int start, bool slow, bool disconnect) : Stream
    {
        private readonly int origin = start;
        private int position = start;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length - origin;
        public override long Position
        {
            get => position - origin;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken ct = default
        )
        {
            if (slow)
                await Task.Delay(20, ct);
            if (disconnect && position > 65536)
                throw new IOException("Synthetic network disconnect");
            var count = Math.Min(Math.Min(buffer.Length, 16384), bytes.Length - position);
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long length) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private static Server Create(Source source, Space? space = null) =>
        new(configure: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IDownloadTransport>(source));
            services.Replace(ServiceDescriptor.Singleton<IDownloadSpace>(space ?? new Space()));
        });

    private static async Task Enable(HttpClient client, bool enabled = true) =>
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PutAsJsonAsync(
                    "/api/downloads/settings",
                    new { enabled, hosts = new[] { "media.example" } }
                )
            ).StatusCode
        );

    private static async Task<DownloadView> Add(
        HttpClient client,
        string name = "Fixture",
        long budget = 4 * 1024 * 1024,
        string? hash = null
    )
    {
        var response = await client.PostAsJsonAsync(
            "/api/downloads",
            new DownloadInput(
                name,
                "https://media.example/" + Uri.EscapeDataString(name) + "?token=synthetic-secret",
                budget,
                hash
            )
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DownloadView>())!;
    }

    private static async Task<DownloadView> Wait(
        HttpClient client,
        string id,
        Func<DownloadView, bool> ready
    )
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        DownloadView? last = null;
        while (DateTime.UtcNow < until)
        {
            last = (await client.GetFromJsonAsync<DownloadView[]>("/api/downloads"))!.Single(x =>
                x.Id == id
            );
            if (ready(last))
                return last;
            await Task.Delay(30);
        }
        Assert.Fail($"Download did not reach expected state; last: {last}");
        return null!;
    }

    private static async Task Act(HttpClient client, DownloadView job, string action) =>
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PutAsJsonAsync(
                    "/api/downloads/" + job.Id,
                    new DownloadAction(action, job.Revision)
                )
            ).StatusCode
        );

    [Fact]
    public async Task DisabledByDefaultAndAllEndpointsRequireAdministrator()
    {
        var source = new Source();
        using var server = Create(source);
        using var admin = await server.Admin();
        Assert.False(
            (await admin.GetFromJsonAsync<DownloadSettingsView>("/api/downloads/settings"))!.Enabled
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await admin.PostAsJsonAsync(
                    "/api/downloads",
                    new DownloadInput("Fixture", "https://media.example/file", 100, null)
                )
            ).StatusCode
        );
        using var anonymous = server.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/downloads/settings")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/downloads/any/file")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Created,
            (
                await admin.PostAsJsonAsync(
                    "/api/users",
                    new
                    {
                        name = "viewer",
                        password = "viewer-long-password",
                        libraries = Array.Empty<int>(),
                    }
                )
            ).StatusCode
        );
        using var viewer = server.CreateClient();
        await Server.Csrf(viewer);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await viewer.PostAsJsonAsync(
                    "/api/auth/login",
                    new { name = "viewer", password = "viewer-long-password" }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await viewer.GetAsync("/api/v1/downloads/settings")).StatusCode
        );
        Assert.Equal(0, source.Calls);
        Assert.False(Directory.Exists(Path.Combine(server.DataPath, "downloads")));
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PutAsJsonAsync(
                    "/api/downloads/settings",
                    new { enabled = true, hosts = new[] { "media.example" } }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task TransferHashesStagesAndServesAuthenticatedRangeWithoutLeakingTokens()
    {
        var source = new Source();
        using var server = Create(source);
        using var client = await server.Admin();
        await Enable(client);
        var hash = Convert.ToHexString(SHA256.HashData(source.Bytes)).ToLowerInvariant();
        var job = await Add(client, hash: hash);
        job = await Wait(client, job.Id, x => x.State is "completed" or "failed");
        Assert.Equal("completed", job.State);
        Assert.Equal(hash, job.ContentHash);
        var json = await client.GetStringAsync("/api/v1/downloads");
        Assert.DoesNotContain("synthetic-secret", json);
        Assert.DoesNotContain("protectedUrl", json);
        using (var scope = server.Services.CreateScope())
            Assert.Equal(
                "",
                (
                    await scope
                        .ServiceProvider.GetRequiredService<MediaDb>()
                        .Downloads.SingleAsync()
                ).ProtectedUrl
            );
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/downloads/{job.Id}/file");
        request.Headers.Range = new RangeHeaderValue(10, 99);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(source.Bytes[10..100], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/downloads/{job.Id}/{job.Revision}")).StatusCode
        );
        Assert.Empty(Directory.GetFiles(Path.Combine(server.DataPath, "downloads")));
    }

    [Fact]
    public async Task PauseResumeUsesExactRangeAndCancelDeletesPartial()
    {
        var source = new Source { Slow = true };
        using var server = Create(source);
        using var client = await server.Admin();
        await Enable(client);
        var job = await Add(client);
        job = await Wait(client, job.Id, x => x.ReceivedBytes > 0);
        await Act(client, job, "pause");
        job = await Wait(client, job.Id, x => x.State == "paused");
        var offset = job.ReceivedBytes;
        Assert.InRange(offset, 1, source.Bytes.Length - 1);
        using (var scope = server.Services.CreateScope())
        {
            var row = await scope
                .ServiceProvider.GetRequiredService<MediaDb>()
                .Downloads.SingleAsync();
            Assert.DoesNotContain("synthetic-secret", row.ProtectedUrl);
            Assert.NotEmpty(row.ProtectedUrl);
        }
        await Act(client, job, "resume");
        job = await Wait(client, job.Id, x => x.State is "completed" or "failed");
        Assert.Equal("completed", job.State);
        Assert.Contains(offset, source.Offsets);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(source.Bytes)).ToLowerInvariant(),
            job.ContentHash
        );
        var cancel = await Add(client, "Cancel");
        cancel = await Wait(client, cancel.Id, x => x.ReceivedBytes > 0);
        await Act(client, cancel, "cancel");
        cancel = await Wait(client, cancel.Id, x => x.State == "cancelled");
        Assert.Equal(0, cancel.ReceivedBytes);
        Assert.False(File.Exists(Path.Combine(server.DataPath, "downloads", cancel.Id + ".part")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ChangedSourceOrIgnoredRangeNeverAppends(bool ignore, bool change)
    {
        var source = new Source { Slow = true };
        using var server = Create(source);
        using var client = await server.Admin();
        await Enable(client);
        var job = await Add(client);
        job = await Wait(client, job.Id, x => x.ReceivedBytes > 0);
        await Act(client, job, "pause");
        job = await Wait(client, job.Id, x => x.State == "paused");
        var offset = job.ReceivedBytes;
        source.IgnoreRange = ignore;
        source.ChangeTag = change;
        await Act(client, job, "resume");
        job = await Wait(client, job.Id, x => x.State == "failed");
        Assert.Equal("source-changed-or-range-ignored", job.Error);
        Assert.Equal(offset, job.ReceivedBytes);
    }

    [Fact]
    public async Task DisablePausesAllAndStaleControlsConflict()
    {
        var source = new Source { Slow = true };
        using var server = Create(source);
        using var client = await server.Admin();
        await Enable(client);
        var first = await Add(client, "First");
        await Wait(client, first.Id, x => x.ReceivedBytes > 0);
        var next = await Add(client, "Next");
        await Act(client, next, "pause");
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await client.PutAsJsonAsync(
                    "/api/downloads/" + next.Id,
                    new DownloadAction("resume", next.Revision)
                )
            ).StatusCode
        );
        await Enable(client, false);
        var calls = source.Calls;
        await Task.Delay(100);
        Assert.Equal(calls, source.Calls);
        await Enable(client);
        Assert.All(
            (await client.GetFromJsonAsync<DownloadView[]>("/api/downloads"))!,
            x => Assert.Equal("paused", x.State)
        );
        Assert.True(
            (
                await client.GetFromJsonAsync<DownloadSettingsView>("/api/downloads/settings")
            )!.ReservedBytes > 0
        );
    }

    [Fact]
    public async Task BudgetRedirectHashAndDisconnectFailuresAreContained()
    {
        var source = new Source();
        var space = new Space();
        using var server = Create(source, space);
        using var client = await server.Admin();
        await Enable(client);
        space.Free = DownloadStorage.Margin;
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await client.PostAsJsonAsync(
                    "/api/downloads",
                    new DownloadInput("No space", "https://media.example/file", 100, null)
                )
            ).StatusCode
        );
        space.Free = DownloadStorage.TotalLimit * 2;
        var job = await Add(client, budget: 100);
        job = await Wait(client, job.Id, x => x.State == "failed");
        Assert.Equal("source-exceeds-budget", job.Error);
        source.Redirect = true;
        var before = source.Calls;
        job = await Add(client);
        await Wait(client, job.Id, x => x.State == "failed");
        Assert.Equal(before + 1, source.Calls);
        source.Redirect = false;
        job = await Add(client, hash: new string('0', 64));
        job = await Wait(client, job.Id, x => x.State == "failed");
        Assert.Equal("content-hash-mismatch", job.Error);
        source.Disconnect = true;
        job = await Add(client);
        job = await Wait(client, job.Id, x => x.State == "failed");
        Assert.Equal("storage-or-connection-error", job.Error);
    }

    [Fact]
    public async Task UnknownLengthIsBoundedAndActualEndLengthIsSaved()
    {
        var source = new Source { UnknownLength = true };
        using var server = Create(source);
        using var client = await server.Admin();
        await Enable(client);
        var job = await Add(client);
        job = await Wait(client, job.Id, x => x.State is "completed" or "failed");
        Assert.Equal("completed", job.State);
        Assert.Equal(source.Bytes.Length, job.TotalBytes);
        job = await Add(client, budget: 100);
        job = await Wait(client, job.Id, x => x.State == "failed");
        Assert.Equal("source-exceeds-budget", job.Error);
        Assert.Equal(0, job.ReceivedBytes);
    }

    [Fact]
    public async Task SpaceExhaustionDuringTransferStopsBeforeCompletionAndCanRetrySafely()
    {
        var source = new Source { Slow = true };
        var space = new Space();
        using var server = Create(source, space);
        using var client = await server.Admin();
        await Enable(client);
        var job = await Add(client);
        await Wait(client, job.Id, x => x.ReceivedBytes > 0);
        space.Free = DownloadStorage.Margin;
        job = await Wait(client, job.Id, x => x.State == "failed");
        Assert.Equal("insufficient-storage", job.Error);
        Assert.InRange(job.ReceivedBytes, 1, source.Bytes.Length - 1);
        space.Free = DownloadStorage.TotalLimit * 2;
        source.Slow = false;
        await Act(client, job, "resume");
        job = await Wait(client, job.Id, x => x.State is "completed" or "failed");
        Assert.Equal("completed", job.State);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(source.Bytes)).ToLowerInvariant(),
            job.ContentHash
        );
    }

    [Fact]
    public async Task ProductionTransportRejectsLoopbackBeforeOpeningASourceConnection()
    {
        using var client = new DownloadNetwork().Create(["127.0.0.1"]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync("https://127.0.0.1/file", timeout.Token)
        );
    }

    [Fact]
    public async Task RestartRecoversInterruptedQueueWithoutFetching()
    {
        var source = new Source();
        using var server = Create(source);
        Directory.CreateDirectory(server.DataPath);
        var options = new DbContextOptionsBuilder<MediaDb>()
            .UseSqlite("Data Source=" + Path.Combine(server.DataPath, "media.db"))
            .Options;
        var id = Guid.NewGuid().ToString("N");
        await using (var db = new MediaDb(options))
        {
            await db.Database.MigrateAsync();
            db.Users.Add(
                new User
                {
                    Name = "admin",
                    PasswordHash = await Passwords.Hash("restart-fixture-password"),
                }
            );
            db.FeaturePolicies.Add(new FeaturePolicy { Key = "downloads", Enabled = true });
            db.DownloadPolicies.Add(new DownloadPolicy { HostsJson = "[\"media.example\"]" });
            db.Downloads.Add(
                new DownloadJob
                {
                    Id = id,
                    UserId = 1,
                    Name = "Interrupted",
                    SourceHost = "media.example",
                    ProtectedUrl = "unavailable-key",
                    State = "running",
                    ReceivedBytes = 3,
                    MaxBytes = 4096,
                }
            );
            await db.SaveChangesAsync();
        }
        Directory.CreateDirectory(Path.Combine(server.DataPath, "downloads"));
        await File.WriteAllBytesAsync(
            Path.Combine(server.DataPath, "downloads", id + ".part"),
            [1, 2, 3]
        );
        using var client = server.CreateClient();
        await Server.Csrf(client);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PostAsJsonAsync(
                    "/api/auth/login",
                    new { name = "admin", password = "restart-fixture-password" }
                )
            ).StatusCode
        );
        var job = await Wait(client, id, x => x.State == "paused");
        Assert.Equal("server-restarted", job.Error);
        Assert.False(job.CanResume);
        Assert.Equal(0, source.Calls);
        Assert.Equal(
            new byte[] { 1, 2, 3 },
            await File.ReadAllBytesAsync(Path.Combine(server.DataPath, "downloads", id + ".part"))
        );
    }

    [Fact]
    public async Task PriorityOrdersPendingDownloads()
    {
        var source = new Source { Slow = true };
        using var server = Create(source);
        using var client = await server.Admin();
        await Enable(client);
        var first = await Add(client, "First");
        await Wait(client, first.Id, x => x.ReceivedBytes > 0);
        var low = await Add(client, "Low");
        var high = await Add(client, "High");
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PutAsJsonAsync(
                    "/api/downloads/" + high.Id,
                    new DownloadAction("priority", high.Revision, 7)
                )
            ).StatusCode
        );
        source.Slow = false;
        await Wait(client, low.Id, x => x.State == "completed");
        Assert.Equal(new[] { "/First", "/High", "/Low" }, source.Paths);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.1.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("2001:db8::1")]
    public void PrivateNetworksRejected(string address) =>
        Assert.False(DownloadNetwork.Public(IPAddress.Parse(address)));

    [Theory]
    [InlineData("http://media.example/file")]
    [InlineData("https://media.example:444/file")]
    [InlineData("https://user:pass@media.example/file")]
    [InlineData("https://other.example/file")]
    [InlineData("https://media.example/file#fragment")]
    public void InvalidUrlsRejected(string url) =>
        Assert.Throws<ArgumentException>(() => DownloadNetwork.Url(url, ["media.example"]));
}
