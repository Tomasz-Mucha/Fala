using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Fala.Helpers;
using Fala.Localization;
using Fala.Models;
using Fala.Services;
using Fala.Views;
using Windows.System;
using Windows.UI.Core;

namespace Fala;

public sealed partial class MainWindow : Window
{
    private bool _scrubbing;
    private bool _syncingSlider;
    private string _shownGuid = "";
    private object? _navParam;
    private TimeSpan _duration;
    private Type? _currentPage = typeof(HomePage);
    private long? _openGroupId;
    private object? _lastNavItem;
    private const int FixedMenuCount = 6;
    private static readonly double[] Speeds = [0.75, 1.0, 1.25, 1.5, 2.0];
    // ComboBox/Slider potrafią wywołać SelectionChanged/ValueChanged już w InitializeComponent
    // (wartości z XAML) — bez tej flagi nadpisywały zapisane tempo i głośność domyślnymi.
    private bool _initializing = true;

    public Loc L => Loc.Current;

    public MainWindow()
    {
        InitializeComponent();
        NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        NavView.IsPaneOpen = true;
        TryEnableMica();
        TryEnableCustomTitleBar();
        RestoreSpeed();
        RestoreVolume();
        _initializing = false;
        ContentFrame.Navigate(typeof(HomePage));
        NavView.SelectedItem = NavView.MenuItems[0];
        _lastNavItem = NavView.MenuItems[0];
        App.Services.Player.StateChanged += Player_StateChanged;
        App.Services.Queue.Changed += (_, _) =>
            DispatcherQueue.TryEnqueue(UpdateNextQueueButton);
        App.Services.Refresh.NewEpisodesChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(async () => await UpdateNewEpisodesBadgeAsync());
        App.Services.Refresh.AutoRefreshCompleted += (_, _) =>
            DispatcherQueue.TryEnqueue(async () => await RefreshVisiblePageAsync());
        UpdateNextQueueButton();
        Closed += (_, _) => App.Services.Player.Dispose();
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
                EnsureKeyboardFocus();
        };
        if (Content is FrameworkElement root)
        {
            root.Loaded += async (_, _) =>
            {
                await ReloadGroupsAsync();
                await UpdateNewEpisodesBadgeAsync();
                EnsureKeyboardFocus();
            };
            root.PreviewKeyDown += Root_PreviewKeyDown;
        }
    }

    public void ApplyLanguage()
    {
        var page = _currentPage ?? ContentFrame.CurrentSourcePageType;
        object? param = page == typeof(LibraryPage)
            ? new LibraryNav(GroupId: _openGroupId)
            : null;
        _ = ReloadGroupsAsync(_openGroupId);
        if (page is not null)
            ContentFrame.Navigate(page, param);
    }

    // Skróty klawiszowe. PreviewKeyDown (tunelowanie), żeby spacja sterowała odtwarzaniem także wtedy,
    // gdy fokus został na przycisku klikniętym myszą. Nie przechwytujemy klawiszy, gdy użytkownik pisze
    // w polu tekstowym albo porusza się klawiaturą po kontrolkach (Tab + strzałki/spacja).
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsKeyDown(VirtualKey.Control);
        if (ctrl && e.Key == VirtualKey.F)
        {
            GoToSearch();
            e.Handled = true;
            return;
        }
        if (ctrl || IsKeyDown(VirtualKey.Menu))
            return;

        var focused = (Content as UIElement)?.XamlRoot is { } xr ? FocusManager.GetFocusedElement(xr) : null;
        if (focused is TextBox or PasswordBox or RichEditBox or AutoSuggestBox)
            return;
        // Spację i ←/→ zostawiamy kontrolce tylko wtedy, gdy użytkownik doszedł do niej klawiaturą (Tab)
        // i te klawisze coś w niej robią: przycisk, element listy, suwak, lista rozwijana, przełącznik.
        // Kontenery (np. SplitView, który dostaje fokus przy starcie) i pozycje menu nie blokują skrótów.
        var keyboardNavigating = focused is Control { FocusState: FocusState.Keyboard }
            && focused is ButtonBase or SelectorItem or Slider or ComboBox or ToggleSwitch;

        switch (e.Key)
        {
            case VirtualKey.Space when !keyboardNavigating:
                App.Services.Player.TogglePlayPause();
                break;
            case VirtualKey.Left when !keyboardNavigating:
                App.Services.Player.Skip(TimeSpan.FromSeconds(-15));
                break;
            case VirtualKey.Right when !keyboardNavigating:
                App.Services.Player.Skip(TimeSpan.FromSeconds(30));
                break;
            case VirtualKey.Add or (VirtualKey)187: // OemPlus
                ChangeSpeed(+1);
                break;
            case VirtualKey.Subtract or (VirtualKey)189: // OemMinus
                ChangeSpeed(-1);
                break;
            case VirtualKey.M:
                ToggleMute();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // Skróty działają tylko, gdy fokus jest w drzewie XAML. Zaraz po starcie (albo po powrocie do okna)
    // fokus bywa „nigdzie” i spacja nic nie robiła, dopóki użytkownik czegoś nie kliknął.
    private void EnsureKeyboardFocus()
    {
        if ((Content as UIElement)?.XamlRoot is not { } root || FocusManager.GetFocusedElement(root) is not null)
            return;
        if (!PlayPauseButton.Focus(FocusState.Programmatic))
            NavView.Focus(FocusState.Programmatic);
    }

    private static bool IsKeyDown(VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void ChangeSpeed(int step)
    {
        // SpeedBox_SelectionChanged ustawia tempo i zapisuje je w ustawieniach.
        var i = Math.Clamp(SpeedBox.SelectedIndex + step, 0, Speeds.Length - 1);
        SpeedBox.SelectedIndex = i;
    }

    public void GoToSearch()
    {
        if (ContentFrame.Content is SearchPage page)
            page.FocusQuery();
        else
            NavView.SelectedItem = NavView.MenuItems[5];
    }

    public void GoToLibrary()
    {
        _openGroupId = null;
        if (!ReferenceEquals(NavView.SelectedItem, NavView.MenuItems[1]))
            NavView.SelectedItem = NavView.MenuItems[1];
    }

    private async Task UpdateNewEpisodesBadgeAsync()
    {
        int count;
        try
        {
            count = await App.Services.Database.CountNewEpisodesAsync();
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("Licznik nowych odcinków", ex);
            return;
        }
        LibraryNavItem.InfoBadge = count > 0 ? new InfoBadge { Value = count } : null;
    }

    // Po automatycznym odświeżeniu aktualizujemy to, co użytkownik ma przed oczami,
    // bez przeładowania Biblioteki (żeby nie zgubić zaznaczenia ani przewinięcia).
    private async Task RefreshVisiblePageAsync()
    {
        try
        {
            switch (ContentFrame.Content)
            {
                case HomePage home:
                    await home.ViewModel.LoadAsync();
                    break;
                case LibraryPage library:
                    await library.ViewModel.RefreshNewCountsAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("Odświeżenie widocznej strony", ex);
        }
    }

    public void GoToLibrary(long podcastId)
    {
        _openGroupId = null;
        _navParam = new LibraryNav(PodcastId: podcastId);
        _currentPage = typeof(LibraryPage);
        if (ReferenceEquals(NavView.SelectedItem, NavView.MenuItems[1]))
        {
            ContentFrame.Navigate(typeof(LibraryPage), _navParam);
            _navParam = null;
        }
        else
        {
            NavView.SelectedItem = NavView.MenuItems[1];
        }
    }

    private void TryEnableMica()
    {
        try
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        }
        catch
        {
            // starszy Windows — zostaje domyślne tło
        }
    }

    private void TryEnableCustomTitleBar()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            var bar = AppWindow.TitleBar;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;

            var ico = Path.Combine(AppContext.BaseDirectory, "icons", "Fala.ico");
            if (File.Exists(ico))
                AppWindow.SetIcon(ico);

            var png = Path.Combine(AppContext.BaseDirectory, "icons", "Fala-icon-C-256.png");
            if (File.Exists(png))
                TitleBarIcon.Source = new BitmapImage(new Uri(png)) { DecodePixelWidth = 36 };
        }
        catch
        {
        }
    }

    private bool _syncingVolume;

    private void RestoreVolume()
    {
        var vol = Math.Clamp(AppSettings.Volume, 0, 1);
        App.Services.Player.SetVolume(vol);
        if (AppSettings.Muted)
            App.Services.Player.ToggleMute();
        _syncingVolume = true;
        VolumeSlider.Value = vol;
        _syncingVolume = false;
        UpdateVolumeIcon();
    }

    private void RestoreSpeed()
    {
        var i = Array.FindIndex(Speeds, s => Math.Abs(s - AppSettings.Speed) < 0.01);
        SpeedBox.SelectedIndex = i >= 0 ? i : 1;
        App.Services.Player.SetSpeed(Speeds[SpeedBox.SelectedIndex]);
    }

    private async void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
            return;

        if (tag == "new-group")
        {
            NavView.SelectedItem = _lastNavItem ?? NavView.MenuItems[0];
            await CreateGroupAsync();
            return;
        }

        _lastNavItem = item;

        if (tag.StartsWith("group:", StringComparison.Ordinal) && long.TryParse(tag[6..], out var groupId))
        {
            _openGroupId = groupId;
            _currentPage = typeof(LibraryPage);
            ContentFrame.Navigate(typeof(LibraryPage), new LibraryNav(GroupId: groupId));
            return;
        }

        switch (tag)
        {
            case "home":
                _openGroupId = null;
                _currentPage = typeof(HomePage);
                ContentFrame.Navigate(typeof(HomePage));
                break;
            case "library":
                _openGroupId = null;
                _currentPage = typeof(LibraryPage);
                ContentFrame.Navigate(typeof(LibraryPage), _navParam ?? new LibraryNav());
                _navParam = null;
                break;
            case "queue":
                _openGroupId = null;
                _currentPage = typeof(QueuePage);
                ContentFrame.Navigate(typeof(QueuePage));
                break;
            case "history":
                _openGroupId = null;
                _currentPage = typeof(HistoryPage);
                ContentFrame.Navigate(typeof(HistoryPage));
                break;
            case "bookmarks":
                _openGroupId = null;
                _currentPage = typeof(BookmarksPage);
                ContentFrame.Navigate(typeof(BookmarksPage));
                break;
            case "search":
                _openGroupId = null;
                _currentPage = typeof(SearchPage);
                ContentFrame.Navigate(typeof(SearchPage));
                break;
            case "settings":
                _openGroupId = null;
                _currentPage = typeof(SettingsPage);
                ContentFrame.Navigate(typeof(SettingsPage));
                break;
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
        => App.Services.Player.TogglePlayPause();

    private void SkipBack_Click(object sender, RoutedEventArgs e)
        => App.Services.Player.Skip(TimeSpan.FromSeconds(-15));

    private void SkipForward_Click(object sender, RoutedEventArgs e)
        => App.Services.Player.Skip(TimeSpan.FromSeconds(30));

    private async void NextQueue_Click(object sender, RoutedEventArgs e)
        => await App.Services.Player.PlayNextInQueueAsync();

    private void UpdateNextQueueButton()
    {
        var hasQueue = App.Services.Queue.Count > 0;
        NextQueueButton.Visibility = hasQueue ? Visibility.Visible : Visibility.Collapsed;
        NextQueueButton.IsEnabled = hasQueue;
    }

    private async void AddBookmark_Click(object sender, RoutedEventArgs e)
        => await AddBookmark_ClickWork();

    private void Mute_Click(object sender, RoutedEventArgs e)
        => ToggleMute();

    private void ToggleMute()
    {
        App.Services.Player.ToggleMute();
        AppSettings.Muted = App.Services.Player.IsMuted;
        AppSettings.Save();
        UpdateVolumeIcon();
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingVolume || _initializing) return;
        App.Services.Player.SetVolume(e.NewValue);
        AppSettings.Volume = e.NewValue;
        AppSettings.Muted = false;
        AppSettings.Save();
        UpdateVolumeIcon();
    }

    private void UpdateVolumeIcon()
    {
        var muted = App.Services.Player.IsMuted || App.Services.Player.VolumeLevel <= 0.001;
        if (muted)
            VolumeIcon.Glyph = "\uE74F";
        else if (App.Services.Player.VolumeLevel < 0.34)
            VolumeIcon.Glyph = "\uE993";
        else if (App.Services.Player.VolumeLevel < 0.67)
            VolumeIcon.Glyph = "\uE994";
        else
            VolumeIcon.Glyph = "\uE767";
    }

    private void SpeedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var i = SpeedBox.SelectedIndex;
        if (i >= 0 && i < Speeds.Length)
        {
            App.Services.Player.SetSpeed(Speeds[i]);
            AppSettings.Speed = Speeds[i];
            AppSettings.Save();
        }
    }

    private void SleepOff_Click(object sender, RoutedEventArgs e)
        => App.Services.Player.ClearSleep();

    private void SleepEnd_Click(object sender, RoutedEventArgs e)
        => App.Services.Player.SleepAtEndOfEpisode();

    private void SleepMinutes_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string s } && int.TryParse(s, out var minutes))
            App.Services.Player.SleepAfter(TimeSpan.FromMinutes(minutes));
    }

    private void ProgressSlider_Loaded(object sender, RoutedEventArgs e)
    {
        ProgressSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _scrubbing = true), true);
        ProgressSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ProgressSlider_Released), true);
        ProgressSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ProgressSlider_Released), true);
    }

    private void ProgressSlider_Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        App.Services.Player.Seek(TimeSpan.FromSeconds(ProgressSlider.Value));
    }

    private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingSlider || !_scrubbing) return;
        var pos = TimeSpan.FromSeconds(e.NewValue);
        PositionText.Text = TimeText.Format(pos);
        if (_duration > TimeSpan.Zero)
            DurationText.Text = TimeText.RemainingClock(pos, _duration);
    }

    private void Player_StateChanged(object? sender, PlayerState e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var trackChanged = _shownGuid != e.EpisodeGuid;
            if (trackChanged)
            {
                _shownGuid = e.EpisodeGuid;
                NowPlayingTitle.Text = string.IsNullOrWhiteSpace(e.EpisodeTitle) ? L.NothingPlaying : e.EpisodeTitle;
                NowPlayingShow.Text = string.IsNullOrWhiteSpace(e.ShowTitle) ? L.PickEpisode : e.ShowTitle;
                ArtworkImage.Source = Artwork.FromUrl(e.ArtworkUrl);
                PaneArtwork.Source = Artwork.FromUrlLarge(e.ArtworkUrl);
                if (NowPlayingSplit.IsPaneOpen)
                    _ = RefreshNowPlayingPaneAsync();
            }

            PlayPauseIcon.Glyph = e.IsPlaying ? "\uE769" : "\uE768";
            BufferingRing.IsActive = e.IsBuffering;
            BufferingRing.Visibility = e.IsBuffering ? Visibility.Visible : Visibility.Collapsed;
            PlayPauseButton.IsEnabled = e.HasTrack;
            SkipBackButton.IsEnabled = e.HasTrack;
            SkipForwardButton.IsEnabled = e.HasTrack;

            var hasDuration = e.Duration > TimeSpan.Zero;
            _duration = e.Duration;
            ProgressSlider.IsEnabled = e.HasTrack && hasDuration;

            if (!_scrubbing)
            {
                var max = hasDuration ? e.Duration.TotalSeconds : Math.Max(1, e.Position.TotalSeconds);
                var pos = Math.Clamp(e.Position.TotalSeconds, 0, Math.Max(max, 1));
                _syncingSlider = true;
                ProgressSlider.Maximum = Math.Max(1, max);
                ProgressSlider.Value = pos;
                _syncingSlider = false;
                PositionText.Text = TimeText.Format(e.Position);
                DurationText.Text = hasDuration
                    ? TimeText.RemainingClock(e.Position, e.Duration)
                    : "−−:−−";
            }

            SleepStatus.Text = e.SleepLabel;
            SleepStatus.Visibility = string.IsNullOrEmpty(e.SleepLabel) ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    private async void Artwork_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.Player.CurrentEpisode is null)
            return;
        await RefreshNowPlayingPaneAsync();
        NowPlayingSplit.IsPaneOpen = true;
    }

    private void CloseNowPlaying_Click(object sender, RoutedEventArgs e)
        => NowPlayingSplit.IsPaneOpen = false;

    private async Task RefreshNowPlayingPaneAsync()
    {
        var ep = App.Services.Player.CurrentEpisode;
        if (ep is null)
        {
            PaneEpisodeTitle.Text = L.NothingPlaying;
            PaneShowTitle.Text = "";
            PaneSummary.Text = "";
            PaneArtwork.Source = null;
            return;
        }

        PaneEpisodeTitle.Text = ep.Title;
        PaneShowTitle.Text = App.Services.Player.CurrentShow;
        PaneArtwork.Source = Artwork.FromUrlLarge(App.Services.Player.CurrentArtwork);

        var summary = PlainText.Notes(ep.Description);
        if (string.IsNullOrWhiteSpace(summary) && ep.PodcastId > 0)
        {
            var show = await App.Services.Database.GetPodcastByIdAsync(ep.PodcastId);
            summary = PlainText.Notes(show?.Description);
            if (string.IsNullOrWhiteSpace(App.Services.Player.CurrentArtwork) && show is not null)
                PaneArtwork.Source = Artwork.FromUrlLarge(show.ArtworkUrl);
        }
        if (string.IsNullOrWhiteSpace(summary))
            PaneSummary.Text = L.Get("NoSummary");
        else
            SetPaneSummary(summary, ep.Duration);
    }

    // Znaczniki czasu w notatkach („12:34 — temat”) stają się linkami przewijającymi odcinek.
    private void SetPaneSummary(string summary, TimeSpan? duration)
    {
        PaneSummary.Inlines.Clear();
        foreach (var (text, at) in TimeText.SplitTimestamps(summary, duration))
        {
            if (at is not { } time)
            {
                AddTextWithLinks(text);
                continue;
            }
            var link = new Hyperlink();
            link.Inlines.Add(new Run { Text = text });
            link.Click += (_, _) => JumpTo(time);
            PaneSummary.Inlines.Add(link);
        }
    }

    private void AddTextWithLinks(string text)
    {
        foreach (var (part, link) in ShowNotes.SplitLinks(text))
        {
            if (link is null)
            {
                PaneSummary.Inlines.Add(new Run { Text = part });
                continue;
            }
            var hyperlink = new Hyperlink { NavigateUri = link };
            hyperlink.Inlines.Add(new Run { Text = part });
            PaneSummary.Inlines.Add(hyperlink);
        }
    }

    private static void JumpTo(TimeSpan time)
    {
        var player = App.Services.Player;
        player.Seek(time);
        if (!player.IsPlaying)
            player.TogglePlayPause();
    }

    private void PanePlayNext_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.Player.CurrentEpisode is not { } ep) return;
        App.Services.Queue.PlayNext(ep, App.Services.Player.CurrentShow, App.Services.Player.CurrentArtwork);
    }

    private void PaneAddToQueue_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.Player.CurrentEpisode is not { } ep) return;
        App.Services.Queue.Add(ep, App.Services.Player.CurrentShow, App.Services.Player.CurrentArtwork);
    }

    private async void PaneAddToHistory_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.Player.CurrentEpisode is not { } ep) return;
        await App.Services.Database.RecordHistoryAsync(ep, App.Services.Player.CurrentShow, App.Services.Player.CurrentArtwork, manual: true);
        await App.Services.Database.SetPlayedAsync(ep.Id, true);
        ep.IsPlayed = true;
    }

    private async void PaneAddBookmark_Click(object sender, RoutedEventArgs e)
        => await AddBookmark_ClickWork();

    private async Task AddBookmark_ClickWork()
    {
        var player = App.Services.Player;
        var root = (Content as FrameworkElement)?.XamlRoot;
        if (root is null) return;
        var draft = await BookmarkDialog.ShowAsync(
            root,
            episode: player.CurrentEpisode,
            position: player.CurrentEpisode is null ? null : player.Position);
        if (draft is null) return;
        await App.Services.Database.AddBookmarkAsync(draft);
    }

    private async void PaneDownload_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.Player.CurrentEpisode is not { } ep) return;
        try
        {
            var path = await App.Services.Downloads.DownloadAsync(ep);
            await App.Services.Database.SetLocalPathAsync(ep.Id, path);
            ep.LocalPath = path;
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn($"Pobieranie odcinka {ep.Id}", ex);
        }
    }

    private async void PaneDeleteDownload_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.Player.CurrentEpisode is not { } ep) return;
        App.Services.Downloads.DeleteEpisodeFile(ep);
        await App.Services.Database.SetLocalPathAsync(ep.Id, null);
        ep.LocalPath = null;
    }

    private async void PaneTogglePlayed_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.Player.CurrentEpisode is not { } ep) return;
        var next = !ep.IsPlayed;
        await App.Services.Database.SetPlayedAsync(ep.Id, next);
        ep.IsPlayed = next;
        if (next)
            await App.Services.Database.RecordHistoryAsync(ep, App.Services.Player.CurrentShow, App.Services.Player.CurrentArtwork, manual: true);
    }

    private void PaneGoToShow_Click(object sender, RoutedEventArgs e)
    {
        var id = App.Services.Player.CurrentEpisode?.PodcastId ?? 0;
        if (id == 0) return;
        NowPlayingSplit.IsPaneOpen = false;
        GoToLibrary(id);
    }

    public async Task ReloadGroupsAsync(long? selectGroupId = null)
    {
        // Najpierw pobieramy grupy, a dopiero potem przebudowujemy menu bez żadnego await po drodze —
        // wcześniej dwa równoległe wywołania (Loaded + zmiana języka) przeplatały się i dublowały pozycje.
        var groups = await App.Services.Database.GetGroupsAsync();

        var selectedTag = (NavView.SelectedItem as NavigationViewItem)?.Tag as string;
        while (NavView.MenuItems.Count > FixedMenuCount)
            NavView.MenuItems.RemoveAt(NavView.MenuItems.Count - 1);

        NavView.MenuItems.Add(new NavigationViewItemSeparator());
        NavView.MenuItems.Add(new NavigationViewItemHeader { Content = L.GroupsHeader });

        NavigationViewItem? toSelect = null;
        foreach (var group in groups)
        {
            var item = CreateGroupNavItem(group);
            NavView.MenuItems.Add(item);
            if (selectGroupId == group.Id || selectedTag == "group:" + group.Id)
                toSelect = item;
        }

        NavView.MenuItems.Add(new NavigationViewItem
        {
            Content = L.NewGroup,
            Tag = "new-group",
            Icon = new FontIcon { Glyph = "\uE710" }
        });

        if (toSelect is not null)
            NavView.SelectedItem = toSelect;
    }

    private NavigationViewItem CreateGroupNavItem(PodcastGroup group)
    {
        var item = new NavigationViewItem
        {
            Content = group.Name,
            Tag = "group:" + group.Id,
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        var flyout = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = L.RenameGroup, Tag = group };
        rename.Click += async (_, _) => await RenameGroupAsync(group);
        var del = new MenuFlyoutItem { Text = L.DeleteGroup, Tag = group };
        del.Click += async (_, _) => await DeleteGroupAsync(group);
        flyout.Items.Add(rename);
        flyout.Items.Add(del);
        item.ContextFlyout = flyout;
        return item;
    }

    private async Task CreateGroupAsync()
    {
        var name = await PromptNameAsync(L.NewGroup, "");
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (await App.Services.Database.GroupNameExistsAsync(name))
        {
            await ShowInfoAsync(L.Get("GroupExists"));
            return;
        }
        var group = await App.Services.Database.CreateGroupAsync(name);
        await ReloadGroupsAsync(group.Id);
    }

    private async Task RenameGroupAsync(PodcastGroup group)
    {
        var name = await PromptNameAsync(L.RenameGroup, group.Name);
        if (string.IsNullOrWhiteSpace(name) || name == group.Name)
            return;
        if (await App.Services.Database.GroupNameExistsAsync(name, group.Id))
        {
            await ShowInfoAsync(L.Get("GroupExists"));
            return;
        }
        await App.Services.Database.RenameGroupAsync(group.Id, name);
        await ReloadGroupsAsync(group.Id);
    }

    private async Task DeleteGroupAsync(PodcastGroup group)
    {
        var dialog = new ContentDialog
        {
            Title = L.DeleteGroup,
            Content = L.Format("ConfirmDeleteGroup", group.Name),
            PrimaryButtonText = L.DeleteGroup,
            CloseButtonText = L.Cancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = (Content as FrameworkElement)?.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        await App.Services.Database.DeleteGroupAsync(group.Id);
        var wasOpen = _openGroupId == group.Id;
        if (wasOpen)
            _openGroupId = null;
        await ReloadGroupsAsync();
        // Przełączamy na Bibliotekę tylko wtedy, gdy usunięta grupa była otwarta.
        if (wasOpen)
            NavView.SelectedItem = NavView.MenuItems[1];
    }

    private async Task<string?> PromptNameAsync(string title, string initial)
    {
        var box = new TextBox
        {
            Text = initial,
            PlaceholderText = L.Get("GroupNamePlaceholder"),
            MaxLength = 80
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = L.Save,
            CloseButtonText = L.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = (Content as FrameworkElement)?.XamlRoot
        };
        box.SelectAll();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        return box.Text?.Trim();
    }

    private async Task ShowInfoAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = L.GroupsHeader,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = (Content as FrameworkElement)?.XamlRoot
        };
        await dialog.ShowAsync();
    }
}
