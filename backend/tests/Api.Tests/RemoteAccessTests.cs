using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Tests;

public sealed class RemoteAccessTests
{
    private static IConfiguration Configuration(
        string? external = "https://media.example",
        string? secure = "true",
        string? host = "media.example",
        string? proxy = "10.1.2.3"
    ) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["MediaServer:ExternalUrl"] = external,
                    ["MediaServer:SecureCookies"] = secure,
                    ["AllowedHosts"] = host,
                    ["MediaServer:TrustedProxies:0"] = proxy,
                }.Where(x => x.Value is not null)
            )
            .Build();

    [Theory]
    [InlineData("http://media.example", "true", "media.example", "10.1.2.3")]
    [InlineData("https://media.example/path", "true", "media.example", "10.1.2.3")]
    [InlineData("https://user:password@media.example", "true", "media.example", "10.1.2.3")]
    [InlineData("https://media.example?token=secret", "true", "media.example", "10.1.2.3")]
    [InlineData("https://media.example", "false", "media.example", "10.1.2.3")]
    [InlineData("https://media.example", "true", "*", "10.1.2.3")]
    [InlineData("https://media.example", "true", "localhost", "10.1.2.3")]
    [InlineData("https://media.example", "true", "media.example", "*")]
    [InlineData(null, "true", "media.example", "10.1.2.3")]
    public void UnsafeOrIncompleteRemoteConfigurationIsRejected(
        string? external,
        string secure,
        string host,
        string proxy
    ) =>
        Assert.Throws<ArgumentException>(() =>
            new RemoteAccess(Configuration(external, secure, host, proxy))
        );

    [Theory]
    [InlineData("10.1.2.3", "https", "203.0.113.9")]
    [InlineData("198.51.100.2", "http", "198.51.100.2")]
    public async Task OnlyExplicitProxyPeersCanForwardSchemeAndClient(
        string peer,
        string scheme,
        string client
    )
    {
        var remote = new RemoteAccess(Configuration());
        var options = new ForwardedHeadersOptions();
        remote.Configure(options);
        Assert.Empty(options.KnownIPNetworks);
        Assert.Single(options.KnownProxies);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("media.example");
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
        context.Request.Headers["X-Forwarded-Host"] = "attacker.example";
        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(options)
        );
        await middleware.Invoke(context);
        Assert.Equal(scheme, context.Request.Scheme);
        Assert.Equal(client, context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("media.example", context.Request.Host.Host);
    }

    [Fact]
    public async Task ReadinessIsAdminOnlyAndLocalDefaultsStayUnconfigured()
    {
        var remote = new RemoteAccess(Configuration(null, "false", "localhost", null));
        Assert.Empty(remote.Proxies);
        Assert.Null(remote.ExternalUrl);
        Assert.NotEmpty(remote.State(new DefaultHttpContext()).Warnings);
        using var server = new Server();
        using var anonymous = server.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/remote-readiness")).StatusCode
        );
        using var admin = await server.Admin();
        var state = await admin.GetFromJsonAsync<RemoteReadiness>("/api/v1/remote-readiness");
        Assert.False(state!.Configured);
        Assert.False(state.SecureCookies);
        Assert.Equal(0, state.TrustedProxyCount);
    }
}
