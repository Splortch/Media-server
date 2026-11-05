using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Downloads;
using MediaServer.Infrastructure;
using MediaServer.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests;

public sealed class MetadataTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j2ioAAAAASUVORK5CYII="
    );

    private static MetadataValue Value(string title) =>
        new(
            title,
            2020,
            "A local description",
            ["Drama"],
            ["Fixture actor"],
            null,
            null,
            null,
            "spoofed",
            "123",
            "spoofed"
        );

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1")]
    public async Task LocalImportManualCorrectionsRevisionAndAccess(string prefix)
    {
        using var server = new Server();
        using var admin = await server.Admin();
        await File.WriteAllTextAsync(
            Path.Combine(server.MediaPath, "fixture.nfo"),
            "<movie><title>Local film</title><year>2020</year><plot>Offline synopsis</plot><genre>Drama</genre><actor><name>Local actor</name></actor><thumb>http://127.0.0.1/private</thumb></movie>"
        );
        await File.WriteAllBytesAsync(Path.Combine(server.MediaPath, "poster.png"), Png);
        await File.WriteAllBytesAsync(Path.Combine(server.MediaPath, "fixture.webm"), new byte[32]);
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            db.Libraries.Add(
                new Library
                {
                    Id = 1,
                    Name = "Home",
                    Path = server.MediaPath,
                }
            );
            db.Items.Add(
                new MediaItem
                {
                    Id = 1,
                    LibraryId = 1,
                    Title = "fixture",
                    RelativePath = "fixture.webm",
                    ContentRating = 18,
                }
            );
            await db.SaveChangesAsync();
        }
        var path = prefix + "/media/1/metadata";
        using var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.False(
            (await admin.GetFromJsonAsync<JsonElement>(prefix + "/metadata/providers"))
                .GetProperty("enabled")
                .GetBoolean()
        );
        Assert.Equal("null", await admin.GetStringAsync(path));
        var response = await admin.PostAsJsonAsync(path + "/local", new MetadataRevision(0));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var local = (await response.Content.ReadFromJsonAsync<MetadataView>())!;
        Assert.Equal("Local film", local.Details.Title);
        Assert.Equal("local-nfo", local.Details.Provider);
        Assert.NotNull(local.PosterUrl);
        Assert.Equal(Png, await admin.GetByteArrayAsync(local.PosterUrl));
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await admin.PutAsJsonAsync(path, new MetadataCorrection(Value("stale"), 0))).StatusCode
        );
        var edited = await admin.PutAsJsonAsync(
            path,
            new MetadataCorrection(Value("Corrected"), 1)
        );
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var corrected = (await edited.Content.ReadFromJsonAsync<MetadataView>())!;
        Assert.Equal("manual", corrected.Details.Provider);
        Assert.Null(corrected.Details.ProviderId);
        Assert.Equal(local.PosterUrl!.Split('?')[0], corrected.PosterUrl!.Split('?')[0]);
        Assert.DoesNotContain(server.MediaPath, await edited.Content.ReadAsStringAsync());
        await File.WriteAllTextAsync(
            Path.Combine(server.MediaPath, "fixture.nfo"),
            "<!DOCTYPE movie [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><movie><title>&x;</title></movie>"
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync(path + "/local", new MetadataRevision(2))).StatusCode
        );
        Assert.Equal(
            "Corrected",
            (await admin.GetFromJsonAsync<MetadataView>(path))!.Details.Title
        );
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
            Assert.Equal(18, db.Items.Single().ContentRating);
            Assert.Equal("Corrected", db.Items.Single().Title);
        }
        var scanResponse = await admin.PostAsync("/api/libraries/1/scan", null);
        Assert.Equal(HttpStatusCode.Accepted, scanResponse.StatusCode);
        var scanId = (await scanResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetInt32();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (true)
            {
                var state = (
                    await admin.GetFromJsonAsync<JsonElement>($"/api/scans/{scanId}", timeout.Token)
                )
                    .GetProperty("state")
                    .GetString();
                if (state == "completed")
                    break;
                Assert.NotEqual("failed", state);
                await Task.Delay(25, timeout.Token);
            }
        }
        using (var scope = server.Services.CreateScope())
            Assert.Equal(
                "Corrected",
                scope.ServiceProvider.GetRequiredService<MediaDb>().Items.Single().Title
            );
        Assert.Equal(
            HttpStatusCode.Created,
            (
                await admin.PostAsJsonAsync(
                    "/api/users",
                    new
                    {
                        name = "limited",
                        password = "metadata-fixture-password",
                        libraries = Array.Empty<int>(),
                    }
                )
            ).StatusCode
        );
        using var member = server.CreateClient();
        await Server.Csrf(member);
        await member.PostAsJsonAsync(
            "/api/auth/login",
            new { name = "limited", password = "metadata-fixture-password" }
        );
        await Server.Csrf(member);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync(path)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await member.GetAsync(corrected.PosterUrl)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await member.PutAsJsonAsync(path, new MetadataCorrection(Value("forbidden"), 2))
            ).StatusCode
        );
    }

    [Fact]
    public async Task ArtworkRejectsActiveContentOversizeAndLinks()
    {
        var root = Path.Combine(TestPaths.TempRoot, "art-tests-" + Guid.NewGuid());
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?> { ["MediaServer:DataDirectory"] = root }
                )
                .Build();
            var cache = new ArtworkCache(config);
            await Assert.ThrowsAsync<MetadataFault>(() =>
                cache.Store(
                    new MemoryStream(Encoding.UTF8.GetBytes("<svg onload='alert(1)'/>")),
                    "image/svg+xml",
                    default
                )
            );
            await Assert.ThrowsAsync<MetadataFault>(() =>
                cache.Store(
                    new MemoryStream(new byte[ArtworkCache.FileLimit + 1]),
                    "image/png",
                    default
                )
            );
            await Assert.ThrowsAsync<MetadataFault>(() =>
                cache.Store(new MemoryStream(Png), "text/html", default)
            );
            var key = await cache.Store(new MemoryStream(Png), "image/png", default);
            using (var file = cache.Open(key))
                Assert.Equal(Png.Length, file.Length);
            Assert.Throws<MetadataFault>(() => cache.Open("../secret"));
            if (!OperatingSystem.IsWindows())
            {
                var target = Path.Combine(root, "artwork-cache", key);
                File.Delete(target);
                File.CreateSymbolicLink(target, "/etc/passwd");
                Assert.Throws<ArgumentException>(() => cache.Open(key));
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LocalEpisodeMetadataNeedsNoProviderAccount()
    {
        using var source = new MemoryStream(
            Encoding.UTF8.GetBytes(
                "<episodedetails><title>Episode title</title><showtitle>Series title</showtitle><season>2</season><episode>3</episode><year>2020</year><plot>Local episode synopsis</plot></episodedetails>"
            )
        );
        var value = LocalMetadata.Parse(source);
        Assert.Equal("Series title", value.Series);
        Assert.Equal(2, value.Season);
        Assert.Equal(3, value.Episode);
        Assert.Equal("Episode title", value.Title);
    }

    private sealed class Source : HttpMessageHandler, IDownloadTransport
    {
        public int Calls;
        public bool Throttle;
        public List<(string Host, string? Auth)> Requests = [];

        public HttpClient Create(string[] hosts) => new(this, false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Calls++;
            Requests.Add((request.RequestUri!.Host, request.Headers.Authorization?.ToString()));
            if (Throttle)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            if (request.RequestUri.Host == "image.tmdb.org")
            {
                var content = new ByteArrayContent(Png);
                content.Headers.ContentType = new("image/png");
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = content }
                );
            }
            object value = request.RequestUri.AbsolutePath.Contains("search")
                ? new
                {
                    results = new[]
                    {
                        new
                        {
                            id = 42,
                            title = "Fixture",
                            release_date = "2020-01-01",
                        },
                    },
                }
                : (object)
                    new
                    {
                        title = "Fixture",
                        release_date = "2020-01-01",
                        overview = "Synopsis",
                        poster_path = "/fixture.png",
                        backdrop_path = (string?)null,
                        genres = new[] { new { name = "Drama" } },
                        credits = new { cast = new[] { new { name = "Fixture actor" } } },
                    };
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) }
            );
        }
    }

    [Fact]
    public async Task ProviderExplicitOptInBoundedCacheCredentialSeparationAndRateLimit()
    {
        var root = Path.Combine(TestPaths.TempRoot, "provider-tests-" + Guid.NewGuid());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["MediaServer:DataDirectory"] = root,
                    ["Metadata:Tmdb:Enabled"] = "true",
                    ["Metadata:Tmdb:ReadAccessToken"] = "private-fixture-token",
                }
            )
            .Build();
        try
        {
            using var source = new Source();
            var provider = new TmdbProvider(config, source);
            var result = await provider.Search(new("Fixture", "movies"), default);
            Assert.Single(result);
            await provider.Search(new("Fixture", "movies"), default);
            Assert.Equal(1, source.Calls);
            var match = await provider.Match(new("42", "movies", 0), default);
            Assert.Equal("tmdb", match.Details.Provider);
            var service = new MetadataService(new ArtworkCache(config), source);
            Assert.NotNull(await service.Image(match.Poster, false, default));
            Assert.All(
                source.Requests.Where(x => x.Host == "api.themoviedb.org"),
                x => Assert.Equal("Bearer private-fixture-token", x.Auth)
            );
            Assert.All(
                source.Requests.Where(x => x.Host == "image.tmdb.org"),
                x => Assert.Null(x.Auth)
            );
            await Assert.ThrowsAsync<MetadataFault>(() =>
                service.Image("https://127.0.0.1/secret.png", false, default)
            );
            await Assert.ThrowsAsync<MetadataFault>(() =>
                provider.Match(new("../42", "movies", 0), default)
            );
            await Assert.ThrowsAsync<MetadataFault>(() =>
                provider.Match(new("42", "tv", 0, 1), default)
            );
            config["Metadata:Tmdb:Enabled"] = "false";
            await Assert.ThrowsAsync<MetadataFault>(() =>
                provider.Match(new("42", "movies", 0), default)
            );
            config["Metadata:Tmdb:Enabled"] = "true";
            source.Throttle = true;
            await Assert.ThrowsAsync<MetadataFault>(() =>
                provider.Match(new("42", "movies", 0), default)
            );
            var calls = source.Calls;
            await Assert.ThrowsAsync<MetadataFault>(() =>
                provider.Match(new("42", "movies", 0), default)
            );
            Assert.Equal(calls, source.Calls);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
