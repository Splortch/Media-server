using System.Xml;
using System.Xml.Linq;
using MediaServer.Core;
using MediaServer.Media;

namespace MediaServer.Metadata;

public sealed class LocalMetadata(ArtworkCache cache)
{
    public static MetadataValue Parse(Stream input)
    {
        using var reader = XmlReader.Create(
            input,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 256 * 1024,
                IgnoreComments = true,
            }
        );
        var root = XDocument.Load(reader).Root ?? throw new MetadataFault("nfo-is-empty");
        if (root.Name.LocalName is not ("movie" or "tvshow" or "episodedetails"))
            throw new MetadataFault("unsupported-nfo-document");
        string Value(string key, int max) =>
            MetadataService.Text(root.Element(key)?.Value ?? "", max);
        int? Number(string key, int min, int max) =>
            int.TryParse(root.Element(key)?.Value, out var n) && n >= min && n <= max ? n : null;
        var title = Value("title", 200);
        if (title.Length == 0)
            throw new MetadataFault("nfo-needs-title");
        return new(
            title,
            Number("year", 1888, 2100),
            Value("plot", 5000),
            root.Elements("genre")
                .Take(20)
                .Select(x => MetadataService.Text(x.Value, 80))
                .ToArray(),
            root.Elements("actor")
                .Take(30)
                .Select(x => MetadataService.Text(x.Element("name")?.Value ?? "", 120))
                .Where(x => x.Length > 0)
                .ToArray(),
            Value("showtitle", 200) is { Length: > 0 } series ? series : null,
            Number("season", 0, 99),
            Number("episode", 1, 999),
            "local-nfo",
            null,
            "Local library sidecar metadata"
        );
    }

    public async Task<MetadataResult> Read(
        MediaItem item,
        Library library,
        PathPolicy paths,
        CancellationToken ct
    )
    {
        var relative = Path.ChangeExtension(item.RelativePath, ".nfo");
        using var stream = paths.OpenRead(library.Path, relative);
        MetadataValue value;
        try
        {
            value = Parse(stream);
        }
        catch (XmlException)
        {
            throw new MetadataFault("nfo-invalid-or-contains-dtd");
        }
        async Task<string?> Art(string[] names)
        {
            var folder = Path.GetDirectoryName(item.RelativePath) ?? "";
            foreach (var name in names)
            {
                var candidate = Path.Combine(folder, name);
                if (!File.Exists(Path.Combine(library.Path, candidate)))
                    continue;
                using var source = paths.OpenRead(library.Path, candidate);
                return await cache.Store(source, null, ct);
            }
            return null;
        }
        // Only fixed adjacent names are read. NFO URLs/paths never initiate requests or choose files.
        var stem = Path.GetFileNameWithoutExtension(item.RelativePath);
        var poster = await Art([
            stem + "-poster.jpg",
            stem + "-poster.png",
            "poster.jpg",
            "poster.png",
        ]);
        var backdrop = await Art([
            stem + "-fanart.jpg",
            stem + "-fanart.png",
            "fanart.jpg",
            "fanart.png",
        ]);
        return new(value, poster, backdrop);
    }
}
