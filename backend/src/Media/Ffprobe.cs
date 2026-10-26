using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaServer.Core;

namespace MediaServer.Media;

public sealed class Ffprobe(string executable) : IMediaProbe
{
    public async Task<ProbeResult> ProbeAsync(string path, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (
            var arg in new[]
            {
                "-v",
                "error",
                "-protocol_whitelist",
                "file",
                "-show_entries",
                "format=duration,format_name:stream=codec_type,codec_name",
                "-of",
                "json",
                path,
            }
        )
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start FFprobe.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var kill = timeout.Token.Register(() =>
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException) { }
        });
        var output = ReadBounded(process.StandardOutput, timeout.Token);
        var error = ReadBounded(process.StandardError, timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var json = await output;
        await error;
        if (process.ExitCode != 0)
            throw new IOException("FFprobe could not read this media file.");
        using var doc = JsonDocument.Parse(json);
        var format = doc.RootElement.GetProperty("format");
        var duration =
            format.TryGetProperty("duration", out var d)
            && double.TryParse(d.GetString(), CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        string Codec(string type) =>
            streams
                .Where(x => x.GetProperty("codec_type").GetString() == type)
                .Select(x => x.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "")
                .FirstOrDefault()
            ?? "";
        if (Codec("video") == "")
            throw new IOException("No video stream found.");
        return new ProbeResult(
            double.IsFinite(duration) ? Math.Max(0, duration) : 0,
            format.GetProperty("format_name").GetString() ?? "",
            Codec("video"),
            Codec("audio")
        );
    }

    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken ct)
    {
        var output = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (output.Length + read > 1024 * 1024)
                throw new IOException("FFprobe output exceeded limit.");
            output.Append(buffer, 0, read);
        }
        return output.ToString();
    }
}

public sealed class DirectPlaybackPlanner : IPlaybackPlanner
{
    public PlaybackPlan Plan(MediaItem item)
    {
        var ext = Path.GetExtension(item.RelativePath).ToLowerInvariant();
        var mime =
            ext is ".mp4" or ".m4v" ? "video/mp4"
            : ext == ".webm" ? "video/webm"
            : "application/octet-stream";
        var compatible =
            (
                mime == "video/mp4"
                && item.VideoCodec == "h264"
                && item.AudioCodec is "aac" or "mp3" or ""
            )
            || (
                mime == "video/webm"
                && item.VideoCodec is "vp8" or "vp9" or "av1"
                && item.AudioCodec is "opus" or "vorbis" or ""
            );
        return new(
            "direct",
            mime,
            compatible
                ? null
                : "This format may not play in your browser. Transcoding is not available yet."
        );
    }
}
