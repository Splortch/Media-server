using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MediaServer.Media;

namespace MediaServer.Metadata;

public sealed class ArtworkCache(IConfiguration config)
{
    public const int FileLimit = 4 * 1024 * 1024;
    public const long TotalLimit = 128L * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string Root =>
        Path.Combine(
            Path.GetFullPath(config["MediaServer:DataDirectory"] ?? "data"),
            "artwork-cache"
        );

    public static string Mime(ReadOnlySpan<byte> bytes)
    {
        if (
            bytes.Length >= 8
            && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
        )
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255)
            return "image/jpeg";
        if (
            bytes.Length >= 12
            && bytes[..4].SequenceEqual("RIFF"u8)
            && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)
        )
            return "image/webp";
        throw new MetadataFault("artwork-must-be-jpeg-png-or-webp");
    }

    public async Task<string> Store(Stream input, string? expectedMime, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + count > FileLimit)
                throw new MetadataFault("artwork-exceeds-four-mib");
            memory.Write(buffer, 0, count);
        }
        var data = memory.ToArray();
        var mime = Mime(data);
        if (expectedMime is not null && mime != expectedMime)
            throw new MetadataFault("artwork-mime-mismatch");
        var key =
            Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()
            + (
                mime == "image/png" ? ".png"
                : mime == "image/jpeg" ? ".jpg"
                : ".webp"
            );
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Root);
            PathPolicy.RejectLinks(Root);
            var policy = new PathPolicy([Root]);
            if (File.Exists(Path.Combine(Root, key)))
            {
                using var existing = Open(key);
                return key;
            }
            var files = Directory.EnumerateFiles(Root).Take(1025).ToArray();
            if (files.Length >= 1024)
                throw new MetadataFault("artwork-cache-full");
            long total = 0;
            foreach (var path in files)
            {
                using var file = policy.OpenRead(Root, Path.GetFileName(path));
                total += file.Length;
            }
            if (total + data.Length > TotalLimit)
                throw new MetadataFault("artwork-cache-full");
            var temporary = Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var file = policy.OpenWrite(Root, temporary, true))
                {
                    await file.WriteAsync(data, ct);
                    await file.FlushAsync(ct);
                }
                File.Move(policy.Resolve(Root, temporary), Path.Combine(Root, key), false);
            }
            finally
            {
                if (File.Exists(Path.Combine(Root, temporary)))
                    File.Delete(policy.Resolve(Root, temporary));
            }
            return key;
        }
        finally
        {
            gate.Release();
        }
    }

    public FileStream Open(string key)
    {
        if (!Regex.IsMatch(key, "^[a-f0-9]{64}\\.(jpg|png|webp)$", RegexOptions.CultureInvariant))
            throw new MetadataFault("invalid-artwork-key");
        var file = new PathPolicy([Root]).OpenRead(Root, key);
        if (file.Length > FileLimit)
        {
            file.Dispose();
            throw new MetadataFault("artwork-exceeds-four-mib");
        }
        return file;
    }
}
