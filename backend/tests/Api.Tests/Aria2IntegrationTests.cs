using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using MediaServer.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Api.Tests;

public sealed class Aria2FactAttribute : FactAttribute
{
    public Aria2FactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_ARIA2_INTEGRATION") != "1")
            Skip = "Run the dedicated actual-aria2 integration job with RUN_ARIA2_INTEGRATION=1.";
    }
}

public sealed class Aria2IntegrationTests
{
    private sealed class Source : HttpMessageHandler, IDownloadTransport
    {
        public readonly byte[] Bytes = Enumerable
            .Range(0, 32768)
            .Select(i => (byte)(i % 251))
            .ToArray();

        public HttpClient Create(string[] hosts) => new(this, false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage message,
            CancellationToken ct
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) }
            );
    }

    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Aria2Fact]
    public async Task RealAria2TransfersPausesResumesCancelsAndPreservesBuiltInIsolation()
    {
        // CI installs the actual daemon on Linux. Windows exercises the optional-client/security tests.
        if (OperatingSystem.IsWindows())
            return;
        var binary = Environment.GetEnvironmentVariable("ARIA2_BINARY") ?? "aria2c";
        var rpcPort = Port();
        var apiPort = Port();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var values = new Dictionary<string, string?>
        {
            ["Downloads__Aria2__Endpoint"] = "http://127.0.0.1:" + rpcPort + "/jsonrpc",
            ["Downloads__Aria2__Secret"] = secret,
            ["Downloads__Aria2__TrustEndpoint"] = "true",
            ["Downloads__Aria2__RelayBaseUrl"] = "http://127.0.0.1:" + apiPort,
        };
        var previous = values.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        var start = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (
            var option in new[]
            {
                "--enable-rpc",
                "--rpc-secret=" + secret,
                "--rpc-listen-port=" + rpcPort,
                "--console-log-level=error",
                "--max-download-limit=8K",
                "--all-proxy=",
                "--http-proxy=",
                "--https-proxy=",
                "--no-netrc=true",
            }
        )
            start.ArgumentList.Add(option);
        using var daemon = Process.Start(start)!;
        _ = daemon.StandardOutput.ReadToEndAsync();
        _ = daemon.StandardError.ReadToEndAsync();
        try
        {
            foreach (var (key, value) in values)
                Environment.SetEnvironmentVariable(key, value);
            var source = new Source();
            using var app = new Server(configure: services =>
                services.Replace(ServiceDescriptor.Singleton<IDownloadTransport>(source))
            );
            app.UseKestrel(apiPort);
            using var client = await app.Admin(new Uri("http://localhost:" + apiPort));
            await client.PutAsJsonAsync(
                "/api/downloads/settings",
                new { enabled = true, hosts = new[] { "source.example" } }
            );
            var input = new DownloadInput(
                "actual aria2",
                "https://source.example/file",
                1024 * 1024,
                Convert.ToHexString(SHA256.HashData(source.Bytes)).ToLowerInvariant(),
                TransferClient: "aria2"
            );
            var created = await client.PostAsJsonAsync("/api/downloads", input);
            created.EnsureSuccessStatusCode();
            var row = (await created.Content.ReadFromJsonAsync<DownloadView>())!;
            async Task<DownloadView> Current() =>
                (await client.GetFromJsonAsync<DownloadView[]>("/api/downloads"))!.Single(x =>
                    x.Id == row.Id
                );
            async Task Until(Func<DownloadView, bool> predicate)
            {
                for (var i = 0; i < 300; i++)
                {
                    row = await Current();
                    if (predicate(row))
                        return;
                    await Task.Delay(100);
                }
                Assert.Fail(
                    "Actual aria2 transfer did not reach the expected state: "
                        + row.State
                        + "/"
                        + row.Error
                );
            }
            async Task Act(string operation) =>
                (
                    await client.PutAsJsonAsync(
                        "/api/downloads/" + row.Id,
                        new DownloadAction(operation, row.Revision)
                    )
                ).EnsureSuccessStatusCode();
            await Until(x => x.State == "running");
            await Act("pause");
            await Until(x => x.State == "paused");
            (
                await client.PutAsJsonAsync(
                    "/api/downloads/" + row.Id,
                    new DownloadAction("priority", row.Revision, 8)
                )
            ).EnsureSuccessStatusCode();
            row = await Current();
            Assert.Equal(8, row.Priority);
            await Act("resume");
            await Until(x => x.State is "completed" or "failed");
            Assert.Equal("completed", row.State);
            Assert.Equal(
                source.Bytes,
                await client.GetByteArrayAsync("/api/downloads/" + row.Id + "/file")
            );
            Assert.Empty(
                Directory.EnumerateFiles(
                    Path.Combine(app.DataPath, "external-downloads"),
                    "payload",
                    SearchOption.AllDirectories
                )
            );
            created = await client.PostAsJsonAsync("/api/downloads", input);
            row = (await created.Content.ReadFromJsonAsync<DownloadView>())!;
            await Until(x => x.State == "running");
            var foreignResponse = await client.PostAsJsonAsync("/api/downloads", input);
            var foreign = (await foreignResponse.Content.ReadFromJsonAsync<DownloadView>())!;
            var rpc = app.Services.GetRequiredService<IExternalDownloadClient>();
            await rpc.Call(
                "addUri",
                [
                    new[] { "https://source.example/foreign" },
                    new Dictionary<string, string>
                    {
                        ["gid"] = foreign.Id[..16],
                        ["dir"] = Path.Combine(app.Root, "foreign"),
                        ["out"] = "payload",
                        ["pause"] = "true",
                    },
                ],
                CancellationToken.None
            );
            await Act("cancel");
            await Until(x => x.State == "cancelled");
            row = foreign;
            await Until(x => x.State == "failed");
            Assert.Equal("external-task-not-owned", row.Error);
            var remote = await rpc.Call(
                "tellStatus",
                [foreign.Id[..16], new[] { "status" }],
                CancellationToken.None
            );
            Assert.Equal("paused", remote.GetProperty("status").GetString());
            daemon.Kill(true);
            await daemon.WaitForExitAsync();
            created = await client.PostAsJsonAsync("/api/downloads", input);
            row = (await created.Content.ReadFromJsonAsync<DownloadView>())!;
            await Until(x => x.State == "failed");
            created = await client.PostAsJsonAsync(
                "/api/downloads",
                input with
                {
                    TransferClient = "builtin",
                }
            );
            row = (await created.Content.ReadFromJsonAsync<DownloadView>())!;
            await Until(x => x.State is "completed" or "failed");
            Assert.Equal("completed", row.State);
        }
        finally
        {
            if (!daemon.HasExited)
            {
                daemon.Kill(true);
                await daemon.WaitForExitAsync();
            }
            foreach (var (key, value) in previous)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
