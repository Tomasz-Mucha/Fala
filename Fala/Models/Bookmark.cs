using Fala.Helpers;

namespace Fala.Models;

public sealed class Bookmark
{
    public long Id { get; set; }
    public long? EpisodeId { get; set; }
    public long? PodcastId { get; set; }
    public string Title { get; set; } = "";
    public string Comment { get; set; } = "";
    public TimeSpan Position { get; set; }
    public string ShowTitle { get; set; } = "";
    public string EpisodeTitle { get; set; } = "";
    public string ArtworkUrl { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }

    public string PositionText => TimeText.Format(Position);
    public bool HasComment => !string.IsNullOrWhiteSpace(Comment);
}
