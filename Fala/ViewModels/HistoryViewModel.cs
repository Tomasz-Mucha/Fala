using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fala.Helpers;
using Fala.Localization;
using Fala.Models;
using Microsoft.UI.Dispatching;

namespace Fala.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private List<HistoryItem> _all = [];

    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool HasItems { get; set; }
    [ObservableProperty] public partial string Query { get; set; } = "";

    public ObservableCollection<HistoryItem> Items { get; } = new();
    public ObservableCollection<string> Suggestions { get; } = new();

    partial void OnQueryChanged(string value) => ApplyFilter();

    [RelayCommand]
    public async Task LoadAsync()
    {
        var rows = await App.Services.Database.GetHistoryAsync();
        await UiThread.RunAsync(_ui, () =>
        {
            _all = rows.ToList();
            ApplyFilter();
        });
    }

    public void ApplyFilter()
    {
        var filtered = HistorySearch.Filter(_all, Query);
        Items.Clear();
        foreach (var row in filtered)
            Items.Add(row);
        HasItems = Items.Count > 0;

        Suggestions.Clear();
        foreach (var s in HistorySearch.Suggest(_all, Query))
            Suggestions.Add(s);

        if (_all.Count == 0)
            Status = Loc.Current.Get("HistoryEmpty");
        else if (string.IsNullOrWhiteSpace(Query))
            Status = Loc.Current.Format("HistoryCount", _all.Count);
        else if (Items.Count == 0)
            Status = Loc.Current.Format("HistoryNoMatches", Query.Trim());
        else
            Status = Loc.Current.Format("HistoryFiltered", Items.Count, _all.Count);
    }

    [RelayCommand]
    private async Task PlayItemAsync(HistoryItem? item)
    {
        if (item is null || !item.CanPlay || item.EpisodeId is not long id)
            return;
        var episode = await App.Services.Database.GetEpisodeByIdAsync(id);
        if (episode is null)
        {
            episode = new Episode
            {
                Id = id,
                PodcastId = item.PodcastId ?? 0,
                Title = item.EpisodeTitle,
                AudioUrl = item.AudioUrl ?? "",
                LocalPath = item.LocalPath
            };
        }
        App.Services.Player.Play(episode, item.ShowTitle, item.ArtworkUrl);
    }

    [RelayCommand]
    private async Task RemoveItemAsync(HistoryItem? item)
    {
        if (item is null) return;
        await App.Services.Database.DeleteHistoryAsync(item.Id);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        await App.Services.Database.ClearHistoryAsync();
        await LoadAsync();
    }
}
