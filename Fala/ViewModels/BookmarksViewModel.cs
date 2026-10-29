using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fala.Helpers;
using Fala.Localization;
using Fala.Models;
using Microsoft.UI.Dispatching;

namespace Fala.ViewModels;

public partial class BookmarksViewModel : ObservableObject
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();

    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool HasItems { get; set; }

    public ObservableCollection<Bookmark> Items { get; } = new();

    [RelayCommand]
    public async Task LoadAsync()
    {
        var rows = await App.Services.Database.GetBookmarksAsync();
        await UiThread.RunAsync(_ui, () =>
        {
            Items.Clear();
            foreach (var row in rows)
                Items.Add(row);
            HasItems = Items.Count > 0;
            Status = Items.Count == 0 ? "" : Loc.Current.Format("BookmarksCount", Items.Count);
        });
    }

    [RelayCommand]
    private async Task PlayItemAsync(Bookmark? item)
    {
        if (item is null) return;
        Episode? episode = null;
        if (item.EpisodeId is long id)
            episode = await App.Services.Database.GetEpisodeByIdAsync(id);
        if (episode is null)
        {
            Status = Loc.Current.Get("BookmarkMissingEpisode");
            return;
        }
        App.Services.Player.Play(episode, item.ShowTitle, item.ArtworkUrl, item.Position);
    }

    [RelayCommand]
    private async Task RemoveItemAsync(Bookmark? item)
    {
        if (item is null) return;
        await App.Services.Database.DeleteBookmarkAsync(item.Id);
        await LoadAsync();
    }
}
