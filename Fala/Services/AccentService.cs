using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Fala.Services;

public sealed class AccentChoice
{
    public required string Key { get; init; }
    public required Color Color { get; init; }
    public bool IsSystem { get; init; }
    public SolidColorBrush Brush => new(Color);
}

public static class AccentService
{
    public static IReadOnlyList<AccentChoice> GetChoices() => BuildChoices();

    public static void Apply()
    {
        var color = Resolve(AppSettings.Accent);
        var white = Color.FromArgb(255, 255, 255, 255);
        var black = Color.FromArgb(255, 0, 0, 0);
        var light1 = Mix(color, white, 0.25);
        var light2 = Mix(color, white, 0.45);
        var light3 = Mix(color, white, 0.65);
        var dark1 = Mix(color, black, 0.20);
        var dark2 = Mix(color, black, 0.40);
        var dark3 = Mix(color, black, 0.60);
        var darkUi = IsDark();
        var text = darkUi ? light2 : dark1;
        var textSecondary = darkUi ? light3 : dark2;
        var disabled = WithAlpha(text, 80);

        var seen = new HashSet<ResourceDictionary>(ReferenceEqualityComparer.Instance);
        void Visit(ResourceDictionary? dict)
        {
            if (dict is null || !seen.Add(dict))
                return;
            Write(dict, color, light1, light2, light3, dark1, dark2, dark3, text, textSecondary, disabled);
            foreach (var merged in dict.MergedDictionaries)
                Visit(merged);
            foreach (var theme in dict.ThemeDictionaries.Values)
            {
                if (theme is ResourceDictionary td)
                    Visit(td);
            }
        }

        Visit(Application.Current.Resources);
        if (App.MainAppWindow?.Content is FrameworkElement root)
            Visit(root.Resources);
    }

    private static void Write(
        ResourceDictionary d,
        Color color, Color light1, Color light2, Color light3,
        Color dark1, Color dark2, Color dark3,
        Color text, Color textSecondary, Color disabled)
    {
        SetColor(d, "SystemAccentColor", color);
        SetColor(d, "SystemAccentColorLight1", light1);
        SetColor(d, "SystemAccentColorLight2", light2);
        SetColor(d, "SystemAccentColorLight3", light3);
        SetColor(d, "SystemAccentColorDark1", dark1);
        SetColor(d, "SystemAccentColorDark2", dark2);
        SetColor(d, "SystemAccentColorDark3", dark3);
        SetColor(d, "AccentFillColorDefault", color);
        SetColor(d, "AccentFillColorSecondary", WithAlpha(color, 144));
        SetColor(d, "AccentFillColorTertiary", WithAlpha(color, 80));
        SetColor(d, "AccentTextFillColorPrimary", text);
        SetColor(d, "AccentTextFillColorSecondary", textSecondary);
        SetColor(d, "AccentTextFillColorDisabled", disabled);

        SetBrush(d, "AccentFillColorDefaultBrush", color);
        SetBrush(d, "AccentFillColorSecondaryBrush", WithAlpha(color, 144));
        SetBrush(d, "AccentFillColorTertiaryBrush", WithAlpha(color, 80));
        SetBrush(d, "AccentFillColorDisabledBrush", WithAlpha(color, 64));
        SetBrush(d, "AccentTextFillColorPrimaryBrush", text);
        SetBrush(d, "AccentTextFillColorSecondaryBrush", textSecondary);
        SetBrush(d, "AccentTextFillColorDisabledBrush", disabled);
        SetBrush(d, "SystemControlForegroundAccentBrush", color);
        SetBrush(d, "SystemControlHighlightAccentBrush", color);
        SetBrush(d, "SystemControlBackgroundAccentBrush", color);
        SetBrush(d, "SystemControlHighlightAltAccentBrush", color);
        SetBrush(d, "AccentButtonBackground", color);
        SetBrush(d, "AccentButtonBackgroundPointerOver", light1);
        SetBrush(d, "AccentButtonBackgroundPressed", dark1);
        SetBrush(d, "AccentButtonBackgroundDisabled", WithAlpha(color, 64));
    }

    public static Color Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key == "system")
            return SystemAccent();
        var match = GetChoices().FirstOrDefault(c => c.Key == key && !c.IsSystem);
        return match?.Color ?? SystemAccent();
    }

    public static Color SystemAccent()
    {
        try
        {
            return new UISettings().GetColorValue(UIColorType.Accent);
        }
        catch
        {
            return Color.FromArgb(255, 0, 120, 212);
        }
    }

    private static IReadOnlyList<AccentChoice> BuildChoices()
    {
        var system = SystemAccent();
        return
        [
            new AccentChoice { Key = "system", Color = system, IsSystem = true },
            new AccentChoice { Key = "blue", Color = Color.FromArgb(255, 0, 120, 212) },
            new AccentChoice { Key = "indigo", Color = Color.FromArgb(255, 107, 105, 214) },
            new AccentChoice { Key = "purple", Color = Color.FromArgb(255, 136, 23, 152) },
            new AccentChoice { Key = "pink", Color = Color.FromArgb(255, 227, 0, 140) },
            new AccentChoice { Key = "red", Color = Color.FromArgb(255, 232, 17, 35) },
            new AccentChoice { Key = "orange", Color = Color.FromArgb(255, 247, 99, 12) },
            new AccentChoice { Key = "gold", Color = Color.FromArgb(255, 255, 140, 0) },
            new AccentChoice { Key = "yellow", Color = Color.FromArgb(255, 255, 185, 0) },
            new AccentChoice { Key = "green", Color = Color.FromArgb(255, 16, 124, 16) },
            new AccentChoice { Key = "teal", Color = Color.FromArgb(255, 0, 183, 195) },
            new AccentChoice { Key = "slate", Color = Color.FromArgb(255, 81, 92, 107) },
        ];
    }

    private static bool IsDark()
    {
        try
        {
            if (App.MainAppWindow?.Content is FrameworkElement fe)
            {
                var theme = fe.ActualTheme;
                if (theme == ElementTheme.Dark) return true;
                if (theme == ElementTheme.Light) return false;
            }
        }
        catch
        {
        }
        return Application.Current.RequestedTheme == ApplicationTheme.Dark;
    }

    private static void SetColor(ResourceDictionary d, string key, Color color)
    {
        try { d[key] = color; } catch { }
    }

    private static void SetBrush(ResourceDictionary d, string key, Color color)
    {
        try
        {
            if (d.ContainsKey(key) && d[key] is SolidColorBrush brush)
            {
                brush.Color = color;
                return;
            }
            d[key] = new SolidColorBrush(color);
        }
        catch
        {
        }
    }

    private static Color Mix(Color a, Color b, double t)
    {
        byte Lerp(byte x, byte y) => (byte)Math.Clamp(x + (y - x) * t, 0, 255);
        return Color.FromArgb(255, Lerp(a.R, b.R), Lerp(a.G, b.G), Lerp(a.B, b.B));
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}
