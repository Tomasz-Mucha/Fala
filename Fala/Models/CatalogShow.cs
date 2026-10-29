using CommunityToolkit.Mvvm.ComponentModel;

namespace Fala.Models;

public partial class CatalogShow : ObservableObject
{
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string FeedUrl { get; set; } = "";
    public string ArtworkUrl { get; set; } = "";
    public string Description { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionLabel))]
    public partial bool IsSubscribed { get; set; }

    public string ActionLabel => IsSubscribed
        ? Localization.Loc.Current.Refresh
        : Localization.Loc.Current.Subscribe;
}
