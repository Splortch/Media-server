using MediaServer.Media;
using Xunit;

namespace Api.Tests;

public sealed class ToneMappingTests
{
    [Theory]
    [InlineData("smpte2084")]
    [InlineData("arib-std-b67")]
    public async Task HdrNeutralRampPreservesBlackNeutralityAndLuminanceOrder(string transfer)
    {
        var root = Path.Combine(TestPaths.TempRoot, "tone-ramp-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var runner = new FfmpegRunner("ffmpeg");
            // Limited-range 10-bit black, mid-code and white in three neutral patches.
            // This catches black lift, channel casts and inverted/clipped tone curves;
            // it is a numerical regression, not reference-display certification.
            var input =
                "nullsrc=size=96x32:rate=1,format=yuv420p10le,"
                + "geq=lum='if(lt(X,32),64,if(lt(X,64),512,940))':cb=512:cr=512,"
                + $"setparams=color_primaries=bt2020:color_trc={transfer}:colorspace=bt2020nc:range=limited";
            await runner.RunAsync(
                [
                    "-filter_threads",
                    "1",
                    "-f",
                    "lavfi",
                    "-i",
                    input,
                    "-frames:v",
                    "1",
                    "-vf",
                    TranscodeProfile.Filter(TranscodeProfile.Ladder[0], true) + ",format=rgb24",
                    "-threads",
                    "1",
                    "-f",
                    "rawvideo",
                    "ramp.rgb",
                ],
                root,
                timeout.Token
            );
            var pixels = await File.ReadAllBytesAsync(Path.Combine(root, "ramp.rgb"));
            Assert.Equal(96 * 32 * 3, pixels.Length);
            var values = new List<int>();
            foreach (var x in new[] { 16, 48, 80 })
            {
                var offset = (16 * 96 + x) * 3;
                var channels = pixels.AsSpan(offset, 3).ToArray();
                Assert.InRange(channels.Max() - channels.Min(), 0, 3);
                values.Add(channels[0]);
            }
            Assert.InRange(values[0], 0, 4);
            Assert.True(values[1] > values[0] + 20, "Midtone collapsed to black.");
            Assert.True(values[2] > values[1] + 20, "Highlights collapsed into midtones.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
