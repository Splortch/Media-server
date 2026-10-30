using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public sealed class MovieRequestTests
{
    private static async Task<HttpClient> Member(
        Server server,
        HttpClient admin,
        string name,
        int library,
        int? maximum = null
    )
    {
        Assert.Equal(
            HttpStatusCode.Created,
            (
                await admin.PostAsJsonAsync(
                    "/api/users",
                    new
                    {
                        name,
                        password = "request-fixture-password",
                        libraries = new[] { library },
                        maximumRating = maximum,
                    }
                )
            ).StatusCode
        );
        var client = server.CreateClient();
        await Server.Csrf(client);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PostAsJsonAsync(
                    "/api/auth/login",
                    new { name, password = "request-fixture-password" }
                )
            ).StatusCode
        );
        await Server.Csrf(client);
        return client;
    }

    private static async Task Seed(Server server)
    {
        using var scope = server.Services.CreateScope();
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
                Path = server.MediaPath,
            }
        );
        db.Items.Add(
            new MediaItem
            {
                Id = 1,
                LibraryId = 2,
                Title = "Private film",
                RelativePath = "private.webm",
                Container = "webm",
            }
        );
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1")]
    public async Task RequestsAreOptInScopedSupportedAndExplicitlyReviewed(string prefix)
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await Seed(server);
        using var first = await Member(server, admin, "first", 1);
        using var second = await Member(server, admin, "second", 1);
        using var other = await Member(server, admin, "other", 2);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await first.GetAsync(prefix + "/requests")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    prefix + "/features/movie-requests",
                    new { enabled = true }
                )
            ).StatusCode
        );
        var created = await first.PostAsJsonAsync(
            prefix + "/requests",
            new
            {
                libraryId = 1,
                title = "Family Film",
                year = 2020,
                note = "For movie night",
            }
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await other.GetAsync(prefix + $"/requests/{id}")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await other.PutAsync(prefix + $"/requests/{id}/support", null)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await second.PostAsJsonAsync(
                    prefix + "/requests",
                    new
                    {
                        libraryId = 1,
                        title = "  FAMILY  Film ",
                        year = 2020,
                    }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await second.PutAsync(prefix + $"/requests/{id}/support", null)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await second.PutAsync(prefix + $"/requests/{id}/support", null)).StatusCode
        );
        var view = await first.GetFromJsonAsync<JsonElement>(prefix + $"/requests/{id}");
        Assert.Equal(1, view.GetProperty("supportCount").GetInt32());
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await first.PutAsJsonAsync(
                    prefix + $"/requests/{id}",
                    new { state = "Considering", revision = 0 }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PutAsJsonAsync(
                    prefix + $"/requests/{id}",
                    new
                    {
                        state = "Added",
                        mediaItemId = 1,
                        revision = 0,
                    }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PutAsJsonAsync(
                    prefix + $"/requests/{id}",
                    new { state = "Added", revision = 0 }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    prefix + $"/requests/{id}",
                    new
                    {
                        state = "Considering",
                        reply = "Will look for a legal copy",
                        revision = 0,
                    }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await admin.PutAsJsonAsync(
                    prefix + $"/requests/{id}",
                    new { state = "Declined", revision = 0 }
                )
            ).StatusCode
        );
        Assert.Equal(
            "Will look for a legal copy",
            (await first.GetFromJsonAsync<JsonElement>(prefix + $"/requests/{id}"))
                .GetProperty("reply")
                .GetString()
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    prefix + "/features/movie-requests",
                    new { enabled = false }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await first.GetAsync(prefix + "/requests")).StatusCode
        );
        await admin.PutAsJsonAsync(prefix + "/features/movie-requests", new { enabled = true });
        Assert.Equal(
            HttpStatusCode.OK,
            (await first.GetAsync(prefix + $"/requests/{id}")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await second.DeleteAsync(prefix + $"/requests/{id}")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await first.DeleteAsync(prefix + $"/requests/{id}")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await second.GetAsync(prefix + $"/requests/{id}")).StatusCode
        );
    }

    [Fact]
    public async Task ParentalRestrictionsCsrfAndLinkedMediaRemainEnforced()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await Seed(server);
        using var child = await Member(server, admin, "child", 1, 13);
        await admin.PutAsJsonAsync("/api/features/movie-requests", new { enabled = true });
        await admin.PostAsJsonAsync(
            "/api/requests",
            new { libraryId = 1, title = "Unclassified adult request" }
        );
        Assert.Empty((await child.GetFromJsonAsync<JsonElement>("/api/requests")).EnumerateArray());
        child.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await child.PostAsJsonAsync(
                    "/api/requests",
                    new { libraryId = 1, title = "Own film" }
                )
            ).StatusCode
        );
        await Server.Csrf(child);
        var created = await child.PostAsJsonAsync(
            "/api/requests",
            new { libraryId = 1, title = "Own film" }
        );
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Items.Add(
                new MediaItem
                {
                    Id = 2,
                    LibraryId = 1,
                    Title = "Own film",
                    RelativePath = "own.webm",
                    Container = "webm",
                    ContentRating = 18,
                }
            );
            await db.SaveChangesAsync();
        }
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    $"/api/requests/{id}",
                    new
                    {
                        state = "Added",
                        mediaItemId = 2,
                        revision = 0,
                    }
                )
            ).StatusCode
        );
        Assert.Equal(
            JsonValueKind.Null,
            (await child.GetFromJsonAsync<JsonElement>($"/api/requests/{id}"))
                .GetProperty("mediaItemId")
                .ValueKind
        );
        Assert.Equal(HttpStatusCode.NotFound, (await child.GetAsync("/api/media/2")).StatusCode);
        using var anonymous = server.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/requests")).StatusCode
        );
    }
}
