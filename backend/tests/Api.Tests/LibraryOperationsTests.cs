using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public sealed class LibraryOperationsTests
{
    private static async Task Scan(HttpClient client, int library, string expected = "completed")
    {
        var response = await client.PostAsync($"/api/libraries/{library}/scan", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/api/scans/{id}");
            if (job.GetProperty("state").GetString() == expected)
                return;
            await Task.Delay(50);
        }
        Assert.Fail("Scan did not reach expected terminal state.");
    }

    [Fact]
    public async Task HashReconciliationPreservesMoveHistoryAndNeverDeletesOriginals()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await File.WriteAllTextAsync(
            Path.Combine(server.MediaPath, "one.webm"),
            "same synthetic fixture"
        );
        await File.WriteAllTextAsync(
            Path.Combine(server.MediaPath, "two.webm"),
            "same synthetic fixture"
        );
        var response = await admin.PostAsJsonAsync(
            "/api/libraries",
            new
            {
                name = "Movies",
                path = server.MediaPath,
                computeHashes = true,
                kind = "tv",
            }
        );
        var library = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
        await Scan(admin, library);
        Assert.Single(
            (await admin.GetFromJsonAsync<JsonElement>("/api/duplicates")).EnumerateArray()
        );
        var items = await admin.GetFromJsonAsync<JsonElement>("/api/media");
        var id = items
            .EnumerateArray()
            .Single(x => x.GetProperty("title").GetString() == "one")
            .GetProperty("id")
            .GetInt32();
        await admin.PutAsJsonAsync(
            $"/api/v1/media/{id}/progress-sync",
            new { seconds = 40, expectedRevision = 0 }
        );
        File.Move(
            Path.Combine(server.MediaPath, "one.webm"),
            Path.Combine(server.MediaPath, "moved.webm")
        );
        // With the duplicate still present, only one old path is an unambiguous move candidate.
        await Scan(admin, library);
        File.Delete(Path.Combine(server.MediaPath, "two.webm"));
        await Scan(admin, library);
        var remaining = Assert.Single(
            (await admin.GetFromJsonAsync<JsonElement>("/api/media")).EnumerateArray()
        );
        Assert.Equal(id, remaining.GetProperty("id").GetInt32());
        Assert.Equal("moved", remaining.GetProperty("title").GetString());
        Assert.Equal(
            40,
            (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/media/{id}/progress"))
                .GetProperty("seconds")
                .GetDouble()
        );
        Assert.Single(
            (
                await admin.GetFromJsonAsync<JsonElement>($"/api/libraries/{library}/missing")
            ).EnumerateArray()
        );
        Assert.Equal(
            "same synthetic fixture",
            await File.ReadAllTextAsync(Path.Combine(server.MediaPath, "moved.webm"))
        );
        Directory.Move(server.MediaPath, server.MediaPath + "-offline");
        await Scan(admin, library, "failed");
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
        Assert.False((await db.Items.IgnoreQueryFilters().SingleAsync(x => x.Id == id)).IsMissing);
    }

    [Fact]
    public async Task OptInWatcherScansNewFilesThroughObservableJobs()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await admin.PostAsJsonAsync(
            "/api/libraries",
            new
            {
                name = "Watched",
                path = server.MediaPath,
                watchEnabled = true,
            }
        );
        await File.WriteAllTextAsync(
            Path.Combine(server.MediaPath, "automatic.webm"),
            "synthetic watcher fixture"
        );
        var found = false;
        for (var i = 0; i < 600; i++)
        {
            if ((await admin.GetFromJsonAsync<JsonElement>("/api/media")).GetArrayLength() == 1)
            {
                using var completedScope = server.Services.CreateScope();
                var db = completedScope.ServiceProvider.GetRequiredService<MediaDb>();
                if (await db.Jobs.AnyAsync(x => x.State == "completed"))
                {
                    found = true;
                    break;
                }
            }
            await Task.Delay(50);
        }
        Assert.True(found);
        Assert.True(File.Exists(Path.Combine(server.MediaPath, "automatic.webm")));
    }
}
