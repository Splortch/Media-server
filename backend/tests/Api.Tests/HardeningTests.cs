using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public sealed class HardeningTests
{
    [Fact]
    public async Task HostFilteringAndAntiforgeryAlsoProtectCredentialChanges()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        using var untrusted = new HttpRequestMessage(HttpMethod.Get, "/health");
        untrusted.Headers.Host = "attacker.invalid";
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(untrusted)).StatusCode);
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PostAsJsonAsync(
                    "/api/auth/password",
                    new
                    {
                        currentPassword = "twelve-character-password",
                        newPassword = "replacement-long-password",
                    }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task ConcurrentLegacyAndVersionedSetupCreatesOneAdministratorAndConsumesToken()
    {
        using var server = new Server();
        using var a = server.CreateClient();
        using var b = server.CreateClient();
        await Server.Csrf(a);
        await Server.Csrf(b);
        var token = await File.ReadAllTextAsync(Path.Combine(server.DataPath, "setup-token"));
        var results = await Task.WhenAll(
            a.PostAsJsonAsync(
                "/api/setup",
                new
                {
                    name = "first",
                    password = "first-long-password",
                    token,
                }
            ),
            b.PostAsJsonAsync(
                "/api/v1/setup",
                new
                {
                    name = "second",
                    password = "second-long-password",
                    token,
                }
            )
        );
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.False(File.Exists(Path.Combine(server.DataPath, "setup-token")));
        using var scope = server.Services.CreateScope();
        Assert.Equal(
            1,
            await scope.ServiceProvider.GetRequiredService<MediaDb>().Users.CountAsync()
        );
    }

    [Fact]
    public async Task PasswordChangeRequiresCurrentPasswordAndRevokesEveryCopiedSession()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        using var other = server.CreateClient();
        await Server.Csrf(other);
        var login = await other.PostAsJsonAsync(
            "/api/auth/login",
            new { name = "admin", password = "twelve-character-password" }
        );
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
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PostAsJsonAsync(
                    "/api/auth/password",
                    new { currentPassword = "wrong", newPassword = "replacement-long-password" }
                )
            ).StatusCode
        );
        Assert.Equal(HttpStatusCode.OK, (await copied.GetAsync("/api/media")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (
                await admin.PostAsJsonAsync(
                    "/api/v1/auth/password",
                    new
                    {
                        currentPassword = "twelve-character-password",
                        newPassword = "replacement-long-password",
                    }
                )
            ).StatusCode
        );
        foreach (var client in new[] { admin, other, copied })
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await client.GetAsync("/api/media")).StatusCode
            );
        await Server.Csrf(other);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (
                await other.PostAsJsonAsync(
                    "/api/auth/login",
                    new { name = "admin", password = "twelve-character-password" }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await other.PostAsJsonAsync(
                    "/api/auth/login",
                    new { name = "admin", password = "replacement-long-password" }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task OfflineRecoveryReplacesTheHashAndRevokesSessionsWithoutReopeningSetup()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            await CredentialRecovery.ResetAsync(db, "recovered-long-password");
            Assert.Empty(await db.Sessions.ToListAsync());
            Assert.True(
                await Passwords.Verify(
                    "recovered-long-password",
                    (await db.Users.SingleAsync()).PasswordHash
                )
            );
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/media")).StatusCode);
        Assert.False(
            (await admin.GetFromJsonAsync<JsonElement>("/api/setup"))
                .GetProperty("required")
                .GetBoolean()
        );
        Assert.False(File.Exists(Path.Combine(server.DataPath, "setup-token")));
    }

    [Fact]
    public async Task ExpiredCookieCannotReadRangesOrWriteProgressAndUsersAreIsolated()
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
            db.Libraries.Add(
                new Library
                {
                    Id = 1,
                    Name = "Movies",
                    Path = server.MediaPath,
                }
            );
            db.Items.Add(
                new MediaItem
                {
                    Id = 1,
                    LibraryId = 1,
                    Title = "Own video",
                    RelativePath = "one.webm",
                    Duration = 100,
                }
            );
            await db.SaveChangesAsync();
        }
        await File.WriteAllTextAsync(Path.Combine(server.MediaPath, "one.webm"), "fixture");
        await admin.PutAsJsonAsync(
            "/api/v1/media/1/progress-sync",
            new { seconds = 40, expectedRevision = 0 }
        );
        using var viewer = server.CreateClient();
        await Server.Csrf(viewer);
        await viewer.PostAsJsonAsync(
            "/api/auth/login",
            new { name = "viewer", password = "viewer-long-password" }
        );
        await Server.Csrf(viewer);
        Assert.Empty(
            (await viewer.GetFromJsonAsync<JsonElement>("/api/progress")).EnumerateArray()
        );
        Assert.Equal(
            0,
            (await viewer.GetFromJsonAsync<JsonElement>("/api/v1/media/1/progress"))
                .GetProperty("seconds")
                .GetDouble()
        );
        await viewer.PutAsJsonAsync(
            "/api/v1/media/1/progress-sync",
            new { seconds = 10, expectedRevision = 0 }
        );
        Assert.Equal(
            40,
            (await admin.GetFromJsonAsync<JsonElement>("/api/v1/media/1/progress"))
                .GetProperty("seconds")
                .GetDouble()
        );
        using (var scope = server.Services.CreateScope())
            await scope
                .ServiceProvider.GetRequiredService<MediaDb>()
                .Sessions.Where(s => s.UserId == 2)
                .ExecuteUpdateAsync(s =>
                    s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-1))
                );
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/media/1/stream");
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.SendAsync(request)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (
                await viewer.PutAsJsonAsync(
                    "/api/v1/media/1/progress-sync",
                    new { seconds = 20, expectedRevision = 1 }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task OpenedMediaIsPinnedAndLinkReplacementNeverReturnsOutsideBytes()
    {
        using var server = new Server();
        var policy = new PathPolicy([server.MediaPath]);
        var file = Path.Combine(server.MediaPath, "movie.webm");
        await File.WriteAllTextAsync(file, "authorized");
        var outside = Path.Combine(server.Root, "secret");
        await File.WriteAllTextAsync(outside, "private");
        using (var stream = policy.OpenRead(server.MediaPath, "movie.webm"))
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.Move(file, file + ".old");
                File.CreateSymbolicLink(file, outside);
            }
            else
                Assert.Throws<IOException>(() => File.Move(file, file + ".old"));
            using var reader = new StreamReader(stream);
            Assert.Equal("authorized", await reader.ReadToEndAsync());
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Assert.Throws<ArgumentException>(() => policy.OpenRead(server.MediaPath, "movie.webm"));
        var directory = Path.Combine(server.MediaPath, "directory");
        Directory.CreateDirectory(directory);
        Assert.Throws<IOException>(() => policy.OpenRead(server.MediaPath, "directory"));
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Directory.CreateSymbolicLink(Path.Combine(server.MediaPath, "link"), server.Root);
            Assert.Throws<ArgumentException>(() =>
                policy.OpenRead(server.MediaPath, "link/secret")
            );
        }
    }
}
