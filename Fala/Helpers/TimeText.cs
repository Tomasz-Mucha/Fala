using System.Net;
using System.Text.RegularExpressions;

namespace Fala.Helpers;

// Bez zależności od WinUI — kompilowane także w projekcie testów.
public static class TimeText
{
    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{t.Minutes}:{t.Seconds:D2}";
    }

    public static bool TryParse(string? raw, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        raw = raw.Trim();
        var parts = raw.Split(':');
        try
        {
            if (parts.Length == 1 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var sec))
            {
                value = TimeSpan.FromSeconds(sec);
                return true;
            }
            if (parts.Length == 2
                && int.TryParse(parts[0], out var m)
                && int.TryParse(parts[1], out var s))
            {
                value = new TimeSpan(0, m, s);
                return true;
            }
            if (parts.Length == 3
                && int.TryParse(parts[0], out var h)
                && int.TryParse(parts[1], out var mm)
                && int.TryParse(parts[2], out var ss))
            {
                value = new TimeSpan(h, mm, ss);
                return true;
            }
        }
        catch
        {
            return false;
        }
        return TimeSpan.TryParse(raw, out value);
    }

    // „12:34” albo „1:02:03”, nie fragment dłuższego ciągu cyfr/dwukropków (daty, numery).
    private static readonly Regex Timestamp = new(@"(?<![\d:])(?:\d{1,2}:)?\d{1,3}:[0-5]\d(?![\d:])", RegexOptions.Compiled);

    /// <summary>
    /// Dzieli tekst (np. opis odcinka) na fragmenty i znaczniki czasu, żeby znaczniki dało się kliknąć.
    /// Znaczniki dalsze niż <paramref name="duration"/> są traktowane jak zwykły tekst.
    /// </summary>
    public static IReadOnlyList<(string Text, TimeSpan? At)> SplitTimestamps(string text, TimeSpan? duration = null)
    {
        var parts = new List<(string, TimeSpan?)>();
        var last = 0;
        foreach (Match m in Timestamp.Matches(text))
        {
            if (!TryParse(m.Value, out var at))
                continue;
            if (duration is { } d && d > TimeSpan.Zero && at > d)
                continue;
            if (m.Index > last)
                parts.Add((text[last..m.Index], null));
            parts.Add((m.Value, at));
            last = m.Index + m.Length;
        }
        if (last < text.Length)
            parts.Add((text[last..], null));
        return parts;
    }

    public static string Remaining(TimeSpan position, TimeSpan? duration)
    {
        if (duration is not { } d || d <= TimeSpan.Zero)
            return Localization.Loc.Current.Get("RemainContinue");
        var left = d - position;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        if (left.TotalHours >= 1)
            return Localization.Loc.Current.Format("RemainHours", (int)left.TotalHours, left.Minutes);
        var mins = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
        return Localization.Loc.Current.Format("RemainMins", mins);
    }

    public static string RemainingClock(TimeSpan position, TimeSpan duration)
    {
        var left = duration - position;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        return "−" + Format(left);
    }
}

public static class PlainText
{
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex LineBreakTags = new(@"<\s*(br|/p|/li|/div|/h\d)\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t ]+", RegexOptions.Compiled);
    private static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    /// <summary>
    /// Notatki odcinka jako tekst z zachowanymi podziałami wierszy (listy rozdziałów ze znacznikami czasu
    /// muszą zostać czytelne), z wyższym limitem niż <see cref="Summary"/>.
    /// </summary>
    public static string Notes(string? html, int maxChars = 8000)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        var s = LineBreakTags.Replace(html, "\n");
        s = Tags.Replace(s, " ");
        s = WebUtility.HtmlDecode(s).Replace("\r\n", "\n").Replace('\r', '\n');
        s = Spaces.Replace(s, " ");
        s = string.Join("\n", s.Split('\n').Select(line => line.Trim()));
        s = BlankLines.Replace(s, "\n\n").Trim();
        if (s.Length > maxChars)
            s = s[..maxChars].TrimEnd() + "…";
        return s;
    }

    public static string Summary(string? html, int maxChars = 1200)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        var s = WebUtility.HtmlDecode(Tags.Replace(html, " "));
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length > maxChars)
            s = s[..maxChars].TrimEnd() + "…";
        return s;
    }
}
