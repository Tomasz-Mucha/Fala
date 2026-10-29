namespace Fala.Models;

public sealed class PodcastGroup
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
}

public sealed record LibraryNav(long? PodcastId = null, long? GroupId = null);
