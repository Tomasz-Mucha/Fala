using Fala.Localization;
using Fala.Models;
using Fala.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Fala.Views;

public sealed partial class SearchPage : Page
{
    public SearchViewModel ViewModel { get; } = new();
    public Loc L => Loc.Current;

    public SearchPage()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusQuery();
    }

    public void FocusQuery()
    {
        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectAll();
    }

    private async void Query_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
            await ViewModel.GoAsync();
    }

    private async void Subscribe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is CatalogShow show)
            await ViewModel.SubscribeAsync(show);
    }
}
