using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fala.Helpers;
using Fala.Localization;
using Fala.Models;
using Microsoft.UI.Dispatching;

namespace Fala.ViewModels;

public partial class HomeItem
{
    public required Episode Episode { get; init; }
    public required Podcast Podcast { get; init; }
    public string ProgressText { get; init; } = "";
}

public partial class HomeViewModel : ObservableObject
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = Loc.Current.Get("Loading");
    [ObservableProperty] public partial bool HasContinue { get; set; }
    [ObservableProperty] public partial bool HasShows { get; set; }
    [ObservableProperty] public partial string FeedUrl { get; set; } = "";

    public IList<HomeItem> ContinueItems { get; } = new System.Collections.ObjectModel.ObservableCollection<HomeItem>();
    public IList<Podcast> Shows { get; } = new System.Collections.ObjectModel.ObservableCollection<Podcast>();
    public IList<HomeItem> RecentItems { get; } = new System.Collections.ObjectModel.ObservableCollection<HomeItem>();

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var shows = await App.Services.Database.GetPodcastsAsync();
            var cont = await App.Services.Database.GetContinueListeningAsync();
            var recent = await App.Services.Database.GetRecentEpisodesAsync();
            await UiThread.RunAsync(_ui, () =>
            {
                Shows.Clear();
                foreach (var s in shows)
                    Shows.Add(s);
                HasShows = Shows.Count > 0;

                ContinueItems.Clear();
                foreach (var (ep, pod) in cont)
                {
                    ContinueItems.Add(new HomeItem
                    {
                        Episode = ep,
                        Podcast = pod,
                        ProgressText = TimeText.Remaining(ep.Position, ep.Duration)
                    });
                }
                HasContinue = ContinueItems.Count > 0;

                RecentItems.Clear();
                foreach (var (ep, pod) in recent)
                    RecentItems.Add(new HomeItem { Episode = ep, Podcast = pod });

                Status = Shows.Count == 0
                    ? Loc.Current.Get("HomeEmpty")
                    : Loc.Current.Format("HomeSummary", Shows.Count, RecentItems.Count);
            });
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("HomeViewModel", ex);
            await UiThread.RunAsync(_ui, () => Status = Loc.Current.Format("ErrorPrefix", ex.Message));
        }
        finally
        {
            await UiThread.RunAsync(_ui, () => IsBusy = false);
        }
    }

    [RelayCommand]
    public async Task RefreshAllAsync()
    {
        IsBusy = true;
        await UiThread.RunAsync(_ui, () => Status = Loc.Current.Get("Refreshing"));
        try
        {
            var summary = await App.Services.Refresh.RefreshAllAsync();
            await LoadAsync();
            await UiThread.RunAsync(_ui, () =>
            {
                var text = summary.Failed == 0
                    ? Loc.Current.Format("RefreshOk", summary.Ok)
                    : Loc.Current.Format("RefreshPartial", summary.Ok, summary.Failed);
                if (summary.TotalNew > 0)
                    text += Loc.Current.Format("RefreshNewSuffix", summary.TotalNew);
                Status = text;
            });
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("HomeViewModel", ex);
            await UiThread.RunAsync(_ui, () => Status = Loc.Current.Format("RefreshError", ex.Message));
        }
        finally
        {
            await UiThread.RunAsync(_ui, () => IsBusy = false);
        }
    }

    public void Play(HomeItem item)
        => App.Services.Player.Play(item.Episode, item.Podcast.Title, item.Podcast.ArtworkUrl);

    public void AddToQueue(HomeItem item)
        => App.Services.Queue.Add(item.Episode, item.Podcast.Title, item.Podcast.ArtworkUrl);

    public void EnqueueNext(HomeItem item)
        => App.Services.Queue.PlayNext(item.Episode, item.Podcast.Title, item.Podcast.ArtworkUrl);

    public async Task AddToHistoryAsync(HomeItem item)
    {
        await App.Services.Database.RecordHistoryAsync(item.Episode, item.Podcast.Title, item.Podcast.ArtworkUrl, manual: true);
        await App.Services.Database.SetPlayedAsync(item.Episode.Id, true);
        item.Episode.IsPlayed = true;
        Status = Loc.Current.Format("AddedToHistory", item.Episode.Title);
    }

    [RelayCommand]
    public async Task SubscribeFeedAsync()
    {
        if (string.IsNullOrWhiteSpace(FeedUrl))
        {
            Status = Loc.Current.Get("PasteRss");
            return;
        }

        IsBusy = true;
        await UiThread.RunAsync(_ui, () => Status = Loc.Current.Get("FetchingRss"));
        try
        {
            var result = await App.Services.Subscriptions.SubscribeAsync(FeedUrl.Trim());
            await UiThread.RunAsync(_ui, () => FeedUrl = "");
            await LoadAsync();
            await UiThread.RunAsync(_ui, () =>
            {
                Status = result.AlreadySubscribed
                    ? Loc.Current.Format("UpdatedShow", result.Podcast.Title, result.EpisodeCount)
                    : Loc.Current.Format("SubscribedShow", result.Podcast.Title, result.EpisodeCount);
            });
            App.MainAppWindow.GoToLibrary(result.Podcast.Id);
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("HomeViewModel", ex);
            await UiThread.RunAsync(_ui, () => Status = Loc.Current.Format("NotAdded", ex.Message));
        }
        finally
        {
            await UiThread.RunAsync(_ui, () => IsBusy = false);
        }
    }
}
