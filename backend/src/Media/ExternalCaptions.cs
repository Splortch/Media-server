using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaServer.Core;

namespace MediaServer.Media;

public record ExternalCaption(
    int Index,
    string RelativePath,
    string Codec,
    string? Language,
    string Title
)
{
    public MediaTrack Track => new(Index, "subtitle", Codec, Language, Title);
}

public static class ExternalCaptions
{
    public const int MaxBytes = 1024 * 1024;

    // IDs depend on the filename, not enumeration order. -1 retains legacy movie.vtt semantics.
    public static IReadOnlyList<ExternalCaption> Discover(
        PathPolicy policy,
        Library library,
        MediaItem item
    )
    {
        var source = policy.Resolve(library.Path, item.RelativePath);
        var directory = Path.GetDirectoryName(source)!;
        var stem = Path.GetFileNameWithoutExtension(source);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var entries = Directory.EnumerateFiles(directory).Take(4097).ToArray();
        if (entries.Length > 4096)
            return [];
        var found = new List<ExternalCaption>();
        foreach (var path in entries.Order(StringComparer.Ordinal))
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var codec = extension switch
            {
                ".vtt" => "webvtt",
                ".srt" => "subrip",
                ".ass" => "ass",
                ".ssa" => "ssa",
                _ => null,
            };
            if (codec is null)
                continue;
            var name = Path.GetFileNameWithoutExtension(path);
            if (!name.Equals(stem, comparison) && !name.StartsWith(stem + ".", comparison))
                continue;
            var relative = Path.GetRelativePath(library.Path, path);
            try
            {
                using var stream = policy.OpenRead(library.Path, relative);
                if (stream.Length > MaxBytes)
                    continue;
            }
            catch (Exception e)
                when (e is IOException or ArgumentException or UnauthorizedAccessException)
            {
                continue;
            }
            var legacy = name.Equals(stem, comparison) && extension == ".vtt";
            var normalized = OperatingSystem.IsWindows() ? relative.ToUpperInvariant() : relative;
            var hash = BinaryPrimitives.ReadUInt32BigEndian(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalized))
            );
            var index = legacy ? -1 : -2 - (int)(hash & 0x3fffffff);
            var suffix = name.Length > stem.Length ? name[(stem.Length + 1)..] : "";
            var language = suffix.Split('.')[0];
            found.Add(
                new ExternalCaption(
                    index,
                    relative,
                    codec,
                    Regex.IsMatch(language, "\\A[a-zA-Z]{2,8}\\z") ? language : null,
                    $"External {extension[1..].ToUpperInvariant()}{(suffix.Length > 0 ? " · " + suffix : "")}"
                )
            );
        }
        // Reject hash collisions rather than selecting a different file under the same identifier.
        return found
            .GroupBy(x => x.Index)
            .Where(x => x.Count() == 1)
            .Select(x => x.Single())
            .Take(32)
            .ToArray();
    }

    public static async Task<byte[]> ReadAsync(
        PathPolicy policy,
        Library library,
        ExternalCaption caption,
        CancellationToken ct
    )
    {
        await using var stream = policy.OpenRead(library.Path, caption.RelativePath);
        if (stream.Length > MaxBytes)
            throw new ArgumentException("Caption quota exceeded.");
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + count > MaxBytes)
                throw new ArgumentException("Caption quota exceeded.");
            await bytes.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return bytes.ToArray();
    }
}
