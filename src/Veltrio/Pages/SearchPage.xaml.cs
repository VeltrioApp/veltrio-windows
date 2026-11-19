using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;
using Windows.System;

namespace Veltrio.Pages;

/// <summary>Search across every enabled addon's searchable catalogs, then
/// the channels of the IPTV playlists. Searches as you type.</summary>
public sealed partial class SearchPage : Page
{
    private CancellationTokenSource? _debounce;
    private int _generation;
    private string _lastQuery = "";

    public SearchPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Query.Focus(FocusState.Programmatic);
        Query.SelectAll();
    }

    private async void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        try { await Task.Delay(350, token); }
        catch (TaskCanceledException) { return; }
        await RunAsync(Query.Text.Trim());
    }

    private async void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        _debounce?.Cancel();
        await RunAsync(Query.Text.Trim());
    }

    private async Task RunAsync(string query)
    {
        if (query == _lastQuery) return;
        _lastQuery = query;
        var generation = ++_generation;
        if (query.Length < 2)
        {
            Results.ItemsSource = null;
            ChannelSection.Visibility = NothingFound.Visibility = Visibility.Collapsed;
            Hint.Visibility = Visibility.Visible;
            return;
        }

        Hint.Visibility = Visibility.Collapsed;
        Searching.IsActive = true;
        var titlesTask = CoreService.SearchAsync(query);
        var channelsTask = IptvStore.SearchAsync(query);
        var titles = await titlesTask;
        var channels = await channelsTask;
        // Typing on made this answer stale.
        if (generation != _generation) return;

        Searching.IsActive = false;
        Results.ItemsSource = titles;
        ChannelList.ItemsSource = channels;
        ChannelCount.Text = $"{channels.Count}";
        ChannelSection.Visibility = channels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NothingFound.Visibility = titles.Count == 0 && channels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPosterClick(object sender, ItemClickEventArgs e) => Navigator.OpenTitle((PosterItem)e.ClickedItem);

    private void OnChannelClick(object sender, ItemClickEventArgs e)
    {
        var channel = (ChannelItem)e.ClickedItem;
        Navigator.Play(new PlayRequest { Title = channel.Name, Subtitle = channel.Group, Url = channel.StreamUrl, IsLive = true });
    }

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();
}
