using System.Text;
using Fala.Models;
using Fala.Services;
using Xunit;

namespace Fala.Tests;

public class OpmlServiceTests
{
    private static Stream Utf8(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Parse_NestedFoldersCaseInsensitiveAttributesAndDuplicates()
    {
        const string opml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE opml>
            <opml version="1.0"><body>
              <outline text="feeds">
                <outline type="rss" text="A" xmlUrl="https://a.example/feed.xml" />
                <outline type="rss" text="A (dup)" xmlUrl="https://a.example/feed.xml/" />
              </outline>
              <outline text="Tech"><outline text="C" xmlurl="https://c.example/rss"/></outline>
              <outline text="tylko folder"/>
              <outline type="link" text="D" url="https://d.example/podcast.rss"/>
            </body></opml>
            """;

        var urls = OpmlService.ParseFeedUrls(Utf8(opml));

        Assert.Equal(
            ["https://a.example/feed.xml", "https://c.example/rss", "https://d.example/podcast.rss"],
            urls);
    }

    [Fact]
    public void ParseEntries_FoldersBecomeGroups()
    {
        const string opml = """
            <opml version="2.0"><body>
              <outline text="Sport">
                <outline type="rss" text="A" xmlUrl="https://a.example/rss" />
                <outline type="rss" text="B" xmlUrl="https://b.example/rss" />
              </outline>
              <outline title="Historia"><outline text="A" xmlUrl="https://a.example/rss/" /></outline>
              <outline type="rss" text="C" xmlUrl="https://c.example/rss" />
            </body></opml>
            """;

        var entries = OpmlService.ParseEntries(Utf8(opml));

        Assert.Equal(3, entries.Count);
        Assert.Equal(["Sport", "Historia"], entries[0].Groups); // ten sam kanał w dwóch folderach
        Assert.Equal(["Sport"], entries[1].Groups);
        Assert.Empty(entries[2].Groups);
    }

    [Fact]
    public void ParseEntries_SingleWrapperFolderIsIgnored()
    {
        // Pocket Casts wkłada wszystkie kanały do jednego folderu „feeds” — to nie jest grupa.
        const string opml = """
            <opml><body><outline text="feeds">
              <outline xmlUrl="https://a.example/rss" /><outline xmlUrl="https://b.example/rss" />
            </outline></body></opml>
            """;

        Assert.All(OpmlService.ParseEntries(Utf8(opml)), e => Assert.Empty(e.Groups));
    }

    [Fact]
    public void Parse_InvalidXmlThrowsInvalidOperation()
        => Assert.Throws<InvalidOperationException>(() => OpmlService.ParseFeedUrls(Utf8("<opml><body>")));

    [Fact]
    public void Build_RoundTripsAndEscapes()
    {
        var podcasts = new[]
        {
            new Podcast { Title = "Zażółć & <gęślą>", FeedUrl = "https://x.pl/rss?a=1&b=2", ArtworkUrl = "https://x.pl/i.jpg" },
            new Podcast { Title = "Drugi", FeedUrl = "https://y.pl/feed" }
        };

        var xml = OpmlService.Build(podcasts);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        Assert.Contains("Zażółć &amp; &lt;gęślą&gt;", xml);
        Assert.Equal(
            ["https://x.pl/rss?a=1&b=2", "https://y.pl/feed"],
            OpmlService.ParseFeedUrls(Utf8(xml)));
    }
}
