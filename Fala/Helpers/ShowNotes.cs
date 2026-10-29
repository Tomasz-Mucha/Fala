using System.Text.RegularExpressions;

namespace Fala.Helpers;

// Bez zależności od WinUI — kompilowane także w projekcie testów.
public static class ShowNotes
{
    private static readonly Regex Url = new(@"https?://[^\s<>""']+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Dzieli tekst notatek na zwykły tekst i adresy stron (do wyświetlenia jako linki).
    /// Końcowa interpunkcja („strona.pl/x.”) nie jest częścią adresu.
    /// </summary>
    public static IReadOnlyList<(string Text, Uri? Link)> SplitLinks(string text)
    {
        var parts = new List<(string, Uri?)>();
        var last = 0;
        foreach (Match m in Url.Matches(text))
        {
            var value = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '…');
            // Nawias zamykający należy do adresu, jeśli adres ma też otwierający (np. Wikipedia).
            if (m.Value.Length > value.Length && m.Value[value.Length] == ')' && value.Contains('('))
                value += ")";
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                continue;
            if (m.Index > last)
                parts.Add((text[last..m.Index], null));
            parts.Add((value, uri));
            last = m.Index + value.Length;
        }
        if (last < text.Length)
            parts.Add((text[last..], null));
        return parts;
    }
}
