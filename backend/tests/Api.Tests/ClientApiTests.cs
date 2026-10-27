using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public sealed class ClientApiTests
{
    [Fact]
    public async Task DatabaseSessionExpirationRejectsAnOtherwiseValidCookie()
    {
        using var server = new Server();
        using var client = await server.Admin();
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            await db.Sessions.ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-1))
            );
        }
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/media")).StatusCode
        );
    }

    [Fact]
    public async Task UpgradePreservesExistingPositionsAndBackfillsCompletion()
    {
        using var server = new Server();
        var options = new DbContextOptionsBuilder<MediaDb>()
            .UseSqlite($"Data Source={Path.Combine(server.Root, "upgrade.db")}")
            .Options;
        await using var db = new MediaDb(options);
        await db.GetService<IMigrator>().MigrateAsync("20261026224241_Initial");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Users (Id,Name,PasswordHash,Role) VALUES (1,'fixture','test-only','Admin'); INSERT INTO Libraries (Id,Name,Path) VALUES (1,'fixture','fixture'); INSERT INTO Items (Id,LibraryId,RelativePath,Title,Container,VideoCodec,AudioCodec,Duration,Size) VALUES (1,1,'fixture.webm','fixture','webm','vp9','opus',100,100); INSERT INTO Progress (UserId,MediaItemId,Seconds,UpdatedAt) VALUES (1,1,98,'2026-10-27 00:00:00');"
        );
        await db.Database.MigrateAsync();
        var state = await db.Progress.SingleAsync();
        Assert.Equal(98, state.Seconds);
        Assert.True(state.Completed);
        Assert.Equal(0, state.Revision);
    }

    [Fact]
    public async Task VersionedApiPreservesSecurityAndDetectsProgressConflicts()
    {
        using var server = new Server();
        using var guest = server.CreateClient();
        var info = await guest.GetFromJsonAsync<JsonElement>("/api/v1/server-info");
        Assert.Equal(1, info.GetProperty("apiVersion").GetInt32());
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await guest.GetAsync("/api/v1/media")).StatusCode
        );
        using var first = await server.Admin();
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            var lib = new Library { Name = "Own media", Path = server.MediaPath };
            db.Libraries.Add(lib);
            await db.SaveChangesAsync();
            db.Items.Add(
                new MediaItem
                {
                    LibraryId = lib.Id,
                    Title = "Test movie",
                    RelativePath = "movie.webm",
                    Duration = 100,
                }
            );
            await db.SaveChangesAsync();
        }
        var list = await first.GetFromJsonAsync<JsonElement>("/api/v1/media?search=test");
        Assert.Single(list.EnumerateArray());
        Assert.Empty(
            (
                await first.GetFromJsonAsync<JsonElement>("/api/v1/media?search=missing")
            ).EnumerateArray()
        );
        var id = list[0].GetProperty("id").GetInt32();
        using var second = server.CreateClient();
        await Server.Csrf(second);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await second.PostAsJsonAsync(
                    "/api/v1/auth/login",
                    new
                    {
                        name = "admin",
                        password = "twelve-character-password",
                        deviceName = "Other device",
                    }
                )
            ).StatusCode
        );
        await Server.Csrf(second);
        var initial = await first.GetFromJsonAsync<JsonElement>($"/api/v1/media/{id}/progress");
        Assert.Equal(0, initial.GetProperty("revision").GetInt64());
        var update = await first.PutAsJsonAsync(
            $"/api/v1/media/{id}/progress-sync",
            new { seconds = 40, expectedRevision = 0 }
        );
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await first.PutAsJsonAsync(
                    $"/api/v1/media/{id}/progress-sync",
                    new { seconds = 40, expectedRevision = 0 }
                )
            ).StatusCode
        );
        var stale = await second.PutAsJsonAsync(
            $"/api/v1/media/{id}/progress-sync",
            new { seconds = 10, expectedRevision = 0 }
        );
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            40,
            (await stale.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("seconds")
                .GetDouble()
        );
        var watching = await second.GetFromJsonAsync<JsonElement>("/api/v1/continue-watching");
        Assert.Single(watching.EnumerateArray());
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await second.PutAsJsonAsync(
                    $"/api/v1/media/{id}/progress-sync",
                    new { seconds = 98, expectedRevision = 1 }
                )
            ).StatusCode
        );
        Assert.Empty(
            (
                await first.GetFromJsonAsync<JsonElement>("/api/v1/continue-watching")
            ).EnumerateArray()
        );
        var tracks = await first.GetFromJsonAsync<JsonElement>($"/api/v1/media/{id}/tracks");
        Assert.False(tracks.GetProperty("canSelectAudio").GetBoolean());
        Assert.Equal(
            HttpStatusCode.NoContent,
            (
                await first.PutAsJsonAsync(
                    "/api/v1/capabilities",
                    new
                    {
                        platform = "windows",
                        player = "WebView2",
                        containers = new[] { "mp4" },
                        codecs = new[] { "h264" },
                    }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await first.PostAsJsonAsync(
                    $"/api/v1/media/{id}/playback-errors",
                    new { code = "arbitrary log injection", client = "test" }
                )
            ).StatusCode
        );
        var sessions = await first.GetFromJsonAsync<JsonElement>("/api/v1/sessions");
        var other = sessions.EnumerateArray().Single(s => !s.GetProperty("current").GetBoolean());
        Assert.Equal(
            HttpStatusCode.NoContent,
            (
                await first.DeleteAsync("/api/v1/sessions/" + other.GetProperty("id").GetString())
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await second.GetAsync("/api/v1/media")).StatusCode
        );
    }

    [Fact]
    public async Task ClientOpenApiMatchesTheRunningServer()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        var schema = await admin.GetStringAsync("/openapi/client-v1.json");
        var doc = JsonDocument.Parse(schema);
        Assert.True(
            doc.RootElement.GetProperty("paths")
                .TryGetProperty("/api/v1/media/{id}/progress-sync", out _)
        );
        var target = Environment.GetEnvironmentVariable("EXPORT_CLIENT_OPENAPI");
        if (target is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            await File.WriteAllTextAsync(target, schema + "\n");
        }
    }
}
