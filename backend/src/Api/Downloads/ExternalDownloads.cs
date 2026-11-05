using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaServer.Core;
using MediaServer.Infrastructure;
using MediaServer.Media;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Downloads;

public record DownloadClientCapabilities(
    string Id,
    bool Configured,
    bool Add,
    bool Status,
    bool Pause,
    bool Resume,
    bool Cancel,
    bool QueuePriority,
    string ResumeSemantics
);

public interface IExternalDownloadClient
{
    DownloadClientCapabilities Capabilities { get; }
    Task<JsonElement> Call(string method, object[] arguments, CancellationToken ct);
}

// One exact operator-trusted RPC endpoint. This is not an exception to source URL policy.
public sealed class Aria2Client : IExternalDownloadClient, IDisposable
{
    private readonly Uri? endpoint;
    private readonly string secret;
    private readonly HttpClient http;
    public DownloadClientCapabilities Capabilities =>
        new(
            "aria2",
            endpoint != null,
            true,
            true,
            true,
            true,
            true,
            true,
            "Resume re-fetches the approved source and verifies SHA-256; queue priority is local."
        );

    public Aria2Client(IConfiguration config)
    {
        secret = config["Downloads:Aria2:Secret"] ?? "";
        var value = config["Downloads:Aria2:Endpoint"];
        if (!string.IsNullOrEmpty(value))
        {
            if (
                !config.GetValue<bool>("Downloads:Aria2:TrustEndpoint")
                || secret.Length is < 16 or > 512
                || !Uri.TryCreate(value, UriKind.Absolute, out endpoint)
                || !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address)
                || endpoint.UserInfo != ""
                || endpoint.Query != ""
                || endpoint.Fragment != ""
                || endpoint.AbsolutePath != "/jsonrpc"
                || !(
                    endpoint.Scheme == "https"
                    || endpoint.Scheme == "http" && IPAddress.IsLoopback(address)
                )
            )
                throw new ArgumentException(
                    "Configure an explicitly trusted exact-IP aria2 endpoint with a protected secret; LAN endpoints require HTTPS."
                );
        }
        http = new HttpClient(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                ActivityHeadersPropagator = null,
            }
        )
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = 65536,
        };
    }

    public async Task<JsonElement> Call(string method, object[] arguments, CancellationToken ct)
    {
        if (endpoint is null)
            throw new DownloadFault("external-client-not-configured");
        if (
            method
            is not (
                "addUri"
                or "tellStatus"
                or "getOption"
                or "forcePause"
                or "unpause"
                or "forceRemove"
                or "removeDownloadResult"
                or "changePosition"
                or "getVersion"
            )
        )
            throw new DownloadFault("external-operation-unsupported");
        try
        {
            using var body = new StringContent(
                JsonSerializer.Serialize(
                    new
                    {
                        jsonrpc = "2.0",
                        id = "media",
                        method = "aria2." + method,
                        @params = new object[] { "token:" + secret }.Concat(arguments),
                    }
                ),
                Encoding.UTF8,
                "application/json"
            );
            using var response = await http.PostAsync(endpoint, body, ct);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.BadRequest))
                throw new DownloadFault("external-client-unavailable");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (memory.Length + count > 65536)
                    throw new DownloadFault("external-response-too-large");
                memory.Write(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(memory.ToArray());
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                var message = error.GetProperty("message").GetString();
                if (
                    method == "tellStatus"
                    && (
                        message == "GID " + arguments[0] + " is not found"
                        || message == "GID#" + arguments[0] + " is not found"
                    )
                )
                    throw new DownloadFault("external-task-missing");
                throw new DownloadFault("external-operation-rejected");
            }
            return document.RootElement.GetProperty("result").Clone();
        }
        catch (Exception e)
            when (e is HttpRequestException or JsonException or InvalidOperationException)
        {
            throw new DownloadFault("external-client-unavailable");
        }
    }

    public void Dispose() => http.Dispose();
}

public sealed class ExternalDownloads(
    IConfiguration config,
    DownloadStorage storage,
    IDownloadTransport transport,
    IServiceScopeFactory scopes,
    IDataProtectionProvider protection,
    IExternalDownloadClient rpc,
    ILogger<ExternalDownloads> logger
)
{
    private sealed record Lease(
        DownloadJob Job,
        string Token,
        string[] Hosts,
        CancellationToken Stop
    )
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, Lease> leases = new();
    private string Root => Path.Combine(Path.GetDirectoryName(storage.Root)!, "external-downloads");
    public DownloadClientCapabilities Capabilities =>
        rpc.Capabilities with
        {
            Configured =
                rpc.Capabilities.Configured
                && !string.IsNullOrEmpty(config["Downloads:Aria2:RelayBaseUrl"]),
        };

    public HttpClient Create(DownloadJob job, string[] hosts)
    {
        if (!Capabilities.Configured || job.ExpectedHash is null)
            throw new DownloadFault("external-client-needs-configuration-and-sha256");
        return new HttpClient(new Handler(this, job, hosts)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class Handler(ExternalDownloads owner, DownloadJob job, string[] hosts)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        ) => owner.Fetch(job, hosts, request.Headers.Range?.Ranges.Single().From ?? 0, ct);
    }

    private async Task Own(string gid, string directory, CancellationToken ct)
    {
        var options = await rpc.Call("getOption", [gid], ct);
        if (
            options.GetProperty("dir").GetString() != directory
            || options.GetProperty("out").GetString() != "payload"
        )
            throw new DownloadFault("external-task-not-owned");
    }

    private async Task Remove(string gid, string directory, CancellationToken ct)
    {
        await Own(gid, directory, ct);
        var status = await rpc.Call("tellStatus", [gid, new[] { "status" }], ct);
        if (status.GetProperty("status").GetString() is not ("complete" or "error" or "removed"))
        {
            await rpc.Call("forcePause", [gid], ct);
            await rpc.Call("forceRemove", [gid], ct);
        }
        await rpc.Call("removeDownloadResult", [gid], ct);
    }

    private void Clean(string directory)
    {
        PathPolicy.RejectLinks(Root);
        PathPolicy.RejectLinks(directory);
        var policy = new PathPolicy([Root]);
        foreach (var name in new[] { "payload", "payload.aria2" })
            if (File.Exists(Path.Combine(directory, name)))
                File.Delete(policy.Resolve(directory, name));
    }

    public async Task Stop(DownloadJob job, CancellationToken ct)
    {
        if (job.TransferClient != "aria2")
            return;
        if (!Guid.TryParseExact(job.Id, "N", out _))
            throw new DownloadFault("external-task-not-owned");
        var remote =
            (config["Downloads:Aria2:RemoteRoot"] ?? Root).TrimEnd('/', '\\') + "/" + job.Id;
        try
        {
            await rpc.Call("tellStatus", [job.Id[..16], new[] { "status" }], ct);
        }
        catch (DownloadFault e) when (e.Code == "external-task-missing")
        {
            if (Directory.Exists(Path.Combine(Root, job.Id)))
                Clean(Path.Combine(Root, job.Id));
            return;
        }
        await Remove(job.Id[..16], remote, ct);
        if (Directory.Exists(Path.Combine(Root, job.Id)))
            Clean(Path.Combine(Root, job.Id));
    }

    private async Task<HttpResponseMessage> Fetch(
        DownloadJob job,
        string[] hosts,
        long offset,
        CancellationToken ct
    )
    {
        if (!Guid.TryParseExact(job.Id, "N", out _))
            throw new DownloadFault("external-task-not-owned");
        var relay = new Uri(config["Downloads:Aria2:RelayBaseUrl"]!, UriKind.Absolute);
        if (
            relay.UserInfo != ""
            || relay.Query != ""
            || relay.Fragment != ""
            || !(relay.Scheme == "https" || relay.Scheme == "http" && relay.IsLoopback)
        )
            throw new DownloadFault("external-relay-requires-https-or-loopback");
        var gid = job.Id[..16];
        var directory = Path.Combine(Root, job.Id);
        var remote =
            (config["Downloads:Aria2:RemoteRoot"] ?? Root).TrimEnd('/', '\\') + "/" + job.Id;
        Directory.CreateDirectory(Root);
        PathPolicy.RejectLinks(Root);
        Directory.CreateDirectory(directory);
        PathPolicy.RejectLinks(directory);
        // A deterministic ID makes timeout/restart reconciliation possible. Never touch a foreign task.
        JsonElement? existing = null;
        try
        {
            existing = await rpc.Call("tellStatus", [gid, new[] { "status" }], ct);
        }
        catch (DownloadFault e) when (e.Code == "external-task-missing") { }
        if (existing is not null)
            await Remove(gid, remote, ct);
        Clean(directory);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var leaseStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lease = new Lease(job, token, hosts, leaseStop.Token);
        var handedOver = false;
        var removed = false;
        if (!leases.TryAdd(job.Id, lease))
            throw new DownloadFault("external-task-already-active");
        try
        {
            var uri = new Uri(relay, "/api/download-relay/" + job.Id).AbsoluteUri;
            await rpc.Call(
                "addUri",
                [
                    new[] { uri },
                    new Dictionary<string, string>
                    {
                        ["gid"] = gid,
                        ["dir"] = remote,
                        ["out"] = "payload",
                        ["pause"] = "true",
                        ["header"] = "Authorization: Bearer " + token,
                        ["split"] = "1",
                        ["max-connection-per-server"] = "1",
                        ["continue"] = "false",
                        ["allow-overwrite"] = "false",
                        ["auto-file-renaming"] = "false",
                        ["file-allocation"] = "none",
                        ["max-tries"] = "1",
                        ["follow-torrent"] = "false",
                        ["follow-metalink"] = "false",
                        ["check-certificate"] = "true",
                    },
                ],
                ct
            );
            await Own(gid, remote, ct);
            await rpc.Call("unpause", [gid], ct);
            while (true)
            {
                await Own(gid, remote, ct);
                var status = await rpc.Call(
                    "tellStatus",
                    [gid, new[] { "status", "totalLength", "completedLength" }],
                    ct
                );
                var total = long.Parse(
                    status.GetProperty("totalLength").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture
                );
                var received = long.Parse(
                    status.GetProperty("completedLength").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture
                );
                await Check(job, ct);
                if (total < 0 || received < 0 || total > job.MaxBytes || received > job.MaxBytes)
                    throw new DownloadFault("source-exceeds-budget");
                var state = status.GetProperty("status").GetString();
                if (state == "complete")
                    break;
                if (state is "error" or "removed")
                    throw new DownloadFault("external-transfer-failed");
                await Task.Delay(250, ct);
            }
            await Remove(gid, remote, ct);
            removed = true;
            var file = new PathPolicy([Root]).OpenRead(directory, "payload");
            if (file.Length > job.MaxBytes || offset > file.Length)
            {
                file.Dispose();
                throw new DownloadFault("source-exceeds-budget");
            }
            file.Position = offset;
            var result = new HttpResponseMessage(
                offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK
            )
            {
                Content = new Payload(file, () => Clean(directory)),
            };
            result.Content.Headers.ContentLength = file.Length - offset;
            if (offset > 0)
                result.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    offset,
                    file.Length - 1,
                    file.Length
                );
            result.Headers.ETag = new EntityTagHeaderValue("\"" + job.ExpectedHash + "\"");
            handedOver = true;
            return result;
        }
        finally
        {
            leaseStop.Cancel();
            leases.TryRemove(job.Id, out _);
            // Revoking the relay stops source traffic even if the daemon is unavailable.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                if (!removed)
                    await Remove(gid, remote, cleanup.Token);
                if (!handedOver)
                    Clean(directory);
            }
            catch (Exception)
            { /* Ownership is checked before every RPC mutation. Reconcile on retry. */
            }
        }
    }

    private sealed class Payload(FileStream file, Action cleanup) : StreamContent(file)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                cleanup();
        }
    }

    private async Task Check(DownloadJob job, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
        var current = await db.Downloads.AsNoTracking().SingleAsync(x => x.Id == job.Id, ct);
        if (
            current.State != "running"
            || current.Desired != "run"
            || !await DownloadWorker.Enabled(db, ct)
            || !await DownloadAccess.Allowed(db, current, ct)
        )
            throw new DownloadFault("request-access-revoked");
        storage.Admit(await DownloadWorker.Budgets(db, ct));
    }

    public async Task Relay(string id, HttpContext context)
    {
        if (
            !leases.TryGetValue(id, out var lease)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()),
                Encoding.UTF8.GetBytes("Bearer " + lease.Token)
            )
        )
        {
            context.Response.StatusCode = 404;
            return;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            lease.Stop
        );
        var ct = cancel.Token;
        if (!await lease.Gate.WaitAsync(0, ct))
        {
            context.Response.StatusCode = 409;
            return;
        }
        try
        {
            if (context.Request.Headers.Range.Count > 0)
                throw new DownloadFault("external-range-unsupported");
            await Check(lease.Job, ct);
            using var client = transport.Create(lease.Hosts);
            var source = protection
                .CreateProtector(DownloadWorker.SecretPurpose)
                .Unprotect(lease.Job.ProtectedUrl);
            var result = await DownloadWorker.Response(
                client,
                DownloadNetwork.Url(source, lease.Hosts),
                lease.Hosts,
                0,
                null,
                ct
            );
            using var response = result.Response;
            if (
                response.StatusCode != HttpStatusCode.OK
                || response.Content.Headers.ContentEncoding.Count > 0
                || response.Content.Headers.ContentLength > lease.Job.MaxBytes
            )
                throw new DownloadFault("source-exceeds-budget");
            context.Response.ContentLength = response.Content.Headers.ContentLength;
            context.Response.ContentType = "application/octet-stream";
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[65536];
            long received = 0;
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(TimeSpan.FromSeconds(30));
                var count = await input.ReadAsync(buffer, stall.Token);
                if (count == 0)
                    break;
                received += count;
                if (received > lease.Job.MaxBytes)
                    throw new DownloadFault("source-exceeds-budget");
                await Check(lease.Job, ct);
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "External relay {JobId} stopped ({ErrorCode})",
                id,
                ex is DownloadFault fault ? fault.Code : ex.GetType().Name
            );
            context.Abort();
        }
        finally
        {
            lease.Gate.Release();
        }
    }
}

public static class ExternalDownloadEndpoints
{
    public static void MapExternalDownloads(this WebApplication app)
    {
        app.MapGet(
                "/api/download-relay/{id}",
                (string id, HttpContext ctx, ExternalDownloads external) => external.Relay(id, ctx)
            )
            .AllowAnonymous();
        foreach (var prefix in new[] { "/api", "/api/v1" })
            app.MapGet(
                    prefix + "/downloads/clients",
                    (ExternalDownloads external) => new[] { external.Capabilities }
                )
                .RequireAuthorization("admin");
    }
}
