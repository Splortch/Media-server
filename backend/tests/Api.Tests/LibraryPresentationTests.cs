using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public sealed class LibraryPresentationTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j2ioAAAAASUVORK5CYII="
    );

    private static async Task Seed(Server server)
    {
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
        db.Libraries.AddRange(
            new Library { Id = 1, Path = server.MediaPath },
            new Library { Id = 2, Path = server.MediaPath }
        );
        for (var id = 1; id <= 5; id++)
        {
            db.Items.Add(
                new MediaItem
                {
                    Id = id,
                    LibraryId = id == 5 ? 2 : 1,
                    RelativePath = id + ".webm",
                    Title = id == 4 ? "A movie" : "Episode " + id,
                    SearchTitle = id == 4 ? "a movie" : "episode " + id,
                    Duration = 100,
                    Size = 100,
                    ContentRating = id == 3 ? 18 : 10,
                }
            );
            if (id != 4)
                db.Metadata.Add(
                    new MediaMetadata
                    {
                        MediaItemId = id,
                        DocumentJson = JsonSerializer.Serialize(
                            new MetadataValue(
                                "Episode " + id,
                                2020,
                                "Synopsis",
                                [],
                                [],
                                "Our series",
                                1,
                                id,
                                "manual",
                                null,
                                ""
                            )
                        ),
                    }
                );
        }
        await db.SaveChangesAsync();
    }

    private static async Task<HttpClient> Viewer(Server server, HttpClient admin)
    {
        var created = await admin.PostAsJsonAsync(
            "/api/users",
            new
            {
                name = "viewer",
                password = "presentation-fixture-password",
                libraries = new[] { 1 },
                maximumRating = 13,
            }
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var client = server.CreateClient();
        await Server.Csrf(client);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PostAsJsonAsync(
                    "/api/auth/login",
                    new { name = "viewer", password = "presentation-fixture-password" }
                )
            ).StatusCode
        );
        await Server.Csrf(client);
        return client;
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1")]
    public async Task CatalogGroupsBeforePagingAndFiltersBeforeCounting(string prefix)
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await Seed(server);
        using var viewer = await Viewer(server, admin);
        var first = (await viewer.GetFromJsonAsync<CatalogPage>(prefix + "/catalog?limit=1"))!;
        Assert.Equal("A movie", Assert.Single(first.Items).Title);
        Assert.NotNull(first.NextCursor);
        var second = (
            await viewer.GetFromJsonAsync<CatalogPage>(
                prefix + "/catalog?limit=1&cursor=" + Uri.EscapeDataString(first.NextCursor)
            )
        )!;
        var series = Assert.Single(second.Items);
        Assert.Equal("Our series", series.Title);
        Assert.Equal(2, series.EpisodeCount);
        Assert.Equal(1, series.Id);
        Assert.Null(second.NextCursor);
        var search = (await viewer.GetFromJsonAsync<CatalogPage>(prefix + "/catalog?prefix=our"))!;
        Assert.Equal(2, Assert.Single(search.Items).EpisodeCount);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await viewer.GetAsync(
                    prefix
                        + "/catalog?prefix=other&cursor="
                        + Uri.EscapeDataString(first.NextCursor)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await viewer.GetAsync(prefix + "/media/5/images")).StatusCode
        );
    }

    [Fact]
    public async Task SeriesSearchFoldsUnicodeLikeTitleSearch()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await Seed(server);
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            foreach (var row in db.Metadata.Where(x => x.MediaItemId < 3))
            {
                var value = JsonSerializer.Deserialize<MetadataValue>(row.DocumentJson)!;
                row.DocumentJson = JsonSerializer.Serialize(
                    value with
                    {
                        Series = "Ångström show",
                    }
                );
            }
            await db.SaveChangesAsync();
        }
        var page = (
            await admin.GetFromJsonAsync<CatalogPage>(
                "/api/catalog?prefix=" + Uri.EscapeDataString("ångström")
            )
        )!;
        Assert.Equal(2, Assert.Single(page.Items).EpisodeCount);
    }

    [Fact]
    public async Task CuratedArtworkUploadChoicesAndRevisionsStayAuthorized()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await Seed(server);
        using var viewer = await Viewer(server, admin);
        var path = "/api/media/4/artwork/upload";
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await viewer.PostAsJsonAsync(
                    path,
                    new ArtworkUpload("poster", Convert.ToBase64String(Png), 0)
                )
            ).StatusCode
        );
        var response = await admin.PostAsJsonAsync(
            path,
            new ArtworkUpload("poster", Convert.ToBase64String(Png), 0)
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = (await response.Content.ReadFromJsonAsync<MetadataView>())!;
        Assert.Equal(Png, await admin.GetByteArrayAsync(view.PosterUrl));
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await admin.PostAsJsonAsync(
                    path,
                    new ArtworkUpload("poster", Convert.ToBase64String(Png), 0)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PostAsJsonAsync(
                    path,
                    new ArtworkUpload("poster", Convert.ToBase64String("<svg/>"u8.ToArray()), 1)
                )
            ).StatusCode
        );
        await File.WriteAllBytesAsync(Path.Combine(server.MediaPath, "4-poster.png"), Png);
        var candidates = (
            await admin.GetFromJsonAsync<ArtworkCandidate[]>("/api/media/4/artwork/candidates")
        )!;
        var local = Assert.Single(candidates, x => x.Key == "local-1");
        Assert.Equal(Png, await admin.GetByteArrayAsync(local.Url));
        var choice = await admin.PostAsJsonAsync(
            "/api/media/4/artwork/choice",
            new ArtworkChoice("backdrop", "existing", 1, "local-1")
        );
        Assert.Equal(HttpStatusCode.OK, choice.StatusCode);
        Assert.Equal(
            Png,
            await admin.GetByteArrayAsync(
                (await choice.Content.ReadFromJsonAsync<MetadataView>())!.BackdropUrl
            )
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PostAsJsonAsync(
                    "/api/media/4/artwork/choice",
                    new ArtworkChoice("poster", "existing", 2, "../../secret")
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task ArtworkGalleryRetainsPreviousSelectionsWithinItsOwnBoundedTitleScope()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await Seed(server);
        string? firstKey = null;
        for (var index = 0; index < 15; index++)
        {
            var bytes = Png.Concat(new[] { (byte)index }).ToArray();
            var upload = await admin.PostAsJsonAsync(
                "/api/media/4/artwork/upload",
                new ArtworkUpload("poster", Convert.ToBase64String(bytes), index)
            );
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            if (index == 0)
                firstKey = Assert
                    .Single(
                        (
                            await admin.GetFromJsonAsync<ArtworkCandidate[]>(
                                "/api/media/4/artwork/candidates"
                            )
                        )!
                    )
                    .Key;
        }
        var options = (
            await admin.GetFromJsonAsync<ArtworkCandidate[]>("/api/media/4/artwork/candidates")
        )!;
        Assert.Equal(12, options.Length);
        Assert.DoesNotContain(options, x => x.Key == firstKey);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.GetAsync("/api/media/4/artwork/candidate/" + firstKey)).StatusCode
        );
        var earlier = options[1];
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(earlier.Url)).StatusCode);
        var chosen = await admin.PostAsJsonAsync(
            "/api/media/4/artwork/choice",
            new ArtworkChoice("poster", "existing", 15, earlier.Key)
        );
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PostAsJsonAsync(
                    "/api/media/1/artwork/choice",
                    new ArtworkChoice("poster", "existing", 1, earlier.Key)
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task WatchActionsPreserveResumeSupportUndoAndRejectStaleRevisions()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await Seed(server);
        using var viewer = await Viewer(server, admin);
        var sync = await viewer.PutAsJsonAsync(
            "/api/v1/media/1/progress-sync",
            new ProgressUpdate(30, 0)
        );
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        var hiddenResponse = await viewer.PostAsJsonAsync(
            "/api/media/1/watch-state",
            new WatchChange("hide", 1)
        );
        Assert.Equal(HttpStatusCode.OK, hiddenResponse.StatusCode);
        var hidden = (await hiddenResponse.Content.ReadFromJsonAsync<WatchState>())!;
        Assert.True(hidden.Hidden);
        Assert.Equal(30, hidden.Progress.Seconds);
        Assert.Empty(
            (await viewer.GetFromJsonAsync<ProgressState[]>("/api/v1/continue-watching"))!
        );
        Assert.Empty(
            (await viewer.GetFromJsonAsync<MediaSummary[]>("/api/continue-watching-media"))!
        );
        var undo = await viewer.PostAsJsonAsync(
            "/api/media/1/watch-state",
            new WatchChange("restore", 2, hidden.Previous)
        );
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Single(
            (await viewer.GetFromJsonAsync<ProgressState[]>("/api/v1/continue-watching"))!
        );
        var watched = await viewer.PostAsJsonAsync(
            "/api/media/1/watch-state",
            new WatchChange("watched", 3)
        );
        Assert.True((await watched.Content.ReadFromJsonAsync<WatchState>())!.Progress.Completed);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await viewer.PostAsJsonAsync(
                    "/api/media/1/watch-state",
                    new WatchChange("unwatched", 3)
                )
            ).StatusCode
        );
        var unwatched = await viewer.PostAsJsonAsync(
            "/api/media/1/watch-state",
            new WatchChange("unwatched", 4)
        );
        Assert.False((await unwatched.Content.ReadFromJsonAsync<WatchState>())!.Progress.Completed);
        Assert.Equal(
            0,
            (await admin.GetFromJsonAsync<WatchState>("/api/media/1/watch-state"))!
                .Progress
                .Revision
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await viewer.PostAsJsonAsync(
                    "/api/media/5/watch-state",
                    new WatchChange("watched", 0)
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task RealGeneratedThumbnailSeekFramesAndReplacementPreserveSource()
    {
        using var server = new Server(realProbe: true);
        using var admin = await server.Admin();
        await Seed(server);
        var runner = new MediaServer.Media.FfmpegRunner("ffmpeg");
        await runner.RunAsync(
            [
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=64x64:rate=12",
                "-t",
                "2",
                "-c:v",
                "libx264",
                "-threads",
                "1",
                "-f",
                "matroska",
                "4.webm",
            ],
            server.MediaPath,
            default
        );
        // Matroska chooses its muxer by this explicit format rather than the extension.
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Items.Single(x => x.Id == 4).Duration = 2;
            await db.SaveChangesAsync();
        }
        await admin.PutAsJsonAsync(
            "/api/experiments/guests",
            new { enabled = true, libraries = new[] { 1 } }
        );
        using var guest = server.CreateClient();
        await Server.Csrf(guest);
        await guest.PostAsync("/api/auth/guest", null);
        foreach (var prefix in new[] { "/api", "/api/v1" })
        {
            var guestImage = await guest.GetAsync(prefix + "/media/4/thumbnail");
            Assert.Equal(HttpStatusCode.OK, guestImage.StatusCode);
            Assert.Equal("image/jpeg", guestImage.Content.Headers.ContentType!.MediaType);
        }
        var source = await File.ReadAllBytesAsync(Path.Combine(server.MediaPath, "4.webm"));
        var image = await admin.GetAsync("/api/media/4/thumbnail");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/jpeg", image.Content.Headers.ContentType!.MediaType);
        Assert.True((await image.Content.ReadAsByteArrayAsync()).Length > 3);
        var seek = await admin.GetAsync("/api/media/4/thumbnail?position=0");
        Assert.Equal(HttpStatusCode.OK, seek.StatusCode);
        var use = await admin.PostAsJsonAsync(
            "/api/media/4/artwork/choice",
            new ArtworkChoice("poster", "thumbnail", 0)
        );
        Assert.Equal(HttpStatusCode.OK, use.StatusCode);
        Assert.Equal(
            source,
            await File.ReadAllBytesAsync(Path.Combine(server.MediaPath, "4.webm"))
        );
        Assert.Empty(
            Directory.EnumerateDirectories(Path.Combine(server.DataPath, "preview-cache"), "job-*")
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.GetAsync("/api/media/4/thumbnail?position=-1")).StatusCode
        );
    }
}
