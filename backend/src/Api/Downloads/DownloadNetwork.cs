using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace MediaServer.Downloads;

public interface IDownloadTransport
{
    HttpClient Create(string[] hosts);
}

public sealed class DownloadNetwork : IDownloadTransport
{
    public static string[] Hosts(IEnumerable<string> input)
    {
        var hosts = input
            .Select(value =>
            {
                if (
                    string.IsNullOrWhiteSpace(value)
                    || value.Length > 253
                    || value.IndexOfAny(['/', ':', '@', '*', '#', '?', '\\']) >= 0
                )
                    throw new ArgumentException(
                        "Use exact public hostnames, without ports, URLs or wildcards."
                    );
                var host = new IdnMapping().GetAscii(value.Trim().TrimEnd('.')).ToLowerInvariant();
                if (Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
                    throw new ArgumentException("Invalid source hostname.");
                return host;
            })
            .Distinct()
            .ToArray();
        if (hosts.Length > 20)
            throw new ArgumentException("Approve at most 20 hosts.");
        return hosts;
    }

    public static Uri Url(string input, string[] hosts)
    {
        if (
            string.IsNullOrWhiteSpace(input)
            || input.Length > 4096
            || !Uri.TryCreate(input, UriKind.Absolute, out var uri)
            || uri.Scheme != "https"
            || uri.Port != 443
            || uri.UserInfo != ""
            || uri.Fragment != ""
            || !hosts.Contains(uri.IdnHost.TrimEnd('.').ToLowerInvariant(), StringComparer.Ordinal)
        )
            throw new ArgumentException(
                "Use an HTTPS URL on an approved host, without credentials, fragments or a custom port."
            );
        return uri;
    }

    public static bool Public(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(
                b[0] is 0 or 10 or 127
                || b[0] >= 224
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && (b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2)))
                || (b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100)))
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)
            );
        return (b[0] & 0xe0) == 0x20
            && !(b[0] == 0x20 && b[1] == 0x02)
            && !(b[0] == 0x20 && b[1] == 1 && (b[2] == 0 || (b[2] == 0x0d && b[3] == 0xb8)));
    }

    public HttpClient Create(string[] hosts) =>
        new(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.None,
                MaxResponseHeadersLength = 16,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                ActivityHeadersPropagator = null,
                ConnectCallback = async (context, ct) =>
                {
                    if (
                        context.DnsEndPoint.Port != 443
                        || !hosts.Contains(
                            context.DnsEndPoint.Host.TrimEnd('.').ToLowerInvariant(),
                            StringComparer.Ordinal
                        )
                    )
                        throw new HttpRequestException("Source not approved.");
                    var addresses = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct))
                        .Distinct()
                        .ToArray();
                    if (addresses.Length is 0 or > 16 || addresses.Any(a => !Public(a)))
                        throw new HttpRequestException("Source network not permitted.");
                    foreach (var address in addresses)
                    {
                        var socket = new Socket(
                            address.AddressFamily,
                            SocketType.Stream,
                            ProtocolType.Tcp
                        );
                        try
                        {
                            await socket.ConnectAsync(new IPEndPoint(address, 443), ct);
                            return new NetworkStream(socket, true);
                        }
                        catch (SocketException)
                        {
                            socket.Dispose();
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    }
                    throw new HttpRequestException("Source connection failed.");
                },
            }
        )
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}
