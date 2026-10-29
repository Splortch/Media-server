using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Api.Tests;

public sealed class Server : WebApplicationFactory<Program>
{
    public string Root { get; } = Path.Combine(TestPaths.TempRoot, "media-tests-" + Guid.NewGuid());
    public string MediaPath => Path.Combine(Root, "videos");
    public string DataPath => Path.Combine(Root, "data");

    private readonly string[]? hardware;
    private readonly bool realProbe;

    public Server(string[]? hardware = null, bool realProbe = false)
    {
        this.hardware = hardware;
        this.realProbe = realProbe;
        Directory.CreateDirectory(MediaPath);
        Environment.SetEnvironmentVariable("MediaServer__DataDirectory", DataPath);
        Environment.SetEnvironmentVariable("MediaServer__AllowedRoots__0", MediaPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMediaProbe>();
            if (realProbe)
                services.AddSingleton<IMediaProbe>(new Ffprobe("ffprobe"));
            else
                services.AddSingleton<IMediaProbe, TestProbe>();
            if (hardware is not null)
                services.Replace(
                    ServiceDescriptor.Singleton(
                        new TranscodeSettings(new[] { "libx264" }.Concat(hardware).ToArray())
                    )
                );
        });
    }

    public async Task<HttpClient> Admin()
    {
        var client = CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );
        await Csrf(client);
        var setup = await client.PostAsJsonAsync(
            "/api/setup",
            new
            {
                name = "admin",
                password = "twelve-character-password",
                token = await File.ReadAllTextAsync(Path.Combine(DataPath, "setup-token")),
            }
        );
        Assert.Equal(HttpStatusCode.Created, setup.StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PostAsJsonAsync(
                    "/api/auth/login",
                    new { name = "admin", password = "twelve-character-password" }
                )
            ).StatusCode
        );
        await Csrf(client);
        return client;
    }

    public static async Task Csrf(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.GetProperty("token").GetString());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("MediaServer__DataDirectory", null);
        Environment.SetEnvironmentVariable("MediaServer__AllowedRoots__0", null);
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException) { }
    }
}

public sealed class TestProbe : IMediaProbe
{
    public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct) =>
        Path.GetFileName(path).StartsWith("broken", StringComparison.Ordinal)
            ? throw new IOException("invalid video")
            : Task.FromResult(new ProbeResult(100, "webm", "vp9", "opus"));
}

public sealed class FoundationTests
{
    [Fact]
    public async Task HealthIsPublicButMediaAndSettingsArePrivate()
    {
        using var server = new Server();
        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        foreach (
            var url in new[]
            {
                "/api/media",
                "/api/media/1",
                "/api/media/1/stream",
                "/api/progress",
                "/api/libraries",
                "/api/status",
                "/openapi/v1.json",
            }
        )
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task SetupRequiresTokenAndCsrfAndCannotRunTwice()
    {
        using var server = new Server();
        using var client = server.CreateClient();
        var body = new
        {
            name = "admin",
            password = "twelve-character-password",
            token = "wrong",
        };
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/setup", body)).StatusCode
        );
        await Server.Csrf(client);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/setup", body)).StatusCode
        );
        using var admin = await server.Admin();
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync("/api/setup", body)).StatusCode
        );
        Assert.False(
            (await admin.GetFromJsonAsync<JsonElement>("/api/setup"))
                .GetProperty("required")
                .GetBoolean()
        );
    }

    [Fact]
    public async Task PasswordHashIsSaltedArgon2id()
    {
        var a = await Passwords.Hash("a long password");
        var b = await Passwords.Hash("a long password");
        Assert.StartsWith("argon2id$", a);
        Assert.NotEqual(a, b);
        Assert.True(await Passwords.Verify("a long password", a));
        Assert.False(await Passwords.Verify("wrong", a));
    }

    [Fact]
    public void PathsRejectTraversalRootPrefixSiblingsAndSymlinks()
    {
        using var server = new Server();
        var policy = new PathPolicy([server.MediaPath]);
        Assert.Equal(server.MediaPath, policy.ValidateDirectory(server.MediaPath));
        Assert.Throws<ArgumentException>(() => policy.ValidateDirectory(server.Root));
        Assert.Throws<ArgumentException>(() => policy.ValidateDirectory("relative"));
        Assert.Throws<ArgumentException>(() => policy.Resolve(server.MediaPath, "../secret.mp4"));
        Assert.False(PathPolicy.Within(server.MediaPath, server.MediaPath + "-other/movie.mp4"));
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(server.MediaPath, "escape"), server.Root);
            Assert.Throws<ArgumentException>(() => policy.Resolve(server.MediaPath, "escape"));
        }
    }

    [Fact]
    public async Task ScanUpsertsWithoutChangingFilesAndSupportsRangesAndProgress()
    {
        using var server = new Server();
        var original = Enumerable.Range(0, 256).Select(x => (byte)x).ToArray();
        var file = Path.Combine(server.MediaPath, "sample.webm");
        await File.WriteAllBytesAsync(file, original);
        await File.WriteAllTextAsync(Path.Combine(server.MediaPath, "broken.mp4"), "not a video");
        using var client = await server.Admin();
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await client.PostAsJsonAsync(
                    "/api/libraries",
                    new { name = "escape", path = server.Root }
                )
            ).StatusCode
        );
        var response = await client.PostAsJsonAsync(
            "/api/libraries",
            new { name = "Movies", path = server.MediaPath }
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var library = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
        for (var i = 0; i < 2; i++)
        {
            var scan = await client.PostAsync($"/api/libraries/{library}/scan", null);
            Assert.Equal(HttpStatusCode.Accepted, scan.StatusCode);
            var job = (await scan.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id")
                .GetInt32();
            var complete = false;
            for (var tries = 0; tries < 100; tries++)
            {
                var status = await client.GetFromJsonAsync<JsonElement>($"/api/scans/{job}");
                if (status.GetProperty("state").GetString() == "completed")
                {
                    Assert.Equal(1, status.GetProperty("failed").GetInt32());
                    complete = true;
                    break;
                }
                await Task.Delay(50);
            }
            Assert.True(complete, "Background scan did not complete");
        }
        var items = await client.GetFromJsonAsync<JsonElement>("/api/media");
        Assert.Single(items.EnumerateArray());
        var id = items[0].GetProperty("id").GetInt32();
        Assert.False(items[0].TryGetProperty("relativePath", out _));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/media/{id}/stream");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(5, 14);
        var stream = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, stream.StatusCode);
        Assert.Equal(original[5..15], await stream.Content.ReadAsByteArrayAsync());
        using var invalid = new HttpRequestMessage(HttpMethod.Get, $"/api/media/{id}/stream");
        invalid.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, null);
        Assert.Equal(
            HttpStatusCode.RequestedRangeNotSatisfiable,
            (await client.SendAsync(invalid)).StatusCode
        );
        using var suffix = new HttpRequestMessage(HttpMethod.Get, $"/api/media/{id}/stream");
        suffix.Headers.TryAddWithoutValidation("Range", "bytes=-8");
        Assert.Equal(
            original[^8..],
            await (await client.SendAsync(suffix)).Content.ReadAsByteArrayAsync()
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (
                await client.PutAsJsonAsync($"/api/media/{id}/progress", new { seconds = 42.5 })
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await client.PutAsJsonAsync($"/api/media/{id}/progress", new { seconds = -1 })
            ).StatusCode
        );
        Assert.Equal(
            42.5,
            (await client.GetFromJsonAsync<JsonElement>("/api/progress"))[0]
                .GetProperty("seconds")
                .GetDouble()
        );
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            Assert.Equal(1, await db.Progress.CountAsync());
            Assert.Equal(2, await db.Jobs.CountAsync());
            Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(file));
        File.Delete(file);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/media/{id}/stream")).StatusCode
        );
    }

    [Fact]
    public async Task LogoutRevokesCopiedCookieAndViewerCannotAdminister()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Users.Add(
                new User
                {
                    Id = 2,
                    Name = "viewer",
                    Role = "Viewer",
                    PasswordHash = await Passwords.Hash("viewer-long-password"),
                }
            );
            await db.SaveChangesAsync();
        }
        using var viewer = server.CreateClient();
        await Server.Csrf(viewer);
        var login = await viewer.PostAsJsonAsync(
            "/api/auth/login",
            new { name = "viewer", password = "viewer-long-password" }
        );
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await Server.Csrf(viewer);
        using var copied = server.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false }
        );
        copied.DefaultRequestHeaders.Add(
            "Cookie",
            login
                .Headers.GetValues("Set-Cookie")
                .Single(x => x.StartsWith("MediaServer.Session=", StringComparison.Ordinal))
                .Split(';')[0]
        );
        Assert.Equal(HttpStatusCode.OK, (await copied.GetAsync("/api/media")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await viewer.GetAsync("/api/libraries")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await viewer.PostAsync("/api/auth/logout", null)).StatusCode
        );
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/media")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await copied.GetAsync("/api/media")).StatusCode);
    }

    [Fact]
    public async Task SensitiveEndpointsAreRateLimited()
    {
        using var server = new Server();
        using var client = server.CreateClient();
        await Server.Csrf(client);
        HttpStatusCode last = 0;
        for (var i = 0; i < 11; i++)
            last = (
                await client.PostAsJsonAsync(
                    "/api/auth/login",
                    new { name = "missing", password = "too short" }
                )
            ).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
