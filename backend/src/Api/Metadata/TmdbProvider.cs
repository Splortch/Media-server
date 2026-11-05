using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaServer.Downloads;

namespace MediaServer.Metadata;

public sealed class TmdbProvider(IConfiguration config, IDownloadTransport transport)
    : IMetadataProvider
{
    public string Id => "tmdb";
    public bool Enabled =>
        config.GetValue<bool>("Metadata:Tmdb:Enabled")
        && !string.IsNullOrEmpty(config["Metadata:Tmdb:ReadAccessToken"]);
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTime last,
        blockedUntil;
    private readonly Dictionary<string, (DateTime Expires, MetadataCandidate[] Rows)> searches =
        new();

    private static string Kind(string kind) =>
        kind switch
        {
            "movies" => "movie",
            "tv" => "tv",
            _ => throw new MetadataFault("choose-movies-or-tv"),
        };

    private static int Number(string id) =>
        int.TryParse(id, out var value)
        && value > 0
        && value.ToString(System.Globalization.CultureInfo.InvariantCulture) == id
            ? value
            : throw new MetadataFault("invalid-provider-id");

    private async Task<JsonElement> Api(string path, CancellationToken ct)
    {
        if (!Enabled)
            throw new MetadataFault("tmdb-disabled-or-key-not-configured");
        var token = config["Metadata:Tmdb:ReadAccessToken"]!;
        if (!Regex.IsMatch(token, "^[A-Za-z0-9._-]{16,4096}$", RegexOptions.CultureInvariant))
            throw new MetadataFault("tmdb-key-configuration-invalid");
        if (!await gate.WaitAsync(0, ct))
            throw new MetadataFault("metadata-provider-busy");
        try
        {
            if (blockedUntil > DateTime.UtcNow)
                throw new MetadataFault("metadata-provider-rate-limited");
            var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - last);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            last = DateTime.UtcNow;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var client = transport.Create(["api.themoviedb.org"]);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri("https://api.themoviedb.org/3/" + path)
            );
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd(
                "MediaServer-Metadata/0.1 (https://github.com/Splortch/Media-server)"
            );
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token
            );
            if ((int)response.StatusCode == 429)
            {
                blockedUntil = DateTime.UtcNow.AddSeconds(
                    Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60, 60, 300)
                );
                throw new MetadataFault("metadata-provider-rate-limited");
            }
            // Redirects never receive the API credential and are never followed.
            if (!response.IsSuccessStatusCode)
                throw new MetadataFault("metadata-provider-unavailable");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = await MetadataService.Bytes(input, 1024 * 1024, deadline.Token);
            using var json = JsonDocument.Parse(bytes);
            return json.RootElement.Clone();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            throw new MetadataFault("metadata-provider-unavailable");
        }
        finally
        {
            gate.Release();
        }
    }

    private static string String(JsonElement obj, string name, int max) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? MetadataService.Text(value.GetString()!, max)
            : "";

    private static int? Year(string date) =>
        date.Length >= 4 && int.TryParse(date[..4], out var year) && year is >= 1888 and <= 2100
            ? year
            : null;

    private static string[] Names(JsonElement obj, string field, int count) =>
        obj.TryGetProperty(field, out var rows) && rows.ValueKind == JsonValueKind.Array
            ? rows.EnumerateArray()
                .Take(count)
                .Select(row => String(row, "name", 120))
                .Where(x => x.Length > 0)
                .ToArray()
            : [];

    public async Task<MetadataCandidate[]> Search(MetadataSearch search, CancellationToken ct)
    {
        if (!Enabled)
            throw new MetadataFault("tmdb-disabled-or-key-not-configured");
        var kind = Kind(search.Kind);
        var query = MetadataService.Text(search.Query, 80);
        if (query.Length < 2 || query != search.Query.Trim())
            throw new MetadataFault("search-needs-two-to-eighty-characters");
        var key = kind + ":" + query;
        lock (searches)
        {
            if (searches.TryGetValue(key, out var cached) && cached.Expires > DateTime.UtcNow)
                return cached.Rows;
        }
        var json = await Api(
            "search/" + kind + "?include_adult=false&page=1&query=" + Uri.EscapeDataString(query),
            ct
        );
        if (
            !json.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
        )
            throw new MetadataFault("invalid-provider-response");
        var rows = results
            .EnumerateArray()
            .Take(10)
            .Select(row => new MetadataCandidate(
                row.GetProperty("id")
                    .GetInt32()
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                search.Kind,
                String(row, kind == "movie" ? "title" : "name", 200),
                Year(String(row, kind == "movie" ? "release_date" : "first_air_date", 10))
            ))
            .ToArray();
        lock (searches)
        {
            if (searches.Count >= 20)
                searches.Clear();
            searches[key] = (DateTime.UtcNow.AddMinutes(5), rows);
        }
        return rows;
    }

    public async Task<MetadataResult> Match(MetadataMatch match, CancellationToken ct)
    {
        var kind = Kind(match.Kind);
        var id = Number(match.Id);
        if (
            match.Season is < 0 or > 99
            || match.Episode is < 1 or > 999
            || (match.Season is null) != (match.Episode is null)
            || kind == "movie" && match.Season is not null
        )
            throw new MetadataFault("supply-valid-season-and-episode-together-for-tv");
        var series = await Api(kind + "/" + id + "?append_to_response=credits", ct);
        var item = series;
        if (kind == "tv" && match.Season is not null)
            item = await Api(
                "tv/"
                    + id
                    + "/season/"
                    + match.Season
                    + "/episode/"
                    + match.Episode
                    + "?append_to_response=credits",
                ct
            );
        var cast = item.TryGetProperty("credits", out var credits)
            ? Names(credits, "cast", 30)
            : [];
        string? Image(JsonElement value, string property) =>
            String(value, property, 120) is { Length: > 0 } path ? path : null;
        var value = new MetadataValue(
            String(item, kind == "movie" ? "title" : "name", 200),
            Year(
                String(
                    item,
                    kind == "movie" ? "release_date"
                        : match.Episode is null ? "first_air_date"
                        : "air_date",
                    10
                )
            ),
            String(item, "overview", 5000),
            Names(series, "genres", 20),
            cast,
            kind == "tv" ? String(series, "name", 200) : null,
            match.Season,
            match.Episode,
            "tmdb",
            match.Id,
            "TMDB — https://www.themoviedb.org"
        );
        return new(
            value,
            Image(series, "poster_path"),
            Image(item, "still_path") ?? Image(series, "backdrop_path")
        );
    }
}
