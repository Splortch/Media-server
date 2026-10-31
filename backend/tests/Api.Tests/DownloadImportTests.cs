using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Downloads;
using MediaServer.Infrastructure;
using MediaServer.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Api.Tests;

public sealed class DownloadImportTests
{
    private sealed class Source : HttpMessageHandler, IDownloadTransport
    {
        public byte[] Bytes = [];
        public int Calls;

        public HttpClient Create(string[] hosts) => new(this, false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Interlocked.Increment(ref Calls);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Bytes),
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture\"");
            return Task.FromResult(response);
        }
    }

    private sealed class Space : IDownloadSpace
    {
        public string? BlockedRoot;

        public long Available(string directory) =>
            directory == BlockedRoot ? 0 : DownloadStorage.TotalLimit * 3;
    }

    private sealed class ProbeGate : IMediaProbe
    {
        public TaskCompletionSource Started =
                new(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProbeResult> ProbeImportAsync(string path, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return await new Ffprobe("ffprobe").ProbeImportAsync(path, ct);
        }

        public async Task<ProbeResult> ProbeAsync(string path, CancellationToken ct)
        {
            if (path.EndsWith(".ingesting", StringComparison.Ordinal))
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return await new Ffprobe("ffprobe").ProbeAsync(path, ct);
        }
    }

    private static Server Create(Source source, Space? space = null, ProbeGate? probe = null) =>
        new(
            realProbe: true,
            configure: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IDownloadTransport>(source));
                services.Replace(ServiceDescriptor.Singleton<IDownloadSpace>(space ?? new Space()));
                if (probe != null)
                    services.Replace(ServiceDescriptor.Singleton<IMediaProbe>(probe));
            }
        );

    private static async Task Video(string path)
    {
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
                "testsrc=size=160x90:rate=12",
                "-t",
                "1",
                "-c:v",
                "libvpx-vp9",
                "-an",
                "-y",
                path,
            }
        )
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    private static async Task Setup(
        Server server,
        HttpClient admin,
        Source source,
        string kind = "movies"
    )
    {
        var fixture = Path.Combine(server.Root, "synthetic.webm");
        await Video(fixture);
        source.Bytes = await File.ReadAllBytesAsync(fixture);
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Libraries.Add(
                new Library
                {
                    Id = 1,
                    Name = "Family",
                    Path = server.MediaPath,
                    Kind = kind,
                }
            );
            await db.SaveChangesAsync();
        }
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync("/api/features/movie-requests", new { enabled = true })
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    "/api/downloads/settings",
                    new { enabled = true, hosts = new[] { "media.example" } }
                )
            ).StatusCode
        );
    }

    private static async Task<HttpClient> Member(
        Server server,
        HttpClient admin,
        string name = "family"
    )
    {
        Assert.Equal(
            HttpStatusCode.Created,
            (
                await admin.PostAsJsonAsync(
                    "/api/users",
                    new
                    {
                        name,
                        password = "fixture-family-password",
                        libraries = new[] { 1 },
                        maximumRating = 13,
                    }
                )
            ).StatusCode
        );
        var client = server.CreateClient();
        await Server.Csrf(client);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PostAsJsonAsync(
                    "/api/auth/login",
                    new { name, password = "fixture-family-password" }
                )
            ).StatusCode
        );
        await Server.Csrf(client);
        return client;
    }

    private static async Task<int> Request(HttpClient member)
    {
        var response = await member.PostAsJsonAsync(
            "/api/requests",
            new
            {
                libraryId = 1,
                title = "Synthetic fixture",
                year = 2026,
                note = "Authorized synthetic test",
            }
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
    }

    private static DownloadImportPlan Plan(string kind = "movies", bool matched = true) =>
        new(
            1,
            kind,
            "Synthetic fixture",
            2026,
            kind == "tv" && matched ? "Fixture series" : null,
            kind == "tv" && matched ? 1 : null,
            kind == "tv" && matched ? 2 : null,
            10,
            ".webm"
        );

    private static DownloadApprovalInput Approval(Source source, DownloadImportPlan? plan = null) =>
        new(
            0,
            new DownloadInput(
                "Fixture",
                "https://media.example/synthetic.webm?token=synthetic-only",
                4 * 1024 * 1024,
                Convert.ToHexString(SHA256.HashData(source.Bytes)).ToLowerInvariant()
            ),
            plan ?? Plan()
        );

    private static async Task<DownloadView> Wait(
        HttpClient admin,
        string id,
        Func<DownloadView, bool> predicate
    )
    {
        var until = DateTime.UtcNow.AddSeconds(25);
        DownloadView? row = null;
        while (DateTime.UtcNow < until)
        {
            row = (await admin.GetFromJsonAsync<DownloadView[]>("/api/downloads"))!.Single(x =>
                x.Id == id
            );
            if (predicate(row))
                return row;
            await Task.Delay(50);
        }
        Assert.Fail("Import state: " + row);
        return null!;
    }

    [Theory]
    [InlineData("movies")]
    [InlineData("tv")]
    public async Task FamilyApprovalImportsThroughRealProbeScanAndAuthenticatedPlayback(string kind)
    {
        var source = new Source();
        using var server = Create(source);
        using var admin = await server.Admin();
        await Setup(server, admin, source, kind);
        using var member = await Member(server, admin);
        var original = Path.Combine(server.MediaPath, "pre-existing.webm");
        await File.WriteAllBytesAsync(original, source.Bytes);
        var originalHash = SHA256.HashData(source.Bytes);
        var request = await Request(member);
        Assert.Equal(0, source.Calls);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await member.PostAsJsonAsync(
                    $"/api/requests/{request}/download",
                    Approval(source, Plan(kind))
                )
            ).StatusCode
        );
        var response = await admin.PostAsJsonAsync(
            $"/api/requests/{request}/download",
            Approval(source, Plan(kind))
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<DownloadView>())!;
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await admin.PostAsJsonAsync(
                    $"/api/requests/{request}/download",
                    Approval(source, Plan(kind))
                )
            ).StatusCode
        );
        job = await Wait(admin, job.Id, x => x.Import?.State is "imported" or "held");
        Assert.True(job.Import!.State == "imported", JsonSerializer.Serialize(job));
        Assert.NotNull(job.Import.MediaItemId);
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            var row = await db.Downloads.SingleAsync(x => x.Id == job.Id);
            Assert.Contains(kind == "tv" ? "TV Shows" : "Movies", row.ImportRelativePath!);
            if (kind == "tv")
                Assert.Contains("S01E02", row.ImportRelativePath!);
            Assert.Equal(
                source.Bytes,
                await File.ReadAllBytesAsync(
                    Path.Combine(server.MediaPath, row.ImportRelativePath!)
                )
            );
            Assert.True(
                File.Exists(Path.Combine(server.DataPath, "downloads", job.Id + ".download"))
            );
        }
        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(original)));
        var status = await member.GetFromJsonAsync<HouseholdDownloadStatus[]>(
            "/api/requests/download-status"
        );
        Assert.Equal("Available", Assert.Single(status!).State);
        using var range = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/media/{job.Import.MediaItemId}/stream"
        );
        range.Headers.Range = new RangeHeaderValue(0, 99);
        using var streamed = await member.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, streamed.StatusCode);
        Assert.Equal(source.Bytes[..100], await streamed.Content.ReadAsByteArrayAsync());
        using var other = await Member(server, admin, "other");
        Assert.Empty(
            (
                await other.GetFromJsonAsync<HouseholdDownloadStatus[]>(
                    "/api/requests/download-status"
                )
            )!
        );
        var json = await member.GetStringAsync("/api/requests/download-status");
        Assert.DoesNotContain("synthetic-only", json);
        Assert.DoesNotContain(server.MediaPath, json);
        Assert.DoesNotContain("sourceHost", json);
    }

    [Fact]
    public async Task UnknownEpisodeWaitsInInboxUntilExplicitMatch()
    {
        var source = new Source();
        using var server = Create(source);
        using var admin = await server.Admin();
        await Setup(server, admin, source, "tv");
        using var member = await Member(server, admin);
        var request = await Request(member);
        var result = await admin.PostAsJsonAsync(
            $"/api/requests/{request}/download",
            Approval(source, Plan("tv", false))
        );
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var job = (await result.Content.ReadFromJsonAsync<DownloadView>())!;
        job = await Wait(admin, job.Id, x => x.State == "completed");
        Assert.Equal("inbox", job.Import!.State);
        Assert.Empty((await member.GetFromJsonAsync<JsonElement[]>("/api/media"))!);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    $"/api/downloads/{job.Id}/import-plan",
                    new DownloadMatchInput(job.Revision, Plan("tv"))
                )
            ).StatusCode
        );
        job = await Wait(admin, job.Id, x => x.Import?.State is "imported" or "held");
        Assert.True(job.Import!.State == "imported", JsonSerializer.Serialize(job));
    }

    [Fact]
    public async Task RevokedLibraryAccessBeforeFinalizationKeepsFilesPrivateAndRecoverable()
    {
        var source = new Source();
        var gate = new ProbeGate();
        using var server = Create(source, probe: gate);
        using var admin = await server.Admin();
        await Setup(server, admin, source);
        using var member = await Member(server, admin);
        var request = await Request(member);
        var response = await admin.PostAsJsonAsync(
            $"/api/requests/{request}/download",
            Approval(source)
        );
        var job = (await response.Content.ReadFromJsonAsync<DownloadView>())!;
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            var user = await db.Users.SingleAsync(x => x.Name == "family");
            await db.Grants.Where(x => x.UserId == user.Id).ExecuteDeleteAsync();
        }
        gate.Release.TrySetResult();
        job = await Wait(admin, job.Id, x => x.Import?.State == "held");
        Assert.Equal("request-access-revoked", job.Import!.Error);
        Assert.Empty(
            (
                await member.GetFromJsonAsync<HouseholdDownloadStatus[]>(
                    "/api/requests/download-status"
                )
            )!
        );
        Assert.Empty(Directory.GetFiles(server.MediaPath, "*.webm", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(server.DataPath, "downloads", job.Id + ".download")));
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await admin.PutAsJsonAsync(
                    $"/api/downloads/{job.Id}/import",
                    new DownloadImportAction(job.Revision, "retry")
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task ReferencingPlaylistsAreRejectedBeforeLibraryScan()
    {
        var source = new Source();
        using var server = Create(source);
        using var admin = await server.Admin();
        await Setup(server, admin, source);
        source.Bytes = System.Text.Encoding.UTF8.GetBytes(
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\n"
                + Path.Combine(server.Root, "synthetic.webm")
                + "\n#EXT-X-ENDLIST\n"
        );
        var response = await admin.PostAsJsonAsync("/api/downloads", Approval(source).Source);
        var job = (await response.Content.ReadFromJsonAsync<DownloadView>())!;
        job = await Wait(admin, job.Id, x => x.State == "completed");
        await admin.PutAsJsonAsync(
            $"/api/downloads/{job.Id}/import-plan",
            new DownloadMatchInput(job.Revision, Plan())
        );
        job = await Wait(admin, job.Id, x => x.Import?.State == "held");
        Assert.NotNull(job.Import!.Error);
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement[]>("/api/media"))!);
        Assert.Empty(Directory.GetFiles(server.MediaPath, "*.webm", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LinkedManagedParentIsRejectedBeforeCreatingFoldersOrFetching()
    {
        if (OperatingSystem.IsWindows())
            return;
        var source = new Source();
        using var server = Create(source);
        using var admin = await server.Admin();
        await Setup(server, admin, source);
        using var member = await Member(server, admin);
        var request = await Request(member);
        var outside = Path.Combine(server.Root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(
            Path.Combine(server.MediaPath, "MediaServer Imports"),
            outside
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PostAsJsonAsync($"/api/requests/{request}/download", Approval(source))
            ).StatusCode
        );
        Assert.Equal(0, source.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task WindowsDeviceNamesAreSanitizedBeforeCreatingMedia()
    {
        var source = new Source();
        using var server = Create(source);
        using var admin = await server.Admin();
        await Setup(server, admin, source);
        var response = await admin.PostAsJsonAsync("/api/downloads", Approval(source).Source);
        var job = (await response.Content.ReadFromJsonAsync<DownloadView>())!;
        job = await Wait(admin, job.Id, x => x.State == "completed");
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    $"/api/downloads/{job.Id}/import-plan",
                    new DownloadMatchInput(job.Revision, Plan() with { Title = "CON", Year = null })
                )
            ).StatusCode
        );
        job = await Wait(admin, job.Id, x => x.Import?.State is "imported" or "held");
        Assert.True(job.Import!.State == "imported", JsonSerializer.Serialize(job));
        using var scope = server.Services.CreateScope();
        var row = await scope
            .ServiceProvider.GetRequiredService<MediaDb>()
            .Downloads.SingleAsync(x => x.Id == job.Id);
        Assert.Contains("CON-title", row.ImportRelativePath!);
    }

    [Fact]
    public async Task WrongContainerCanBeDiscardedAndRematchedWithoutDownloadingAgain()
    {
        var source = new Source();
        using var server = Create(source);
        using var admin = await server.Admin();
        await Setup(server, admin, source);
        var response = await admin.PostAsJsonAsync("/api/downloads", Approval(source).Source);
        var job = (await response.Content.ReadFromJsonAsync<DownloadView>())!;
        job = await Wait(admin, job.Id, x => x.State == "completed");
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    $"/api/downloads/{job.Id}/import-plan",
                    new DownloadMatchInput(job.Revision, Plan() with { Extension = ".mp4" })
                )
            ).StatusCode
        );
        job = await Wait(admin, job.Id, x => x.Import?.State == "held");
        Assert.Equal("import-container-mismatch", job.Import!.Error);
        await server.Services.GetRequiredService<DownloadControl>().Idle(CancellationToken.None);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    $"/api/downloads/{job.Id}/import",
                    new DownloadImportAction(job.Revision, "discard")
                )
            ).StatusCode
        );
        job = await Wait(admin, job.Id, x => x.Import?.State == "discarded");
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    $"/api/downloads/{job.Id}/import-plan",
                    new DownloadMatchInput(job.Revision, Plan())
                )
            ).StatusCode
        );
        job = await Wait(admin, job.Id, x => x.Import?.State is "imported" or "held");
        Assert.True(job.Import!.State == "imported", JsonSerializer.Serialize(job));
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task ManagedFolderCollisionNeverClaimsOrOverwritesExistingLibraryFiles()
    {
        var source = new Source();
        using var server = Create(source);
        using var admin = await server.Admin();
        await Setup(server, admin, source);
        var response = await admin.PostAsJsonAsync("/api/downloads", Approval(source).Source);
        var job = (await response.Content.ReadFromJsonAsync<DownloadView>())!;
        job = await Wait(admin, job.Id, x => x.State == "completed");
        var folder = Path.Combine(
            server.MediaPath,
            "MediaServer Imports",
            "Movies",
            "Synthetic-fixture (2026)",
            job.Id
        );
        Directory.CreateDirectory(folder);
        var original = Path.Combine(folder, "original.webm");
        await File.WriteAllBytesAsync(original, source.Bytes);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PutAsJsonAsync(
                    $"/api/downloads/{job.Id}/import-plan",
                    new DownloadMatchInput(job.Revision, Plan())
                )
            ).StatusCode
        );
        Assert.Equal(source.Bytes, await File.ReadAllBytesAsync(original));
        Assert.False(File.Exists(Path.Combine(folder, ".mediaserver-owner")));
        Assert.Null(
            (await admin.GetFromJsonAsync<DownloadView[]>("/api/downloads"))!
                .Single(x => x.Id == job.Id)
                .Import
        );
    }

    [Fact]
    public async Task HoldingAndDiscardingWaitsForScannerAndPreservesUnrelatedFiles()
    {
        var source = new Source();
        var gate = new ProbeGate();
        using var server = Create(source, probe: gate);
        using var admin = await server.Admin();
        await Setup(server, admin, source);
        var response = await admin.PostAsJsonAsync("/api/downloads", Approval(source).Source);
        var job = (await response.Content.ReadFromJsonAsync<DownloadView>())!;
        job = await Wait(admin, job.Id, x => x.State == "completed");
        await admin.PutAsJsonAsync(
            $"/api/downloads/{job.Id}/import-plan",
            new DownloadMatchInput(job.Revision, Plan())
        );
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        job = (await admin.GetFromJsonAsync<DownloadView[]>("/api/downloads"))!.Single(x =>
            x.Id == job.Id
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    $"/api/downloads/{job.Id}/import",
                    new DownloadImportAction(job.Revision, "hold")
                )
            ).StatusCode
        );
        await server.Services.GetRequiredService<DownloadControl>().Idle(CancellationToken.None);
        job = await Wait(admin, job.Id, x => x.Import?.State == "held");
        int scanId;
        string folder;
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            var row = await db.Downloads.SingleAsync(x => x.Id == job.Id);
            folder = Path.GetDirectoryName(
                Path.Combine(server.MediaPath, row.ImportRelativePath!)
            )!;
            var scan = new ScanJob { LibraryId = 1, State = "running" };
            db.Jobs.Add(scan);
            await db.SaveChangesAsync();
            scanId = scan.Id;
        }
        var original = Path.Combine(folder, "unrelated.txt");
        await File.WriteAllTextAsync(original, "keep this original");
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await admin.PutAsJsonAsync(
                    $"/api/downloads/{job.Id}/import",
                    new DownloadImportAction(job.Revision, "discard")
                )
            ).StatusCode
        );
        using (var scope = server.Services.CreateScope())
            await scope
                .ServiceProvider.GetRequiredService<MediaDb>()
                .Jobs.Where(x => x.Id == scanId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "completed"));
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    $"/api/downloads/{job.Id}/import",
                    new DownloadImportAction(job.Revision, "discard")
                )
            ).StatusCode
        );
        Assert.Equal("keep this original", await File.ReadAllTextAsync(original));
        Assert.False(File.Exists(Path.Combine(folder, "payload.ingesting")));
        Assert.True(File.Exists(Path.Combine(server.DataPath, "downloads", job.Id + ".download")));
    }

    [Fact]
    public async Task RestartAfterFinalRenameRecoversHiddenItemWithoutFetchingOrOverwriting()
    {
        var source = new Source();
        using var server = Create(source);
        var fixture = Path.Combine(server.Root, "recovery.webm");
        await Video(fixture);
        source.Bytes = await File.ReadAllBytesAsync(fixture);
        Directory.CreateDirectory(server.DataPath);
        var id = Guid.NewGuid().ToString("N");
        var relative = Path.Combine(
            "MediaServer Imports",
            "Movies",
            "Recovery",
            id,
            "Recovered.webm"
        );
        var options = new DbContextOptionsBuilder<MediaDb>()
            .UseSqlite("Data Source=" + Path.Combine(server.DataPath, "media.db"))
            .Options;
        await using (var db = new MediaDb(options))
        {
            await db.Database.MigrateAsync();
            db.Users.Add(
                new User
                {
                    Id = 1,
                    Name = "admin",
                    PasswordHash = await Passwords.Hash("recovery-fixture-password"),
                }
            );
            db.Libraries.Add(
                new Library
                {
                    Id = 1,
                    Name = "Recovery",
                    Path = server.MediaPath,
                }
            );
            db.FeaturePolicies.Add(new FeaturePolicy { Key = "downloads", Enabled = true });
            db.Downloads.Add(
                new DownloadJob
                {
                    Id = id,
                    UserId = 1,
                    Name = "Recovered",
                    State = "completed",
                    MaxBytes = 4 * 1024 * 1024,
                    TotalBytes = source.Bytes.Length,
                    ReceivedBytes = source.Bytes.Length,
                    ContentHash = Convert
                        .ToHexString(SHA256.HashData(source.Bytes))
                        .ToLowerInvariant(),
                    TargetLibraryId = 1,
                    TargetRoot = server.MediaPath,
                    Classification = "movies",
                    ImportTitle = "Recovered",
                    ImportRating = 10,
                    ImportExtension = ".webm",
                    ImportRelativePath = relative,
                    ImportNonce = "recovery-proof",
                    ImportState = "scanning",
                }
            );
            db.Items.Add(
                new MediaItem
                {
                    LibraryId = 1,
                    Title = "Recovered",
                    RelativePath = relative,
                    ImportJobId = id,
                    PendingImport = true,
                    IsMissing = true,
                    ContentRating = 10,
                }
            );
            await db.SaveChangesAsync();
        }
        Directory.CreateDirectory(Path.Combine(server.DataPath, "downloads"));
        await File.WriteAllBytesAsync(
            Path.Combine(server.DataPath, "downloads", id + ".download"),
            source.Bytes
        );
        var final = Path.Combine(server.MediaPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        await File.WriteAllBytesAsync(final, source.Bytes);
        var stamp = File.GetLastWriteTimeUtc(final);
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(final)!, ".mediaserver-owner"),
            id + ":recovery-proof"
        );
        using var admin = server.CreateClient();
        await Server.Csrf(admin);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PostAsJsonAsync(
                    "/api/auth/login",
                    new { name = "admin", password = "recovery-fixture-password" }
                )
            ).StatusCode
        );
        await Server.Csrf(admin);
        var job = await Wait(admin, id, x => x.Import?.State == "held");
        Assert.Equal("server-restarted", job.Import!.Error);
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement[]>("/api/media"))!);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    $"/api/downloads/{id}/import",
                    new DownloadImportAction(job.Revision, "retry")
                )
            ).StatusCode
        );
        job = await Wait(admin, id, x => x.Import?.State is "imported" or "held");
        Assert.True(job.Import!.State == "imported", JsonSerializer.Serialize(job));
        Assert.Equal(0, source.Calls);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(final));
        Assert.Equal(source.Bytes, await File.ReadAllBytesAsync(final));
    }

    [Fact]
    public async Task ApprovalRejectsTraversalRatingAndDestinationStorageWithoutFetching()
    {
        var source = new Source();
        var space = new Space();
        using var server = Create(source, space);
        using var admin = await server.Admin();
        await Setup(server, admin, source);
        using var member = await Member(server, admin);
        var request = await Request(member);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PostAsJsonAsync(
                    $"/api/requests/{request}/download",
                    Approval(source, Plan() with { Title = "../escape" })
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PostAsJsonAsync(
                    $"/api/requests/{request}/download",
                    Approval(source, Plan() with { Rating = 18 })
                )
            ).StatusCode
        );
        space.BlockedRoot = server.MediaPath;
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PostAsJsonAsync($"/api/requests/{request}/download", Approval(source))
            ).StatusCode
        );
        Assert.Equal(0, source.Calls);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PostAsJsonAsync(
                    $"/api/requests/{request}/download/decline",
                    new DownloadDecline(0, "")
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (
                await admin.PostAsJsonAsync(
                    $"/api/requests/{request}/download/decline",
                    new DownloadDecline(0, "No authorized source available.")
                )
            ).StatusCode
        );
        var row = await member.GetFromJsonAsync<JsonElement>($"/api/requests/{request}");
        Assert.Equal("Declined", row.GetProperty("state").GetString());
        Assert.Equal("No authorized source available.", row.GetProperty("reply").GetString());
    }
}
