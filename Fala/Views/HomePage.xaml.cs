using Fala.Localization;
using Fala.Models;
using Fala.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Fala.Views;

public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; } = new();
    public Loc L => Loc.Current;

    public HomePage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private void Continue_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HomeItem item)
            ViewModel.Play(item);
    }

    private void Recent_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HomeItem item)
            ViewModel.Play(item);
    }

    private void Show_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Podcast podcast)
            App.MainAppWindow.GoToLibrary(podcast.Id);
    }

    private async void Feed_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
            await ViewModel.SubscribeFeedAsync();
    }

    private void HomeItemMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout)
            return;
        var item = FlyoutHomeItem(flyout);
        flyout.Items.Clear();
        if (item is null)
            return;

        var playNext = new MenuFlyoutItem { Text = L.PlayNext, Tag = item };
        playNext.Click += (_, _) => ViewModel.EnqueueNext(item);
        var add = new MenuFlyoutItem { Text = L.AddToQueue, Tag = item };
        add.Click += (_, _) => ViewModel.AddToQueue(item);
        var hist = new MenuFlyoutItem { Text = L.AddToHistory, Tag = item };
        hist.Click += async (_, _) => await ViewModel.AddToHistoryAsync(item);
        flyout.Items.Add(playNext);
        flyout.Items.Add(add);
        flyout.Items.Add(hist);
    }

    private static HomeItem? FlyoutHomeItem(MenuFlyout flyout)
    {
        if (flyout.Target is not FrameworkElement fe)
            return null;
        if (fe.Tag is HomeItem tagged)
            return tagged;
        return fe.DataContext as HomeItem;
    }
}
