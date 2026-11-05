using System.Text.Json;
using System.Text.RegularExpressions;
using MediaServer.Core;
using MediaServer.Downloads;
using MediaServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Metadata;

public sealed class MetadataService(ArtworkCache cache, IDownloadTransport transport)
{
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public static string Text(string? text, int limit)
    {
        var value = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (value.Length > limit)
            throw new MetadataFault("metadata-text-too-long");
        return value;
    }

    public static MetadataValue Validate(MetadataValue input)
    {
        if (
            input.Year is < 1888 or > 2100
            || input.Season is < 0 or > 99
            || input.Episode is < 1 or > 999
            || (input.Genres?.Length ?? 0) > 20
            || (input.Cast?.Length ?? 0) > 30
        )
            throw new MetadataFault("metadata-values-out-of-range");
        var title = Text(input.Title, 200);
        if (title.Length == 0)
            throw new MetadataFault("metadata-title-required");
        return input with
        {
            Title = title,
            Synopsis = Text(input.Synopsis, 5000),
            Genres = (input.Genres ?? []).Select(x => Text(x, 80)).ToArray(),
            Cast = (input.Cast ?? []).Select(x => Text(x, 120)).ToArray(),
            Series = input.Series is null ? null : Text(input.Series, 200),
        };
    }

    public static async Task<byte[]> Bytes(Stream input, int limit, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > limit)
                throw new MetadataFault("metadata-response-too-large");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public async Task<string?> Image(string? path, bool backdrop, CancellationToken ct)
    {
        if (path is null)
            return null;
        if (
            !Regex.IsMatch(
                path,
                "^/[A-Za-z0-9_-]{1,100}\\.(jpg|png|webp)$",
                RegexOptions.CultureInvariant
            )
        )
            throw new MetadataFault("unsafe-provider-artwork-path");
        using var client = transport.Create(["image.tmdb.org"]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://image.tmdb.org/t/p/" + (backdrop ? "w1280" : "w500") + path
        );
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token
        );
        if (!response.IsSuccessStatusCode)
            throw new MetadataFault("artwork-provider-unavailable");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        return await cache.Store(
            input,
            response.Content.Headers.ContentType?.MediaType,
            timeout.Token
        );
    }

    public static MetadataView View(MediaMetadata row, int id, string prefix)
    {
        var value =
            JsonSerializer.Deserialize<MetadataValue>(row.DocumentJson)
            ?? throw new MetadataFault("stored-metadata-invalid");
        return new(
            value,
            row.PosterKey is null
                ? null
                : prefix + "/media/" + id + "/artwork/poster?revision=" + row.Revision,
            row.BackdropKey is null
                ? null
                : prefix + "/media/" + id + "/artwork/backdrop?revision=" + row.Revision,
            row.Revision
        );
    }

    public static async Task<MediaMetadata> Save(
        MediaDb db,
        MediaItem item,
        MetadataValue value,
        string? poster,
        string? backdrop,
        long revision,
        CancellationToken ct
    )
    {
        value = Validate(value);
        var row = await db.Metadata.SingleOrDefaultAsync(x => x.MediaItemId == item.Id, ct);
        if ((row?.Revision ?? 0) != revision)
            throw new MetadataFault("metadata-revision-conflict");
        if (row is null)
        {
            row = new() { MediaItemId = item.Id, Revision = 0 };
            db.Metadata.Add(row);
        }
        row.DocumentJson = JsonSerializer.Serialize(value);
        row.PosterKey = poster;
        row.BackdropKey = backdrop;
        row.Revision++;
        item.Title = value.Title;
        item.SearchTitle = value.Title.ToLowerInvariant();
        await db.SaveChangesAsync(ct);
        return row;
    }
}
