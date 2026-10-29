using System.Text;
using Fala.Helpers;
using Fala.Localization;
using Fala.Models;
using Fala.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Fala.Views;

public sealed partial class BookmarksPage : Page
{
    public BookmarksViewModel ViewModel { get; } = new();
    public Loc L => Loc.Current;

    public BookmarksPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
        => await ViewModel.LoadAsync();

    private async void Bookmark_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Bookmark item)
            await ViewModel.PlayItemCommand.ExecuteAsync(item);
    }

    private async void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        var player = App.Services.Player;
        var draft = await BookmarkDialog.ShowAsync(
            XamlRoot,
            episode: player.CurrentEpisode,
            position: player.CurrentEpisode is null ? null : player.Position);
        if (draft is null) return;
        await App.Services.Database.AddBookmarkAsync(draft);
        await ViewModel.LoadAsync();
    }

    private async void EditBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Bookmark item })
            return;
        var edited = await BookmarkDialog.ShowAsync(XamlRoot, existing: item);
        if (edited is null) return;
        await App.Services.Database.UpdateBookmarkAsync(edited);
        await ViewModel.LoadAsync();
    }

    private async void ExportBookmarks_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var bookmarks = await App.Services.Database.GetBookmarksAsync();
            if (bookmarks.Count == 0)
            {
                ViewModel.Status = L.Get("BookmarksNothingToExport");
                return;
            }

            var picker = new FileSavePicker { SuggestedFileName = L.Get("BookmarksFileName") };
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
            picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
            picker.FileTypeChoices.Add(L.Get("PlainTextFile"), new List<string> { ".txt" });
            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;

            var markdown = BookmarkExport.ToMarkdown(bookmarks, DateTimeOffset.Now);
            await File.WriteAllTextAsync(file.Path, markdown, new UTF8Encoding(false));
            ViewModel.Status = L.Format("BookmarksExported", bookmarks.Count);
        }
        catch (Exception ex)
        {
            Log.Warn("Eksport zakładek", ex);
            ViewModel.Status = L.Format("ErrorPrefix", ex.Message);
        }
    }

    private async void RemoveBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Bookmark item })
            await ViewModel.RemoveItemCommand.ExecuteAsync(item);
    }
}
