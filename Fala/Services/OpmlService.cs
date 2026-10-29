using System.Xml;
using System.Xml.Linq;
using Fala.Models;

namespace Fala.Services;

/// <summary>Import i eksport subskrypcji w formacie OPML (standard wymiany między aplikacjami podcastowymi).</summary>
public static class OpmlService
{
    /// <summary>Zwraca adresy kanałów z pliku OPML (bez duplikatów, w kolejności z pliku).</summary>
    public static IReadOnlyList<string> ParseFeedUrls(Stream stream)
        => ParseEntries(stream).Select(e => e.Url).ToList();

    /// <summary>
    /// Kanały z pliku OPML razem z folderami, w których leżały (import folderów jako grup Fali).
    /// Jeden wspólny folder obejmujący wszystkie kanały (np. „feeds” w eksporcie Pocket Casts)
    /// to tylko opakowanie — wtedy grupy są pomijane.
    /// </summary>
    public static IReadOnlyList<OpmlEntry> ParseEntries(Stream stream)
    {
        var doc = Load(stream);
        var byUrl = new Dictionary<string, (string Url, List<string> Groups)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var outline in doc.Descendants().Where(e => e.Name.LocalName == "outline"))
        {
            var url = Attr(outline, "xmlUrl") ?? Attr(outline, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;
            url = url.Trim();
            var key = url.TrimEnd('/');
            if (!byUrl.TryGetValue(key, out var entry))
            {
                entry = (url, []);
                byUrl[key] = entry;
                order.Add(key);
            }
            var folder = FolderName(outline);
            if (folder is not null && !entry.Groups.Contains(folder, StringComparer.CurrentCultureIgnoreCase))
                entry.Groups.Add(folder);
        }

        var entries = order.Select(k => byUrl[k]).ToList();
        var folders = entries.SelectMany(e => e.Groups).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var singleWrapper = folders.Count == 1 && entries.All(e => e.Groups.Count == 1);
        return entries
            .Select(e => new OpmlEntry(e.Url, singleWrapper ? [] : e.Groups))
            .ToList();
    }

    private static string? FolderName(XElement outline)
    {
        var parent = outline.Parent;
        if (parent is null || parent.Name.LocalName != "outline")
            return null;
        if (Attr(parent, "xmlUrl") is not null)
            return null;
        var name = (Attr(parent, "text") ?? Attr(parent, "title"))?.Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static XDocument Load(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true
        };
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException(Localization.Loc.Current.Format("OpmlInvalid", ex.Message));
        }
    }

    public static string Build(IEnumerable<Podcast> podcasts)
    {
        var body = new XElement("body");
        foreach (var p in podcasts)
        {
            var outline = new XElement("outline",
                new XAttribute("type", "rss"),
                new XAttribute("text", p.Title),
                new XAttribute("title", p.Title),
                new XAttribute("xmlUrl", p.FeedUrl));
            if (!string.IsNullOrWhiteSpace(p.ArtworkUrl))
                outline.Add(new XAttribute("imageUrl", p.ArtworkUrl));
            body.Add(outline);
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("opml",
                new XAttribute("version", "2.0"),
                new XElement("head",
                    new XElement("title", "Fala"),
                    new XElement("dateCreated", DateTimeOffset.UtcNow.ToString("r"))),
                body));
        return doc.Declaration + Environment.NewLine + doc;
    }

    // Nazwy atrybutów w plikach z różnych aplikacji mają różną wielkość liter (xmlUrl / xmlurl).
    private static string? Attr(XElement el, string name)
        => el.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}

/// <param name="Groups">Foldery OPML, w których leżał kanał (puste = bez grupy).</param>
public sealed record OpmlEntry(string Url, IReadOnlyList<string> Groups);
