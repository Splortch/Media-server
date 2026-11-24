using System.Net;
using System.Net.Http.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public sealed class WatchlistTests
{
    [Fact]
    public async Task SavesArePrivateAndRecommendationsRecheckLibraryAndRatingAccess()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Libraries.AddRange(
                new Library { Id = 1, Path = server.MediaPath },
                new Library { Id = 2, Path = server.MediaPath }
            );
            db.Items.AddRange(
                Item(1, "Saved title"),
                Item(2, "Shared recommendation"),
                Item(3, "Restricted rating", rating: 18),
                Item(4, "Other library", library: 2),
                Item(5, "Already watched"),
                Item(6, "Still watching"),
                Item(7, "Another recommendation")
            );
            await db.SaveChangesAsync();
        }

        const string password = "watchlist-fixture-password";
        Assert.Equal(
            HttpStatusCode.Created,
            (
                await admin.PostAsJsonAsync(
                    "/api/users",
                    new
                    {
                        name = "viewer",
                        password,
                        libraries = new[] { 1 },
                        maximumRating = 13,
                    }
                )
            ).StatusCode
        );
        using var viewer = await SignIn(server, password);
        using var secondDevice = await SignIn(server, password);
        using var anonymous = server.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/watchlist")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await viewer.PutAsync("/api/watchlist/1", null)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await viewer.PutAsync("/api/v1/watchlist/1", null)).StatusCode
        );
        Assert.True(
            (await secondDevice.GetFromJsonAsync<WatchlistStatus>("/api/watchlist/1"))!.Saved
        );
        Assert.False((await admin.GetFromJsonAsync<WatchlistStatus>("/api/watchlist/1"))!.Saved);
        Assert.Empty((await admin.GetFromJsonAsync<WatchlistPage>("/api/watchlist"))!.Items);
        Assert.Empty(
            (
                await admin.GetFromJsonAsync<WatchlistRecommendations>(
                    "/api/watchlist/recommendations"
                )
            )!.Items
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await viewer.PutAsync("/api/watchlist/3", null)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await viewer.PutAsync("/api/watchlist/4", null)).StatusCode
        );
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            var uid = db.Users.Single(x => x.Name == "viewer").Id;
            db.History.Add(new WatchHistory { UserId = uid, MediaItemId = 5 });
            db.Progress.Add(
                new PlaybackProgress
                {
                    UserId = uid,
                    MediaItemId = 6,
                    Seconds = 10,
                }
            );
            await db.SaveChangesAsync();
        }

        Assert.Equal(
            1,
            Assert
                .Single((await viewer.GetFromJsonAsync<WatchlistPage>("/api/watchlist"))!.Items)
                .Id
        );
        var recommendations = (
            await secondDevice.GetFromJsonAsync<WatchlistRecommendations>(
                "/api/v1/watchlist/recommendations"
            )
        )!;
        Assert.Equal("Saved title", recommendations.BasedOn);
        Assert.Equal(new[] { 7, 2 }, recommendations.Items.Select(x => x.Id));

        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Grants.Remove(db.Grants.Single());
            await db.SaveChangesAsync();
        }
        Assert.Empty((await viewer.GetFromJsonAsync<WatchlistPage>("/api/watchlist"))!.Items);
        Assert.Null(
            (
                await viewer.GetFromJsonAsync<WatchlistRecommendations>(
                    "/api/watchlist/recommendations"
                )
            )!.BasedOn
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await viewer.GetAsync("/api/watchlist/1")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await viewer.DeleteAsync("/api/watchlist/1")).StatusCode
        );
    }

    [Fact]
    public async Task PagingHasStableOrderingWhenSavedTitlesHaveTheSameTimestamp()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Libraries.Add(new Library { Id = 1, Path = server.MediaPath });
            var user = db.Users.Single(x => x.Name == "admin");
            var timestamp = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            for (var id = 1; id <= 62; id++)
            {
                db.Items.Add(Item(id, $"Title {id}"));
                db.Watchlist.Add(
                    new WatchlistEntry
                    {
                        UserId = user.Id,
                        MediaItemId = id,
                        AddedAt = timestamp,
                    }
                );
            }
            await db.SaveChangesAsync();
        }
        var first = (await admin.GetFromJsonAsync<WatchlistPage>("/api/watchlist"))!;
        Assert.Equal(Enumerable.Range(3, 60).Reverse(), first.Items.Select(x => x.Id));
        Assert.NotNull(first.NextCursor);
        await admin.DeleteAsync("/api/watchlist/62");
        var second = (
            await admin.GetFromJsonAsync<WatchlistPage>(
                "/api/watchlist?cursor=" + Uri.EscapeDataString(first.NextCursor)
            )
        )!;
        Assert.Equal(new[] { 2, 1 }, second.Items.Select(x => x.Id));
        Assert.Null(second.NextCursor);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.GetAsync("/api/watchlist?cursor=broken")).StatusCode
        );
    }

    private static MediaItem Item(int id, string title, int library = 1, int rating = 10) =>
        new()
        {
            Id = id,
            LibraryId = library,
            RelativePath = id + ".webm",
            Title = title,
            SearchTitle = title.ToLowerInvariant(),
            ContentRating = rating,
        };

    private static async Task<HttpClient> SignIn(Server server, string password)
    {
        var client = server.CreateClient();
        await Server.Csrf(client);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PostAsJsonAsync("/api/auth/login", new { name = "viewer", password })
            ).StatusCode
        );
        await Server.Csrf(client);
        return client;
    }
}
