using System.Reflection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;
using static Veltrio.Pages.ServiceCard;

namespace Veltrio.Pages;

/// <summary>Settings: debrid services, Stremio / Trakt / SIMKL accounts,
/// playback defaults, theme, storage, versions.</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataFolder.Text = Storage.Folder;
        // The informational version keeps a pre-release suffix (0.1.0-beta.3).
        var app = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";
        string core;
        try { core = CoreService.Version; }
        catch { core = "indisponible"; }
        Versions.Text = $"Veltrio {app} · core {core} · libmpv · Windows App SDK";
        LoadPlaybackChoices();
        BuildThemeChips();
        _ = MeasureTorrentCacheAsync();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        DebridService.Changed += OnDebridChanged;
        StremioAccountService.Changed += OnAccountsChanged;
        TmdbService.Changed += OnAccountsChanged;
        TraktService.Changed += OnAccountsChanged;
        SimklService.Changed += OnAccountsChanged;
        DownloadManager.Changed += OnDownloadsChanged;
        Updater.Changed += OnUpdaterChanged;
        ShowUpdateState();
        BuildDebridCards();
        BuildAccountCards();
        BuildDownloads();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        DebridService.Changed -= OnDebridChanged;
        StremioAccountService.Changed -= OnAccountsChanged;
        TmdbService.Changed -= OnAccountsChanged;
        TraktService.Changed -= OnAccountsChanged;
        SimklService.Changed -= OnAccountsChanged;
        DownloadManager.Changed -= OnDownloadsChanged;
        Updater.Changed -= OnUpdaterChanged;
    }

    private void OnUpdaterChanged() => DispatcherQueue.TryEnqueue(ShowUpdateState);

    // -------------------- updates --------------------

    private void ShowUpdateState()
    {
        UpdateButton.IsEnabled = Updater.State is Updater.UpdateState.Idle or Updater.UpdateState.UpToDate
            or Updater.UpdateState.Available or Updater.UpdateState.Failed or Updater.UpdateState.Ready;
        UpdateLabel.Text = Updater.State switch
        {
            Updater.UpdateState.Available => "METTRE À JOUR",
            Updater.UpdateState.Ready => "REDÉMARRER POUR METTRE À JOUR",
            _ => "RECHERCHER UNE MISE À JOUR",
        };
        UpdateStatus.Text = Updater.State switch
        {
            Updater.UpdateState.NotInstalled => "Les mises à jour automatiques concernent la version installée (Veltrio-win-Setup.exe).",
            Updater.UpdateState.Checking => "Recherche…",
            Updater.UpdateState.UpToDate => $"Veltrio {Updater.CurrentVersion} est à jour.",
            Updater.UpdateState.Available => $"Une mise à jour est disponible (Veltrio {Updater.NewVersion}).",
            Updater.UpdateState.Downloading => $"Téléchargement de la version {Updater.NewVersion}… {Updater.Progress} %",
            Updater.UpdateState.Ready => $"La version {Updater.NewVersion} est prête à installer.",
            Updater.UpdateState.Failed => $"Recherche impossible : {Updater.Error}",
            _ => "",
        };
        if (Updater.State == Updater.UpdateState.NotInstalled) UpdateButton.Visibility = Visibility.Collapsed;
    }

    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (Updater.State == Updater.UpdateState.Available) _ = Updater.DownloadAndInstallAsync();
        else if (Updater.State == Updater.UpdateState.Ready) Updater.RestartNow();
        else _ = Updater.CheckAsync();
    }

    private void OnShowOnboarding(object sender, RoutedEventArgs e)
    {
        AppPrefs.Current.OnboardingDone = false;
        AppPrefs.Current.Save();
        Navigator.Go(typeof(OnboardingPage));
    }

    private void OnDownloadsChanged() => DispatcherQueue.TryEnqueue(BuildDownloads);

    private void OnDebridChanged() => DispatcherQueue.TryEnqueue(BuildDebridCards);

    private void OnAccountsChanged() => DispatcherQueue.TryEnqueue(BuildAccountCards);

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnOpenSources(object sender, RoutedEventArgs e) => Navigator.Window.ShowSection(3);

    private async void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Storage.Folder);
        await Windows.System.Launcher.LaunchFolderPathAsync(Storage.Folder);
    }

    // -------------------- playback --------------------

    private static readonly (string Value, string Label)[] Decoders =
    {
        ("auto-safe", "Automatique (sûr)"), ("auto", "Automatique (tout essayer)"), ("d3d11va", "Direct3D 11"), ("no", "Logiciel uniquement"),
    };

    private static readonly int[] CacheSizes = { 50, 150, 300, 500, 1000, 1500 };

    private bool _loadingChoices;

    private void LoadPlaybackChoices()
    {
        _loadingChoices = true;
        var prefs = Player.PlayerPrefs.Current;
        Fill(AudioLang, Player.PlayerPrefs.Languages.Select(l => (l.Code, l.Name)), prefs.AudioLang);
        Fill(SubtitleLang, Player.PlayerPrefs.Languages.Select(l => (l.Code, l.Code == "" ? "Aucune préférence" : l.Name)), prefs.SubtitleLang);
        Fill(Hwdec, Decoders, prefs.Hwdec);
        Fill(CacheSize, CacheSizes.Select(n => (n.ToString(), $"{n} Mio")), prefs.CacheMiB.ToString());
        AutoNext.IsOn = prefs.AutoNextEpisode;
        _loadingChoices = false;
    }

    private static void Fill(ComboBox box, IEnumerable<(string Value, string Label)> options, string selected)
    {
        box.Items.Clear();
        foreach (var (value, label) in options)
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            box.Items.Add(item);
            if (value == selected) box.SelectedItem = item;
        }
        box.SelectedIndex = Math.Max(0, box.SelectedIndex);
    }

    private static string Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void OnPlaybackChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingChoices) return;
        var prefs = Player.PlayerPrefs.Current;
        prefs.AudioLang = Selected(AudioLang);
        prefs.SubtitleLang = Selected(SubtitleLang);
        prefs.Hwdec = Selected(Hwdec) is { Length: > 0 } hw ? hw : "auto-safe";
        prefs.CacheMiB = int.TryParse(Selected(CacheSize), out var mib) ? mib : 150;
        prefs.AutoNextEpisode = AutoNext.IsOn;
        prefs.Save();
    }

    // -------------------- appearance --------------------

    private static readonly AppTheme StartedWith = AppPrefs.Current.Theme;

    private void BuildThemeChips()
    {
        ThemeChips.Children.Clear();
        foreach (var (theme, label) in new[] { (AppTheme.Dark, "SOMBRE"), (AppTheme.Light, "CLAIR"), (AppTheme.System, "COMME WINDOWS") })
        {
            var chip = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = label, IsChecked = AppPrefs.Current.Theme == theme, Style = Res<Style>("VeltrioChip"),
            };
            chip.Click += (_, _) =>
            {
                AppPrefs.Current.Theme = theme;
                AppPrefs.Current.Save();
                BuildThemeChips();
            };
            ThemeChips.Children.Add(chip);
        }
        RestartButton.Visibility = AppPrefs.Current.Theme != StartedWith ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRestart(object sender, RoutedEventArgs e) => Microsoft.Windows.AppLifecycle.AppInstance.Restart("");

    // -------------------- storage --------------------

    /// <summary>Where the core's torrent engine downloads (its temp folder).</summary>
    private static readonly string TorrentFolder = Path.Combine(Path.GetTempPath(), "veltrio-torrents");

    private async Task MeasureTorrentCacheAsync()
    {
        var bytes = await Task.Run(() =>
        {
            try
            {
                return Directory.Exists(TorrentFolder)
                    ? new DirectoryInfo(TorrentFolder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                    : 0L;
            }
            catch
            {
                return 0L;
            }
        });
        TorrentCache.Text = $"{TorrentFolder} · {FormatSize(bytes)}";
        ClearCacheButton.IsEnabled = bytes > 0;
    }

    private void BuildDownloads()
    {
        DownloadList.Children.Clear();
        var items = DownloadManager.Items.OrderByDescending(d => d.CreatedAt).ToList();
        NoDownloads.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in items) DownloadList.Children.Add(DownloadRow(item));
    }

    private FrameworkElement DownloadRow(DownloadItem item)
    {
        var row = new Grid
        {
            Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 16,
            Background = Res<Brush>("VeltrioSurfaceBrush"), BorderBrush = Res<Brush>("VeltrioBorderBrush"), BorderThickness = new Thickness(1),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { Spacing = 4 };
        texts.Children.Add(new TextBlock
        {
            Text = item.Subtitle is { Length: > 0 } sub ? $"{item.Title} · {sub}" : item.Title,
            FontFamily = Res<FontFamily>("VeltrioSerif"), FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("VeltrioTextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var size = item.BytesExpected > 0 ? $"{FormatSize(item.BytesDownloaded)} sur {FormatSize(item.BytesExpected)}" : FormatSize(item.BytesDownloaded);
        texts.Children.Add(new TextBlock
        {
            Text = item.Status switch
            {
                DownloadStatus.Completed => $"TÉLÉCHARGÉ · {FormatSize(item.BytesDownloaded).ToUpperInvariant()}",
                DownloadStatus.Downloading => $"EN COURS · {item.Progress:P0} · {size.ToUpperInvariant()}",
                DownloadStatus.Paused => $"EN PAUSE · {item.Progress:P0}",
                _ => $"ÉCHEC · {item.Error}",
            },
            Style = Res<Style>(item.Status == DownloadStatus.Failed ? "VeltrioBody" : "VeltrioEyebrow"),
            Foreground = Res<Brush>(item.Status == DownloadStatus.Failed ? "VeltrioDangerBrush" : "VeltrioTextTertiaryBrush"),
            FontSize = item.Status == DownloadStatus.Failed ? 12 : 10, TextWrapping = TextWrapping.Wrap,
        });
        if (item.Status is DownloadStatus.Downloading or DownloadStatus.Paused)
            texts.Children.Add(new ProgressBar { Value = item.Progress * 100, Maximum = 100, MinHeight = 2 });
        row.Children.Add(texts);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        switch (item.Status)
        {
            case DownloadStatus.Completed:
                actions.Children.Add(ActionButton("LIRE", ghost: false, () => Navigator.Play(new PlayRequest
                {
                    Title = item.Title, Subtitle = item.Subtitle, Url = item.LocalPath,
                    ContentType = item.ContentType, ContentId = item.ContentId,
                    VideoId = item.ContentType == "series" ? item.Id : null,
                    Season = item.Season, Episode = item.Episode, Poster = item.Poster,
                })));
                break;
            case DownloadStatus.Downloading:
                actions.Children.Add(ActionButton("PAUSE", ghost: true, () => DownloadManager.Pause(item.Id)));
                break;
            default:
                actions.Children.Add(ActionButton("REPRENDRE", ghost: true, () => DownloadManager.Resume(item.Id)));
                break;
        }
        actions.Children.Add(ActionButton("SUPPRIMER", ghost: true, () => DownloadManager.Remove(item.Id)));
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        return row;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} Mo",
        > 0 => "moins de 1 Mo",
        _ => "vide",
    };

    private async void OnClearCache(object sender, RoutedEventArgs e)
    {
        ClearCacheButton.IsEnabled = false;
        await Task.Run(() =>
        {
            if (!Directory.Exists(TorrentFolder)) return;
            foreach (var entry in new DirectoryInfo(TorrentFolder).EnumerateFileSystemInfos())
            {
                // A file the engine still holds open stays.
                try
                {
                    if (entry is DirectoryInfo dir) dir.Delete(recursive: true);
                    else entry.Delete();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        });
        await MeasureTorrentCacheAsync();
    }

    // -------------------- debrid cards --------------------

    private void BuildDebridCards()
    {
        DebridCards.Children.Clear();
        foreach (var account in DebridService.Accounts) DebridCards.Children.Add(Card(account));
    }

    private FrameworkElement Card(DebridAccount account)
    {
        var (card, actions) = Shell(account.Name, account.Color,
            account.IsConnected ? Status(account) : account.Tagline, account.IsConnected);

        if (account.IsConnected)
        {
            actions.Children.Add(ActionButton("DÉCONNECTER", ghost: true, () => DebridService.Disconnect(account.Provider)));
        }
        else if (account.UsesCode)
        {
            if (account.PendingCode is null)
            {
                actions.Children.Add(ActionButton(account.IsBusy ? "…" : "CONNECTER", ghost: false,
                    () => _ = DebridService.StartCodeFlowAsync(account.Provider), enabled: !account.IsBusy));
            }
            else
            {
                actions.Children.Add(ActionButton("ANNULER", ghost: true, DebridService.CancelCodeFlow));
                AddRow(card, CodePanel(account.PendingCode, account.PendingUrl!));
            }
        }
        else
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new PasswordBox { PlaceholderText = $"Clé d'API {account.Name}" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"Clé {account.Name}");
            row.Children.Add(box);
            var connect = ActionButton(account.IsBusy ? "VÉRIFICATION…" : "CONNECTER", ghost: false,
                () => _ = DebridService.ConnectWithKeyAsync(account.Provider, box.Password), enabled: !account.IsBusy);
            Grid.SetColumn(connect, 1);
            row.Children.Add(connect);
            var where = ActionButton("OÙ LA TROUVER ?", ghost: true, () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(account.KeyPageUrl)));
            Grid.SetColumn(where, 2);
            row.Children.Add(where);
            AddRow(card, row);
        }

        AddError(card, account.Error);
        return card;
    }

    private static string Status(DebridAccount a)
    {
        var parts = new List<string> { "CONNECTÉ" };
        if (!string.IsNullOrEmpty(a.User)) parts.Add(a.User!.ToUpperInvariant());
        if (a.Premium is { } premium) parts.Add(premium ? "PREMIUM" : "COMPTE GRATUIT");
        return string.Join(" · ", parts);
    }

    // -------------------- account cards --------------------

    private string _stremioEmail = "";

    private void BuildAccountCards()
    {
        AccountCards.Children.Clear();
        AccountCards.Children.Add(StremioCard());
        AccountCards.Children.Add(TmdbCard());
        AccountCards.Children.Add(TraktCard());
        AccountCards.Children.Add(SimklCard());
    }

    private FrameworkElement TmdbCard()
    {
        var connected = TmdbService.IsConnected;
        var (card, actions) = Shell("TMDB", "#01B4E4",
            connected ? $"CONNECTÉ · {TmdbService.Language}" : "Optionnel · titres et résumés traduits.", connected);
        if (connected)
        {
            actions.Children.Add(ActionButton("DÉCONNECTER", ghost: true, TmdbService.Disconnect));
            var box = new ComboBox { MinWidth = 220 };
            foreach (var (code, label) in TmdbService.Languages)
            {
                var item = new ComboBoxItem { Content = label, Tag = code };
                box.Items.Add(item);
                if (code == TmdbService.Language) box.SelectedItem = item;
            }
            box.SelectionChanged += (_, _) =>
            {
                if ((box.SelectedItem as ComboBoxItem)?.Tag is string lang) TmdbService.SetLanguage(lang);
            };
            AddRow(card, box);
        }
        else
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new PasswordBox { PlaceholderText = "Clé API ou jeton TMDB" };
            row.Children.Add(box);
            var connect = ActionButton(TmdbService.IsBusy ? "VÉRIFICATION…" : "CONNECTER", ghost: false,
                () => _ = TmdbService.ConnectAsync(box.Password), enabled: !TmdbService.IsBusy);
            Grid.SetColumn(connect, 1);
            row.Children.Add(connect);
            AddRow(card, row);
        }
        AddError(card, TmdbService.Error);
        AddNote(card, "Traduit automatiquement les titres et résumés des fiches Cinemeta dans la langue choisie (clé gratuite sur themoviedb.org/settings/api).");
        return card;
    }

    private FrameworkElement StremioCard()
    {
        var connected = StremioAccountService.IsConnected;
        var (card, actions) = Shell("Stremio", "#7B5BF5",
            connected ? $"CONNECTÉ · {StremioAccountService.Email?.ToUpperInvariant()}" : "Compte Stremio, par e-mail et mot de passe.", connected);
        if (connected)
        {
            actions.Children.Add(ActionButton("DÉCONNECTER", ghost: true, StremioAccountService.SignOut));
            AddRow(card, ImportRow("IMPORTER LA BIBLIOTHÈQUE", StremioAccountService.IsBusy, StremioAccountService.LastResult,
                () => _ = StremioAccountService.ImportLibraryAsync()));
            AddRow(card, ImportRow("INSTALLER LES ADDONS DU COMPTE", StremioAccountService.IsInstallingAddons, StremioAccountService.AddonResult,
                () => _ = StremioAccountService.InstallAccountAddonsAsync()));
        }
        else
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var email = new TextBox { PlaceholderText = "E-mail", Text = _stremioEmail, InputScope = new Microsoft.UI.Xaml.Input.InputScope
                { Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.EmailSmtpAddress) } } };
            email.TextChanged += (_, _) => _stremioEmail = email.Text;
            var password = new PasswordBox { PlaceholderText = "Mot de passe" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(email, "E-mail Stremio");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(password, "Mot de passe Stremio");
            void SignIn()
            {
                if (email.Text.Trim().Length > 0 && password.Password.Length > 0)
                    _ = StremioAccountService.SignInAsync(email.Text.Trim(), password.Password);
            }
            password.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) SignIn(); };
            row.Children.Add(email);
            Grid.SetColumn(password, 1);
            row.Children.Add(password);
            var connect = ActionButton(StremioAccountService.IsBusy ? "CONNEXION…" : "CONNECTER", ghost: false, SignIn,
                enabled: !StremioAccountService.IsBusy);
            Grid.SetColumn(connect, 2);
            row.Children.Add(connect);
            AddRow(card, row);
        }
        AddError(card, StremioAccountService.Error);
        AddNote(card, "Importe vos titres enregistrés, les films vus et l'endroit où vous vous êtes arrêté, et peut installer les addons du compte. Rien n'est renvoyé à Stremio.");
        return card;
    }

    private FrameworkElement TraktCard()
    {
        var connected = TraktService.IsConnected;
        var status = !connected ? TraktService.IsAvailable ? "Connexion par code, sans mot de passe." : "Indisponible dans cette version."
            : string.Join(" · ", new[] { "CONNECTÉ", TraktService.User?.ToUpperInvariant(), TraktService.IsVip ? "VIP" : null }.OfType<string>());
        var (card, actions) = Shell("Trakt", "#ED1C24", status, connected);
        if (connected)
        {
            actions.Children.Add(ActionButton("DÉCONNECTER", ghost: true, TraktService.Disconnect));
            AddRow(card, ImportRow("IMPORTER LISTE DE SUIVI ET HISTORIQUE", TraktService.IsImporting, TraktService.LastResult,
                () => _ = TraktService.ImportLibraryAsync()));
        }
        else if (TraktService.PendingCode is { } code)
        {
            actions.Children.Add(ActionButton("ANNULER", ghost: true, TraktService.CancelDeviceAuth));
            AddRow(card, CodePanel(code, TraktService.PendingUrl!));
        }
        else if (TraktService.IsAvailable)
        {
            actions.Children.Add(ActionButton(TraktService.IsBusy ? "…" : "CONNECTER", ghost: false,
                () => _ = TraktService.StartDeviceAuthAsync(), enabled: !TraktService.IsBusy));
        }
        AddError(card, TraktService.Error);
        AddNote(card, TraktService.IsAvailable
            ? "Veltrio indique à Trakt ce que vous regardez (début, pause, fin) et peut importer votre liste de suivi et votre historique dans la Collection."
            : "Trakt demande une application enregistrée (VELTRIO_TRAKT_CLIENT_ID et VELTRIO_TRAKT_CLIENT_SECRET, sur trakt.tv/oauth/applications), absente de cette version.");
        return card;
    }

    private FrameworkElement SimklCard()
    {
        var connected = SimklService.IsConnected;
        var (card, actions) = Shell("SIMKL", "#8A8A8A",
            connected ? "CONNECTÉ" : SimklService.IsAvailable ? "Connexion par code, sans mot de passe." : "Indisponible dans cette version.", connected);
        if (connected)
        {
            actions.Children.Add(ActionButton("DÉCONNECTER", ghost: true, SimklService.Disconnect));
            AddRow(card, ImportRow("IMPORTER LES LISTES", SimklService.IsImporting, SimklService.LastResult,
                () => _ = SimklService.ImportLibraryAsync()));
        }
        else if (SimklService.PendingCode is { } code)
        {
            actions.Children.Add(ActionButton("ANNULER", ghost: true, SimklService.CancelPinFlow));
            AddRow(card, CodePanel(code, SimklService.PendingUrl!));
        }
        else if (SimklService.IsAvailable)
        {
            actions.Children.Add(ActionButton(SimklService.IsBusy ? "…" : "CONNECTER", ghost: false,
                () => _ = SimklService.StartPinFlowAsync(), enabled: !SimklService.IsBusy));
        }
        AddError(card, SimklService.Error);
        AddNote(card, SimklService.IsAvailable
            ? "Veltrio indique à SIMKL ce que vous regardez et peut importer vos listes « à voir » et « terminé »."
            : "SIMKL demande un identifiant client propre à l'app (VELTRIO_SIMKL_CLIENT_ID, sur simkl.com/settings/developer), absent de cette version.");
        return card;
    }

    /// <summary>An import button with the last result beside it.</summary>
    private static FrameworkElement ImportRow(string title, bool working, string? result, Action action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        row.Children.Add(ActionButton(working ? "EN COURS…" : title, ghost: true, action, enabled: !working));
        if (result is not null)
            row.Children.Add(new TextBlock { Text = result, Style = Res<Style>("VeltrioBody"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
}
