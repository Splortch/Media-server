using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaServer.Core;
using MediaServer.Infrastructure;
using Microsoft.AspNetCore.DataProtection;

namespace MediaServer.Downloads;

public record CatalogItem(
    string Provider,
    string Id,
    string Title,
    string Url,
    long Bytes,
    string Mime,
    string Sha1,
    string License,
    string LicenseUrl,
    string SourcePage,
    string Attribution,
    string RightsEvidence,
    string? ApprovalToken = null
);

public interface IDownloadCatalog
{
    string Id { get; }
    bool Enabled { get; }
    Task<CatalogItem[]> Search(string query, CancellationToken ct);
    Task<CatalogItem> Resolve(string id, CancellationToken ct);
}

// An operator-reviewed CC0 subset of Wikimedia Commons, not an assertion about all search results.
public sealed class CommonsCatalog(IConfiguration config, IDownloadTransport transport)
    : IDownloadCatalog
{
    public string Id => "wikimedia-commons-cc0";

    private sealed record Reviewed(string File, string Evidence);

    private Reviewed[] ReviewedFiles =>
        config
            .GetSection("Downloads:Commons:ReviewedFiles")
            .GetChildren()
            .Take(21)
            .Select(x => new Reviewed(x["File"] ?? "", x["Evidence"] ?? ""))
            .ToArray();
    public bool Enabled =>
        config.GetValue<bool>("Downloads:Commons:Enabled") && ReviewedFiles.Length is > 0 and <= 20;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CatalogItem[] cache = [];
    private DateTime expires,
        lastRequest,
        blockedUntil;
    private const string Cc0 = "https://creativecommons.org/publicdomain/zero/1.0/";

    public static string Plain(string? value, int limit)
    {
        var plain = WebUtility.HtmlDecode(
            Regex.Replace(
                value ?? "",
                "<[^>]*>",
                "",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)
            )
        );
        var text = new string(plain.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text[..Math.Min(text.Length, limit)];
    }

    public static CatalogItem? Map(JsonElement page, string evidence)
    {
        try
        {
            var info = page.GetProperty("imageinfo")[0];
            var metadata = info.GetProperty("extmetadata");
            string Field(string key) =>
                metadata.TryGetProperty(key, out var value)
                    ? value.GetProperty("value").GetString() ?? ""
                    : "";
            var license = Field("LicenseUrl")
                .Replace("http://", "https://", StringComparison.Ordinal);
            if (
                !(license == Cc0 || license == Cc0 + "deed.en")
                || Field("LicenseShortName") != "CC0"
            )
                return null;
            var url = DownloadNetwork.Url(
                info.GetProperty("url").GetString()!,
                ["upload.wikimedia.org"]
            );
            if (!url.AbsolutePath.StartsWith("/wikipedia/commons/", StringComparison.Ordinal))
                return null;
            var mime = info.GetProperty("mime").GetString()!;
            var bytes = info.GetProperty("size").GetInt64();
            var sha1 = info.GetProperty("sha1").GetString()!;
            var title = page.GetProperty("title").GetString()!;
            var artist = Plain(Field("Artist"), 300);
            if (
                mime is not ("video/webm" or "video/mp4")
                || bytes is < 1 or > DownloadStorage.TotalLimit
                || !Regex.IsMatch(sha1, "^[a-f0-9]{40}$", RegexOptions.CultureInvariant)
                || !title.StartsWith("File:", StringComparison.Ordinal)
                || title.Length > 200
                || artist.Length == 0
            )
                return null;
            if (
                !Uri.TryCreate(evidence, UriKind.Absolute, out var rights)
                || rights.Scheme != "https"
                || rights.UserInfo != ""
                || rights.Fragment != ""
            )
                return null;
            var id = page.GetProperty("pageid")
                .GetInt32()
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new(
                "wikimedia-commons-cc0",
                id,
                Plain(title[5..], 80),
                url.AbsoluteUri,
                bytes,
                mime,
                sha1,
                "CC0 1.0 Universal public-domain dedication",
                Cc0,
                "https://commons.wikimedia.org/wiki/" + Uri.EscapeDataString(title),
                artist + " — Wikimedia Commons — CC0 1.0",
                rights.AbsoluteUri
            );
        }
        catch (Exception e)
            when (e
                    is KeyNotFoundException
                        or InvalidOperationException
                        or ArgumentException
                        or IndexOutOfRangeException
            )
        {
            return null;
        }
    }

    private async Task<CatalogItem[]> Read(string? id, CancellationToken ct)
    {
        if (!Enabled)
            throw new DownloadFault("catalog-disabled");
        var reviewed = ReviewedFiles;
        if (
            reviewed.Any(x =>
                x.File.Length is < 1 or > 195 || x.File.Contains('|') || x.File.Any(char.IsControl)
            )
        )
            throw new DownloadFault("catalog-review-configuration-invalid");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        ct = timeout.Token;
        if (!await gate.WaitAsync(0, ct))
            throw new DownloadFault("catalog-busy-try-later");
        try
        {
            if (id is null && expires > DateTime.UtcNow)
                return cache;
            var delay = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - lastRequest);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
            if (blockedUntil > DateTime.UtcNow)
                throw new DownloadFault("catalog-rate-limited");
            lastRequest = DateTime.UtcNow;
            var select = id is null
                ? "titles="
                    + Uri.EscapeDataString(string.Join('|', reviewed.Select(x => "File:" + x.File)))
                : "pageids=" + id;
            var uri = new Uri(
                "https://commons.wikimedia.org/w/api.php?action=query&maxlag=5&format=json&formatversion=2&prop=imageinfo&iiprop=url%7Csize%7Cmime%7Csha1%7Cextmetadata&"
                    + select
            );
            using var client = transport.Create(["commons.wikimedia.org"]);
            var result = await DownloadWorker.Response(
                client,
                uri,
                ["commons.wikimedia.org"],
                0,
                null,
                ct,
                "MediaServer-Catalog/0.1 (https://github.com/Splortch/Media-server)"
            );
            using var response = result.Response;
            if ((int)response.StatusCode == 429)
            {
                blockedUntil = DateTime.UtcNow.AddSeconds(
                    Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60, 60, 300)
                );
                throw new DownloadFault("catalog-rate-limited");
            }
            if (!response.IsSuccessStatusCode)
                throw new DownloadFault("catalog-unavailable");
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await body.ReadAsync(buffer, ct)) > 0)
            {
                if (memory.Length + count > 512 * 1024)
                    throw new DownloadFault("catalog-response-too-large");
                memory.Write(buffer, 0, count);
            }
            using var json = JsonDocument.Parse(memory.ToArray());
            if (
                !json.RootElement.TryGetProperty("query", out var query)
                || !query.TryGetProperty("pages", out var pages)
            )
                throw new DownloadFault("catalog-unavailable");
            var items = new List<CatalogItem>();
            foreach (var page in pages.EnumerateArray().Take(20))
            {
                if (!page.TryGetProperty("title", out var title))
                    continue;
                var review = reviewed.SingleOrDefault(x => "File:" + x.File == title.GetString());
                if (review is null)
                    continue;
                var item = Map(page, review.Evidence);
                if (item is not null)
                    items.Add(item);
            }
            if (id is null)
            {
                cache = items.ToArray();
                expires = DateTime.UtcNow.AddMinutes(10);
            }
            return items.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            throw new DownloadFault("catalog-unavailable");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CatalogItem[]> Search(string query, CancellationToken ct)
    {
        if (query.Length > 80 || query.Any(char.IsControl))
            throw new ArgumentException("Use a search of at most 80 characters.");
        return (await Read(null, ct))
            .Where(x =>
                query.Length == 0 || x.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            )
            .Take(10)
            .ToArray();
    }

    public async Task<CatalogItem> Resolve(string id, CancellationToken ct)
    {
        if (
            !int.TryParse(id, out var page)
            || page <= 0
            || page.ToString(System.Globalization.CultureInfo.InvariantCulture) != id
        )
            throw new DownloadFault("catalog-entry-unavailable");
        return (await Read(id, ct)).SingleOrDefault(x => x.Id == id)
            ?? throw new DownloadFault("catalog-rights-or-file-unavailable");
    }
}

public sealed class CatalogApproval(IDownloadCatalog provider, IDataProtectionProvider protection)
{
    private readonly IDataProtector tokens = protection.CreateProtector(
        "MediaServer.CatalogApproval.v1"
    );

    private sealed record Ticket(string Id, string Url, string Sha1, long Bytes, DateTime Expires);

    public bool Enabled => provider.Enabled;

    public async Task<CatalogItem[]> Search(string query, CancellationToken ct) =>
        (await provider.Search(query, ct))
            .Select(item =>
                item with
                {
                    ApprovalToken = tokens.Protect(
                        JsonSerializer.Serialize(
                            new Ticket(
                                item.Id,
                                item.Url,
                                item.Sha1,
                                item.Bytes,
                                DateTime.UtcNow.AddMinutes(15)
                            )
                        )
                    ),
                }
            )
            .ToArray();

    public async Task<(DownloadInput Input, string? Sha1, string? Attribution)> Prepare(
        DownloadInput input,
        CancellationToken ct
    )
    {
        if (input.CatalogToken is null)
            return (input, null, null);
        if (input.CatalogToken.Length > 8192)
            throw new ArgumentException("Catalog approval token is too large.");
        try
        {
            var ticket = JsonSerializer.Deserialize<Ticket>(tokens.Unprotect(input.CatalogToken));
            if (ticket is null || ticket.Expires < DateTime.UtcNow)
                throw new DownloadFault("catalog-approval-expired");
            var item = await provider.Resolve(ticket.Id, ct);
            if (
                item.Url != ticket.Url
                || item.Sha1 != ticket.Sha1
                || item.Bytes != ticket.Bytes
                || input.Url != item.Url
                || input.BudgetBytes < item.Bytes
            )
                throw new DownloadFault("catalog-file-changed-or-budget-insufficient");
            return (
                input with
                {
                    Url = item.Url,
                },
                item.Sha1,
                item.Attribution
                    + " | "
                    + item.SourcePage
                    + " | "
                    + item.LicenseUrl
                    + " | Evidence: "
                    + item.RightsEvidence
            );
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            throw new DownloadFault("catalog-approval-invalid");
        }
    }
}

public static class DownloadCatalogEndpoints
{
    public static void MapDownloadCatalog(this WebApplication app)
    {
        foreach (var prefix in new[] { "/api", "/api/v1" })
        {
            var group = app.MapGroup(prefix + "/downloads/catalog").RequireAuthorization("admin");
            group.MapGet(
                "/capabilities",
                (CatalogApproval catalog) =>
                    new { enabled = catalog.Enabled, provider = "wikimedia-commons-cc0" }
            );
            group.MapGet(
                "",
                async (string? query, CatalogApproval catalog, MediaDb db, CancellationToken ct) =>
                {
                    if (!await DownloadWorker.Enabled(db, ct))
                        return Results.Forbid();
                    try
                    {
                        return Results.Ok(await catalog.Search(query ?? "", ct));
                    }
                    catch (DownloadFault fault)
                    {
                        return Results.BadRequest(new { message = fault.Code });
                    }
                }
            );
        }
    }
}
