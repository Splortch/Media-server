using MediaServer.Core;
using MediaServer.Media;
using MediaServer.Services;
using Xunit;

namespace Api.Tests;

public sealed class ExternalCaptionTests
{
    [Fact]
    public void SanitizerRejectsMetadataMasqueradingAsCuesAndAcceptsUtf8Bom()
    {
        var safe = PlaybackSessions.SanitizeVtt(
            "\uFEFFWEBVTT\n\nSTYLE\n::cue { content: \" --> \"; }\n\nNOTE private --> metadata\nignored\n\ncue-id\n00:00:00.000 --> 00:00:01.000 position:0%\n<b>Caption</b>\u0001\n"
        );
        Assert.StartsWith("WEBVTT\n\n", safe);
        Assert.Contains("Caption", safe);
        Assert.DoesNotContain("STYLE", safe);
        Assert.DoesNotContain("NOTE", safe);
        Assert.DoesNotContain("position:", safe);
        Assert.DoesNotContain("<", safe);
        Assert.False(safe.Contains('\u0001'));
    }

    [Fact]
    public async Task DiscoveryIsScopedStableBoundedAndRejectsLinks()
    {
        var root = Path.Combine(TestPaths.TempRoot, "captions-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var library = new Library { Path = root };
            var item = new MediaItem { RelativePath = "film.mkv" };
            var policy = new PathPolicy([root]);
            await File.WriteAllTextAsync(Path.Combine(root, "film.mkv"), "media unchanged");
            foreach (
                var file in new[]
                {
                    "film.vtt",
                    "film.eng.srt",
                    "film.swe.forced.ass",
                    "other.eng.srt",
                    "film-malicious.srt",
                }
            )
                await File.WriteAllTextAsync(Path.Combine(root, file), "caption");
            var initial = ExternalCaptions.Discover(policy, library, item);
            Assert.Equal(3, initial.Count);
            Assert.Equal(-1, initial.Single(x => x.Codec == "webvtt").Index);
            var swedish = initial.Single(x => x.Language == "swe");
            Assert.Contains("forced", swedish.Title);
            await File.WriteAllTextAsync(Path.Combine(root, "film.deu.srt"), "German");
            Assert.Equal(
                swedish.Index,
                ExternalCaptions
                    .Discover(policy, library, item)
                    .Single(x => x.Language == "swe")
                    .Index
            );
            var oversized = Path.Combine(root, "film.fra.srt");
            await File.WriteAllBytesAsync(oversized, new byte[ExternalCaptions.MaxBytes + 1]);
            Assert.DoesNotContain(
                ExternalCaptions.Discover(policy, library, item),
                x => x.Language == "fra"
            );
            await Assert.ThrowsAsync<ArgumentException>(() =>
                ExternalCaptions.ReadAsync(
                    policy,
                    library,
                    new(-10, "../outside.srt", "subrip", null, "bad"),
                    default
                )
            );
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(
                    Path.Combine(root, "film.ita.srt"),
                    Path.Combine(root, "film.eng.srt")
                );
                Assert.DoesNotContain(
                    ExternalCaptions.Discover(policy, library, item),
                    x => x.Language == "ita"
                );
            }
            Assert.Equal(
                "media unchanged",
                await File.ReadAllTextAsync(Path.Combine(root, "film.mkv"))
            );
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
