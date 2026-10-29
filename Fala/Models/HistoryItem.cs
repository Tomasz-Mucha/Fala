namespace Fala.Models;

public sealed class HistoryItem
{
    public long Id { get; set; }
    public long? EpisodeId { get; set; }
    public long? PodcastId { get; set; }
    public string ShowTitle { get; set; } = "";
    public string EpisodeTitle { get; set; } = "";
    public string ArtworkUrl { get; set; } = "";
    public DateTimeOffset HeardUtc { get; set; }
    public bool IsManual { get; set; }
    public string? AudioUrl { get; set; }
    public string? LocalPath { get; set; }

    public bool CanPlay =>
        EpisodeId is > 0 &&
        (!string.IsNullOrWhiteSpace(LocalPath) || !string.IsNullOrWhiteSpace(AudioUrl));

    public string WhenText => HeardUtc.ToLocalTime().ToString("g");
}
