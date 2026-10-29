using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Fala.Localization;
using Fala.Models;

namespace Fala.Services;

public sealed class RssService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex LinkTag = new(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Attr = new(
        @"(rel|type|href|title)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagStrip = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NumericOffset = new(@"([+-])(\d{2})(\d{2})$", RegexOptions.Compiled);
    private const int MaxFeedBytes = 40_000_000;

    // Strefy spotykane w pubDate (RFC 822), których DateTimeOffset.TryParse nie rozumie.
    private static readonly Dictionary<string, string> ZoneOffsets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UT"] = "+00:00", ["UTC"] = "+00:00", ["GMT"] = "+00:00", ["Z"] = "+00:00",
        ["EST"] = "-05:00", ["EDT"] = "-04:00",
        ["CST"] = "-06:00", ["CDT"] = "-05:00",
        ["MST"] = "-07:00", ["MDT"] = "-06:00",
        ["PST"] = "-08:00", ["PDT"] = "-07:00",
        ["CET"] = "+01:00", ["CEST"] = "+02:00",
        ["BST"] = "+01:00", ["IST"] = "+05:30",
        ["AEST"] = "+10:00", ["AEDT"] = "+11:00"
    };

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        // Część serwerów kanałów odrzuca nietypowe klienty — dopisek „Mozilla/5.0” zwiększa zgodność.
        c.DefaultRequestHeaders.UserAgent.ParseAdd(Helpers.AppInfo.UserAgent + " Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        c.DefaultRequestHeaders.Accept.ParseAdd(
            "application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.8, text/html;q=0.6, */*;q=0.5");
        return c;
    }

    public static bool LooksLikeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var v = value.Trim();
        if (v.Contains(' ') || v.Contains('\t') || v.Contains('\n'))
            return false;
        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("feed://", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("feed:", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!v.Contains('.'))
            return false;
        if (v.Contains('/') || v.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            || v.EndsWith(".rss", StringComparison.OrdinalIgnoreCase)
            || v.Contains("feed", StringComparison.OrdinalIgnoreCase)
            || v.Contains("podcast", StringComparison.OrdinalIgnoreCase)
            || v.Contains("rss", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate("https://" + v, UriKind.Absolute, out _);
        return false;
    }

    public static string NormalizeUrl(string value)
    {
        var v = value.Trim();
        if (v.StartsWith("feed://", StringComparison.OrdinalIgnoreCase))
            v = "https://" + v[7..];
        else if (v.StartsWith("feed:https://", StringComparison.OrdinalIgnoreCase)
                 || v.StartsWith("feed:http://", StringComparison.OrdinalIgnoreCase))
            v = v[5..];
        else if (!v.Contains("://"))
            v = "https://" + v.TrimStart('/');
        if (!Uri.TryCreate(v, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(Loc.Current.Get("BadUrl"));
        return uri.ToString();
    }

    public async Task<(Podcast Podcast, List<Episode> Episodes)> FetchAsync(
        string feedUrl,
        CancellationToken ct = default)
    {
        try
        {
            var url = NormalizeUrl(feedUrl);
            var page = await DownloadAsync(url, ct);

            if (LooksLikeHtml(page))
            {
                var discovered = DiscoverFeedUrl(page.Text, page.FinalUrl);
                if (discovered is null)
                    throw new InvalidOperationException(Loc.Current.Get("NoFeedOnPage"));
                page = await DownloadAsync(discovered, ct);
            }

            var doc = ParseXml(page);
            var relocated = doc.Descendants().FirstOrDefault(e => Local(e) == "new-feed-url")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(relocated))
            {
                var next = AbsoluteUrl(relocated, page.FinalUrl);
                if (!string.IsNullOrWhiteSpace(next)
                    && !string.Equals(next, page.FinalUrl, StringComparison.OrdinalIgnoreCase))
                {
                    page = await DownloadAsync(next, ct);
                    doc = ParseXml(page);
                }
            }

            if (IsAtom(doc))
                return ParseAtom(doc, page.FinalUrl);
            return ParseRss(doc, page.FinalUrl);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Anulowanie przez wywołującego ma przejść dalej jako OperationCanceledException,
            // a nie być raportowane jako timeout.
            throw new InvalidOperationException(Loc.Current.Get("Timeout"));
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(Loc.Current.Format("HttpFail", ex.Message));
        }
    }

    private static async Task<Downloaded> DownloadAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            throw new InvalidOperationException(
                Loc.Current.Format("HttpStatus", code, resp.ReasonPhrase ?? "", url));
        }

        // Odrzucamy za duży kanał na podstawie nagłówka, zanim ściągniemy całość do pamięci.
        if (resp.Content.Headers.ContentLength > MaxFeedBytes)
            throw new InvalidOperationException(Loc.Current.Get("FeedTooBig"));

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length == 0)
            throw new InvalidOperationException(Loc.Current.Get("EmptyFeed"));
        if (bytes.Length > MaxFeedBytes)
            throw new InvalidOperationException(Loc.Current.Get("FeedTooBig"));

        var media = resp.Content.Headers.ContentType?.MediaType;
        var final = resp.RequestMessage?.RequestUri?.ToString() ?? url;
        return new Downloaded(final, media, bytes, resp.Content.Headers.ContentType?.CharSet);
    }

    private static string DecodeText(byte[] bytes, string? charset)
    {
        Encoding enc = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { enc = Encoding.GetEncoding(charset); } catch { }
        }
        var text = enc.GetString(bytes);
        return text.TrimStart('\uFEFF');
    }

    private static bool LooksLikeHtml(Downloaded page)
    {
        var mediaType = page.MediaType;
        if (mediaType is not null
            && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
            && !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            return true;
        var trimmed = page.Head.TrimStart();
        return trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    private static string? DiscoverFeedUrl(string html, string pageUrl)
    {
        string? best = null;
        var bestScore = -1;
        foreach (Match tag in LinkTag.Matches(html))
        {
            string? rel = null, type = null, href = null, title = null;
            foreach (Match a in Attr.Matches(tag.Value))
            {
                var name = a.Groups[1].Value.ToLowerInvariant();
                var val = a.Groups[2].Success ? a.Groups[2].Value
                        : a.Groups[3].Success ? a.Groups[3].Value
                        : a.Groups[4].Value;
                switch (name)
                {
                    case "rel": rel = val; break;
                    case "type": type = val; break;
                    case "href": href = val; break;
                    case "title": title = val; break;
                }
            }

            if (string.IsNullOrWhiteSpace(href))
                continue;
            var relL = rel ?? "";
            var typeL = type ?? "";
            var titleL = title ?? "";
            if (relL.Length > 0 && relL.IndexOf("alternate", StringComparison.OrdinalIgnoreCase) < 0
                && relL.IndexOf("feed", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var score = 0;
            if (typeL.Contains("rss", StringComparison.OrdinalIgnoreCase)) score += 5;
            if (typeL.Contains("atom", StringComparison.OrdinalIgnoreCase)) score += 4;
            if (typeL.Contains("xml", StringComparison.OrdinalIgnoreCase)) score += 2;
            if (titleL.Contains("podcast", StringComparison.OrdinalIgnoreCase)) score += 3;
            if (href.Contains("podcast", StringComparison.OrdinalIgnoreCase)
                || href.Contains("rss", StringComparison.OrdinalIgnoreCase)
                || href.Contains("feed", StringComparison.OrdinalIgnoreCase))
                score += 2;
            if (score == 0)
                continue;

            var abs = AbsoluteUrl(WebUtility.HtmlDecode(href), pageUrl);
            if (abs is null)
                continue;
            if (score > bestScore)
            {
                bestScore = score;
                best = abs;
            }
        }
        return best;
    }

    private static XDocument ParseXml(Downloaded page)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };
        try
        {
            using var ms = new MemoryStream(page.Bytes);
            using var reader = XmlReader.Create(ms, settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            var extracted = ExtractXml(page.Text);
            using var sr = new StringReader(extracted);
            using var reader = XmlReader.Create(sr, settings);
            try
            {
                return XDocument.Load(reader);
            }
            catch (XmlException ex)
            {
                throw new InvalidOperationException(Loc.Current.Get("NotRss") + " " + ex.Message);
            }
        }
    }

    private static string ExtractXml(string text)
    {
        var rss = text.IndexOf("<rss", StringComparison.OrdinalIgnoreCase);
        var rdf = text.IndexOf("<rdf:RDF", StringComparison.OrdinalIgnoreCase);
        var feed = text.IndexOf("<feed", StringComparison.OrdinalIgnoreCase);
        var start = MinPositive(rss, rdf, feed);
        if (start < 0)
            return text.TrimStart('\uFEFF');
        return text[start..];
    }

    private static int MinPositive(params int[] values)
    {
        var min = int.MaxValue;
        foreach (var v in values)
        {
            if (v >= 0 && v < min)
                min = v;
        }
        return min == int.MaxValue ? -1 : min;
    }

    /// <summary>Parsuje gotowy dokument RSS/Atom (bez pobierania) — używane w testach.</summary>
    internal static (Podcast Podcast, List<Episode> Episodes) ParseDocument(string xml, string feedUrl)
    {
        var doc = XDocument.Parse(xml);
        return IsAtom(doc) ? ParseAtom(doc, feedUrl) : ParseRss(doc, feedUrl);
    }

    private static bool IsAtom(XDocument doc)
        => Local(doc.Root) == "feed";

    private static (Podcast, List<Episode>) ParseRss(XDocument doc, string feedUrl)
    {
        var root = doc.Root ?? throw new InvalidOperationException(Loc.Current.Get("NotRss"));
        var channel = Elems(root, "channel").FirstOrDefault() ?? root;
        var podcast = ReadPodcast(channel, feedUrl);

        // RSS 2.0: <item> w <channel>; RSS 1.0 (RDF): <item> jako rodzeństwo <channel>.
        var items = Elems(channel, "item").ToList();
        if (items.Count == 0)
            items = Elems(root, "item").ToList();

        var episodes = new List<Episode>();
        foreach (var item in items)
        {
            var ep = ReadItem(item, feedUrl);
            if (ep is not null)
                episodes.Add(ep);
        }
        return (podcast, episodes);
    }

    private static (Podcast, List<Episode>) ParseAtom(XDocument doc, string feedUrl)
    {
        var feed = doc.Root ?? throw new InvalidOperationException(Loc.Current.Get("NotRss"));
        var self = AtomLink(feed, "self", feedUrl) ?? feedUrl;
        var podcast = new Podcast
        {
            Title = Plain(ChildText(feed, "title")) is { Length: > 0 } t ? t : "Bez tytułu",
            Author = FirstAuthor(feed),
            FeedUrl = self,
            Description = Plain(ChildText(feed, "subtitle") ?? ChildText(feed, "summary") ?? ChildText(feed, "content")),
            ArtworkUrl = ArtworkFrom(feed, self)
        };

        var episodes = new List<Episode>();
        foreach (var entry in Elems(feed, "entry"))
        {
            var ep = ReadAtomEntry(entry, self);
            if (ep is not null)
                episodes.Add(ep);
        }
        return (podcast, episodes);
    }

    private static Podcast ReadPodcast(XElement channel, string feedUrl)
        => new()
        {
            Title = Plain(ChildText(channel, "title")) is { Length: > 0 } t ? t : "Bez tytułu",
            Author = FirstAuthor(channel),
            FeedUrl = feedUrl,
            Description = Plain(ChildText(channel, "description") ?? ChildText(channel, "summary")),
            ArtworkUrl = ArtworkFrom(channel, feedUrl)
        };

    private static Episode? ReadItem(XElement item, string feedUrl)
    {
        var audio = AudioFrom(item, feedUrl);
        if (audio is null)
            return null;

        var guid = ChildText(item, "guid")
                   ?? ChildText(item, "id")
                   ?? ChildText(item, "link")
                   ?? audio;

        return new Episode
        {
            Guid = guid.Trim(),
            Title = Plain(ChildText(item, "title")) is { Length: > 0 } t ? t : "Odcinek",
            AudioUrl = audio,
            Description = ChildText(item, "encoded") ?? ChildText(item, "description") ?? ChildText(item, "summary") ?? "",
            PublishedUtc = ParseDate(ChildText(item, "pubDate") ?? ChildText(item, "date") ?? ChildText(item, "published")),
            Duration = ParseDuration(ChildText(item, "duration") ?? AttrOf(item, "content", "duration"))
        };
    }

    private static Episode? ReadAtomEntry(XElement entry, string feedUrl)
    {
        var audio = AudioFrom(entry, feedUrl);
        if (audio is null)
            return null;

        var guid = ChildText(entry, "id") ?? AtomLink(entry, "alternate", feedUrl) ?? audio;
        return new Episode
        {
            Guid = guid.Trim(),
            Title = Plain(ChildText(entry, "title")) is { Length: > 0 } t ? t : "Odcinek",
            AudioUrl = audio,
            Description = ChildText(entry, "content") ?? ChildText(entry, "summary") ?? "",
            PublishedUtc = ParseDate(ChildText(entry, "published") ?? ChildText(entry, "updated")),
            Duration = ParseDuration(ChildText(entry, "duration") ?? AttrOf(entry, "content", "duration"))
        };
    }

    private static string? AudioFrom(XElement item, string feedUrl)
    {
        foreach (var enc in Elems(item, "enclosure"))
        {
            var url = AbsoluteUrl(enc.Attribute("url")?.Value ?? enc.Attribute("href")?.Value, feedUrl);
            if (IsAudio(url, enc.Attribute("type")?.Value))
                return url;
        }

        foreach (var content in Elems(item, "content"))
        {
            var url = AbsoluteUrl(content.Attribute("url")?.Value, feedUrl);
            var medium = content.Attribute("medium")?.Value;
            var type = content.Attribute("type")?.Value;
            if (medium is not null && medium.Equals("video", StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsAudio(url, type) || (url is not null && medium?.Equals("audio", StringComparison.OrdinalIgnoreCase) == true))
                return url;
        }

        foreach (var link in Elems(item, "link"))
        {
            var rel = link.Attribute("rel")?.Value ?? "";
            var href = AbsoluteUrl(link.Attribute("href")?.Value ?? link.Value, feedUrl);
            var type = link.Attribute("type")?.Value;
            if (rel.Equals("enclosure", StringComparison.OrdinalIgnoreCase) && IsAudio(href, type))
                return href;
            if (IsAudio(href, type))
                return href;
        }

        return null;
    }

    private static bool IsAudio(string? url, string? type)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (type is not null)
        {
            if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                return true;
            if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        var path = url.Split('?', '#')[0];
        return path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".aac", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".opus", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/audio", StringComparison.OrdinalIgnoreCase);
    }

    private static string ArtworkFrom(XElement el, string feedUrl)
    {
        foreach (var img in Elems(el, "image"))
        {
            var href = img.Attribute("href")?.Value
                       ?? ChildText(img, "url")
                       ?? ChildText(img, "href")
                       ?? img.Value;
            var abs = AbsoluteUrl(href, feedUrl);
            if (!string.IsNullOrWhiteSpace(abs))
                return abs;
        }
        foreach (var name in new[] { "thumbnail", "logo", "icon" })
        {
            foreach (var n in Elems(el, name))
            {
                var abs = AbsoluteUrl(n.Attribute("url")?.Value ?? n.Attribute("href")?.Value ?? n.Value, feedUrl);
                if (!string.IsNullOrWhiteSpace(abs))
                    return abs;
            }
        }
        return "";
    }

    private static string FirstAuthor(XElement el)
    {
        var itunes = Elems(el, "author")
            .Select(a => Plain(a.Value))
            .FirstOrDefault(s => s.Length > 0);
        if (!string.IsNullOrWhiteSpace(itunes))
            return itunes;
        var name = Elems(el, "author").Select(a => Plain(ChildText(a, "name"))).FirstOrDefault(s => s.Length > 0);
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        return Plain(ChildText(el, "creator") ?? ChildText(el, "managingEditor"));
    }

    private static string? AtomLink(XElement el, string rel, string baseUrl)
    {
        foreach (var link in Elems(el, "link"))
        {
            var r = link.Attribute("rel")?.Value ?? "alternate";
            if (r.Equals(rel, StringComparison.OrdinalIgnoreCase))
                return AbsoluteUrl(link.Attribute("href")?.Value ?? link.Value, baseUrl);
        }
        return null;
    }

    private static IEnumerable<XElement> Elems(XElement el, string local)
        => el.Elements().Where(e => Local(e) == local);

    private static string Local(XElement? e)
        => e?.Name.LocalName ?? "";

    private static XElement? Child(XElement el, string local)
        => Elems(el, local).FirstOrDefault();

    private static string? ChildText(XElement? el, string local)
        => el is null ? null : Child(el, local)?.Value.Trim();

    private static string? AttrOf(XElement el, string local, string attr)
        => Elems(el, local).Select(e => e.Attribute(attr)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string Plain(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";
        s = WebUtility.HtmlDecode(s.Trim());
        if (s.Contains('<'))
            s = TagStrip.Replace(s, " ");
        return Whitespace.Replace(s, " ").Trim();
    }

    private static string? AbsoluteUrl(string? value, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = WebUtility.HtmlDecode(value.Trim());
        if (Uri.TryCreate(value, UriKind.Absolute, out var abs)
            && abs.Scheme is "http" or "https")
            return abs.ToString();
        if (!string.IsNullOrWhiteSpace(baseUrl)
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out var b)
            && Uri.TryCreate(b, value, out var rel)
            && rel.Scheme is "http" or "https")
            return rel.ToString();
        return null;
    }

    internal static DateTimeOffset? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var s = raw.Trim();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.DateTimeStyles style =
            System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeUniversal;

        // ISO 8601 (Atom, dc:date) i poprawne RFC 1123.
        if (DateTimeOffset.TryParse(s, inv, style, out var dto))
            return dto.ToUniversalTime();

        // RFC 822 z pubDate: nazwa dnia bywa błędna (TryParse wtedy odrzuca datę),
        // a strefa bywa nazwana (EST, PDT…). Usuwamy dzień tygodnia i zamieniamy strefę na offset.
        var comma = s.IndexOf(',');
        if (comma is > 0 and <= 10)
            s = s[(comma + 1)..].Trim();
        var space = s.LastIndexOf(' ');
        if (space > 0 && ZoneOffsets.TryGetValue(s[(space + 1)..], out var offset))
            s = s[..space] + " " + offset;
        else
            s = NumericOffset.Replace(s, "$1$2:$3");

        if (DateTimeOffset.TryParse(s, inv, style, out dto))
            return dto.ToUniversalTime();
        return null;
    }

    internal static TimeSpan? ParseDuration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        raw = raw.Trim();
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && seconds < 100_000)
            return TimeSpan.FromSeconds(seconds);
        // „mm:ss” / „hh:mm:ss” liczone ręcznie — TimeSpan.Parse odrzuca np. „75:30” (minuty > 59).
        var parts = raw.Split(':');
        if (parts.Length is 2 or 3)
        {
            double total = 0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 0)
                    return null;
                total = total * 60 + n;
            }
            return TimeSpan.FromSeconds(total);
        }
        return null;
    }

    private sealed class Downloaded(string finalUrl, string? mediaType, byte[] bytes, string? charset)
    {
        private string? _text;

        public string FinalUrl { get; } = finalUrl;
        public string? MediaType { get; } = mediaType;
        public byte[] Bytes { get; } = bytes;

        // Pełny tekst dekodujemy tylko wtedy, gdy jest potrzebny (strona HTML albo uszkodzony XML) —
        // poprawny kanał idzie z bajtów prosto do XmlReadera.
        public string Text => _text ??= DecodeText(Bytes, charset);

        public string Head => DecodeText(Bytes[..Math.Min(Bytes.Length, 2048)], charset);
    }
}
