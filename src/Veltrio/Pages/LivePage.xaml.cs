using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>Live TV: the selected M3U playlist's channels, by group.</summary>
public sealed partial class LivePage : Page
{
    public sealed record GroupEntry(string Name, int Count);

    private IReadOnlyList<ChannelItem> _channels = Array.Empty<ChannelItem>();
    private bool _populating;

    public LivePage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        var playlists = IptvStore.Playlists;
        var any = playlists.Count > 0;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Browser.Visibility = Toolbar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any) return;

        _populating = true;
        PlaylistPicker.ItemsSource = playlists.Select(p => p.Name).ToList();
        var selected = IptvStore.Selected;
        PlaylistPicker.SelectedIndex = selected is null ? 0 : playlists.ToList().FindIndex(p => p.Id == selected.Id);
        _populating = false;
        await LoadAsync(refresh: false);
    }

    private PlaylistRef? Current =>
        PlaylistPicker.SelectedIndex >= 0 && PlaylistPicker.SelectedIndex < IptvStore.Playlists.Count
            ? IptvStore.Playlists[PlaylistPicker.SelectedIndex]
            : null;

    private async Task LoadAsync(bool refresh)
    {
        if (Current is not { } playlist) return;
        Loading.IsActive = true;
        Message.Visibility = Visibility.Collapsed;
        Channels.ItemsSource = null;
        Groups.ItemsSource = null;
        try
        {
            _channels = await IptvStore.ChannelsAsync(playlist, refresh);
            // The user may have switched playlist meanwhile.
            if (Current?.Id != playlist.Id) return;
            Eyebrow.Text = $"DIRECT · {_channels.Count} CHAÎNES";
            var groups = new List<GroupEntry> { new("Toutes les chaînes", _channels.Count) };
            groups.AddRange(_channels.GroupBy(c => c.Group).Select(g => new GroupEntry(g.Key, g.Count())));
            Groups.ItemsSource = groups;
            Groups.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            _channels = Array.Empty<ChannelItem>();
            Message.Text = CoreService.Humanize(ex);
            Message.Visibility = Visibility.Visible;
        }
        finally
        {
            Loading.IsActive = false;
        }
    }

    private void ApplyFilter()
    {
        var group = Groups.SelectedIndex > 0 ? (Groups.SelectedItem as GroupEntry)?.Name : null;
        var text = Filter.Text.Trim();
        var visible = _channels
            .Where(c => group is null || c.Group == group)
            .Where(c => text.Length == 0 || c.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        Channels.ItemsSource = visible;
        Message.Text = text.Length > 0 ? $"Aucune chaîne ne correspond à « {text} »." : "Aucune chaîne dans ce groupe.";
        Message.Visibility = visible.Count == 0 && !Loading.IsActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnPlaylistChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating || Current is null) return;
        IptvStore.Selected = Current;
        await LoadAsync(refresh: false);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadAsync(refresh: true);

    private void OnGroupChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnChannelClick(object sender, ItemClickEventArgs e)
    {
        var channel = (ChannelItem)e.ClickedItem;
        Navigator.Play(new PlayRequest { Title = channel.Name, Subtitle = channel.Group, Url = channel.StreamUrl, IsLive = true });
    }

    private void OnOpenSources(object sender, RoutedEventArgs e) => Navigator.Window.ShowSection(3);
}
