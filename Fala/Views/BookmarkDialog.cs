using Fala.Helpers;
using Fala.Localization;
using Fala.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fala.Views;

public static class BookmarkDialog
{
    public static async Task<Bookmark?> ShowAsync(
        XamlRoot root,
        Bookmark? existing = null,
        Episode? episode = null,
        Podcast? podcast = null,
        TimeSpan? position = null)
    {
        var L = Loc.Current;
        var titleBox = new TextBox
        {
            Header = L.BookmarkTitle,
            Text = existing?.Title ?? episode?.Title ?? "",
            PlaceholderText = L.BookmarkTitle
        };
        var posBox = new TextBox
        {
            Header = L.BookmarkPosition,
            Text = TimeText.Format(existing?.Position ?? position ?? TimeSpan.Zero),
            PlaceholderText = "12:34"
        };
        var commentBox = new TextBox
        {
            Header = L.BookmarkComment,
            Text = existing?.Comment ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 88
        };

        ComboBox? showBox = null;
        ComboBox? episodeBox = null;
        var panel = new StackPanel { Spacing = 10, MinWidth = 360 };

        if (existing is null && episode is null)
        {
            var podcasts = await App.Services.Database.GetPodcastsAsync();
            showBox = new ComboBox
            {
                Header = L.NavLibrary,
                ItemsSource = podcasts,
                DisplayMemberPath = "Title",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            episodeBox = new ComboBox
            {
                Header = L.BookmarkEpisode,
                DisplayMemberPath = "Title",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            showBox.SelectionChanged += async (_, _) =>
            {
                if (showBox.SelectedItem is Podcast p)
                    episodeBox.ItemsSource = await App.Services.Database.GetEpisodesAsync(p.Id);
            };
            if (podcasts.Count > 0)
            {
                showBox.SelectedIndex = 0;
            }
            panel.Children.Add(showBox);
            panel.Children.Add(episodeBox);
        }
        else
        {
            var show = podcast?.Title ?? existing?.ShowTitle ?? App.Services.Player.CurrentShow;
            var epTitle = episode?.Title ?? existing?.EpisodeTitle ?? App.Services.Player.CurrentEpisode?.Title ?? "";
            panel.Children.Add(new TextBlock
            {
                Text = show,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            panel.Children.Add(new TextBlock
            {
                Text = epTitle,
                Opacity = 0.75
            });
        }

        panel.Children.Add(titleBox);
        panel.Children.Add(posBox);
        panel.Children.Add(commentBox);

        var dialog = new ContentDialog
        {
            Title = existing is null ? L.AddBookmark : L.EditBookmark,
            Content = panel,
            PrimaryButtonText = L.Save,
            CloseButtonText = L.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        var title = titleBox.Text?.Trim() ?? "";
        if (title.Length == 0)
            title = episode?.Title ?? existing?.Title ?? L.AddBookmark;
        if (!TimeText.TryParse(posBox.Text, out var pos))
            pos = existing?.Position ?? position ?? TimeSpan.Zero;

        Episode? ep = episode;
        if (ep is null && existing?.EpisodeId is long eid)
            ep = await App.Services.Database.GetEpisodeByIdAsync(eid);
        ep ??= episodeBox?.SelectedItem as Episode;
        Podcast? pod = podcast;
        if (pod is null && showBox?.SelectedItem is Podcast selectedShow)
            pod = selectedShow;
        if (pod is null && ep is not null)
        {
            var all = await App.Services.Database.GetPodcastsAsync();
            pod = all.FirstOrDefault(p => p.Id == ep.PodcastId);
        }

        return new Bookmark
        {
            Id = existing?.Id ?? 0,
            EpisodeId = ep?.Id ?? existing?.EpisodeId,
            PodcastId = pod?.Id ?? ep?.PodcastId ?? existing?.PodcastId,
            Title = title,
            Comment = commentBox.Text?.Trim() ?? "",
            Position = pos,
            ShowTitle = pod?.Title ?? existing?.ShowTitle ?? App.Services.Player.CurrentShow,
            EpisodeTitle = ep?.Title ?? existing?.EpisodeTitle ?? "",
            ArtworkUrl = pod?.ArtworkUrl ?? existing?.ArtworkUrl ?? App.Services.Player.CurrentArtwork,
            CreatedUtc = existing?.CreatedUtc ?? DateTimeOffset.UtcNow
        };
    }
}
