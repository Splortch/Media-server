using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public sealed class SeriesEpisodeTests
{
    private static async Task Seed(Server server)
    {
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
        db.Libraries.AddRange(
            new Library
            {
                Id = 1,
                Path = server.MediaPath,
                Kind = "tv",
            },
            new Library
            {
                Id = 2,
                Path = server.MediaPath,
                Kind = "tv",
            }
        );
        // Same episode numbers in other series/libraries must not be merged.
        foreach (
            var (id, library, series, season, episode, rating) in new[]
            {
                (1, 1, "Our show", 1, 1, 10),
                (2, 1, "Our show", 1, 2, 10),
                (3, 1, "Our show", 2, 1, 10),
                (4, 1, "Our show", 3, 1, 18),
                (5, 2, "Our show", 4, 1, 10),
                (6, 1, "Other show", 5, 1, 10),
                (7, 1, "Our show", 0, 1, 10),
                (8, 1, "Our show", 6, 1, 10),
            }
        )
        {
            db.Items.Add(
                new MediaItem
                {
                    Id = id,
                    LibraryId = library,
                    Title = "Episode " + id,
                    RelativePath = id + ".webm",
                    Duration = 120,
                    ContentRating = rating,
                    IsMissing = id == 8,
                }
            );
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
                            series,
                            season,
                            episode,
                            "manual",
                            null,
                            "Manual metadata"
                        )
                    ),
                }
            );
        }
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1")]
    public async Task SeasonsEpisodesNextAndPrivateProgressFollowAuthorizedItems(string prefix)
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await Seed(server);
        var path = prefix + "/media/1/episodes";
        using var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        var created = await admin.PostAsJsonAsync(
            "/api/users",
            new
            {
                name = "viewer",
                password = "episode-fixture-password",
                libraries = new[] { 1 },
                maximumRating = 13,
            }
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var uid = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Progress.AddRange(
                new PlaybackProgress
                {
                    UserId = uid,
                    MediaItemId = 2,
                    Seconds = 45,
                },
                new PlaybackProgress
                {
                    UserId = 1,
                    MediaItemId = 2,
                    Seconds = 90,
                    Completed = true,
                }
            );
            await db.SaveChangesAsync();
        }
        using var member = server.CreateClient();
        await Server.Csrf(member);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await member.PostAsJsonAsync(
                    "/api/auth/login",
                    new { name = "viewer", password = "episode-fixture-password" }
                )
            ).StatusCode
        );
        var navigation = (await member.GetFromJsonAsync<SeriesNavigation>(path))!;
        Assert.Equal("Our show", navigation.Series);
        Assert.Equal(new[] { 0, 1, 2 }, navigation.Seasons);
        Assert.Equal(new[] { 1, 2 }, navigation.Episodes.Select(x => x.Id));
        Assert.Equal(45, navigation.Episodes[1].Seconds);
        Assert.False(navigation.Episodes[1].Completed);
        Assert.Equal(2, navigation.NextEpisode!.Id);
        var second = (
            await member.GetFromJsonAsync<SeriesNavigation>(prefix + "/media/2/episodes")
        )!;
        Assert.Equal(3, second.NextEpisode!.Id);
        var last = (await member.GetFromJsonAsync<SeriesNavigation>(prefix + "/media/3/episodes"))!;
        Assert.Null(last.NextEpisode);
        var seasonTwo = (await member.GetFromJsonAsync<SeriesNavigation>(path + "?season=2"))!;
        Assert.Equal(3, Assert.Single(seasonTwo.Episodes).Id);
        Assert.Equal(2, seasonTwo.Season);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await member.GetAsync(prefix + "/media/4/episodes")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await member.GetAsync(prefix + "/media/5/episodes")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await member.GetAsync(path + "?season=-1")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await member.GetAsync(path + "?offset=100001")).StatusCode
        );
    }

    [Fact]
    public async Task MissingIdentificationIsEmptyAndLargeSeasonsArePaged()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await Seed(server);
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Metadata.Remove(db.Metadata.Single(x => x.MediaItemId == 6));
            for (var id = 10; id < 215; id++)
            {
                db.Items.Add(
                    new MediaItem
                    {
                        Id = id,
                        LibraryId = 1,
                        RelativePath = id + ".webm",
                        Title = "Paged",
                    }
                );
                db.Metadata.Add(
                    new MediaMetadata
                    {
                        MediaItemId = id,
                        DocumentJson = JsonSerializer.Serialize(
                            new MetadataValue(
                                "Paged",
                                null,
                                "",
                                [],
                                [],
                                "Paged show",
                                1,
                                id - 9,
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
        var none = (await admin.GetFromJsonAsync<SeriesNavigation>("/api/media/6/episodes"))!;
        Assert.Null(none.Series);
        Assert.Empty(none.Seasons);
        var page = (await admin.GetFromJsonAsync<SeriesNavigation>("/api/media/10/episodes"))!;
        Assert.Equal(200, page.Episodes.Length);
        Assert.Equal(200, page.NextOffset);
        var rest = (
            await admin.GetFromJsonAsync<SeriesNavigation>("/api/media/10/episodes?offset=200")
        )!;
        Assert.Equal(5, rest.Episodes.Length);
        Assert.Null(rest.NextOffset);
        Assert.Empty(page.Episodes.Select(x => x.Id).Intersect(rest.Episodes.Select(x => x.Id)));
    }
}
