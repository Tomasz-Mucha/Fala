using Fala.Localization;
using Fala.Models;
using Fala.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Fala.Views;

public sealed partial class LibraryPage : Page
{
    public LibraryViewModel ViewModel { get; } = new();
    public Loc L => Loc.Current;

    public LibraryPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        switch (e.Parameter)
        {
            case LibraryNav { GroupId: var g, PodcastId: var p }:
                await ViewModel.LoadForAsync(g);
                if (p is long pid)
                    await ViewModel.SelectByIdAsync(pid);
                break;
            case long id:
                await ViewModel.LoadForAsync(null);
                await ViewModel.SelectByIdAsync(id);
                break;
            default:
                await ViewModel.LoadForAsync(null);
                break;
        }
    }

    private async void Podcasts_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListViewBase { SelectedItem: Podcast p } && !ViewModel.IsShowingEpisodesOf(p))
            await ViewModel.SelectAsync(p);
    }

    private void Episodes_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Episode ep)
            ViewModel.Play(ep);
    }

    private async void Feed_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
            await SubscribeFromFlyoutAsync();
    }

    private void AddPodcastFlyout_Opened(object sender, object e)
        => FeedUrlBox.Focus(FocusState.Programmatic);

    private async void SubscribeFromFlyout_Click(object sender, RoutedEventArgs e)
        => await SubscribeFromFlyoutAsync();

    private async Task SubscribeFromFlyoutAsync()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.FeedUrl))
            return;
        // Panel zamykamy od razu — postęp i wynik widać w nagłówku strony.
        AddPodcastFlyout.Hide();
        await ViewModel.AddFeedAsync();
    }

    private void SearchCatalog_Click(object sender, RoutedEventArgs e)
    {
        AddPodcastFlyout.Hide();
        App.MainAppWindow.GoToSearch();
    }

    private async void DownloadAll_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is null)
        {
            ViewModel.Status = Loc.Current.Get("SelectPodcast");
            return;
        }

        var title = ViewModel.Selected.Title;
        var pending = ViewModel.AllEpisodes.Count(ep => !ep.IsDownloaded);
        if (pending == 0)
        {
            ViewModel.Status = Loc.Current.Get("AllDownloaded");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = Loc.Current.Get("DownloadDialogTitle"),
            Content = Loc.Current.Format("DownloadDialogBody", pending, title),
            PrimaryButtonText = Loc.Current.Get("Download"),
            CloseButtonText = Loc.Current.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.DownloadSelectedPodcastAsync();
    }

    private void PodcastMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout)
            return;
        var podcast = FlyoutItem<Podcast>(flyout);
        flyout.Items.Clear();
        if (podcast is null)
            return;

        IReadOnlyList<PodcastGroup> groups;
        try
        {
            groups = App.Services.Database.GetGroupsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("Wczytanie grup do menu", ex);
            groups = [];
        }

        var add = new MenuFlyoutSubItem { Text = L.AddToGroup };
        if (groups.Count == 0)
        {
            add.Items.Add(new MenuFlyoutItem { Text = Loc.Current.Get("NoGroupsYet"), IsEnabled = false });
        }
        else
        {
            foreach (var group in groups)
            {
                var item = new MenuFlyoutItem
                {
                    Text = group.Name,
                    Tag = (podcast.Id, group.Id)
                };
                item.Click += AddToGroup_Click;
                add.Items.Add(item);
            }
        }
        flyout.Items.Add(add);

        if (ViewModel.IsGroupView)
        {
            var remove = new MenuFlyoutItem { Text = L.RemoveFromGroup, Tag = podcast.Id };
            remove.Click += RemovePodcastFromGroup_Click;
            flyout.Items.Add(remove);
        }
    }

    private void EpisodeMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout)
            return;
        var episode = FlyoutItem<Episode>(flyout);
        flyout.Items.Clear();
        if (episode is null)
            return;

        AddMenu(flyout, L.Play, () => ViewModel.PlayEpisodeCommand.Execute(episode));
        AddMenu(flyout, L.PlayNext, () => ViewModel.EnqueueNextCommand.Execute(episode));
        AddMenu(flyout, L.AddToQueue, () => ViewModel.AddToQueueCommand.Execute(episode));
        AddMenu(flyout, L.AddToHistory, () => ViewModel.AddToHistoryCommand.Execute(episode));
        AddMenu(flyout, L.AddBookmark, () => _ = AddBookmarkForEpisodeAsync(episode));
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenu(flyout, L.DownloadToDisk, () => ViewModel.DownloadEpisodeCommand.Execute(episode));
        AddMenu(flyout, L.DeleteDownload, () => ViewModel.DeleteDownloadCommand.Execute(episode));
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenu(flyout, L.TogglePlayed, () => ViewModel.TogglePlayedCommand.Execute(episode));
    }

    private async Task AddBookmarkForEpisodeAsync(Episode episode)
    {
        var draft = await BookmarkDialog.ShowAsync(
            XamlRoot,
            episode: episode,
            podcast: ViewModel.Selected,
            position: episode.Position);
        if (draft is null) return;
        await App.Services.Database.AddBookmarkAsync(draft);
    }

    private static void AddMenu(MenuFlyout flyout, string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        flyout.Items.Add(item);
    }

    private static T? FlyoutItem<T>(MenuFlyout flyout) where T : class
    {
        if (flyout.Target is not FrameworkElement fe)
            return null;
        if (fe.Tag is T tagged)
            return tagged;
        return fe.DataContext as T;
    }

    private async void AddToGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: (long podcastId, long groupId) })
            return;
        await App.Services.Database.AddPodcastToGroupAsync(groupId, podcastId);
        if (ViewModel.IsGroupView && ViewModel.GroupId == groupId)
            await ViewModel.LoadAsync();
    }

    private async void RemovePodcastFromGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: long id })
            await ViewModel.RemovePodcastFromCurrentGroupAsync(id);
    }

    private async void RemoveFromGroup_Click(object sender, RoutedEventArgs e)
        => await ViewModel.RemoveSelectedFromGroupAsync();

    private async void AddPodcasts_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.GroupId is not long gid)
            return;

        var all = await App.Services.Database.GetPodcastsAsync();
        var inGroup = ViewModel.Podcasts.Select(p => p.Id).ToHashSet();
        var candidates = all.Where(p => !inGroup.Contains(p.Id)).ToList();
        if (candidates.Count == 0)
        {
            ViewModel.Status = Loc.Current.Get("AllInGroup");
            return;
        }

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            ItemsSource = candidates,
            Height = 320,
            DisplayMemberPath = "Title"
        };

        var dialog = new ContentDialog
        {
            Title = Loc.Current.Get("PickPodcasts"),
            Content = list,
            PrimaryButtonText = L.AddPodcasts,
            CloseButtonText = L.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var ids = list.SelectedItems.OfType<Podcast>().Select(p => p.Id).ToList();
        if (ids.Count > 0)
            await ViewModel.AddPodcastsToGroupAsync(ids);
    }
}
