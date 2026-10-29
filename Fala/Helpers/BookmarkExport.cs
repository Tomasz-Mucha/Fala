using System.Globalization;
using System.Text;
using Fala.Localization;
using Fala.Models;

namespace Fala.Helpers;

/// <summary>Eksport zakładek do Markdown: podcast → odcinek → zakładki według pozycji.</summary>
public static class BookmarkExport
{
    public static string ToMarkdown(IEnumerable<Bookmark> bookmarks, DateTimeOffset exportedAt)
    {
        var L = Loc.Current;
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(L.Get("BookmarksExportTitle"));
        sb.AppendLine();
        sb.Append('_').Append(L.Format("BookmarksExportedAt", exportedAt.ToLocalTime().ToString("g", L.Culture))).AppendLine("_");

        var byShow = bookmarks
            .GroupBy(b => Fallback(b.ShowTitle))
            .OrderBy(g => g.Key, StringComparer.Create(L.Culture, ignoreCase: true));
        foreach (var show in byShow)
        {
            sb.AppendLine().Append("## ").AppendLine(show.Key);
            foreach (var episode in show.GroupBy(b => Fallback(b.EpisodeTitle)).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.AppendLine().Append("### ").AppendLine(episode.Key);
                sb.AppendLine();
                foreach (var b in episode.OrderBy(b => b.Position))
                {
                    sb.Append("- **").Append(TimeText.Format(b.Position)).Append("** — ").AppendLine(OneLine(b.Title));
                    if (!string.IsNullOrWhiteSpace(b.Comment))
                    {
                        foreach (var line in b.Comment.Trim().Split('\n'))
                            sb.Append("  > ").AppendLine(line.TrimEnd('\r'));
                    }
                }
            }
        }
        return sb.ToString();

        static string Fallback(string s) => string.IsNullOrWhiteSpace(s) ? "—" : OneLine(s);
        static string OneLine(string s) => s.ReplaceLineEndings(" ").Trim();
    }
}
