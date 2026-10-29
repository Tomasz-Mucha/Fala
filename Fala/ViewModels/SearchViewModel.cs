using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fala.Helpers;
using Fala.Localization;
using Fala.Models;
using Fala.Services;
using Microsoft.UI.Dispatching;

namespace Fala.ViewModels;

public partial class SearchViewModel : ObservableObject
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private HashSet<string> _subscribed = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _searchCts;

    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = Loc.Current.Get("SearchHint");
    [ObservableProperty] public partial bool QueryIsUrl { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsList))]
    public partial bool IsTiles { get; set; } = AppSettings.SearchTiles;

    public bool IsList => !IsTiles;
    public string PrimaryActionLabel => QueryIsUrl ? Loc.Current.SubscribeRssAction : Loc.Current.SearchAction;

    public IList<CatalogShow> Results { get; } = new System.Collections.ObjectModel.ObservableCollection<CatalogShow>();

    partial void OnQueryChanged(string value)
        => QueryIsUrl = RssService.LooksLikeUrl(value);

    partial void OnQueryIsUrlChanged(bool value)
        => OnPropertyChanged(nameof(PrimaryActionLabel));

    partial void OnIsTilesChanged(bool value)
    {
        AppSettings.SearchTiles = value;
        AppSettings.Save();
    }

    [RelayCommand]
    private void ShowTiles() => IsTiles = true;

    [RelayCommand]
    private void ShowList() => IsTiles = false;

    [RelayCommand]
    public async Task GoAsync()
    {
        if (QueryIsUrl || RssService.LooksLikeUrl(Query))
            await SubscribeUrlAsync(Query);
        else
            await SearchAsync();
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        // Nowe wyszukiwanie anuluje poprzednie — inaczej wyniki obu zapytań trafiały do jednej listy.
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var ct = cts.Token;

        IsBusy = true;
        await UiThread.RunAsync(_ui, () => Results.Clear());
        try
        {
            await RefreshSubscribedAsync();
            var list = await App.Services.Catalog.SearchAsync(Query, ct);
            if (ct.IsCancellationRequested)
                return;
            await UiThread.RunAsync(_ui, () =>
            {
                foreach (var s in list)
                {
                    s.IsSubscribed = IsSubscribed(s.FeedUrl);
                    Results.Add(s);
                }
                Status = Results.Count == 0 ? Loc.Current.Get("NoResults") : Loc.Current.Format("ResultCount", Results.Count);
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("SearchViewModel", ex);
            await UiThread.RunAsync(_ui, () => Status = Loc.Current.Format("SearchError", ex.Message));
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
                await UiThread.RunAsync(_ui, () => IsBusy = false);
            }
            cts.Dispose();
        }
    }

    [RelayCommand]
    public async Task SubscribeUrlAsync(string? url)
    {
        var target = string.IsNullOrWhiteSpace(url) ? Query : url;
        if (string.IsNullOrWhiteSpace(target))
        {
            Status = Loc.Current.Get("PasteRss");
            return;
        }

        IsBusy = true;
        await UiThread.RunAsync(_ui, () => Status = Loc.Current.Get("FetchingRss"));
        try
        {
            var result = await App.Services.Subscriptions.SubscribeAsync(target);
            await RefreshSubscribedAsync();
            await UiThread.RunAsync(_ui, () =>
            {
                Query = "";
                Status = FormatResult(result);
            });
            App.MainAppWindow.GoToLibrary(result.Podcast.Id);
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("SearchViewModel", ex);
            await UiThread.RunAsync(_ui, () => Status = Loc.Current.Format("NotAdded", ex.Message));
        }
        finally
        {
            await UiThread.RunAsync(_ui, () => IsBusy = false);
        }
    }

    [RelayCommand]
    public async Task SubscribeAsync(CatalogShow? show)
    {
        if (show is null) return;
        IsBusy = true;
        await UiThread.RunAsync(_ui, () => Status = Loc.Current.Format("Subscribing", show.Title));
        try
        {
            var result = await App.Services.Subscriptions.SubscribeAsync(show.FeedUrl);
            show.IsSubscribed = true;
            await RefreshSubscribedAsync();
            await UiThread.RunAsync(_ui, () => Status = FormatResult(result));
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("SearchViewModel", ex);
            await UiThread.RunAsync(_ui, () => Status = Loc.Current.Format("NotAdded", ex.Message));
        }
        finally
        {
            await UiThread.RunAsync(_ui, () => IsBusy = false);
        }
    }

    private async Task RefreshSubscribedAsync()
    {
        var shows = await App.Services.Database.GetPodcastsAsync();
        _subscribed = shows
            .Select(p => NormalizeKey(p.FeedUrl))
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private bool IsSubscribed(string feedUrl)
        => _subscribed.Contains(NormalizeKey(feedUrl));

    private static string NormalizeKey(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            return (u.Host + u.AbsolutePath).TrimEnd('/').ToLowerInvariant();
        return url.Trim().TrimEnd('/').ToLowerInvariant();
    }

    private static string FormatResult(SubscribeResult result)
    {
        var text = result.AlreadySubscribed
            ? Loc.Current.Format("UpdatedShow", result.Podcast.Title, result.EpisodeCount)
            : Loc.Current.Format("SubscribedShow", result.Podcast.Title, result.EpisodeCount);
        if (result.EpisodeCount == 0)
            text += Loc.Current.Get("NoAudioEpisodes");
        return text;
    }
}
