using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class LyricsServiceTests
{
    [Fact]
    public void ParseLrcMuxResult_ReturnsTimedLinesAndActualProvider()
    {
        const string json = """
            {
              "meta": {
                "level": "word",
                "source": { "id": "musixmatch", "name": "Musixmatch" }
              },
              "lines": [
                { "text": "Second line", "start": 2450, "end": 4000 },
                { "text": "First line", "start": 1200, "end": 2400 }
              ]
            }
            """;

        LyricsResult? result = LyricsService.ParseLrcMuxResult(json);

        Assert.NotNull(result);
        Assert.Equal("Musixmatch via lrc mux", result.Provider);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal("First line", result.Lines[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), result.Lines[0].Time);
        Assert.Equal("Second line", result.Lines[1].Text);
    }

    [Fact]
    public void ParseLrcMuxResult_RejectsUnsynchronisedLyrics()
    {
        const string json = """
            {
              "meta": {
                "level": "none",
                "source": { "id": "genius", "name": "Genius" }
              },
              "lines": [
                { "text": "Plain lyric", "start": 0 }
              ]
            }
            """;

        Assert.Null(LyricsService.ParseLrcMuxResult(json));
    }
}
