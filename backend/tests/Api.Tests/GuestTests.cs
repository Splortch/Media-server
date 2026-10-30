using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public sealed class GuestTests
{
    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1")]
    public async Task OptInIsScopedReadOnlyAndRevocable(string prefix)
    {
        using var server = new Server();
        using var admin = await server.Admin();
        using var guest = server.CreateClient();
        await Server.Csrf(guest);
        Assert.False(
            (await guest.GetFromJsonAsync<JsonElement>(prefix + "/auth/guest"))
                .GetProperty("enabled")
                .GetBoolean()
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await guest.PostAsync(prefix + "/auth/guest", null)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await guest.GetAsync(prefix + "/media")).StatusCode
        );
        int publicId,
            privateId;
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Libraries.AddRange(
                new Library
                {
                    Id = 1,
                    Name = "Shared",
                    Path = server.MediaPath,
                },
                new Library
                {
                    Id = 2,
                    Name = "Private",
                    Path = server.MediaPath,
                }
            );
            await File.WriteAllBytesAsync(
                Path.Combine(server.MediaPath, "shared.webm"),
                new byte[100]
            );
            var shared = new MediaItem
            {
                LibraryId = 1,
                Title = "Shared film",
                RelativePath = "shared.webm",
                Container = "webm",
            };
            var hidden = new MediaItem
            {
                LibraryId = 2,
                Title = "Private film",
                RelativePath = "shared.webm",
                Container = "webm",
            };
            db.Items.AddRange(shared, hidden);
            await db.SaveChangesAsync();
            publicId = shared.Id;
            privateId = hidden.Id;
            db.Progress.Add(
                new PlaybackProgress
                {
                    UserId = 1,
                    MediaItemId = publicId,
                    Seconds = 30,
                }
            );
            await db.SaveChangesAsync();
        }
        var policy = new { enabled = true, libraries = new[] { 1 } };
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(prefix + "/experiments/guests", policy)).StatusCode
        );
        guest.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await guest.PostAsync(prefix + "/auth/guest", null)).StatusCode
        );
        await Server.Csrf(guest);
        Assert.Equal(
            HttpStatusCode.OK,
            (await guest.PostAsync(prefix + "/auth/guest", null)).StatusCode
        );
        await Server.Csrf(guest);
        var items = await guest.GetFromJsonAsync<JsonElement>(prefix + "/media");
        Assert.Single(items.EnumerateArray());
        Assert.Equal(publicId, items[0].GetProperty("id").GetInt32());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.GetAsync(prefix + $"/media/{privateId}")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.GetAsync(prefix + $"/media/{privateId}/stream")).StatusCode
        );
        var request = new HttpRequestMessage(HttpMethod.Get, prefix + $"/media/{publicId}/stream");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 9);
        Assert.Equal(HttpStatusCode.PartialContent, (await guest.SendAsync(request)).StatusCode);
        Assert.Empty(
            (await guest.GetFromJsonAsync<JsonElement>(prefix + "/progress")).EnumerateArray()
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await guest.GetAsync(prefix + "/history")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await guest.GetAsync(prefix + "/experiments/guests")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await guest.GetAsync(prefix + "/users")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await guest.PutAsJsonAsync(
                    prefix + $"/media/{publicId}/progress",
                    new { seconds = 99 }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await guest.PutAsJsonAsync(prefix + "/experiments/guests", policy)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await admin.PutAsJsonAsync(
                    prefix + "/experiments/guests",
                    new { enabled = false, libraries = new[] { 1 } }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await guest.GetAsync(prefix + $"/media/{publicId}/stream")).StatusCode
        );
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(prefix + "/media")).StatusCode);
    }

    [Fact]
    public async Task RejectsEmptyUnknownAndDuplicateGrantsAndExpiresSessions()
    {
        using var server = new Server();
        using var admin = await server.Admin();
        foreach (var ids in new[] { Array.Empty<int>(), new[] { 999 }, new[] { 1, 1 } })
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await admin.PutAsJsonAsync(
                        "/api/experiments/guests",
                        new { enabled = true, libraries = ids }
                    )
                ).StatusCode
            );
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Libraries.Add(
                new Library
                {
                    Id = 1,
                    Name = "Shared",
                    Path = server.MediaPath,
                }
            );
            await db.SaveChangesAsync();
        }
        await admin.PutAsJsonAsync(
            "/api/experiments/guests",
            new { enabled = true, libraries = new[] { 1 } }
        );
        using var guest = server.CreateClient();
        await Server.Csrf(guest);
        await guest.PostAsync("/api/auth/guest", null);
        await Server.Csrf(guest);
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            var account = await db.Users.SingleAsync(x => x.Role == "Guest");
            var session = await db.Sessions.SingleAsync(x => x.UserId == account.Id);
            Assert.InRange((session.ExpiresAt - DateTime.UtcNow).TotalHours, 3.9, 4.01);
            Assert.False(account.AllLibraries);
            session.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/media")).StatusCode);
    }
}
