using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fala.Helpers;
using Fala.Localization;
using Fala.Models;
using Fala.Services;
using Microsoft.UI.Dispatching;

namespace Fala.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial string FeedUrl { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedArtwork))]
    [NotifyPropertyChangedFor(nameof(SelectedTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedAuthor))]
    public partial Podcast? Selected { get; set; }
    [ObservableProperty] public partial bool IsGroupView { get; set; }
    [ObservableProperty] public partial string PageTitle { get; set; } = Loc.Current.LibraryTitle;
    public bool HasSelection => Selected is not null;
    public string SelectedArtwork => Selected?.ArtworkUrl ?? "";
    public string SelectedTitle => Selected?.Title ?? "";
    public string SelectedAuthor => Selected?.Author ?? "";
    public long? GroupId { get; private set; }
    private CancellationTokenSource? _downloadCts;
    private long? _episodesFor;
    private List<Episode> _allEpisodes = [];

    // 0 = wszystkie, 1 = nieodsłuchane, 2 = pobrane
    [ObservableProperty] public partial int EpisodeFilter { get; set; } = AppSettings.EpisodeFilter;
    // 0 = najnowsze najpierw, 1 = najstarsze najpierw
    [ObservableProperty] public partial int EpisodeSort { get; set; } = AppSettings.EpisodeSort;
    [ObservableProperty] public partial bool IsFilteredEmpty { get; set; }

    public IList<Podcast> Podcasts { get; } = new System.Collections.ObjectModel.ObservableCollection<Podcast>();
    /// <summary>Odcinki widoczne na liście (po filtrze i sortowaniu).</summary>
    public IList<Episode> Episodes { get; } = new System.Collections.ObjectModel.ObservableCollection<Episode>();
    /// <summary>Wszystkie odcinki wybranego podcastu, niezależnie od filtra.</summary>
    public IReadOnlyList<Episode> AllEpisodes => _allEpisodes;

    partial void OnEpisodeFilterChanged(int value)
    {
        AppSettings.EpisodeFilter = value;
        AppSettings.Save();
        ApplyEpisodeView();
    }

    partial void OnEpisodeSortChanged(int value)
    {
        AppSettings.EpisodeSort = value;
        AppSettings.Save();
        ApplyEpisodeView();
    }

    private void ApplyEpisodeView()
    {
        IEnumerable<Episode> view = EpisodeFilter switch
        {
            1 => _allEpisodes.Where(e => !e.IsPlayed),
            2 => _allEpisodes.Where(e => e.IsDownloaded),
            _ => _allEpisodes
        };
        // Baza zwraca od najnowszych.
        if (EpisodeSort == 1)
            view = view.Reverse();

        Episodes.Clear();
        foreach (var e in view)
            Episodes.Add(e);
        IsFilteredEmpty = Episodes.Count == 0 && _allEpisodes.Count > 0;
    }

    public async Task LoadForAsync(long? groupId)
    {
        GroupId = groupId;
        IsGroupView = groupId is not null;
        await LoadAsync();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IReadOnlyList<Podcast> list;
        string title;
        if (GroupId is long gid)
        {
            var group = await App.Services.Database.GetGroupAsync(gid);
            title = group?.Name ?? Loc.Current.LibraryTitle;
            list = await App.Services.Database.GetGroupPodcastsAsync(gid);
        }
        else
        {
            title = Loc.Current.LibraryTitle;
            list = await App.Services.Database.GetPodcastsAsync();
        }

        await UiThread.RunAsync(_ui, () =>
        {
            PageTitle = title;
            Podcasts.Clear();
            foreach (var p in list)
                Podcasts.Add(p);
            Status = Podcasts.Count == 0
                ? (IsGroupView ? Loc.Current.Get("GroupEmpty") : Loc.Current.Get("LibraryEmpty"))
                : Loc.Current.Format("PodcastCount", Podcasts.Count);
        });
    }

    [RelayCommand]
    public async Task AddFeedAsync()
    {
        if (string.IsNullOrWhiteSpace(FeedUrl))
        {
            Status = Loc.Current.Get("PasteRssShort");
            return;
        }

        IsBusy = true;
        Status = Loc.Current.Get("FetchingRss");
        try
        {
            var result = await App.Services.Subscriptions.SubscribeAsync(FeedUrl.Trim());
            await UiThread.RunAsync(_ui, () =>
            {
                FeedUrl = "";
                Status = result.AlreadySubscribed
                    ? Loc.Current.Format("UpdatedShow", result.Podcast.Title, result.EpisodeCount)
                    : Loc.Current.Format("SubscribedShow", result.Podcast.Title, result.EpisodeCount);
            });
            if (GroupId is long gid)
                await App.Services.Database.AddPodcastToGroupAsync(gid, result.Podcast.Id);
            await LoadAsync();
            await SelectByIdAsync(result.Podcast.Id);
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("LibraryViewModel", ex);
            await UiThread.RunAsync(_ui, () => Status = Loc.Current.Format("AddFailed", ex.Message));
        }
        finally
        {
            await UiThread.RunAsync(_ui, () => IsBusy = false);
        }
    }

    [RelayCommand]
    public async Task RefreshSelectedAsync()
    {
        if (Selected is null) return;
        IsBusy = true;
        try
        {
            var result = await App.Services.Subscriptions.SubscribeAsync(Selected.FeedUrl);
            await UiThread.RunAsync(_ui, () => Status = Loc.Current.Format("RefreshedShow", result.Podcast.Title, result.EpisodeCount));
            await SelectByIdAsync(result.Podcast.Id);
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("LibraryViewModel", ex);
            await UiThread.RunAsync(_ui, () => Status = Loc.Current.Format("RefreshError", ex.Message));
        }
        finally
        {
            await UiThread.RunAsync(_ui, () => IsBusy = false);
        }
    }

    [RelayCommand]
    public async Task RemoveSelectedAsync()
    {
        if (Selected is null) return;
        var id = Selected.Id;
        App.Services.Downloads.DeletePodcastFiles(id);
        await App.Services.Database.DeletePodcastAsync(id);
        await UiThread.RunAsync(_ui, ClearSelection);
        await LoadAsync();
    }

    public async Task SelectAsync(Podcast podcast)
    {
        var episodes = await App.Services.Database.GetEpisodesAsync(podcast.Id);
        await UiThread.RunAsync(_ui, () =>
        {
            // Ustawiane przed Selected: zmiana Selected wywołuje SelectionChanged w GridView,
            // które dzięki temu nie ładuje tych samych odcinków drugi raz.
            _episodesFor = podcast.Id;
            Selected = podcast;
            _allEpisodes = episodes.ToList();
            ApplyEpisodeView();
        });

        // Otwarcie podcastu = nowe odcinki zobaczone. Znacznik „Nowy” zostaje na liście do następnego
        // załadowania, żeby było widać, które to były.
        if (podcast.NewCount > 0 || episodes.Any(e => e.IsNew))
        {
            await App.Services.Database.ClearNewAsync(podcast.Id);
            await UiThread.RunAsync(_ui, () => podcast.NewCount = 0);
            App.Services.Refresh.NotifyNewEpisodesChanged();
        }
    }

    /// <summary>Aktualizuje liczniki nowych odcinków na kafelkach bez przeładowania listy.</summary>
    public async Task RefreshNewCountsAsync()
    {
        var fresh = GroupId is long gid
            ? await App.Services.Database.GetGroupPodcastsAsync(gid)
            : await App.Services.Database.GetPodcastsAsync();
        var counts = fresh.ToDictionary(p => p.Id, p => p.NewCount);
        await UiThread.RunAsync(_ui, () =>
        {
            foreach (var p in Podcasts)
                p.NewCount = counts.GetValueOrDefault(p.Id);
        });
    }

    [RelayCommand]
    private async Task MarkAllPlayedAsync() => await SetAllPlayedAsync(true);

    [RelayCommand]
    private async Task MarkAllUnplayedAsync() => await SetAllPlayedAsync(false);

    private async Task SetAllPlayedAsync(bool played)
    {
        if (Selected is not { } podcast) return;
        await App.Services.Database.SetAllPlayedAsync(podcast.Id, played);
        await SelectAsync(podcast);
        Status = Loc.Current.Get(played ? "MarkedAllPlayed" : "MarkedAllUnplayed");
    }

    public bool IsShowingEpisodesOf(Podcast podcast) => _episodesFor == podcast.Id;

    partial void OnSelectedChanged(Podcast? value)
    {
        if (value is null)
            _episodesFor = null;
    }

    private void ClearSelection()
    {
        _episodesFor = null;
        Selected = null;
        _allEpisodes = [];
        Episodes.Clear();
        IsFilteredEmpty = false;
    }

    public async Task SelectByIdAsync(long podcastId)
    {
        var match = Podcasts.FirstOrDefault(p => p.Id == podcastId);
        if (match is null)
        {
            await LoadAsync();
            match = Podcasts.FirstOrDefault(p => p.Id == podcastId);
        }
        if (match is not null)
            await SelectAsync(match);
    }

    public async Task AddPodcastsToGroupAsync(IEnumerable<long> podcastIds)
    {
        if (GroupId is not long gid) return;
        foreach (var id in podcastIds)
            await App.Services.Database.AddPodcastToGroupAsync(gid, id);
        await LoadAsync();
    }

    public async Task RemoveSelectedFromGroupAsync()
    {
        if (GroupId is not long gid || Selected is null) return;
        var id = Selected.Id;
        await App.Services.Database.RemovePodcastFromGroupAsync(gid, id);
        await UiThread.RunAsync(_ui, ClearSelection);
        await LoadAsync();
    }

    public async Task RemovePodcastFromCurrentGroupAsync(long podcastId)
    {
        if (GroupId is not long gid) return;
        await App.Services.Database.RemovePodcastFromGroupAsync(gid, podcastId);
        if (Selected?.Id == podcastId)
        {
            await UiThread.RunAsync(_ui, ClearSelection);
        }
        await LoadAsync();
    }

    public void Play(Episode episode)
    {
        if (Selected is null) return;
        App.Services.Player.Play(episode, Selected.Title, Selected.ArtworkUrl);
    }

    [RelayCommand]
    private void AddToQueue(Episode? episode)
    {
        if (episode is null || Selected is null) return;
        App.Services.Queue.Add(episode, Selected.Title, Selected.ArtworkUrl);
        Status = Loc.Current.Format("AddedToQueue", episode.Title);
    }

    [RelayCommand]
    private async Task AddToHistoryAsync(Episode? episode)
    {
        if (episode is null || Selected is null) return;
        await App.Services.Database.RecordHistoryAsync(episode, Selected.Title, Selected.ArtworkUrl, manual: true);
        await App.Services.Database.SetPlayedAsync(episode.Id, true);
        episode.IsPlayed = true;
        Status = Loc.Current.Format("AddedToHistory", episode.Title);
        if (Selected is not null)
            await SelectAsync(Selected);
    }

    [RelayCommand]
    private void EnqueueNext(Episode? episode)
    {
        if (episode is null || Selected is null) return;
        App.Services.Queue.PlayNext(episode, Selected.Title, Selected.ArtworkUrl);
        Status = Loc.Current.Format("QueuedNext", episode.Title);
    }

    [RelayCommand]
    private void PlayEpisode(Episode? episode)
    {
        if (episode is not null)
            Play(episode);
    }

    [RelayCommand]
    private async Task DownloadEpisodeAsync(Episode? episode)
    {
        if (episode is null) return;
        if (IsDownloading)
        {
            Status = Loc.Current.Get("BusyDownload");
            return;
        }

        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        try
        {
            await DownloadOneAsync(episode, _downloadCts.Token);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    public async Task DownloadSelectedPodcastAsync()
    {
        if (Selected is null) return;
        if (IsDownloading)
        {
            Status = Loc.Current.Get("BusyDownload");
            return;
        }
        var pending = _allEpisodes.Where(e => !e.IsDownloaded && !e.IsDownloading).ToList();
        if (pending.Count == 0)
        {
            Status = Loc.Current.Get("AllDownloaded");
            return;
        }

        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;
        IsDownloading = true;
        var ok = 0;
        try
        {
            for (var i = 0; i < pending.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await UiThread.RunAsync(_ui, () =>
                    Status = Loc.Current.Format("DownloadingN", i + 1, pending.Count, pending[i].Title));
                if (await DownloadOneAsync(pending[i], ct))
                    ok++;
            }
            await UiThread.RunAsync(_ui, () =>
                Status = Loc.Current.Format("SavedN", ok, pending.Count));
        }
        catch (OperationCanceledException)
        {
            await UiThread.RunAsync(_ui, () => Status = Loc.Current.Get("DownloadCanceled"));
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void CancelDownload()
    {
        _downloadCts?.Cancel();
        Status = Loc.Current.Get("StoppingDownload");
    }

    private async Task<bool> DownloadOneAsync(Episode episode, CancellationToken ct = default)
    {
        if (episode.IsDownloaded)
        {
            Status = Loc.Current.Get("AlreadyOnDisk");
            return true;
        }
        if (episode.IsDownloading)
            return false;

        episode.IsDownloading = true;
        episode.DownloadPercent = 0;
        try
        {
            // Progress<T> utworzony na wątku UI sam wraca na ten wątek.
            var progress = new Progress<double>(p => episode.DownloadPercent = p);
            var path = await App.Services.Downloads.DownloadAsync(episode, progress, ct);
            await App.Services.Database.SetLocalPathAsync(episode.Id, path);
            await UiThread.RunAsync(_ui, () =>
            {
                episode.LocalPath = path;
                Status = Loc.Current.Format("OnDiskTitle", episode.Title);
            });
            return true;
        }
        catch (OperationCanceledException)
        {
            await UiThread.RunAsync(_ui, () => Status = Loc.Current.Get("DownloadCanceled"));
            return false;
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("LibraryViewModel", ex);
            await UiThread.RunAsync(_ui, () => Status = Loc.Current.Format("DownloadFailed", ex.Message));
            return false;
        }
        finally
        {
            await UiThread.RunAsync(_ui, () =>
            {
                episode.IsDownloading = false;
                episode.DownloadPercent = episode.IsDownloaded ? 100 : 0;
            });
        }
    }

    [RelayCommand]
    private async Task DeleteDownloadAsync(Episode? episode)
    {
        if (episode is null) return;
        if (App.Services.Player.CurrentEpisode?.Id == episode.Id
            && !string.IsNullOrWhiteSpace(episode.LocalPath))
        {
            Status = Loc.Current.Get("StopBeforeDelete");
            return;
        }
        App.Services.Downloads.DeleteEpisodeFile(episode);
        await App.Services.Database.SetLocalPathAsync(episode.Id, null);
        await UiThread.RunAsync(_ui, () =>
        {
            episode.LocalPath = null;
            Status = Loc.Current.Get("DeletedLocal");
        });
    }

    [RelayCommand]
    private async Task TogglePlayedAsync(Episode? episode)
    {
        if (episode is null) return;
        var next = !episode.IsPlayed;
        await App.Services.Database.SetPlayedAsync(episode.Id, next);
        episode.IsPlayed = next;
        if (next && Selected is not null)
            await App.Services.Database.RecordHistoryAsync(episode, Selected.Title, Selected.ArtworkUrl, manual: true);
        if (Selected is not null)
            await SelectAsync(Selected);
    }
}
