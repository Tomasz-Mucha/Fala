using CommunityToolkit.Mvvm.ComponentModel;

namespace Fala.Models;

public partial class Podcast : ObservableObject
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string FeedUrl { get; set; } = "";
    public string ArtworkUrl { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Liczba nowych odcinków, których użytkownik jeszcze nie widział w Bibliotece.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNew))]
    public partial int NewCount { get; set; }

    public bool HasNew => NewCount > 0;
}
