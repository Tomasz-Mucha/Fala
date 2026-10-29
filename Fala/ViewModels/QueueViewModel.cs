using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fala.Helpers;
using Fala.Localization;
using Fala.Models;
using Fala.Services;
using Microsoft.UI.Dispatching;

namespace Fala.ViewModels;

public partial class QueueViewModel : ObservableObject
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();

    [ObservableProperty] public partial string NowTitle { get; set; } = Loc.Current.NothingPlaying;
    [ObservableProperty] public partial string NowShow { get; set; } = Loc.Current.PickEpisode;
    [ObservableProperty] public partial string? NowArtwork { get; set; }
    [ObservableProperty] public partial bool HasNowPlaying { get; set; }
    [ObservableProperty] public partial bool HasItems { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";

    public IList<QueueItem> Items => App.Services.Queue.Upcoming;

    public QueueViewModel()
    {
        if (App.Services.Player.CurrentEpisode is { } ep)
        {
            HasNowPlaying = true;
            NowTitle = ep.Title;
            NowShow = App.Services.Player.CurrentShow;
            NowArtwork = string.IsNullOrWhiteSpace(App.Services.Player.CurrentArtwork)
                ? null
                : App.Services.Player.CurrentArtwork;
        }
        RefreshStatus();
    }

    // Subskrypcje wiązane z czasem życia strony: wcześniej każda wizyta w Kolejce dopinała
    // nowe handlery do serwisów (żyjących cały czas działania aplikacji) i nigdy ich nie odpinała,
    // więc stare strony zostawały w pamięci i dalej przetwarzały każdy tick odtwarzacza.
    public void Attach()
    {
        App.Services.Player.StateChanged += Player_StateChanged;
        App.Services.Queue.Changed += Queue_Changed;
        RefreshStatus();
    }

    public void Detach()
    {
        App.Services.Player.StateChanged -= Player_StateChanged;
        App.Services.Queue.Changed -= Queue_Changed;
    }

    private void Player_StateChanged(object? sender, PlayerState e)
        => _ui.TryEnqueue(() => ApplyPlayer(e));

    private void Queue_Changed(object? sender, EventArgs e)
        => _ui.TryEnqueue(RefreshStatus);

    private void ApplyPlayer(PlayerState e)
    {
        HasNowPlaying = e.HasTrack;
        NowTitle = string.IsNullOrWhiteSpace(e.EpisodeTitle) ? Loc.Current.NothingPlaying : e.EpisodeTitle;
        NowShow = string.IsNullOrWhiteSpace(e.ShowTitle) ? Loc.Current.PickEpisode : e.ShowTitle;
        NowArtwork = string.IsNullOrWhiteSpace(e.ArtworkUrl) ? null : e.ArtworkUrl;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        HasItems = Items.Count > 0;
        Status = Items.Count == 0 ? "" : Loc.Current.Format("QueueCount", Items.Count);
    }

    [RelayCommand]
    private void PlayItem(QueueItem? item)
    {
        if (item is null) return;
        App.Services.Queue.Remove(item);
        App.Services.Player.Play(item.Episode, item.ShowTitle, item.ArtworkUrl);
        RefreshStatus();
    }

    [RelayCommand]
    private void RemoveItem(QueueItem? item)
    {
        if (item is null) return;
        App.Services.Queue.Remove(item);
        RefreshStatus();
    }

    [RelayCommand]
    private void MoveUp(QueueItem? item)
    {
        if (item is null) return;
        var i = IndexOf(item);
        if (i > 0)
            App.Services.Queue.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveDown(QueueItem? item)
    {
        if (item is null) return;
        var i = IndexOf(item);
        if (i >= 0 && i < Items.Count - 1)
            App.Services.Queue.Move(i, i + 1);
    }

    [RelayCommand]
    private void Clear()
    {
        App.Services.Queue.Clear();
        RefreshStatus();
    }

    [RelayCommand]
    private async Task PlayNextAsync()
        => await App.Services.Player.PlayNextInQueueAsync();

    private int IndexOf(QueueItem item)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (ReferenceEquals(Items[i], item) || Items[i].Id == item.Id)
                return i;
        }
        return -1;
    }
}
