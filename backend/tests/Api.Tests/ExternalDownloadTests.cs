using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Downloads;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Api.Tests;

public sealed class ExternalDownloadTests
{
    [Theory]
    [InlineData("http://192.168.1.2:6800/jsonrpc", true)]
    [InlineData("https://example.com/jsonrpc", true)]
    [InlineData("http://127.0.0.1:6800/jsonrpc?secret=bad", true)]
    [InlineData("http://127.0.0.1:6800/jsonrpc", false)]
    [InlineData("https://127.0.0.1/other", true)]
    public void EndpointTrustIsSeparateAndNeverDisablesTls(string endpoint, bool trust)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Downloads:Aria2:Endpoint"] = endpoint,
                    ["Downloads:Aria2:TrustEndpoint"] = trust.ToString(),
                    ["Downloads:Aria2:Secret"] = "synthetic-test-secret",
                }
            )
            .Build();
        Assert.Throws<ArgumentException>(() => new Aria2Client(config));
    }

    [Fact]
    public void ExternalRequiresKnownClientAndExpectedHash()
    {
        var input = new DownloadInput(
            "fixture",
            "https://example.com/file",
            1024,
            null,
            TransferClient: "aria2"
        );
        Assert.Throws<ArgumentException>(() => DownloadEndpoints.Validate(input));
        Assert.Throws<ArgumentException>(() =>
            DownloadEndpoints.Validate(
                input with
                {
                    TransferClient = "unimplemented",
                    Sha256 = new string('a', 64),
                }
            )
        );
        DownloadEndpoints.Validate(input with { Sha256 = new string('a', 64) });
        DownloadEndpoints.Validate(input with { TransferClient = "builtin" });
        // External endpoint trust never changes manual source validation.
        Assert.Throws<ArgumentException>(() =>
            DownloadNetwork.Url("http://127.0.0.1/file", ["127.0.0.1"])
        );
    }

    [Fact]
    public async Task OptionalClientIsAbsentByDefaultAndCapabilitiesAreAdminOnly()
    {
        using var app = new Server();
        using var anonymous = app.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/downloads/clients")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anonymous.GetAsync("/api/download-relay/" + new string('a', 32))).StatusCode
        );
        using var admin = await app.Admin();
        var text = await admin.GetStringAsync("/api/downloads/clients");
        using var json = JsonDocument.Parse(text);
        Assert.False(json.RootElement[0].GetProperty("configured").GetBoolean());
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("endpoint", text, StringComparison.OrdinalIgnoreCase);
        var response = await admin.PostAsJsonAsync(
            "/api/downloads",
            new DownloadInput(
                "fixture",
                "https://example.com/file",
                1024,
                new string('a', 64),
                TransferClient: "aria2"
            )
        );
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
