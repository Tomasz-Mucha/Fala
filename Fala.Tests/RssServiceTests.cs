using Fala.Services;
using Xunit;

namespace Fala.Tests;

public class RssServiceTests
{
    [Theory]
    [InlineData("Mon, 02 Jan 2006 15:04:05 +0000", "2006-01-02T15:04:05Z")]
    [InlineData("Tue, 02 Jan 2006 15:04:05 GMT", "2006-01-02T15:04:05Z")] // błędny dzień tygodnia
    [InlineData("Mon, 02 Jan 2006 15:04:05 EST", "2006-01-02T20:04:05Z")]
    [InlineData("Thu, 15 Aug 2024 10:00:00 PDT", "2024-08-15T17:00:00Z")]
    [InlineData("Wed, 7 May 2025 08:00:00 -0400", "2025-05-07T12:00:00Z")]
    [InlineData("15 Aug 2024 10:00:00 +0200", "2024-08-15T08:00:00Z")]
    [InlineData("Mon, 2 Sep 2024 06:00 +0000", "2024-09-02T06:00:00Z")]
    [InlineData("2024-08-15T10:00:00+02:00", "2024-08-15T08:00:00Z")]
    [InlineData("2024-08-15T10:00:00Z", "2024-08-15T10:00:00Z")]
    public void ParseDate_ReturnsUtc(string raw, string expectedUtc)
    {
        var parsed = RssService.ParseDate(raw);

        Assert.NotNull(parsed);
        Assert.Equal(DateTimeOffset.Parse(expectedUtc), parsed.Value);
        Assert.Equal(TimeSpan.Zero, parsed.Value.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("Fri, 30 Feb 2024 10:00:00 GMT")]
    public void ParseDate_InvalidReturnsNull(string? raw)
        => Assert.Null(RssService.ParseDate(raw));

    [Theory]
    [InlineData("3600", 3600)]
    [InlineData("75:30", 4530)]      // minuty > 59
    [InlineData("1:02:03", 3723)]
    [InlineData("05:00", 300)]
    [InlineData("1:75:00", 8100)]
    [InlineData("01:02:03.5", 3723.5)]
    public void ParseDuration_Parses(string raw, double expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RssService.ParseDuration(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("-5:00")]
    [InlineData("1:2:3:4")]
    public void ParseDuration_InvalidReturnsNull(string? raw)
        => Assert.Null(RssService.ParseDuration(raw));

    [Fact]
    public void ParseDocument_Rss()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
              <channel>
                <title>Mój &amp; Podcast</title>
                <itunes:author>Autor</itunes:author>
                <description><![CDATA[<p>Opis <b>kanału</b></p>]]></description>
                <itunes:image href="https://example.com/cover.jpg"/>
                <item>
                  <title>Odcinek 2</title>
                  <guid>ep-2</guid>
                  <pubDate>Tue, 02 Jan 2024 10:00:00 EST</pubDate>
                  <itunes:duration>75:30</itunes:duration>
                  <enclosure url="https://example.com/ep2.mp3" type="audio/mpeg" length="1"/>
                </item>
                <item>
                  <title>Tylko wideo</title>
                  <enclosure url="https://example.com/video.mp4" type="video/mp4"/>
                </item>
                <item>
                  <title>Odcinek 1</title>
                  <enclosure url="/audio/ep1.m4a" type="audio/x-m4a"/>
                </item>
              </channel>
            </rss>
            """;

        var (podcast, episodes) = RssService.ParseDocument(xml, "https://example.com/feed.xml");

        Assert.Equal("Mój & Podcast", podcast.Title);
        Assert.Equal("Autor", podcast.Author);
        Assert.Equal("Opis kanału", podcast.Description);
        Assert.Equal("https://example.com/cover.jpg", podcast.ArtworkUrl);

        Assert.Equal(2, episodes.Count); // odcinek wideo pominięty
        var ep2 = episodes[0];
        Assert.Equal("ep-2", ep2.Guid);
        Assert.Equal("https://example.com/ep2.mp3", ep2.AudioUrl);
        Assert.Equal(TimeSpan.FromSeconds(4530), ep2.Duration);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 15, 0, 0, TimeSpan.Zero), ep2.PublishedUtc);

        // Względny adres audio rozwiązany względem kanału; brak <guid> → guid = adres audio.
        Assert.Equal("https://example.com/audio/ep1.m4a", episodes[1].AudioUrl);
        Assert.Equal(episodes[1].AudioUrl, episodes[1].Guid);
    }

    [Fact]
    public void ParseDocument_Atom()
    {
        const string xml = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom cast</title>
              <link rel="self" href="https://example.org/atom.xml"/>
              <author><name>Ktoś</name></author>
              <entry>
                <id>urn:1</id>
                <title>Wpis</title>
                <published>2024-03-01T12:00:00+01:00</published>
                <link rel="enclosure" type="audio/mpeg" href="https://example.org/1.mp3"/>
              </entry>
            </feed>
            """;

        var (podcast, episodes) = RssService.ParseDocument(xml, "https://example.org/x");

        Assert.Equal("Atom cast", podcast.Title);
        Assert.Equal("Ktoś", podcast.Author);
        Assert.Equal("https://example.org/atom.xml", podcast.FeedUrl);
        var ep = Assert.Single(episodes);
        Assert.Equal("urn:1", ep.Guid);
        Assert.Equal("https://example.org/1.mp3", ep.AudioUrl);
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 11, 0, 0, TimeSpan.Zero), ep.PublishedUtc);
    }

    [Theory]
    [InlineData("feed://example.com/rss", "https://example.com/rss")]
    [InlineData("feed:https://example.com/rss", "https://example.com/rss")]
    [InlineData("example.com/podcast.xml", "https://example.com/podcast.xml")]
    [InlineData("  http://example.com/feed  ", "http://example.com/feed")]
    public void NormalizeUrl(string input, string expected)
        => Assert.Equal(expected, RssService.NormalizeUrl(input));

    [Fact]
    public void NormalizeUrl_RejectsNonHttp()
        => Assert.Throws<InvalidOperationException>(() => RssService.NormalizeUrl("ftp://example.com/rss"));

    [Theory]
    [InlineData("https://example.com/feed", true)]
    [InlineData("example.com/rss", true)]
    [InlineData("feeds.example.com", true)]
    [InlineData("the daily", false)]
    [InlineData("radio", false)]
    public void LooksLikeUrl(string input, bool expected)
        => Assert.Equal(expected, RssService.LooksLikeUrl(input));
}
