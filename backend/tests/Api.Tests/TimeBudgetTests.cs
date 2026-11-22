using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public sealed class TimeBudgetTests
{
    [Fact]
    public async Task PickerUsesPrivateProgressAndSelectsTheNextAvailableRegularEpisodeBeforeFiltering()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        using var anonymous = server.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/time-budget?minutes=30")).StatusCode
        );
        await Seed(server);
        using var created = await admin.PostAsJsonAsync(
            "/api/users",
            new
            {
                name = "viewer",
                password = "fixture-viewer-password",
                libraries = new[] { 1 },
                maximumRating = 13,
            }
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var viewerId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Progress.AddRange(
                new PlaybackProgress
                {
                    UserId = viewerId,
                    MediaItemId = 1,
                    Seconds = 3000,
                },
                new PlaybackProgress
                {
                    UserId = viewerId,
                    MediaItemId = 4,
                    Seconds = 1200,
                },
                new PlaybackProgress
                {
                    UserId = viewerId,
                    MediaItemId = 11,
                    Completed = true,
                },
                new PlaybackProgress
                {
                    UserId = viewerId,
                    MediaItemId = 14,
                    Seconds = 1200,
                }
            );
            await db.SaveChangesAsync();
        }
        using var viewer = server.CreateClient();
        await Server.Csrf(viewer);
        using var login = await viewer.PostAsJsonAsync(
            "/api/auth/login",
            new { name = "viewer", password = "fixture-viewer-password" }
        );
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var normal = await Search(viewer, "minutes=30");
        Assert.Equal(2, Assert.Single(normal.Items).Id);
        var fast = await Search(viewer, "minutes=30&speed=1.5");
        Assert.Equal(new[] { 1, 14, 2 }, fast.Items.Select(x => x.Id));
        Assert.Equal(1600, fast.Items[0].RemainingSeconds);
        Assert.True(fast.Items[0].Started);
        Assert.Equal("Local series", fast.Items[1].Series);
        Assert.Equal(2, fast.Items[1].Season);
        Assert.Equal(1, fast.Items[1].Episode);
        Assert.Equal(
            2,
            Assert
                .Single((await Search(viewer, "minutes=30&speed=1.5&includeStarted=false")).Items)
                .Id
        );
        var administrator = await Search(admin, "minutes=30&speed=1.5");
        Assert.DoesNotContain(administrator.Items, item => item.Id == 1);
        Assert.Contains(administrator.Items, item => item.Id == 11);
        Assert.DoesNotContain(administrator.Items, item => item.Id == 14);
    }

    [Theory]
    [InlineData("minutes=0")]
    [InlineData("minutes=721")]
    [InlineData("minutes=35&speed=1.1")]
    [InlineData("minutes=35&speed=NaN")]
    public async Task RejectsUnsupportedBudgetAndSpeed(string query)
    {
        using var server = new Server();
        using var admin = await server.Admin();
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.GetAsync("/api/time-budget?" + query)).StatusCode
        );
    }

    private static async Task<TimeBudgetPage> Search(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<TimeBudgetPage>("/api/v1/time-budget?" + query))!;

    private static async Task Seed(Server server)
    {
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
        db.Libraries.AddRange(
            new Library
            {
                Id = 1,
                Path = server.MediaPath,
                Name = "Allowed",
            },
            new Library
            {
                Id = 2,
                Path = server.MediaPath,
                Name = "Restricted",
            }
        );
        foreach (
            var (id, minutes, season, episode) in new[]
            {
                (1, 90, -1, 0),
                (2, 30, -1, 0),
                (3, 0, -1, 0),
                (4, 20, -1, 0),
                (5, 20, -1, 0),
                (6, 20, -1, 0),
                (10, 10, 0, 1),
                (11, 25, 1, 1),
                (12, 25, 1, 2),
                (13, 25, 1, 3),
                (14, 60, 2, 1),
                (15, 15, 2, 2),
            }
        )
        {
            db.Items.Add(
                new MediaItem
                {
                    Id = id,
                    LibraryId = id == 6 ? 2 : 1,
                    Title = "Title " + id,
                    RelativePath = id + ".webm",
                    Duration = minutes * 60,
                    ContentRating = id is 5 or 13 ? 18 : 10,
                    IsMissing = id == 12,
                }
            );
            if (season >= 0)
                db.Metadata.Add(
                    new MediaMetadata
                    {
                        MediaItemId = id,
                        DocumentJson = JsonSerializer.Serialize(
                            new
                            {
                                Series = "Local series",
                                Season = season,
                                Episode = episode,
                            }
                        ),
                    }
                );
        }
        await db.SaveChangesAsync();
    }
}
