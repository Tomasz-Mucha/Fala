using System.Text;
using Fala.Localization;
using Fala.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Fala.Views;

public sealed partial class SettingsPage : Page
{
    public Loc L => Loc.Current;
    private bool _ready;

    public SettingsPage()
    {
        InitializeComponent();
        ApplyTexts();
        FolderText.Text = App.Services.Database.DataDirectory;
        DownloadsText.Text = App.Services.Downloads.RootPath;
        ThemeBox.SelectedIndex = AppSettings.Theme switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0
        };
        LanguageBox.SelectedIndex = AppSettings.Language switch
        {
            "sv" => 1,
            "en" => 2,
            _ => 0
        };
        var accents = AccentService.GetChoices();
        AccentGrid.ItemsSource = accents;
        AccentGrid.SelectedItem = accents.FirstOrDefault(c => c.Key == AppSettings.Accent)
            ?? accents[0];
        RefreshBox.SelectedIndex = Math.Max(0, Array.IndexOf(FeedRefreshService.IntervalChoices, AppSettings.AutoRefreshHours));
        NotificationsSwitch.IsOn = AppSettings.NewEpisodeNotifications;
        _ready = true;
    }

    private void ApplyTexts()
    {
        ThemeSystemItem.Content = L.ThemeSystem;
        ThemeLightItem.Content = L.ThemeLight;
        ThemeDarkItem.Content = L.ThemeDark;
        OpenDownloadsButton.Content = L.Open;
        OpenFolderButton.Content = L.Open;
        AboutText.Text = L.Format("AboutLine", Fala.Helpers.AppInfo.Version);
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;
        if (LanguageBox.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;
        if (tag == AppSettings.Language)
            return;
        AppSettings.Language = tag;
        AppSettings.Save();
        Loc.Current.SetLanguage(tag);
        App.MainAppWindow.ApplyLanguage();
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;
        if (ThemeBox.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;
        AppSettings.Theme = tag switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        AppSettings.Save();
        AppSettings.Apply(App.MainAppWindow); // wywołuje też AccentService.Apply()
        // Elementy utworzone przed zmianą motywu potrafią zachować stare kolory — odtwarzamy
        // bieżącą stronę i menu (tak samo jak przy zmianie języka).
        App.MainAppWindow.ApplyLanguage();
    }

    private void RefreshBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || RefreshBox.SelectedIndex < 0)
            return;
        AppSettings.AutoRefreshHours = FeedRefreshService.IntervalChoices[RefreshBox.SelectedIndex];
        AppSettings.Save();
        App.Services.Refresh.Reschedule();
    }

    private void NotificationsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;
        AppSettings.NewEpisodeNotifications = NotificationsSwitch.IsOn;
        AppSettings.Save();
    }

    private void AccentGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AccentGrid.SelectedItem is AccentChoice choice)
            ApplyAccent(choice);
    }

    private void ApplyAccent(AccentChoice choice)
    {
        if (!_ready)
            return;
        AppSettings.Accent = choice.Key;
        AppSettings.Save();
        AccentService.Apply();
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Launcher.LaunchFolderPathAsync(App.Services.Database.DataDirectory);
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("Otwieranie folderu danych", ex);
        }
    }

    private async void OpenNotices_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("Otwieranie pliku licencji", ex);
        }
    }

    private async void OpenDownloads_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(App.Services.Downloads.RootPath);
            await Launcher.LaunchFolderPathAsync(App.Services.Downloads.RootPath);
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("Otwieranie folderu pobranych", ex);
        }
    }

    private async void ImportOpml_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add(".opml");
        picker.FileTypeFilter.Add(".xml");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        SetOpmlBusy(true);
        try
        {
            IReadOnlyList<OpmlEntry> entries;
            await using (var stream = File.OpenRead(file.Path))
                entries = OpmlService.ParseEntries(stream);
            if (entries.Count == 0)
            {
                ShowOpmlStatus(L.Get("OpmlEmpty"));
                return;
            }

            ShowOpmlStatus(L.Format("OpmlImporting", 0, entries.Count));
            var progress = new Progress<int>(n => ShowOpmlStatus(L.Format("OpmlImporting", n, entries.Count)));
            var result = await App.Services.Subscriptions.ImportAsync(entries, progress);
            var text = L.Format("OpmlImported", result.Added, result.Skipped);
            if (result.CreatedGroups > 0)
            {
                text += L.Format("OpmlGroupsCreated", result.CreatedGroups);
                await App.MainAppWindow.ReloadGroupsAsync();
            }
            if (result.Failed.Count > 0)
                text += L.Format("OpmlFailed", result.Failed.Count);
            ShowOpmlStatus(text);
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("SettingsPage", ex);
            ShowOpmlStatus(L.Format("ErrorPrefix", ex.Message));
        }
        finally
        {
            SetOpmlBusy(false);
        }
    }

    private async void ExportOpml_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var podcasts = await App.Services.Database.GetPodcastsAsync();
            if (podcasts.Count == 0)
            {
                ShowOpmlStatus(L.Get("OpmlNothingToExport"));
                return;
            }

            var picker = new FileSavePicker { SuggestedFileName = L.Get("OpmlFileName") };
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
            picker.FileTypeChoices.Add("OPML", new List<string> { ".opml" });
            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;

            await File.WriteAllTextAsync(file.Path, OpmlService.Build(podcasts), new UTF8Encoding(false));
            ShowOpmlStatus(L.Format("OpmlExported", podcasts.Count));
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("SettingsPage", ex);
            ShowOpmlStatus(L.Format("ErrorPrefix", ex.Message));
        }
    }

    private void SetOpmlBusy(bool busy)
    {
        OpmlProgress.IsActive = busy;
        ImportOpmlButton.IsEnabled = !busy;
        ExportOpmlButton.IsEnabled = !busy;
    }

    private void ShowOpmlStatus(string text)
    {
        OpmlStatus.Text = text;
        OpmlStatus.Visibility = Visibility.Visible;
    }
}
