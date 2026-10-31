using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

public record RemoteReadiness(
    bool Configured,
    bool RequestIsHttps,
    bool SecureCookies,
    int TrustedProxyCount,
    string[] Warnings
);

public sealed class RemoteAccess
{
    public Uri? ExternalUrl { get; }
    public IPAddress[] Proxies { get; }
    public bool SecureCookies { get; }

    public RemoteAccess(IConfiguration config)
    {
        SecureCookies = config.GetValue<bool>("MediaServer:SecureCookies");
        var addresses = config.GetSection("MediaServer:TrustedProxies").Get<string[]>() ?? [];
        if (addresses.Length > 8 || addresses.Any(x => !IPAddress.TryParse(x, out _)))
            throw new ArgumentException(
                "TrustedProxies must contain at most eight explicit proxy IP addresses, never wildcards or hostnames."
            );
        Proxies = addresses.Select(IPAddress.Parse).Distinct().ToArray();
        var external = config["MediaServer:ExternalUrl"];
        if (!string.IsNullOrEmpty(external))
        {
            if (
                !Uri.TryCreate(external, UriKind.Absolute, out var url)
                || url.Scheme != "https"
                || !string.IsNullOrEmpty(url.UserInfo)
                || !string.IsNullOrEmpty(url.Query)
                || !string.IsNullOrEmpty(url.Fragment)
                || url.AbsolutePath != "/"
            )
                throw new ArgumentException(
                    "ExternalUrl must be an HTTPS origin, without credentials, a path, query or fragment."
                );
            if (!SecureCookies)
                throw new ArgumentException("ExternalUrl requires MediaServer:SecureCookies=true.");
            var hosts = (config["AllowedHosts"] ?? "").Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            if (hosts.Contains("*") || !hosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "List the explicit ExternalUrl hostname in AllowedHosts; wildcard hosts are not accepted for remote access."
                );
            ExternalUrl = url;
        }
        if (Proxies.Length > 0 && ExternalUrl is null)
            throw new ArgumentException(
                "TrustedProxies requires an explicit HTTPS ExternalUrl and secure cookies."
            );
    }

    public void Configure(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.RequireHeaderSymmetry = true;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in Proxies)
            options.KnownProxies.Add(proxy);
    }

    public RemoteReadiness State(HttpContext context)
    {
        var warnings = new List<string>();
        if (ExternalUrl is null)
            warnings.Add(
                "Remote access is not configured. Prefer a private VPN; keep the default loopback listener."
            );
        if (!SecureCookies)
            warnings.Add(
                "Secure-only cookies are disabled. Do not use unencrypted remote connections."
            );
        if (ExternalUrl is not null && !context.Request.IsHttps)
            warnings.Add(
                "This request is not HTTPS after trusted proxy processing. Check TLS termination and explicit proxy addresses."
            );
        return new(
            ExternalUrl is not null,
            context.Request.IsHttps,
            SecureCookies,
            Proxies.Length,
            warnings.ToArray()
        );
    }

    public void Map(WebApplication app)
    {
        foreach (var prefix in new[] { "/api", "/api/v1" })
        {
            var group = app.MapGroup(prefix).RequireAuthorization("admin");
            if (prefix.EndsWith("v1", StringComparison.Ordinal))
                group.WithGroupName("client-v1");
            group.MapGet("/remote-readiness", State);
        }
    }
}
