using System.Text.Json;
using MediaServer.Media;
using Xunit;

namespace Api.Tests;

public sealed class MovieMetadataTests
{
    [Fact]
    public void ReadsCaseInsensitiveTagsAndFiltersInvalidChapterTimes()
    {
        using var json = JsonDocument.Parse(
            """
            {"format":{"tags":{"TITLE":" Movie ","SYNOPSIS":"A story","DATE":"2025","GENRE":"Drama","DIRECTOR":"A Director"}},
             "chapters":[
               {"start_time":"60","end_time":"150","tags":{"title":"Finale"}},
               {"start_time":"0","end_time":"60"},
               {"start_time":"-1","end_time":"5"},
               {"start_time":"NaN","end_time":"5"},
               {"start_time":"110","end_time":"120"},
               {"start_time":"30","end_time":"20"}]}
            """
        );
        var result = Ffprobe.ReadMetadata(json.RootElement, 100);
        Assert.Equal("Movie", result.Title);
        Assert.Equal("A story", result.Description);
        Assert.Equal("2025", result.ReleaseDate);
        Assert.Equal("Drama", result.Genre);
        Assert.Equal("A Director", result.Director);
        Assert.Equal(2, result.Chapters.Count);
        Assert.Equal(0, result.Chapters[0].Start);
        Assert.Equal("Finale", result.Chapters[1].Title);
        Assert.Equal(100, result.Chapters[1].End);
    }

    [Fact]
    public void UntaggedFilesHaveEmptyMetadata()
    {
        using var json = JsonDocument.Parse("""{"format":{}}""");
        var result = Ffprobe.ReadMetadata(json.RootElement, 100);
        Assert.Null(result.Title);
        Assert.Null(result.Description);
        Assert.Empty(result.Chapters);
    }
}
