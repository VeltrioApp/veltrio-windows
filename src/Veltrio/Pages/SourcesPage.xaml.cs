using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;
using Windows.System;

namespace Veltrio.Pages;

/// <summary>Addons (install, enable, order, remove), suggestions, the
/// IPTV playlists and the media servers.</summary>
public sealed partial class SourcesPage : Page
{
    public SourcesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        MediaServers.Changed += OnServersChanged;
        Refresh();
        BuildServerCards();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => MediaServers.Changed -= OnServersChanged;

    private void OnServersChanged() => DispatcherQueue.TryEnqueue(BuildServerCards);

    private static T Res<T>(string key) => (T)Application.Current.Resources[key];

    private void Refresh()
    {
        var addons = CoreService.Addons;
        AddonCount.Text = $"{addons.Count} ADDON{(addons.Count > 1 ? "S" : "")}";
        NoAddons.Visibility = addons.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddonList.Children.Clear();
        for (var i = 0; i < addons.Count; i++) AddonList.Children.Add(AddonRow(addons[i], i, addons.Count));

        SuggestedList.Children.Clear();
        foreach (var s in CoreService.SuggestedAddons) SuggestedList.Children.Add(SuggestedRow(s.Name, s.Url, s.Blurb));

        PlaylistList.Children.Clear();
        foreach (var p in IptvStore.Playlists) PlaylistList.Children.Add(PlaylistRow(p));
    }

    // -------------------- rows --------------------

    private static Grid Row()
    {
        var grid = new Grid
        {
            Padding = new Thickness(0, 16, 0, 16),
            ColumnSpacing = 18,
            BorderBrush = Res<Brush>("VeltrioBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    private static FrameworkElement Logo(string? url, string glyph)
    {
        var plate = new Grid { Width = 48, Height = 48, Background = Res<Brush>("VeltrioSurfaceBrush"), VerticalAlignment = VerticalAlignment.Top };
        plate.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18, Foreground = Res<Brush>("VeltrioTextQuaternaryBrush") });
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            plate.Children.Add(new Image { Source = new BitmapImage(uri), Stretch = Stretch.Uniform, Margin = new Thickness(6) });
        return plate;
    }

    private static StackPanel Texts(string title, string meta, string? body)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = Res<FontFamily>("VeltrioSerif"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("VeltrioTextPrimaryBrush"),
        });
        panel.Children.Add(new TextBlock { Text = meta, Style = Res<Style>("VeltrioCaption"), CharacterSpacing = 80 });
        if (!string.IsNullOrWhiteSpace(body))
            panel.Children.Add(new TextBlock { Text = body, Style = Res<Style>("VeltrioBody"), FontSize = 12, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis });
        return panel;
    }

    private static Button IconButton(string glyph, string tooltip, Action action, bool enabled = true)
    {
        var button = new Button
        {
            Style = Res<Style>("VeltrioLinkButton"),
            Content = new FontIcon { Glyph = glyph, FontSize = 13 },
            Padding = new Thickness(6),
            IsEnabled = enabled,
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private Grid AddonRow(AddonInfo addon, int index, int count)
    {
        var row = Row();
        var enabled = CoreService.IsEnabled(addon);
        row.Opacity = enabled ? 1 : 0.55;
        row.Children.Add(Logo(addon.Logo, ""));

        var types = string.Join(" · ", addon.Types.Select(t => CoreService.TypeLabel(t, plural: true).ToUpperInvariant()));
        var resources = string.Join(" · ", addon.Resources.Select(r => r switch
        {
            "catalog" => "CATALOGUES",
            "meta" => "FICHES",
            "stream" => "FLUX",
            "subtitles" => "SOUS-TITRES",
            "addon_catalog" => "CATALOGUE D'ADDONS",
            _ => r.ToUpperInvariant(),
        }));
        var texts = Texts(addon.Name, $"V{addon.Version}   {types}   {resources}", addon.Description);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var toggle = new ToggleSwitch { IsOn = enabled, OnContent = "", OffContent = "", MinWidth = 0, Width = 56 };
        ToolTipService.SetToolTip(toggle, enabled ? "Désactiver" : "Activer");
        toggle.Toggled += (_, _) => { CoreService.SetEnabled(addon, toggle.IsOn); Refresh(); };
        actions.Children.Add(toggle);
        actions.Children.Add(IconButton("", "Monter", () => { CoreService.Move(addon, -1); Refresh(); }, index > 0));
        actions.Children.Add(IconButton("", "Descendre", () => { CoreService.Move(addon, 1); Refresh(); }, index < count - 1));
        actions.Children.Add(IconButton("", "Supprimer", () => { CoreService.RemoveAddon(addon); Refresh(); }));
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        return row;
    }

    private Grid SuggestedRow(string name, string url, string blurb)
    {
        var row = Row();
        row.Children.Add(Logo(null, ""));
        var texts = Texts(name, url, blurb);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);

        var installed = CoreService.IsInstalled(url);
        var button = new Button
        {
            Style = Res<Style>(installed ? "VeltrioGhostButton" : "VeltrioPrimaryButton"),
            Content = installed ? "INSTALLÉ" : "INSTALLER",
            Padding = new Thickness(20, 9, 20, 9),
            IsEnabled = !installed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            button.Content = "INSTALLATION…";
            ShowError(InstallError, await CoreService.InstallAddonAsync(url));
            Refresh();
        };
        Grid.SetColumn(button, 2);
        row.Children.Add(button);
        return row;
    }

    private Grid PlaylistRow(PlaylistRef playlist)
    {
        var row = Row();
        row.Children.Add(Logo(null, ""));
        var texts = Texts(playlist.Name, playlist.SourceLabel, null);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        var remove = IconButton("", "Supprimer la playlist", () => { IptvStore.Remove(playlist); Refresh(); });
        remove.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);
        return row;
    }

    // -------------------- actions --------------------

    private static void ShowError(TextBlock block, string? error)
    {
        block.Text = error ?? "";
        block.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AddonUrl.Text)) return;
        InstallButton.IsEnabled = false;
        InstallLabel.Text = "INSTALLATION…";
        var error = await CoreService.InstallAddonAsync(AddonUrl.Text);
        InstallButton.IsEnabled = true;
        InstallLabel.Text = "INSTALLER";
        ShowError(InstallError, error);
        if (error is null) AddonUrl.Text = "";
        Refresh();
    }

    private void OnAddonUrlKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) OnInstall(sender, e);
    }

    private void OnBrowseCatalog(object sender, RoutedEventArgs e) => Navigator.Go(typeof(AddonCatalogPage));

    private async void OnAddPlaylist(object sender, RoutedEventArgs e)
    {
        if (await AddPlaylistDialog.ShowAsync(XamlRoot)) Refresh();
    }

    // -------------------- media servers --------------------

    // What's typed in a Jellyfin / Emby form survives the card being rebuilt.
    private readonly Dictionary<MediaServerKind, (string Url, string User)> _serverForms = new();

    private void BuildServerCards()
    {
        ServerCards.Children.Clear();
        foreach (var server in MediaServers.All) ServerCards.Children.Add(ServerCard(server));
    }

    private FrameworkElement ServerCard(MediaServer server)
    {
        var status = server.IsConnected
            ? string.Join(" · ", new[] { "CONNECTÉ", server.User?.ToUpperInvariant(), server.Host?.ToUpperInvariant() }.OfType<string>())
            : server is PlexServer ? "Connexion par code sur plex.tv, sans mot de passe." : "Adresse du serveur, nom d'utilisateur et mot de passe.";
        var (card, actions) = ServiceCard.Shell(server.Name, server.Color, status, server.IsConnected);
        if (server.IsConnected)
        {
            actions.Children.Add(ServiceCard.ActionButton("PARCOURIR", ghost: false, () =>
            {
                LibraryPage.RequestedServer = server.Kind;
                Navigator.Window.ShowSection(1);
            }));
            actions.Children.Add(ServiceCard.ActionButton("DÉCONNECTER", ghost: true, server.Disconnect));
        }
        else if (server is PlexServer plex)
        {
            if (plex.PendingCode is { } code)
            {
                actions.Children.Add(ServiceCard.ActionButton("ANNULER", ghost: true, plex.CancelPinFlow));
                ServiceCard.AddRow(card, ServiceCard.CodePanel(code, plex.PendingUrl!));
            }
            else
            {
                actions.Children.Add(ServiceCard.ActionButton(plex.IsBusy ? "…" : "CONNECTER", ghost: false,
                    () => _ = plex.StartPinFlowAsync(), enabled: !plex.IsBusy));
            }
        }
        else if (server is MediaBrowserServer browser)
        {
            ServiceCard.AddRow(card, LoginRow(browser));
        }
        ServiceCard.AddError(card, server.Error);
        return card;
    }

    private FrameworkElement LoginRow(MediaBrowserServer server)
    {
        var form = _serverForms.TryGetValue(server.Kind, out var typed) ? typed : (Url: server.Url ?? "", User: "");
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var url = new TextBox { PlaceholderText = $"http://{server.Name.ToLowerInvariant()}.local:8096", Text = form.Url, FontFamily = Res<FontFamily>("VeltrioMono") };
        var user = new TextBox { PlaceholderText = "Utilisateur", Text = form.User };
        var password = new PasswordBox { PlaceholderText = "Mot de passe" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(url, $"Adresse {server.Name}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(user, $"Utilisateur {server.Name}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(password, $"Mot de passe {server.Name}");
        void Remember() => _serverForms[server.Kind] = (url.Text, user.Text);
        url.TextChanged += (_, _) => Remember();
        user.TextChanged += (_, _) => Remember();
        void Connect()
        {
            if (url.Text.Trim().Length > 0 && user.Text.Trim().Length > 0)
                _ = server.ConnectAsync(url.Text, user.Text, password.Password);
        }
        password.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) Connect(); };
        row.Children.Add(url);
        Grid.SetColumn(user, 1);
        row.Children.Add(user);
        Grid.SetColumn(password, 2);
        row.Children.Add(password);
        var connect = ServiceCard.ActionButton(server.IsBusy ? "CONNEXION…" : "CONNECTER", ghost: false, Connect, enabled: !server.IsBusy);
        Grid.SetColumn(connect, 3);
        row.Children.Add(connect);
        return row;
    }
}
