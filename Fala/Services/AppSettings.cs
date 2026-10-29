using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace Fala.Services;

public static class AppSettings
{
    // Aplikacja działa bez pakietu MSIX (WindowsPackageType=None), a wtedy ApplicationData.Current
    // rzuca InvalidOperationException — ustawienia trzymamy więc w pliku JSON obok bazy.
    private static readonly string FilePath = Path.Combine(Helpers.AppPaths.DataDirectory, "settings.json");

    private static readonly object SaveLock = new();

    public static ElementTheme Theme { get; set; } = ElementTheme.Default;
    public static double Speed { get; set; } = 1.0;
    public static double Volume { get; set; } = 1.0;
    public static bool Muted { get; set; }
    public static string Language { get; set; } = "pl";
    public static string Accent { get; set; } = "system";
    public static bool SearchTiles { get; set; } = true;
    public static int AutoRefreshHours { get; set; } = 3;
    public static bool NewEpisodeNotifications { get; set; } = true;
    public static DateTimeOffset? LastRefreshUtc { get; set; }
    public static int EpisodeFilter { get; set; }
    public static int EpisodeSort { get; set; }

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(FilePath));
                if (data is not null)
                    Apply(data);
                return;
            }
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("Odczyt settings.json — używam ustawień domyślnych", ex);
            // uszkodzony plik — zostają wartości domyślne
            return;
        }

        LoadLegacy();
    }

    public static void Save()
    {
        var data = new SettingsData
        {
            Theme = Theme.ToString(),
            Speed = Speed,
            Volume = Volume,
            Muted = Muted,
            Language = Language,
            Accent = Accent,
            SearchTiles = SearchTiles,
            AutoRefreshHours = AutoRefreshHours,
            NewEpisodeNotifications = NewEpisodeNotifications,
            LastRefreshUtc = LastRefreshUtc,
            EpisodeFilter = EpisodeFilter,
            EpisodeSort = EpisodeSort
        };
        try
        {
            lock (SaveLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data));
                File.Move(tmp, FilePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("Zapis settings.json", ex);
        }
    }

    public static void Apply(Window window)
    {
        if (window.Content is FrameworkElement fe)
            fe.RequestedTheme = Theme;
        AccentService.Apply();
    }

    private static void Apply(SettingsData d)
    {
        if (Enum.TryParse<ElementTheme>(d.Theme, out var theme))
            Theme = theme;
        if (d.Speed is > 0 and <= 4)
            Speed = d.Speed.Value;
        if (d.Volume is { } vol)
            Volume = Math.Clamp(vol, 0, 1);
        Muted = d.Muted ?? false;
        if (d.Language is "pl" or "sv" or "en")
            Language = d.Language;
        if (!string.IsNullOrWhiteSpace(d.Accent))
            Accent = d.Accent;
        SearchTiles = d.SearchTiles ?? true;
        if (d.AutoRefreshHours is { } h && FeedRefreshService.IntervalChoices.Contains(h))
            AutoRefreshHours = h;
        NewEpisodeNotifications = d.NewEpisodeNotifications ?? true;
        LastRefreshUtc = d.LastRefreshUtc;
        EpisodeFilter = d.EpisodeFilter is >= 0 and <= 2 ? d.EpisodeFilter.Value : 0;
        EpisodeSort = d.EpisodeSort is 1 ? 1 : 0;
    }

    // Wersja spakowana (MSIX) trzymała ustawienia w LocalSettings — przenosimy je przy pierwszym starcie.
    private static void LoadLegacy()
    {
        try
        {
            var v = ApplicationData.Current.LocalSettings.Values;
            Apply(new SettingsData
            {
                Theme = v["Theme"] as string,
                Speed = v["Speed"] switch { double d => d, float f => f, int i => i, _ => null },
                Volume = v["Volume"] switch { double d => d, float f => f, int i => i / 100.0, _ => null },
                Muted = v["Muted"] as bool?,
                Language = v["Language"] as string,
                Accent = v["Accent"] as string,
                SearchTiles = v["SearchTiles"] as bool?
            });
            Save();
        }
        catch
        {
            // brak tożsamości pakietu — zostają wartości domyślne
        }
    }

    private sealed class SettingsData
    {
        public string? Theme { get; set; }
        public double? Speed { get; set; }
        public double? Volume { get; set; }
        public bool? Muted { get; set; }
        public string? Language { get; set; }
        public string? Accent { get; set; }
        public bool? SearchTiles { get; set; }
        public int? AutoRefreshHours { get; set; }
        public bool? NewEpisodeNotifications { get; set; }
        public DateTimeOffset? LastRefreshUtc { get; set; }
        public int? EpisodeFilter { get; set; }
        public int? EpisodeSort { get; set; }
    }
}
