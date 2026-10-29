using System.Globalization;
using System.Text;

namespace MediaServer.Media;

public record FfmpegProgress(
    long Frames,
    double? FramesPerSecond,
    double? OutputSeconds,
    double? Speed
);

// Only numeric allowlisted fields leave the codec process. Never retain paths or raw output.
public static class FfmpegProgressReader
{
    public static async Task ReadAsync(
        TextReader reader,
        Action<FfmpegProgress> publish,
        CancellationToken ct
    )
    {
        var buffer = new char[1024];
        var line = new StringBuilder(256);
        var oversized = false;
        long frames = 0;
        double? fps = null,
            output = null,
            speed = null;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                var c = buffer[i];
                if (c != '\n')
                {
                    if (line.Length < 256 && !oversized)
                        line.Append(c);
                    else
                        oversized = true;
                    continue;
                }
                if (!oversized)
                {
                    var parts = line.ToString().TrimEnd('\r').Split('=', 2);
                    if (parts.Length == 2)
                    {
                        var value = parts[1].Trim();
                        switch (parts[0])
                        {
                            case "frame":
                                if (
                                    long.TryParse(
                                        value,
                                        NumberStyles.None,
                                        CultureInfo.InvariantCulture,
                                        out var n
                                    ) && n is >= 0 and <= 1_000_000_000
                                )
                                    frames = n;
                                break;
                            case "fps":
                                fps = Number(value, 1_000_000);
                                break;
                            case "out_time_us":
                                output = Number(value, 86_400_000_000) / 1_000_000;
                                break;
                            case "speed":
                                speed = Number(value.TrimEnd('x'), 1_000_000);
                                break;
                            case "progress" when value is "continue" or "end":
                                publish(new(frames, fps, output, speed));
                                frames = 0;
                                fps = output = speed = null;
                                break;
                        }
                    }
                }
                line.Clear();
                oversized = false;
            }
        }
    }

    private static double? Number(string value, double maximum) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
        && double.IsFinite(result)
        && result >= 0
        && result <= maximum
            ? result
            : null;
}
