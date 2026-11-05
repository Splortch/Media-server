using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MediaServer.Downloads;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Api.Tests;

public sealed class CatalogTests
{
    private sealed class Source : HttpMessageHandler, IDownloadTransport
    {
        public readonly byte[] Bytes = Enumerable
            .Range(0, 32768)
            .Select(i => (byte)(i % 251))
            .ToArray();
        public string License = "CC0",
            Url = "https://upload.wikimedia.org/wikipedia/commons/f/ff/Fixture.webm";
        public bool CorruptMedia;
        public HttpStatusCode CatalogStatus = HttpStatusCode.OK;
        public int CatalogCalls,
            MediaCalls;
        public string LastQuery = "";

        public JsonElement Page() =>
            JsonSerializer.SerializeToElement(
                new
                {
                    pageid = 42,
                    title = "File:Fixture.webm",
                    imageinfo = new[]
                    {
                        new
                        {
                            url = Url,
                            mime = "video/webm",
                            size = Bytes.Length,
                            sha1 = Convert.ToHexString(SHA1.HashData(Bytes)).ToLowerInvariant(),
                            extmetadata = new Dictionary<string, object>
                            {
                                ["LicenseShortName"] = new { value = License },
                                ["LicenseUrl"] = new
                                {
                                    value = "https://creativecommons.org/publicdomain/zero/1.0/",
                                },
                                ["Copyrighted"] = new { value = "True" },
                                ["Artist"] = new
                                {
                                    value = "<a href='https://example.org'>Synthetic creator</a>",
                                },
                            },
                        },
                    },
                }
            );

        public HttpClient Create(string[] hosts) => new(this, false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            if (request.RequestUri!.Host == "commons.wikimedia.org")
            {
                CatalogCalls++;
                LastQuery = request.RequestUri.Query;
                return Task.FromResult(
                    new HttpResponseMessage(CatalogStatus)
                    {
                        Content = JsonContent.Create(
                            new { query = new { pages = new[] { Page() } } }
                        ),
                    }
                );
            }
            MediaCalls++;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(
                        CorruptMedia ? Bytes.Reverse().ToArray() : Bytes
                    ),
                }
            );
        }
    }

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Downloads:Commons:Enabled"] = "true",
                    ["Downloads:Commons:ReviewedFiles:0:File"] = "Fixture.webm",
                    ["Downloads:Commons:ReviewedFiles:0:Evidence"] =
                        "https://commons.wikimedia.org/wiki/File:Fixture.webm",
                }
            )
            .Build();

    [Fact]
    public void MapsOnlyReviewedExplicitCc0AndPublicMediaLinksWithPlainAttribution()
    {
        var source = new Source();
        var item = CommonsCatalog.Map(source.Page(), "https://example.org/rights");
        Assert.NotNull(item);
        Assert.Contains("Synthetic creator", item.Attribution);
        Assert.DoesNotContain("<", item.Attribution);
        source.License = "CC-BY";
        Assert.Null(CommonsCatalog.Map(source.Page(), "https://example.org/rights"));
        source.License = "CC0";
        source.Url = "https://127.0.0.1/file.webm";
        Assert.Null(CommonsCatalog.Map(source.Page(), "https://example.org/rights"));
        source.Url = "https://upload.wikimedia.org/wikipedia/commons/fixture.webm";
        Assert.Null(CommonsCatalog.Map(source.Page(), "file:///private/rights"));
        Assert.Null(
            CommonsCatalog.Map(
                JsonSerializer.SerializeToElement(new { pageid = 42 }),
                "https://example.org/rights"
            )
        );
    }

    [Fact]
    public async Task SearchesAreBoundedCachedAndDoNotSendPrivateSearchTerms()
    {
        var source = new Source();
        var catalog = new CommonsCatalog(Config(), source);
        Assert.Single(await catalog.Search("Fixture", CancellationToken.None));
        Assert.Empty(await catalog.Search("private household preference", CancellationToken.None));
        Assert.Equal(1, source.CatalogCalls);
        Assert.DoesNotContain("private", source.LastQuery);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            catalog.Search(new string('a', 81), CancellationToken.None)
        );
        await Assert.ThrowsAsync<DownloadFault>(() =>
            catalog.Resolve("../42", CancellationToken.None)
        );
        Assert.False(new CommonsCatalog(new ConfigurationBuilder().Build(), source).Enabled);
    }

    [Fact]
    public async Task RateLimitsBackOffWithoutMutatingTheQueueOrRepeatingProviderCalls()
    {
        var source = new Source { CatalogStatus = HttpStatusCode.TooManyRequests };
        var catalog = new CommonsCatalog(Config(), source);
        Assert.Equal(
            "catalog-rate-limited",
            (
                await Assert.ThrowsAsync<DownloadFault>(() =>
                    catalog.Search("", CancellationToken.None)
                )
            ).Code
        );
        Assert.Equal(
            "catalog-rate-limited",
            (
                await Assert.ThrowsAsync<DownloadFault>(() =>
                    catalog.Search("", CancellationToken.None)
                )
            ).Code
        );
        Assert.Equal(1, source.CatalogCalls);
        Assert.Equal(0, source.MediaCalls);
    }

    [Fact]
    public async Task CatalogSelectionNeedsFreshAdminApprovalAndDownloadsThroughTheRealQueue()
    {
        var source = new Source();
        var settings = Config();
        using var app = new Server(configure: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IDownloadTransport>(source));
            services.Replace(
                ServiceDescriptor.Singleton<IDownloadCatalog>(new CommonsCatalog(settings, source))
            );
        });
        using var anonymous = app.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/downloads/catalog")).StatusCode
        );
        using var admin = await app.Admin();
        await admin.PutAsJsonAsync(
            "/api/downloads/settings",
            new { enabled = true, hosts = new[] { "upload.wikimedia.org" } }
        );
        var items = await admin.GetFromJsonAsync<CatalogItem[]>(
            "/api/downloads/catalog?query=Fixture"
        );
        var item = Assert.Single(items!);
        Assert.Equal(0, source.MediaCalls);
        var input = new DownloadInput(
            item.Title,
            item.Url,
            1024 * 1024,
            null,
            CatalogToken: item.ApprovalToken
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PostAsJsonAsync(
                    "/api/downloads",
                    input with
                    {
                        Url = "https://upload.wikimedia.org/wikipedia/commons/changed.webm",
                    }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await admin.PostAsJsonAsync(
                    "/api/downloads",
                    input with
                    {
                        CatalogToken = "forged",
                    }
                )
            ).StatusCode
        );
        source.License = "CC-BY";
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/downloads", input)).StatusCode
        );
        Assert.Equal(0, source.MediaCalls);
        source.License = "CC0";
        var response = await admin.PostAsJsonAsync("/api/downloads", input);
        response.EnsureSuccessStatusCode();
        var row = (await response.Content.ReadFromJsonAsync<DownloadView>())!;
        for (var i = 0; i < 100; i++)
        {
            row = (await admin.GetFromJsonAsync<DownloadView[]>("/api/downloads"))!.Single(x =>
                x.Id == row.Id
            );
            if (row.State is "completed" or "failed")
                break;
            await Task.Delay(100);
        }
        Assert.Equal("completed", row.State);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(source.Bytes)).ToLowerInvariant(),
            row.ContentHash
        );
        Assert.Equal(
            source.Bytes,
            await admin.GetByteArrayAsync("/api/downloads/" + row.Id + "/file")
        );
        Assert.Equal(1, source.MediaCalls);
        source.CorruptMedia = true;
        response = await admin.PostAsJsonAsync("/api/downloads", input);
        response.EnsureSuccessStatusCode();
        row = (await response.Content.ReadFromJsonAsync<DownloadView>())!;
        for (var i = 0; i < 100; i++)
        {
            row = (await admin.GetFromJsonAsync<DownloadView[]>("/api/downloads"))!.Single(x =>
                x.Id == row.Id
            );
            if (row.State is "completed" or "failed")
                break;
            await Task.Delay(100);
        }
        Assert.Equal("failed", row.State);
        Assert.Equal("catalog-content-changed", row.Error);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.GetAsync("/api/downloads/" + row.Id + "/file")).StatusCode
        );
    }
}
