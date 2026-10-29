using Fala.Helpers;
using Fala.Models;
using Xunit;

namespace Fala.Tests;

public class HistorySearchTests
{
    private static readonly HistoryItem[] Items =
    [
        new() { Id = 1, ShowTitle = "Własne Ścieżki", EpisodeTitle = "Łódź nocą", HeardUtc = DateTimeOffset.UtcNow.AddDays(-1), IsManual = true },
        new() { Id = 2, ShowTitle = "Tech Talk", EpisodeTitle = "Nowy procesor", HeardUtc = DateTimeOffset.UtcNow.AddDays(-2) },
        new() { Id = 3, ShowTitle = "Tech Talk", EpisodeTitle = "Łódź i technologia", HeardUtc = DateTimeOffset.UtcNow.AddDays(-3) },
    ];

    [Theory]
    [InlineData("Własne Ścieżki", "wlasne sciezki")]
    [InlineData("ŁÓDŹ", "lodz")]
    [InlineData("Gęślą jaźń", "gesla jazn")]
    [InlineData("Igår", "igar")]
    public void Fold_RemovesDiacriticsIncludingPolishL(string input, string expected)
        => Assert.Equal(expected, HistorySearch.Fold(input));

    [Fact]
    public void Filter_EmptyQueryReturnsAll()
        => Assert.Equal(3, HistorySearch.Filter(Items, "  ").Count);

    [Fact]
    public void Filter_TokenWithoutDiacriticsMatchesTitleWithThem()
    {
        var result = HistorySearch.Filter(Items, "lodz");

        Assert.Equal([1L, 3L], result.Select(x => x.Id).ToArray());
    }

    [Theory]
    [InlineData("ręcznie")]
    [InlineData("własne")] // wcześniej nie działało: „ł” nie ma rozkładu Unicode
    [InlineData("manual")]
    public void Filter_ManualKeyword(string keyword)
    {
        var result = HistorySearch.Filter(Items, keyword);

        Assert.Equal(1L, Assert.Single(result).Id);
    }

    [Fact]
    public void Filter_ShowPrefix()
    {
        var result = HistorySearch.Filter(Items, "show:\"tech talk\" procesor");

        Assert.Equal(2L, Assert.Single(result).Id);
    }

    [Fact]
    public void Suggest_ReturnsDistinctShowsFirst()
    {
        var result = HistorySearch.Suggest(Items, "tech");

        Assert.Equal("Tech Talk", result[0]);
        Assert.Equal(result.Count, result.Distinct().Count());
    }
}
