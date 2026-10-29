using Fala.Localization;
using Fala.Models;
using Fala.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Fala.Views;

public sealed partial class HistoryPage : Page
{
    public HistoryViewModel ViewModel { get; } = new();
    public Loc L => Loc.Current;

    public HistoryPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
        => await ViewModel.LoadAsync();

    private void Search_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ViewModel.Query = args.ChosenSuggestion as string ?? args.QueryText ?? "";
        ViewModel.ApplyFilter();
    }

    private void Search_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string s)
            ViewModel.Query = s;
    }

    private async void RemoveHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HistoryItem item })
            await ViewModel.RemoveItemCommand.ExecuteAsync(item);
    }

    private async void History_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryItem item)
            await ViewModel.PlayItemCommand.ExecuteAsync(item);
    }

    private async void AddFromLibrary_Click(object sender, RoutedEventArgs e)
    {
        var podcasts = await App.Services.Database.GetPodcastsAsync();
        if (podcasts.Count == 0)
        {
            ViewModel.Status = Loc.Current.Get("LibraryEmpty");
            return;
        }

        var showBox = new ComboBox
        {
            ItemsSource = podcasts,
            DisplayMemberPath = "Title",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0
        };
        var episodesList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            Height = 280,
            DisplayMemberPath = "Title"
        };

        async void LoadEpisodes()
        {
            if (showBox.SelectedItem is not Podcast p) return;
            var eps = await App.Services.Database.GetEpisodesAsync(p.Id);
            episodesList.ItemsSource = eps;
        }

        showBox.SelectionChanged += (_, _) => LoadEpisodes();
        LoadEpisodes();

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = L.NavLibrary });
        panel.Children.Add(showBox);
        panel.Children.Add(episodesList);

        var dialog = new ContentDialog
        {
            Title = L.AddFromLibrary,
            Content = panel,
            PrimaryButtonText = L.AddToHistory,
            CloseButtonText = L.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        if (showBox.SelectedItem is not Podcast podcast)
            return;

        foreach (var episode in episodesList.SelectedItems.OfType<Episode>())
        {
            await App.Services.Database.RecordHistoryAsync(episode, podcast.Title, podcast.ArtworkUrl, manual: true);
            await App.Services.Database.SetPlayedAsync(episode.Id, true);
        }
        await ViewModel.LoadAsync();
    }

    private async void AddCustom_Click(object sender, RoutedEventArgs e)
    {
        var showBox = new TextBox { PlaceholderText = Loc.Current.Get("CustomShowPlaceholder") };
        var titleBox = new TextBox { PlaceholderText = Loc.Current.Get("CustomEpisodePlaceholder") };
        var date = new CalendarDatePicker
        {
            Date = DateTimeOffset.Now,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        panel.Children.Add(showBox);
        panel.Children.Add(titleBox);
        panel.Children.Add(date);

        var dialog = new ContentDialog
        {
            Title = L.AddCustom,
            Content = panel,
            PrimaryButtonText = L.Save,
            CloseButtonText = L.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        if (string.IsNullOrWhiteSpace(showBox.Text) || string.IsNullOrWhiteSpace(titleBox.Text))
        {
            ViewModel.Status = Loc.Current.Get("CustomHistoryNeedFields");
            return;
        }

        var when = date.Date ?? DateTimeOffset.Now;
        await App.Services.Database.AddCustomHistoryAsync(showBox.Text, titleBox.Text, when);
        await ViewModel.LoadAsync();
    }
}
