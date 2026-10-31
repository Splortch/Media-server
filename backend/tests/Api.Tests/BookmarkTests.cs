using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public sealed class BookmarkTests
{
    private static async Task Seed(Server server)
    {
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
        db.Libraries.AddRange(
            new Library
            {
                Id = 1,
                Name = "Home",
                Path = server.MediaPath,
            },
            new Library
            {
                Id = 2,
                Name = "Restricted",
                Path = server.MediaPath,
            }
        );
        db.Items.AddRange(
            new MediaItem
            {
                Id = 1,
                LibraryId = 1,
                Title = "Fixture",
                RelativePath = "private-path.webm",
                Duration = 120,
                Fingerprint = new string('A', 64),
            },
            new MediaItem
            {
                Id = 2,
                LibraryId = 2,
                Title = "Restricted",
                RelativePath = "private-other.webm",
                Duration = 120,
                Fingerprint = new string('B', 64),
            }
        );
        await db.SaveChangesAsync();
    }

    private static async Task<HttpClient> Member(Server server, HttpClient admin)
    {
        Assert.Equal(
            HttpStatusCode.Created,
            (
                await admin.PostAsJsonAsync(
                    "/api/users",
                    new
                    {
                        name = "viewer",
                        password = "bookmark-fixture-password",
                        libraries = new[] { 1 },
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
                    new { name = "viewer", password = "bookmark-fixture-password" }
                )
            ).StatusCode
        );
        await Server.Csrf(client);
        return client;
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1")]
    public async Task PrivateCrudRevisionAclAndPortableImport(string prefix)
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await Seed(server);
        using var viewer = await Member(server, admin);
        using var anonymous = server.CreateClient();
        var path = prefix + "/media/1/bookmarks";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        var created = await admin.PostAsJsonAsync(
            path,
            new BookmarkInput(32.5, "Favorite scene", "Private <script>text</script>")
        );
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var first = (await created.Content.ReadFromJsonAsync<BookmarkView>())!;
        Assert.Empty((await viewer.GetFromJsonAsync<BookmarkView[]>(path))!);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await viewer.PutAsJsonAsync(
                    path + "/" + first.Id,
                    new BookmarkInput(1, "Stolen", "", 1)
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await viewer.DeleteAsync(path + "/" + first.Id + "/1")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await viewer.GetAsync(prefix + "/media/2/bookmarks/export")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await admin.PutAsJsonAsync(
                    path + "/" + first.Id,
                    new BookmarkInput(33, "Wrong revision", "", 0)
                )
            ).StatusCode
        );
        var changed = await admin.PutAsJsonAsync(
            path + "/" + first.Id,
            new BookmarkInput(33, "Renamed scene", "Private note", 1)
        );
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await admin.DeleteAsync(path + "/" + first.Id + "/1")).StatusCode
        );
        var export = await admin.GetStringAsync(path + "/export");
        Assert.DoesNotContain("private-path", export);
        Assert.DoesNotContain("mediaItemId", export);
        Assert.DoesNotContain("userId", export);
        var archive = JsonSerializer.Deserialize<BookmarkArchive>(
            export,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;
        Assert.Equal("sha256:" + new string('a', 64), archive.MediaKey);
        Assert.Equal(
            HttpStatusCode.OK,
            (await viewer.PostAsJsonAsync(path + "/import", archive)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (await viewer.PostAsJsonAsync(path + "/import", archive)).StatusCode
        );
        Assert.Single((await viewer.GetFromJsonAsync<BookmarkView[]>(path))!);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync(prefix + "/media/2/bookmarks/import", archive)).StatusCode
        );
        var invalid = archive with
        {
            Bookmarks =
            [
                new BookmarkTransfer(Guid.NewGuid().ToString("N"), 3, "Would be added", ""),
                new BookmarkTransfer(Guid.NewGuid().ToString("N"), 121, "Out of range", ""),
            ],
        };
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync(path + "/import", invalid)).StatusCode
        );
        Assert.Single((await admin.GetFromJsonAsync<BookmarkView[]>(path))!);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await admin.DeleteAsync(path + "/" + first.Id + "/2")).StatusCode
        );
        Assert.Empty((await admin.GetFromJsonAsync<BookmarkView[]>(path))!);
        Assert.Single((await viewer.GetFromJsonAsync<BookmarkView[]>(path))!);
    }

    [Fact]
    public async Task BoundedValidatedAndNoCsrfBypass()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await Seed(server);
        const string path = "/api/media/1/bookmarks";
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync(path, new BookmarkInput(-1, "Bad", ""))).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync(path, new BookmarkInput(1, "", ""))).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PostAsJsonAsync(path, new BookmarkInput(1, "Bad", new string('n', 501)))
            ).StatusCode
        );
        for (var i = 0; i < 15; i++)
            Assert.Equal(
                HttpStatusCode.OK,
                (
                    await admin.PostAsJsonAsync(path, new BookmarkInput(i, "Scene " + i, ""))
                ).StatusCode
            );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync(path, new BookmarkInput(1, "Too many", ""))).StatusCode
        );
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync(path, new BookmarkInput(1, "CSRF", ""))).StatusCode
        );
    }
}
