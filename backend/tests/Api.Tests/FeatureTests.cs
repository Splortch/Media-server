using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Api.Tests;

public sealed class FeatureTests
{
    private static async Task<HttpClient> Viewer(Server server, HttpClient admin)
    {
        var created = await admin.PostAsJsonAsync(
            "/api/users",
            new
            {
                name = "viewer",
                password = "viewer-fixture-password",
                libraries = Array.Empty<int>(),
            }
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var viewer = server.CreateClient();
        await Server.Csrf(viewer);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await viewer.PostAsJsonAsync(
                    "/api/auth/login",
                    new { name = "viewer", password = "viewer-fixture-password" }
                )
            ).StatusCode
        );
        await Server.Csrf(viewer);
        return viewer;
    }

    private static JsonElement Find(JsonElement state, string key) =>
        state
            .GetProperty("features")
            .EnumerateArray()
            .Single(f => f.GetProperty("key").GetString() == key);

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1")]
    public async Task FeatureMutationsRetainAntiforgeryProtection(string prefix)
    {
        using var server = new Server();
        using var admin = await server.Admin();
        using var viewer = await Viewer(server, admin);
        viewer.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await viewer.PutAsJsonAsync(
                    prefix + "/preferences/features",
                    new { hidden = new[] { "watch-history" } }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await viewer.DeleteAsync(prefix + "/preferences/features")).StatusCode
        );
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PutAsJsonAsync(
                    prefix + "/features/smart-collections",
                    new { enabled = false }
                )
            ).StatusCode
        );
        var unchanged = await admin.GetFromJsonAsync<JsonElement>(prefix + "/features");
        Assert.True(Find(unchanged, "smart-collections").GetProperty("enabled").GetBoolean());
        Assert.False(
            Find(await viewer.GetFromJsonAsync<JsonElement>(prefix + "/features"), "watch-history")
                .GetProperty("hidden")
                .GetBoolean()
        );
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1")]
    public async Task VisibilityIsPrivateValidatedAndDoesNotOverrideAdminPolicy(string prefix)
    {
        using var server = new Server();
        using var anonymous = server.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync(prefix + "/features")).StatusCode
        );
        using var admin = await server.Admin();
        using var viewer = await Viewer(server, admin);
        var changed = await viewer.PutAsJsonAsync(
            prefix + "/preferences/features",
            new { hidden = new[] { "smart-collections", "watch-history" } }
        );
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.True(
            Find(await changed.Content.ReadFromJsonAsync<JsonElement>(), "smart-collections")
                .GetProperty("hidden")
                .GetBoolean()
        );
        Assert.True(
            Find(
                    await admin.GetFromJsonAsync<JsonElement>(prefix + "/features"),
                    "smart-collections"
                )
                .GetProperty("visible")
                .GetBoolean()
        );
        // A second device using the same authenticated account reads server-owned preferences.
        using var second = server.CreateClient();
        await Server.Csrf(second);
        await second.PostAsJsonAsync(
            "/api/auth/login",
            new { name = "viewer", password = "viewer-fixture-password" }
        );
        Assert.False(
            Find(await second.GetFromJsonAsync<JsonElement>(prefix + "/features"), "watch-history")
                .GetProperty("visible")
                .GetBoolean()
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await viewer.PutAsJsonAsync(
                    prefix + "/features/smart-collections",
                    new { enabled = false }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PutAsJsonAsync(
                    prefix + "/features/watch-history",
                    new { enabled = false }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await viewer.PutAsJsonAsync(
                    prefix + "/preferences/features",
                    new { hidden = new[] { "playback" } }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await viewer.PutAsJsonAsync(
                    prefix + "/preferences/features",
                    new { hidden = new[] { "smart-collections", "smart-collections" } }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync(prefix + "/features/smart-collections", new { })).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    prefix + "/features/smart-collections",
                    new { enabled = false }
                )
            ).StatusCode
        );
        var reset = await viewer.DeleteAsync(prefix + "/preferences/features");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var feature = Find(
            await reset.Content.ReadFromJsonAsync<JsonElement>(),
            "smart-collections"
        );
        Assert.False(feature.GetProperty("enabled").GetBoolean());
        Assert.False(feature.GetProperty("visible").GetBoolean());
        Assert.False(feature.GetProperty("hidden").GetBoolean());
        Assert.True(
            Find(await viewer.GetFromJsonAsync<JsonElement>(prefix + "/features"), "watch-history")
                .GetProperty("visible")
                .GetBoolean()
        );
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1")]
    public async Task DisablingCollectionsPreservesDataAndCoreLibraryWhileRejectingNewUse(
        string prefix
    )
    {
        using var server = new Server();
        using var admin = await server.Admin();
        var created = await admin.PostAsJsonAsync(
            prefix + "/collections",
            new { name = "Keep me" }
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    prefix + "/features/smart-collections",
                    new { enabled = false }
                )
            ).StatusCode
        );
        Assert.Empty(
            (await admin.GetFromJsonAsync<JsonElement>(prefix + "/collections")).EnumerateArray()
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await admin.PostAsJsonAsync(prefix + "/collections", new { name = "Blocked" })
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await admin.GetAsync(prefix + "/media-page?collectionId=" + id)).StatusCode
        );
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(prefix + "/media-page")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    prefix + "/features/smart-collections",
                    new { enabled = true }
                )
            ).StatusCode
        );
        var restored = await admin.GetFromJsonAsync<JsonElement>(prefix + "/collections");
        Assert.Equal("Keep me", restored.EnumerateArray().Single().GetProperty("name").GetString());
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.GetAsync(prefix + "/media-page?collectionId=" + id)).StatusCode
        );
    }
}
