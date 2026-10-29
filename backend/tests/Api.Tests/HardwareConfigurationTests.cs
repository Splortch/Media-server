using MediaServer.Media;
using Xunit;

namespace Api.Tests;

public sealed class HardwareConfigurationTests
{
    [Theory]
    [InlineData("/dev/dri/renderD128")]
    [InlineData("/dev/dri/renderD129")]
    public void AllowsExplicitRenderNodes(string device)
    {
        var settings = new TranscodeSettings(["libx264", "h264_vaapi"], device);
        settings.Validate();
        Assert.Equal(
            new[] { "-vaapi_device", device },
            TranscodeProfile.DeviceArguments("h264_vaapi", settings)
        );
    }

    [Theory]
    [InlineData("/dev/dri/../secret")]
    [InlineData("/tmp/device")]
    [InlineData("/dev/dri/renderD128,extra=value")]
    public void RejectsUnscopedDeviceNames(string device) =>
        Assert.Throws<ArgumentException>(() =>
            new TranscodeSettings(["libx264"], device).Validate()
        );

    [Theory]
    [InlineData(-2)]
    [InlineData(32)]
    public void RejectsInvalidGpuIndexes(int gpu) =>
        Assert.Throws<ArgumentException>(() =>
            new TranscodeSettings(["libx264"], NvencGpu: gpu).Validate()
        );

    [Fact]
    public void UsesSameExplicitNvencAdapterForProbeAndPlayback()
    {
        var settings = new TranscodeSettings(["h264_nvenc"], NvencGpu: 1);
        settings.Validate();
        Assert.Equal(
            new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-gpu", "1" },
            TranscodeProfile.EncoderArguments("h264_nvenc", settings)
        );
        Assert.DoesNotContain("-gpu", TranscodeProfile.EncoderArguments("libx264", settings));
    }

    [Fact]
    public void RejectsArbitraryQsvConfiguration() =>
        Assert.Throws<ArgumentException>(() =>
            new TranscodeSettings(["h264_qsv"], QsvDevice: "0,child_device=untrusted").Validate()
        );
}
