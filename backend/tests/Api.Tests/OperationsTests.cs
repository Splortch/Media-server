using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Api.Tests;

public sealed class OperationsTests(ITestOutputHelper output)
{
    private static async Task<HttpClient> Login(Server server, string name, string password)
    {
        var client = server.CreateClient();
        await Server.Csrf(client);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/auth/login", new { name, password })).StatusCode
        );
        await Server.Csrf(client);
        return client;
    }

    private static async Task<int> CreateViewer(HttpClient admin, int library, int? rating = 13)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/users",
            new
            {
                name = "viewer",
                password = "viewer-fixture-password",
                libraries = new[] { library },
                maximumRating = rating,
            }
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
    }

    [Fact]
    public async Task LibraryAndRatingRestrictionsCoverLegacyAndVersionedMediaRoutes()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Libraries.AddRange(
                new Library
                {
                    Id = 1,
                    Name = "Family",
                    Path = server.MediaPath,
                },
                new Library
                {
                    Id = 2,
                    Name = "Private",
                    Path = server.MediaPath + "/private",
                }
            );
            db.Items.AddRange(
                new MediaItem
                {
                    Id = 1,
                    LibraryId = 1,
                    Title = "Allowed",
                    RelativePath = "one.webm",
                    ContentRating = 10,
                },
                new MediaItem
                {
                    Id = 2,
                    LibraryId = 1,
                    Title = "Mature",
                    RelativePath = "two.webm",
                    ContentRating = 18,
                },
                new MediaItem
                {
                    Id = 3,
                    LibraryId = 1,
                    Title = "Unrated",
                    RelativePath = "three.webm",
                },
                new MediaItem
                {
                    Id = 4,
                    LibraryId = 2,
                    Title = "Private",
                    RelativePath = "four.webm",
                    ContentRating = 10,
                }
            );
            await db.SaveChangesAsync();
        }
        var uid = await CreateViewer(admin, 1);
        using var viewer = await Login(server, "viewer", "viewer-fixture-password");
        foreach (var prefix in new[] { "/api", "/api/v1" })
        {
            Assert.Single(
                (await viewer.GetFromJsonAsync<JsonElement>(prefix + "/media")).EnumerateArray()
            );
            foreach (var id in new[] { 2, 3, 4 })
            foreach (var suffix in new[] { "", "/stream", "/playback-info" })
                Assert.Equal(
                    HttpStatusCode.NotFound,
                    (await viewer.GetAsync($"{prefix}/media/{id}{suffix}")).StatusCode
                );
        }
        foreach (var id in new[] { 2, 3, 4 })
        {
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await viewer.GetAsync($"/api/v1/media/{id}/tracks")).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await viewer.GetAsync($"/api/v1/media/{id}/progress")).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.NotFound,
                (
                    await viewer.PutAsJsonAsync(
                        $"/api/v1/media/{id}/progress-sync",
                        new { seconds = 0, expectedRevision = 0 }
                    )
                ).StatusCode
            );
            Assert.Equal(
                HttpStatusCode.NotFound,
                (
                    await viewer.PostAsJsonAsync(
                        $"/api/v1/media/{id}/playback-errors",
                        new { code = "unknown", client = "fixture" }
                    )
                ).StatusCode
            );
        }
        Assert.Equal(
            1,
            (await viewer.GetFromJsonAsync<JsonElement>("/api/status"))
                .GetProperty("libraryCount")
                .GetInt32()
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await viewer.GetAsync("/api/monitoring")).StatusCode
        );
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/users")).StatusCode);
        var accounts = await admin.GetStringAsync("/api/users");
        Assert.DoesNotContain("passwordHash", accounts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("viewer-fixture-password", accounts);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await admin.PutAsJsonAsync("/api/media/3/rating", new { rating = 10 })).StatusCode
        );
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/media/3")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    $"/api/users/{uid}",
                    new
                    {
                        role = "Viewer",
                        disabled = true,
                        allLibraries = false,
                        maximumRating = 13,
                        libraries = new[] { 1 },
                    }
                )
            ).StatusCode
        );
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/media")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PutAsJsonAsync(
                    "/api/users/1",
                    new
                    {
                        role = "Viewer",
                        disabled = true,
                        allLibraries = false,
                        libraries = Array.Empty<int>(),
                    }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task AdminResetRevokesViewerSessionsAndHistorySurvivesResumeDeletion()
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
                    Title = "Movie",
                    RelativePath = "movie.webm",
                    ContentRating = 10,
                    Duration = 100,
                }
            );
            await db.SaveChangesAsync();
        }
        var uid = await CreateViewer(admin, 1);
        using var viewer = await Login(server, "viewer", "viewer-fixture-password");
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await viewer.PutAsJsonAsync(
                    "/api/v1/media/1/progress-sync",
                    new { seconds = 98, expectedRevision = 0 }
                )
            ).StatusCode
        );
        using (var scope = server.Services.CreateScope())
            await scope
                .ServiceProvider.GetRequiredService<MediaDb>()
                .Progress.Where(x => x.UserId == uid)
                .ExecuteDeleteAsync();
        Assert.Single(
            (await viewer.GetFromJsonAsync<JsonElement>("/api/history")).EnumerateArray()
        );
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>("/api/history")).EnumerateArray());
        Assert.Equal(
            HttpStatusCode.NoContent,
            (
                await admin.PostAsJsonAsync(
                    $"/api/users/{uid}/password",
                    new { password = "replacement-fixture-password" }
                )
            ).StatusCode
        );
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/media")).StatusCode);
        using var restored = await Login(server, "viewer", "replacement-fixture-password");
        Assert.Single(
            (await restored.GetFromJsonAsync<JsonElement>("/api/history")).EnumerateArray()
        );
    }

    [Fact]
    public async Task IndexedCursorSearchAndCollectionsRemainBoundedAtOneHundredThousandItems()
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
                    Name = "Benchmark",
                    Path = server.MediaPath,
                }
            );
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(
                "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100000) INSERT INTO Items(LibraryId,RelativePath,Title,SearchTitle,Container,VideoCodec,AudioCodec,Duration,Size,TracksJson,ContentRating,IsMissing,Fingerprint,ModifiedTicks) SELECT 1,printf('%06d.webm',x),printf('Movie %06d',x),printf('movie %06d',x),'webm','vp9','opus',100,100,'[]',10,0,'',0 FROM n"
            );
        }
        var timer = Stopwatch.StartNew();
        var first = await admin.GetFromJsonAsync<JsonElement>(
            "/api/media-page?prefix=movie%20099&limit=20"
        );
        timer.Stop();
        output.WriteLine($"100k library first HTTP page: {timer.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(20, first.GetProperty("items").GetArrayLength());
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var second = await admin.GetFromJsonAsync<JsonElement>(
            "/api/media-page?prefix=movie%20099&limit=20&cursor=" + Uri.EscapeDataString(cursor)
        );
        Assert.Empty(
            first
                .GetProperty("items")
                .EnumerateArray()
                .Select(x => x.GetProperty("id").GetInt32())
                .Intersect(
                    second
                        .GetProperty("items")
                        .EnumerateArray()
                        .Select(x => x.GetProperty("id").GetInt32())
                )
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.GetAsync(
                    "/api/media-page?prefix=different&cursor=" + Uri.EscapeDataString(cursor)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.GetAsync("/api/media-page?cursor=../../private")).StatusCode
        );
        var collection = await admin.PostAsJsonAsync(
            "/api/collections",
            new
            {
                name = "Nine",
                prefix = "movie 099",
                libraryId = 1,
                videoCodec = "vp9",
            }
        );
        var id = (await collection.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
        Assert.Equal(
            20,
            (
                await admin.GetFromJsonAsync<JsonElement>(
                    $"/api/media-page?collectionId={id}&limit=20"
                )
            )
                .GetProperty("items")
                .GetArrayLength()
        );
        var viewerId = await CreateViewer(admin, 1);
        using var viewer = await Login(server, "viewer", "viewer-fixture-password");
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await viewer.GetAsync($"/api/media-page?collectionId={id}")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await viewer.DeleteAsync($"/api/collections/{id}")).StatusCode
        );
    }

    [Fact]
    public async Task ActiveStreamCountersAreRemovedOnDisposalAndMetricsAreSanitized()
    {
        var registry = new StreamRegistry();
        using (var stream = registry.Track(new MemoryStream(new byte[100]), 2, 3))
        {
            await stream.ReadExactlyAsync(new byte[40]);
            Assert.Equal(40, Assert.Single(registry.Snapshot()).BytesRead);
            Assert.Equal(2, Assert.Single(registry.Snapshot()).UserId);
        }
        Assert.Empty(registry.Snapshot());
        using var server = new Server();
        using var admin = await server.Admin();
        var raw = await admin.GetStringAsync("/api/monitoring");
        Assert.DoesNotContain(server.MediaPath, raw);
        Assert.DoesNotContain(server.DataPath, raw);
        Assert.DoesNotContain("MediaServer.Session", raw);
        var parsed = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.False(parsed.GetProperty("transcodingSupported").GetBoolean());
        Assert.Equal(
            JsonValueKind.Null,
            parsed.GetProperty("transcodingFramesPerSecond").ValueKind
        );
    }
}
