using System.Text.Json;
using MediaServer.Core;

namespace MediaServer.Media;

public record VideoColor(
    int Width,
    int Height,
    int? BitDepth,
    string? PixelFormat,
    string? Primaries,
    string? Transfer,
    string? Matrix,
    bool HasMasteringMetadata,
    string? SideDataJson = null
)
{
    public bool Hdr => Transfer is "smpte2084" or "arib-std-b67";
}

public record Rendition(string Name, int Height, int Kilobits);

public static class TranscodeProfile
{
    public static readonly Rendition[] Ladder =
    [
        new("360p", 360, 800),
        new("480p", 480, 1400),
        new("720p", 720, 2800),
    ];

    public static VideoColor? Color(MediaItem item)
    {
        try
        {
            return JsonSerializer.Deserialize<VideoColor>(item.ColorJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Filter(Rendition rendition, bool hdr) =>
        (
            hdr
                ? "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,sidedata=mode=delete,"
                : ""
        )
        + $"scale=w=-2:h='min({rendition.Height},ih)':force_original_aspect_ratio=decrease,format=yuv420p";

    // All selections are server-defined, never raw client command-line arguments.
    public static string[] EncoderArguments(string encoder) =>
        encoder switch
        {
            "h264_nvenc" => ["-c:v", encoder, "-preset", "p4"],
            "h264_qsv" => ["-c:v", encoder, "-preset", "medium"],
            "h264_vaapi" => ["-c:v", encoder],
            "h264_amf" => ["-c:v", encoder, "-quality", "balanced"],
            "libx264" => ["-c:v", encoder, "-preset", "veryfast"],
            _ => throw new ArgumentException("Unsupported encoder."),
        };
}
