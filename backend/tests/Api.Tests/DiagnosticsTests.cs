using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public async Task MissingMediaDiagnosticsAreAuthenticatedAndNeverExposePaths()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
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
                    RelativePath = "private-name.webm",
                    Title = "Video",
                }
            );
            await db.SaveChangesAsync();
        }
        using var guest = server.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await guest.GetAsync("/api/media/1/playback-info")).StatusCode
        );
        var response = await admin.GetStringAsync("/api/media/1/playback-info");
        Assert.DoesNotContain(server.MediaPath, response);
        Assert.DoesNotContain("private-name", response);
        using var doc = JsonDocument.Parse(response);
        Assert.False(doc.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("media_unavailable", doc.RootElement.GetProperty("code").GetString());
        Assert.NotEmpty(doc.RootElement.GetProperty("correlationId").GetString()!);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.GetAsync("/api/media/1/stream")).StatusCode
        );
        await File.WriteAllTextAsync(
            Path.Combine(server.MediaPath, "private-name.webm"),
            "fixture"
        );
        Assert.True(
            (await admin.GetFromJsonAsync<JsonElement>("/api/v1/media/1/playback-info"))
                .GetProperty("available")
                .GetBoolean()
        );
    }

    [Fact]
    public async Task ScanFailuresHaveBoundedAdminOnlyReasonsAndCorrelationReferences()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        for (var i = 0; i < 103; i++)
            await File.WriteAllTextAsync(
                Path.Combine(server.MediaPath, $"broken-{i}.mp4"),
                "corrupt fixture"
            );
        var added = await admin.PostAsJsonAsync(
            "/api/libraries",
            new { name = "Movies", path = server.MediaPath }
        );
        var library = (await added.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
        var scan = await admin.PostAsync($"/api/libraries/{library}/scan", null);
        var id = (await scan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var completed = false;
        for (var i = 0; i < 300; i++)
        {
            var job = await admin.GetFromJsonAsync<JsonElement>($"/api/scans/{id}");
            if (job.GetProperty("state").GetString() == "completed")
            {
                Assert.Equal(103, job.GetProperty("failed").GetInt32());
                completed = true;
                break;
            }
            await Task.Delay(50);
        }
        Assert.True(completed);
        var raw = await admin.GetStringAsync($"/api/scans/{id}/issues");
        Assert.DoesNotContain(server.MediaPath, raw);
        var issues = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal(100, issues.GetArrayLength());
        Assert.All(
            issues.EnumerateArray(),
            x =>
            {
                Assert.StartsWith("broken-", x.GetProperty("fileName").GetString());
                Assert.Equal("probe_failed", x.GetProperty("code").GetString());
                Assert.Equal(32, x.GetProperty("correlationId").GetString()!.Length);
            }
        );
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
        await viewer.PostAsJsonAsync(
            "/api/auth/login",
            new { name = "viewer", password = "viewer-long-password" }
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await viewer.GetAsync($"/api/scans/{id}/issues")).StatusCode
        );
    }

    [Fact]
    public async Task MissingProbeIsCategorizedWithoutExposingExecutableOrMediaPaths()
    {
        var probe = new Ffprobe(
            Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())
        );
        var error = await Assert.ThrowsAsync<MediaProbeException>(() =>
            probe.ProbeAsync("private-file.mp4", CancellationToken.None)
        );
        Assert.Equal("probe_unavailable", error.Code);
        Assert.DoesNotContain("private", error.Message);
    }
}
