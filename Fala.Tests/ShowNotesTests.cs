using Fala.Helpers;
using Fala.Models;
using Xunit;

namespace Fala.Tests;

public class ShowNotesTests
{
    [Fact]
    public void SplitTimestamps_FindsTimestampsAndKeepsText()
    {
        var parts = TimeText.SplitTimestamps("00:00 Wstęp\n12:34 Temat\n1:02:03 Koniec");

        Assert.Equal(
            [
                ("00:00", (TimeSpan?)TimeSpan.Zero),
                (" Wstęp\n", null),
                ("12:34", new TimeSpan(0, 12, 34)),
                (" Temat\n", null),
                ("1:02:03", new TimeSpan(1, 2, 3)),
                (" Koniec", null)
            ],
            parts);
    }

    [Theory]
    [InlineData("Data 2024:10:05 i 1:2:3:4 to nie znaczniki")]
    [InlineData("Wynik 3:1 w meczu")] // sekundy muszą mieć dwie cyfry
    [InlineData("Bez znaczników")]
    public void SplitTimestamps_IgnoresNonTimestamps(string text)
        => Assert.All(TimeText.SplitTimestamps(text), p => Assert.Null(p.At));

    [Fact]
    public void SplitTimestamps_IgnoresTimesBeyondDuration()
    {
        // Odcinek 10-minutowy: „20:00” (20. minuta) nie może być znacznikiem w tym odcinku.
        var parts = TimeText.SplitTimestamps("Start 05:00, live o 20:00", TimeSpan.FromMinutes(10));

        Assert.Equal([new TimeSpan(0, 5, 0)], parts.Where(p => p.At is not null).Select(p => p.At!.Value));
        Assert.Equal("Start 05:00, live o 20:00", string.Concat(parts.Select(p => p.Text)));
    }

    [Fact]
    public void Notes_KeepsLineBreaksFromHtml()
    {
        var notes = PlainText.Notes("<p>Rozdziały:</p><ul><li>00:00&nbsp;Wstęp</li><li>12:34 Temat &amp; goście</li></ul>");

        Assert.Equal("Rozdziały:\n00:00 Wstęp\n12:34 Temat & goście", notes);
    }

    [Fact]
    public void BookmarkExport_GroupsByShowAndEpisodeSortedByPosition()
    {
        var bookmarks = new[]
        {
            new Bookmark { ShowTitle = "B Show", EpisodeTitle = "Ep 1", Title = "Późniejszy", Position = TimeSpan.FromMinutes(20) },
            new Bookmark { ShowTitle = "B Show", EpisodeTitle = "Ep 1", Title = "Wcześniejszy", Position = TimeSpan.FromMinutes(5), Comment = "Linia 1\nLinia 2" },
            new Bookmark { ShowTitle = "A Show", EpisodeTitle = "Ep X", Title = "Jedyny", Position = TimeSpan.FromSeconds(90) },
        };

        var md = BookmarkExport.ToMarkdown(bookmarks, new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));

        var a = md.IndexOf("## A Show");
        var b = md.IndexOf("## B Show");
        Assert.True(a > 0 && b > a, "podcasty alfabetycznie");
        Assert.True(md.IndexOf("**5:00** — Wcześniejszy") < md.IndexOf("**20:00** — Późniejszy"), "zakładki według pozycji");
        Assert.Contains("- **1:30** — Jedyny", md);
        Assert.Contains("  > Linia 1\n  > Linia 2", md.ReplaceLineEndings("\n"));
    }
}
