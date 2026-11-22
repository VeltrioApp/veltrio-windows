using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>Live TV the way TV apps lay it out, as on macOS: a numbered
/// channel list with what's on, the selected channel large with its
/// programme and what's next, a guide grid, and for Xtream accounts their
/// films and series.</summary>
public sealed partial class LivePage : Page
{
    private const string AllGroups = "Toutes les chaînes";

    private PlaylistRef? _playlist;
    private ChannelKind _kind = ChannelKind.Live;
    private bool _guide;
    private IReadOnlyList<ChannelItem> _items = Array.Empty<ChannelItem>();
    private List<ChannelRow> _rows = new();
    private ChannelItem? _selected;
    private bool _populating;
    private int _load;
    private readonly DispatcherQueueTimer _clock;
    private readonly Dictionary<string, IReadOnlyList<XtreamProgramme>> _shortEpg = new();

    public LivePage()
    {
        InitializeComponent();
        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(30);
        _clock.Tick += (_, _) => RefreshTimes();
        EpgStore.Changed += () => DispatcherQueue.TryEnqueue(OnGuideLoaded);
        IptvStore.Changed += () => DispatcherQueue.TryEnqueue(() => _ = ReloadPlaylistsAsync());
    }

    private static T Res<T>(string key) => (T)Application.Current.Resources[key];

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _clock.Start();
        if (e.NavigationMode == NavigationMode.Back && _playlist is not null)
        {
            RefreshTimes();
            return;
        }
        await ReloadPlaylistsAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _clock.Stop();

    // -------------------- sources --------------------

    private async Task ReloadPlaylistsAsync()
    {
        var playlists = IptvStore.Playlists;
        var any = playlists.Count > 0;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        FilterBar.Visibility = PlaylistPicker.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any)
        {
            _playlist = null;
            LiveView.Visibility = GuideView.Visibility = VodGrid.Visibility = Visibility.Collapsed;
            TitleText.Text = "TV en direct";
            Eyebrow.Text = "DIRECT";
            return;
        }
        _populating = true;
        PlaylistPicker.ItemsSource = playlists.Select(p => p.Name).ToList();
        var selected = IptvStore.Selected ?? playlists[0];
        PlaylistPicker.SelectedIndex = playlists.ToList().FindIndex(p => p.Id == selected.Id);
        _populating = false;
        if (_playlist?.Id != selected.Id)
        {
            _playlist = selected;
            _kind = ChannelKind.Live;
            await LoadAsync(refresh: false);
        }
    }

    /// <summary>Saving the choice raises IptvStore.Changed, which reloads.</summary>
    private void OnPlaylistChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating || PlaylistPicker.SelectedIndex < 0 || PlaylistPicker.SelectedIndex >= IptvStore.Playlists.Count) return;
        var playlist = IptvStore.Playlists[PlaylistPicker.SelectedIndex];
        if (playlist.Id != _playlist?.Id) IptvStore.Selected = playlist;
    }

    private async void OnAddPlaylist(object sender, RoutedEventArgs e) => await AddPlaylistDialog.ShowAsync(XamlRoot);

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        await LoadAsync(refresh: true);
        if (_playlist is not null && _kind == ChannelKind.Live) _ = EpgStore.LoadAsync(_playlist, _items, force: true);
    }

    // -------------------- loading --------------------

    private async Task LoadAsync(bool refresh)
    {
        if (_playlist is not { } playlist) return;
        var load = ++_load;
        BuildChips();
        TitleText.Text = playlist.Name;
        Loading.IsActive = true;
        Message.Visibility = Visibility.Collapsed;
        try
        {
            var items = await IptvStore.ItemsAsync(playlist, _kind, refresh);
            if (load != _load) return;
            _items = items;
        }
        catch (Exception ex)
        {
            if (load != _load) return;
            _items = Array.Empty<ChannelItem>();
            Message.Text = CoreService.Humanize(ex);
            Message.Visibility = Visibility.Visible;
        }
        finally
        {
            if (load == _load) Loading.IsActive = false;
        }

        Eyebrow.Text = _kind switch
        {
            ChannelKind.Movie => $"FILMS · {_items.Count}",
            ChannelKind.Series => $"SÉRIES · {_items.Count}",
            _ => $"DIRECT · {_items.Count} CHAÎNES",
        };
        _populating = true;
        var groups = _items.GroupBy(i => i.Group).Select(g => $"{g.Key}  ({g.Count()})").ToList();
        var all = _kind switch { ChannelKind.Movie => "Tous les films", ChannelKind.Series => "Toutes les séries", _ => AllGroups };
        GroupPicker.ItemsSource = new[] { $"{all}  ({_items.Count})" }.Concat(groups).ToList();
        GroupPicker.SelectedIndex = 0;
        _populating = false;
        if (_kind == ChannelKind.Live) _ = EpgStore.LoadAsync(playlist, _items);
        ApplyFilter();
    }

    private void BuildChips()
    {
        ModeChips.Children.Clear();
        if (_playlist?.IsXtream == true)
        {
            foreach (var (label, kind) in new[] { ("TV EN DIRECT", ChannelKind.Live), ("FILMS", ChannelKind.Movie), ("SÉRIES", ChannelKind.Series) })
                ModeChips.Children.Add(Chip(label, kind == _kind, async () => { _kind = kind; _guide = false; await LoadAsync(false); }));
        }
        ViewChips.Children.Clear();
        if (_kind == ChannelKind.Live)
        {
            var favCount = _items.Count(c => IptvStore.IsFavorite(c.Id));
            ViewChips.Children.Add(Chip("LISTE", !_guide && !_favoritesOnly, () => { _guide = false; _favoritesOnly = false; ApplyFilter(); return Task.CompletedTask; }));
            ViewChips.Children.Add(Chip($"★ FAVORIS ({favCount})", _favoritesOnly, () => { _favoritesOnly = !_favoritesOnly; if (_favoritesOnly) _guide = false; ApplyFilter(); return Task.CompletedTask; }));
            ViewChips.Children.Add(Chip("GUIDE TV", _guide, () => { _guide = true; _favoritesOnly = false; ShowView(); return Task.CompletedTask; }));
            if (_selected is { } sel)
            {
                var isFav = IptvStore.IsFavorite(sel.Id);
                ViewChips.Children.Add(Chip(isFav ? "★ RETIRER FAVORI" : "☆ AJOUTER FAVORI", isFav, () => { IptvStore.ToggleFavorite(sel.Id); ApplyFilter(); return Task.CompletedTask; }));
            }
            var activeGroup = GroupPicker.SelectedIndex > 0 ? VisibleGroups().ElementAtOrDefault(GroupPicker.SelectedIndex - 1)?.Key : null;
            if (activeGroup is not null)
                ViewChips.Children.Add(Chip("MASQUER CE BOUQUET", false, () => { IptvStore.ToggleHiddenGroup(activeGroup); GroupPicker.SelectedIndex = 0; ApplyFilter(); return Task.CompletedTask; }));
            if (IptvStore.HiddenGroups.Count > 0)
                ViewChips.Children.Add(Chip($"RÉAFFICHER ({IptvStore.HiddenGroups.Count})", false, () => { foreach (var g in IptvStore.HiddenGroups.ToList()) IptvStore.ToggleHiddenGroup(g); ApplyFilter(); return Task.CompletedTask; }));
        }
        Filter.PlaceholderText = _kind switch { ChannelKind.Movie => "Rechercher un film", ChannelKind.Series => "Rechercher une série", _ => "Rechercher une chaîne" };
    }

    private bool _favoritesOnly;
    private List<IGrouping<string, ChannelItem>> VisibleGroups() =>
        _items.GroupBy(i => i.Group).Where(g => !IptvStore.IsGroupHidden(g.Key) && (!AppPrefs.Current.KidsMode || !AppPrefs.IsAdultIptvGroup(g.Key))).ToList();

    private ToggleButton Chip(string label, bool isChecked, Func<Task> action)
    {
        var chip = new ToggleButton { Content = label, IsChecked = isChecked, Style = Res<Style>("VeltrioChip") };
        chip.Click += async (_, _) =>
        {
            await action();
            BuildChips();
        };
        return chip;
    }

    private IEnumerable<ChannelItem> Filtered()
    {
        var group = GroupPicker.SelectedIndex > 0 ? VisibleGroups().ElementAtOrDefault(GroupPicker.SelectedIndex - 1)?.Key : null;
        var text = Filter.Text.Trim();
        return _items.Where(c => (!AppPrefs.Current.KidsMode || !AppPrefs.IsAdultIptvGroup(c.Group)) && (_favoritesOnly ? IptvStore.IsFavorite(c.Id) : (!IptvStore.IsGroupHidden(c.Group) && (group is null || c.Group == group))))
                     .Where(c => text.Length == 0 || c.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase));
    }

    private void ApplyFilter()
    {
        var visible = Filtered().ToList();
        if (_kind == ChannelKind.Live)
        {
            var now = DateTime.UtcNow;
            _rows = visible.Select(c => new ChannelRow(c)).ToList();
            foreach (var row in _rows) row.Refresh(now);
            ChannelList.ItemsSource = _rows;
            var keep = _rows.FindIndex(r => r.Channel.Id == _selected?.Id);
            ChannelList.SelectedIndex = keep >= 0 ? keep : _rows.Count > 0 ? 0 : -1;
            if (_rows.Count == 0) ShowHero(null);
        }
        else
        {
            VodGrid.ItemsSource = visible;
        }
        var text = Filter.Text.Trim();
        Message.Text = text.Length > 0 ? $"Rien ne correspond à « {text} »." : "Rien dans ce groupe.";
        Message.Visibility = visible.Count == 0 && !Loading.IsActive && _items.Count > 0 ? Visibility.Visible : Message.Visibility;
        ShowView();
    }

    private void ShowView()
    {
        var live = _kind == ChannelKind.Live;
        LiveView.Visibility = live && !_guide ? Visibility.Visible : Visibility.Collapsed;
        GuideView.Visibility = live && _guide ? Visibility.Visible : Visibility.Collapsed;
        VodGrid.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        if (live && _guide) BuildGuide();
    }

    private void OnGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_populating) ApplyFilter();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnGuideLoaded()
    {
        if (_kind != ChannelKind.Live) return;
        RefreshTimes();
        if (_guide) BuildGuide();
        if (EpgStore.Error is { } error && _items.Count > 0)
        {
            Message.Text = error;
            Message.Visibility = Visibility.Visible;
        }
    }

    private void RefreshTimes()
    {
        var now = DateTime.UtcNow;
        foreach (var row in _rows) row.Refresh(now);
        ShowHero(_selected);
    }

    // -------------------- list + hero --------------------

    private void OnChannelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelList.SelectedItem is ChannelRow row) ShowHero(row.Channel);
    }

    private void OnChannelDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Watch(_selected);

    private void OnChannelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        Watch(_selected);
        e.Handled = true;
    }

    private void ShowHero(ChannelItem? channel)
    {
        _selected = channel;
        Hero.Visibility = channel is null ? Visibility.Collapsed : Visibility.Visible;
        if (channel is null) return;

        var now = DateTime.UtcNow;
        var current = EpgStore.Current(channel, now);
        var upcoming = EpgStore.Upcoming(channel, now).Take(5).ToList();

        HeroName.Text = channel.Name;
        HeroGroup.Text = channel.Group.ToUpperInvariant();
        HeroNumber.Text = channel.Number > 0 ? $"CH {channel.NumberLabel}" : "";
        HeroImage.Source = Uri.TryCreate(current?.Image, UriKind.Absolute, out var still) ? new BitmapImage(still) : null;
        HeroLogo.Source = current?.Image is null && Uri.TryCreate(channel.Logo, UriKind.Absolute, out var logo) ? new BitmapImage(logo) : null;
        CatchupButton.Visibility = channel.HasCatchup && _playlist?.IsXtream == true ? Visibility.Visible : Visibility.Collapsed;

        if (current is not null)
        {
            ShowNow(current.Title, current.TimeRange, current.ProgressAt(now), current.Description);
            ShowUpNext(upcoming.Select(p => (p.Start.ToLocalTime().ToString("HH:mm"), p.Title)));
        }
        else if (_playlist?.Xtream is { } account && channel.XtreamId is { } id)
        {
            // No XMLTV entry: Xtream's short EPG for this one channel.
            if (_shortEpg.TryGetValue(id, out var shortEpg)) ShowShortEpg(shortEpg, now);
            else
            {
                ShowNow(null, null, 0, null);
                _ = LoadShortEpgAsync(account, channel, id);
            }
        }
        else
        {
            ShowNow(null, null, 0, null);
            ShowUpNext(upcoming.Select(p => (p.Start.ToLocalTime().ToString("HH:mm"), p.Title)));
        }
    }

    private async Task LoadShortEpgAsync(XtreamAccount account, ChannelItem channel, string id)
    {
        var entries = await XtreamClient.ShortEpgAsync(account, id);
        _shortEpg[id] = entries;
        if (_selected?.Id == channel.Id) ShowShortEpg(entries, DateTime.UtcNow);
    }

    private void ShowShortEpg(IReadOnlyList<XtreamProgramme> entries, DateTime now)
    {
        var current = entries.FirstOrDefault(p => p.Start <= now && now < p.End);
        ShowNow(current?.Title, current is null ? null : $"{current.Start.ToLocalTime():HH:mm} – {current.End.ToLocalTime():HH:mm}",
            current is null ? 0 : (now - current.Start) / (current.End - current.Start), current?.Description);
        ShowUpNext(entries.Where(p => p.Start > now).Take(5).Select(p => (p.Start.ToLocalTime().ToString("HH:mm"), p.Title)));
    }

    private void ShowNow(string? title, string? time, double progress, string? description)
    {
        NowPanel.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
        NoGuide.Visibility = title is null ? Visibility.Visible : Visibility.Collapsed;
        NowTitle.Text = title ?? "";
        NowTime.Text = time ?? "";
        NowProgress.Value = Math.Clamp(progress, 0, 1) * 100;
        NowDescription.Text = description ?? "";
        NowDescription.Visibility = string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowUpNext(IEnumerable<(string Time, string Title)> next)
    {
        UpNextList.Children.Clear();
        foreach (var (time, title) in next)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            line.Children.Add(new TextBlock { Text = time, Style = Res<Style>("VeltrioNumeral"), Width = 46 });
            line.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = Res<Brush>("VeltrioTextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            UpNextList.Children.Add(line);
        }
        UpNextPanel.Visibility = UpNextList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWatch(object sender, RoutedEventArgs e) => Watch(_selected);

    private void Watch(ChannelItem? channel)
    {
        if (channel is not null) Navigator.Play(PlayRequest.ForChannel(channel, _playlist));
    }

    /// <summary>Past programmes the provider keeps, to replay one.</summary>
    private async void OnCatchup(object sender, RoutedEventArgs e)
    {
        if (_selected is not { XtreamId: { } id } channel || _playlist?.Xtream is not { } account) return;
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
        var loading = new MenuFlyoutItem { Text = "Chargement…", IsEnabled = false };
        menu.Items.Add(loading);
        menu.ShowAt((FrameworkElement)sender);
        var programmes = await XtreamClient.CatchupAsync(account, id);
        menu.Items.Clear();
        if (programmes.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = "Aucun programme à revoir.", IsEnabled = false });
        foreach (var p in programmes.Take(40))
        {
            var item = new MenuFlyoutItem { Text = $"{p.TimeRange}   {p.Title}" };
            item.Click += (_, _) => PlayCatchup(channel, account, p);
            menu.Items.Add(item);
        }
    }

    private void PlayCatchup(ChannelItem channel, XtreamAccount account, XtreamProgramme p) =>
        Navigator.Play(new PlayRequest
        {
            Title = channel.Name,
            Subtitle = $"Replay · {p.TimeRange} · {p.Title}",
            Url = XtreamClient.TimeshiftUrl(account, channel.XtreamId!, p),
            Channel = channel,
            Playlist = _playlist,
        });

    // -------------------- films / series --------------------

    private void OnVodClick(object sender, ItemClickEventArgs e)
    {
        var item = (ChannelItem)e.ClickedItem;
        if (item.Kind == ChannelKind.Series && _playlist is not null)
            Navigator.Go(typeof(IptvSeriesPage), new IptvSeriesRequest(_playlist, item));
        else
            Navigator.Play(PlayRequest.ForChannel(item, _playlist));
    }

    // -------------------- guide grid --------------------

    private const double PxPerMinute = 5, RowHeight = 58, NameWidth = 230, HeaderHeight = 34;
    private readonly List<FrameworkElement> _pinnedLeft = new();
    private readonly List<FrameworkElement> _pinnedTop = new();
    private FrameworkElement? _corner;

    /// <summary>Channels as rows, programmes as blocks on a time axis from
    /// half an hour back to six hours ahead. The channel column and the
    /// time ruler stay pinned while scrolling.</summary>
    private void BuildGuide()
    {
        GuideCanvas.Children.Clear();
        _pinnedLeft.Clear();
        _pinnedTop.Clear();
        var now = DateTime.UtcNow;
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute >= 30 ? 30 : 0, 0, DateTimeKind.Utc).AddMinutes(-30);
        var end = start.AddHours(6.5);
        double X(DateTime t) => NameWidth + (t - start).TotalMinutes * PxPerMinute;
        var channels = Filtered().Take(300).ToList();
        GuideCanvas.Width = X(end) + 20;
        GuideCanvas.Height = HeaderHeight + channels.Count * RowHeight + 20;

        if (!EpgStore.HasGuide)
        {
            var text = new TextBlock
            {
                Text = EpgStore.IsLoading ? "Chargement du guide TV…" : "Pas de guide TV pour cette playlist.",
                Style = Res<Style>("VeltrioBody"),
            };
            Canvas.SetLeft(text, 0);
            Canvas.SetTop(text, 12);
            GuideCanvas.Children.Add(text);
            return;
        }

        // Time ruler.
        for (var t = start; t < end; t = t.AddMinutes(30))
        {
            var label = new TextBlock { Text = t.ToLocalTime().ToString("HH:mm"), Style = Res<Style>("VeltrioNumeral"), Padding = new Thickness(6, 9, 0, 0) };
            var cell = new Border { Width = 30 * PxPerMinute, Height = HeaderHeight, Background = Res<Brush>("VeltrioBackgroundBrush"), Child = label,
                BorderBrush = Res<Brush>("VeltrioBorderBrush"), BorderThickness = new Thickness(1, 0, 0, 1) };
            Place(cell, X(t), 0, 5);
            _pinnedTop.Add(cell);
        }

        for (var i = 0; i < channels.Count; i++)
        {
            var channel = channels[i];
            var y = HeaderHeight + i * RowHeight;
            foreach (var p in EpgStore.Schedule(channel).Where(p => p.End > start && p.Start < end))
            {
                var left = X(p.Start < start ? start : p.Start);
                var width = Math.Max(4, X(p.End > end ? end : p.End) - left - 2);
                var isNow = p.Start <= now && now < p.End;
                var isPast = p.End <= now;
                var canReplay = isPast && channel.HasCatchup && _playlist?.IsXtream == true;
                var block = new Border
                {
                    Width = width,
                    Height = RowHeight - 6,
                    Padding = new Thickness(8, 6, 6, 6),
                    Background = Res<Brush>(isNow ? "VeltrioAccentSoftBrush" : "VeltrioSurfaceBrush"),
                    BorderBrush = Res<Brush>(isNow ? "VeltrioAccentBrush" : "VeltrioBorderBrush"),
                    BorderThickness = new Thickness(isNow ? 2 : 1, 0, 0, 0),
                    Opacity = isPast && !canReplay ? 0.45 : 1,
                    Child = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = p.Title, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                                Foreground = Res<Brush>("VeltrioTextPrimaryBrush") },
                            new TextBlock { Text = canReplay ? $"{p.TimeRange} · REPLAY" : p.TimeRange, Style = Res<Style>("VeltrioCaption") },
                        },
                    },
                };
                ToolTipService.SetToolTip(block, string.IsNullOrEmpty(p.Description) ? p.Title : $"{p.Title}\n\n{p.Description}");
                if (isNow) block.Tapped += (_, _) => Watch(channel);
                else if (canReplay && _playlist?.Xtream is { } account)
                    block.Tapped += (_, _) => PlayCatchup(channel, account, new XtreamProgramme { Start = p.Start, End = p.End, Title = p.Title });
                Place(block, left + 1, y + 3, 1);
            }

            // Channel name cell, pinned left.
            var name = new Border
            {
                Width = NameWidth, Height = RowHeight, Padding = new Thickness(0, 0, 12, 0),
                Background = Res<Brush>("VeltrioBackgroundBrush"),
                BorderBrush = Res<Brush>("VeltrioBorderBrush"), BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = channel.NumberLabel, Style = Res<Style>("VeltrioCaption"), Width = 30, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = channel.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxWidth = NameWidth - 50, VerticalAlignment = VerticalAlignment.Center, Foreground = Res<Brush>("VeltrioTextPrimaryBrush") },
                    },
                },
            };
            name.Tapped += (_, _) => Watch(channel);
            Place(name, 0, y, 6);
            _pinnedLeft.Add(name);
        }

        // Now line.
        var line = new Rectangle { Width = 2, Height = GuideCanvas.Height - HeaderHeight, Fill = Res<Brush>("VeltrioAccentBrush"), IsHitTestVisible = false };
        Place(line, X(now), HeaderHeight, 4);
        _corner = new Border { Width = NameWidth, Height = HeaderHeight, Background = Res<Brush>("VeltrioBackgroundBrush"),
            BorderBrush = Res<Brush>("VeltrioBorderBrush"), BorderThickness = new Thickness(0, 0, 1, 1) };
        Place(_corner, 0, 0, 7);

        // Open on "now", a little before it.
        GuideScroller.ChangeView(Math.Max(0, X(now) - NameWidth - 120), null, null, disableAnimation: true);
        OnGuideScrolled(GuideScroller, null);
    }

    private void Place(FrameworkElement element, double x, double y, int z)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        Canvas.SetZIndex(element, z);
        GuideCanvas.Children.Add(element);
    }

    private void OnGuideScrolled(object? sender, ScrollViewerViewChangedEventArgs? e)
    {
        var dx = GuideScroller.HorizontalOffset;
        var dy = GuideScroller.VerticalOffset;
        foreach (var cell in _pinnedLeft) Canvas.SetLeft(cell, dx);
        foreach (var cell in _pinnedTop) Canvas.SetTop(cell, dy);
        if (_corner is not null)
        {
            Canvas.SetLeft(_corner, dx);
            Canvas.SetTop(_corner, dy);
        }
    }
}
