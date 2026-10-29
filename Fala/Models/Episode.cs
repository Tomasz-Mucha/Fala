using CommunityToolkit.Mvvm.ComponentModel;

namespace Fala.Models;

public partial class Episode : ObservableObject
{
    public long Id { get; set; }
    public long PodcastId { get; set; }
    public string Guid { get; set; } = "";
    public string Title { get; set; } = "";
    public string AudioUrl { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset? PublishedUtc { get; set; }
    public TimeSpan? Duration { get; set; }
    public TimeSpan Position { get; set; }
    public bool IsPlayed { get; set; }
    /// <summary>Dodany przy ostatnim odświeżeniu kanału, zanim użytkownik otworzył podcast.</summary>
    public bool IsNew { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloaded))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadButton))]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    public partial string? LocalPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloadButton))]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty] public partial double DownloadPercent { get; set; }

    public bool IsDownloaded => !string.IsNullOrWhiteSpace(LocalPath);
    public bool ShowDownloadButton => !IsDownloaded && !IsDownloading;
    public double TitleOpacity => IsPlayed ? 0.55 : 1.0;

    public double ProgressPercent
    {
        get
        {
            if (IsPlayed) return 100;
            if (Duration is not { } d || d.TotalSeconds <= 0)
                return Position.TotalSeconds > 0 ? 8 : 0;
            return Math.Clamp(100.0 * Position.TotalSeconds / d.TotalSeconds, 0, 100);
        }
    }

    public bool ShowProgress => IsPlayed || Position.TotalSeconds >= 2;

    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            if (IsNew)
                parts.Add(Localization.Loc.Current.Get("NewBadge"));
            if (PublishedUtc is { } p)
                parts.Add(p.ToLocalTime().ToString("d"));
            if (Duration is { } d)
                parts.Add(Helpers.TimeText.Format(d));
            if (IsDownloading)
                parts.Add(Localization.Loc.Current.Format("DownloadingPct", DownloadPercent.ToString("0")));
            else if (IsDownloaded)
                parts.Add(Localization.Loc.Current.Get("OnDisk"));
            if (IsPlayed)
                parts.Add(Localization.Loc.Current.Get("Played"));
            return string.Join(" · ", parts);
        }
    }

    partial void OnDownloadPercentChanged(double value)
        => OnPropertyChanged(nameof(MetaText));
}
