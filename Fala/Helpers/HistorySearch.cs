using System.Globalization;
using System.Text;
using Fala.Models;

namespace Fala.Helpers;

public static class HistorySearch
{
    public static IReadOnlyList<HistoryItem> Filter(IEnumerable<HistoryItem> source, string? query)
    {
        var parsed = Parse(query);
        if (parsed.IsEmpty)
            return source.ToList();

        return source
            .Select(item => (item, score: Score(item, parsed)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.item.HeardUtc)
            .Select(x => x.item)
            .ToList();
    }

    public static IReadOnlyList<string> Suggest(IEnumerable<HistoryItem> source, string? query, int take = 8)
    {
        var q = Fold(query ?? "");
        var shows = source
            .Select(x => x.ShowTitle)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Where(s => q.Length == 0 || Fold(s).Contains(q))
            .Take(take)
            .ToList();

        if (q.Length == 0)
            return shows;

        var titles = source
            .Select(x => x.EpisodeTitle)
            .Where(s => Fold(s).Contains(q))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(0, take - shows.Count));

        return shows.Concat(titles).Take(take).ToList();
    }

    private static int Score(HistoryItem item, ParsedQuery q)
    {
        var title = Fold(item.EpisodeTitle);
        var show = Fold(item.ShowTitle);
        var when = item.HeardUtc.ToLocalTime();
        var score = 1;

        if (q.Manual is true && !item.IsManual) return 0;
        if (q.Manual is false && item.IsManual) return 0;
        if (q.From is { } from && when < from) return 0;
        if (q.To is { } to && when > to) return 0;

        if (q.Show is { Length: > 0 } showQ)
        {
            if (!show.Contains(showQ)) return 0;
            score += show.StartsWith(showQ, StringComparison.Ordinal) ? 40 : 20;
        }

        if (q.Title is { Length: > 0 } titleQ)
        {
            if (!title.Contains(titleQ)) return 0;
            score += title.StartsWith(titleQ, StringComparison.Ordinal) ? 50 : 25;
        }

        foreach (var token in q.Tokens)
        {
            var inTitle = title.Contains(token);
            var inShow = show.Contains(token);
            if (!inTitle && !inShow)
                return 0;
            if (inTitle && title.Equals(token, StringComparison.Ordinal)) score += 80;
            else if (inTitle && title.StartsWith(token, StringComparison.Ordinal)) score += 40;
            else if (inTitle) score += 20;
            if (inShow) score += 12;
        }

        return score;
    }

    private static ParsedQuery Parse(string? raw)
    {
        var result = new ParsedQuery();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (var piece in Split(raw))
        {
            var p = piece.Trim().Trim('"');
            if (p.Length == 0) continue;
            var fold = Fold(p);

            if (fold is "recznie" or "reczny" or "manual" or "manuell" or "wlasny" or "wlasne")
            {
                result.Manual = true;
                continue;
            }
            if (fold is "auto" or "odsluchane" or "played")
            {
                result.Manual = false;
                continue;
            }

            if (TryPrefix(p, "show:", out var show) || TryPrefix(p, "podcast:", out show)
                || TryPrefix(p, "podkast:", out show))
            {
                result.Show = Fold(show);
                continue;
            }
            if (TryPrefix(p, "title:", out var title) || TryPrefix(p, "odcinek:", out title)
                || TryPrefix(p, "episode:", out title) || TryPrefix(p, "avsnitt:", out title))
            {
                result.Title = Fold(title);
                continue;
            }

            if (TryDateKeyword(fold, out var from, out var to))
            {
                result.From = from;
                result.To = to;
                continue;
            }

            result.Tokens.Add(fold);
        }

        return result;
    }

    private static bool TryDateKeyword(string fold, out DateTimeOffset from, out DateTimeOffset to)
    {
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Date, now.Offset);
        from = default;
        to = default;

        if (fold is "dzisiaj" or "today" or "idag")
        {
            from = today;
            to = today.AddDays(1).AddTicks(-1);
            return true;
        }
        if (fold is "wczoraj" or "yesterday" or "igar" or "igår")
        {
            from = today.AddDays(-1);
            to = today.AddTicks(-1);
            return true;
        }
        if (fold is "tydzien" or "tydzień" or "week" or "vecka")
        {
            from = today.AddDays(-7);
            to = now;
            return true;
        }
        if (fold is "miesiac" or "miesiąc" or "month" or "manad" or "månad")
        {
            from = today.AddDays(-30);
            to = now;
            return true;
        }
        if (fold.Length == 4 && int.TryParse(fold, out var year) && year is >= 2000 and <= 2100)
        {
            from = new DateTimeOffset(year, 1, 1, 0, 0, 0, now.Offset);
            to = from.AddYears(1).AddTicks(-1);
            return true;
        }
        return false;
    }

    private static bool TryPrefix(string raw, string prefix, out string value)
    {
        value = "";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        value = raw[prefix.Length..].Trim().Trim('"');
        return value.Length > 0;
    }

    private static IEnumerable<string> Split(string raw)
    {
        var cur = new StringBuilder();
        var quote = false;
        foreach (var c in raw)
        {
            if (c == '"')
            {
                quote = !quote;
                continue;
            }
            if (!quote && char.IsWhiteSpace(c))
            {
                if (cur.Length > 0)
                {
                    yield return cur.ToString();
                    cur.Clear();
                }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0)
            yield return cur.ToString();
    }

    public static string Fold(string value)
    {
        var norm = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (var c in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            // „ł” nie ma rozkładu kanonicznego (FormD), więc trzeba je zamienić ręcznie.
            sb.Append(c is 'ł' or 'Ł' ? 'l' : char.ToLowerInvariant(c));
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private sealed class ParsedQuery
    {
        public List<string> Tokens { get; } = [];
        public string? Show { get; set; }
        public string? Title { get; set; }
        public bool? Manual { get; set; }
        public DateTimeOffset? From { get; set; }
        public DateTimeOffset? To { get; set; }
        public bool IsEmpty =>
            Tokens.Count == 0 && Show is null && Title is null && Manual is null && From is null && To is null;
    }
}
