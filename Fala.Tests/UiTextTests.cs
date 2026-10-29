using Fala.Helpers;
using Fala.Localization;
using Xunit;

namespace Fala.Tests;

public class UiTextTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(11, 2)]
    [InlineData(12, 2)] // 12–14: forma „podcastów”, mimo końcówki 2–4
    [InlineData(14, 2)]
    [InlineData(22, 1)]
    [InlineData(25, 2)]
    [InlineData(0, 2)]
    public void PluralForm_Polish(long n, int expected)
        => Assert.Equal(expected, Loc.PluralForm("pl", n, 3));

    [Theory]
    [InlineData("en", 1, 0)]
    [InlineData("en", 2, 1)]
    [InlineData("sv", 1, 0)]
    [InlineData("sv", 5, 1)]
    public void PluralForm_TwoForms(string lang, long n, int expected)
        => Assert.Equal(expected, Loc.PluralForm(lang, n, 2));

    [Fact]
    public void Format_ChoosesPolishForms()
    {
        // Loc.Current domyślnie po polsku (bez wywołania SetLanguage).
        Assert.Equal("1 zakładka", Loc.Current.Format("BookmarksCount", 1));
        Assert.Equal("3 zakładki", Loc.Current.Format("BookmarksCount", 3));
        Assert.Equal("12 zakładek", Loc.Current.Format("BookmarksCount", 12));
        Assert.Equal("1 podcast · 22 najnowsze odcinki", Loc.Current.Format("HomeSummary", 1, 22));
    }

    [Fact]
    public void SplitLinks_FindsUrlsWithoutTrailingPunctuation()
    {
        var parts = ShowNotes.SplitLinks("Strona: https://example.com/a?b=1. Więcej (https://pl.wikipedia.org/wiki/Fala_(fizyka)).");

        var links = parts.Where(p => p.Link is not null).Select(p => p.Text).ToArray();
        Assert.Equal(["https://example.com/a?b=1", "https://pl.wikipedia.org/wiki/Fala_(fizyka)"], links);
        Assert.Equal("Strona: https://example.com/a?b=1. Więcej (https://pl.wikipedia.org/wiki/Fala_(fizyka)).",
                     string.Concat(parts.Select(p => p.Text)));
    }

    [Fact]
    public void SplitLinks_PlainTextStaysIntact()
        => Assert.Equal([("bez linków", (Uri?)null)], ShowNotes.SplitLinks("bez linków"));
}
