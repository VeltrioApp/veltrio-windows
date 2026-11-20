using System.Reflection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>Settings: debrid services, Stremio / Trakt / SIMKL accounts,
/// where data lives, playback notes, versions.</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataFolder.Text = Storage.Folder;
        var app = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        string core;
        try { core = CoreService.Version; }
        catch { core = "indisponible"; }
        Versions.Text = $"Veltrio {app} · core {core} · libmpv · Windows App SDK";
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        DebridService.Changed += OnDebridChanged;
        StremioAccountService.Changed += OnAccountsChanged;
        TraktService.Changed += OnAccountsChanged;
        SimklService.Changed += OnAccountsChanged;
        BuildDebridCards();
        BuildAccountCards();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        DebridService.Changed -= OnDebridChanged;
        StremioAccountService.Changed -= OnAccountsChanged;
        TraktService.Changed -= OnAccountsChanged;
        SimklService.Changed -= OnAccountsChanged;
    }

    private void OnDebridChanged() => DispatcherQueue.TryEnqueue(BuildDebridCards);

    private void OnAccountsChanged() => DispatcherQueue.TryEnqueue(BuildAccountCards);

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnOpenSources(object sender, RoutedEventArgs e) => Navigator.Window.ShowSection(3);

    private async void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Storage.Folder);
        await Windows.System.Launcher.LaunchFolderPathAsync(Storage.Folder);
    }

    // -------------------- debrid cards --------------------

    private static T Res<T>(string key) => (T)Application.Current.Resources[key];

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
            actions.Children.Add(Button("DÉCONNECTER", ghost: true, () => DebridService.Disconnect(account.Provider)));
        }
        else if (account.UsesCode)
        {
            if (account.PendingCode is null)
            {
                actions.Children.Add(Button(account.IsBusy ? "…" : "CONNECTER", ghost: false,
                    () => _ = DebridService.StartCodeFlowAsync(account.Provider), enabled: !account.IsBusy));
            }
            else
            {
                actions.Children.Add(Button("ANNULER", ghost: true, DebridService.CancelCodeFlow));
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
            var connect = Button(account.IsBusy ? "VÉRIFICATION…" : "CONNECTER", ghost: false,
                () => _ = DebridService.ConnectWithKeyAsync(account.Provider, box.Password), enabled: !account.IsBusy);
            Grid.SetColumn(connect, 1);
            row.Children.Add(connect);
            var where = Button("OÙ LA TROUVER ?", ghost: true, () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(account.KeyPageUrl)));
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
        AccountCards.Children.Add(TraktCard());
        AccountCards.Children.Add(SimklCard());
    }

    private FrameworkElement StremioCard()
    {
        var connected = StremioAccountService.IsConnected;
        var (card, actions) = Shell("Stremio", "#7B5BF5",
            connected ? $"CONNECTÉ · {StremioAccountService.Email?.ToUpperInvariant()}" : "Compte Stremio, par e-mail et mot de passe.", connected);
        if (connected)
        {
            actions.Children.Add(Button("DÉCONNECTER", ghost: true, StremioAccountService.SignOut));
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
            var connect = Button(StremioAccountService.IsBusy ? "CONNEXION…" : "CONNECTER", ghost: false, SignIn,
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
            actions.Children.Add(Button("DÉCONNECTER", ghost: true, TraktService.Disconnect));
            AddRow(card, ImportRow("IMPORTER LISTE DE SUIVI ET HISTORIQUE", TraktService.IsImporting, TraktService.LastResult,
                () => _ = TraktService.ImportLibraryAsync()));
        }
        else if (TraktService.PendingCode is { } code)
        {
            actions.Children.Add(Button("ANNULER", ghost: true, TraktService.CancelDeviceAuth));
            AddRow(card, CodePanel(code, TraktService.PendingUrl!));
        }
        else if (TraktService.IsAvailable)
        {
            actions.Children.Add(Button(TraktService.IsBusy ? "…" : "CONNECTER", ghost: false,
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
        var (card, actions) = Shell("SIMKL", "#FFFFFF",
            connected ? "CONNECTÉ" : SimklService.IsAvailable ? "Connexion par code, sans mot de passe." : "Indisponible dans cette version.", connected);
        if (connected)
        {
            actions.Children.Add(Button("DÉCONNECTER", ghost: true, SimklService.Disconnect));
            AddRow(card, ImportRow("IMPORTER LES LISTES", SimklService.IsImporting, SimklService.LastResult,
                () => _ = SimklService.ImportLibraryAsync()));
        }
        else if (SimklService.PendingCode is { } code)
        {
            actions.Children.Add(Button("ANNULER", ghost: true, SimklService.CancelPinFlow));
            AddRow(card, CodePanel(code, SimklService.PendingUrl!));
        }
        else if (SimklService.IsAvailable)
        {
            actions.Children.Add(Button(SimklService.IsBusy ? "…" : "CONNECTER", ghost: false,
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
        row.Children.Add(Button(working ? "EN COURS…" : title, ghost: true, action, enabled: !working));
        if (result is not null)
            row.Children.Add(new TextBlock { Text = result, Style = Res<Style>("VeltrioBody"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    // -------------------- card parts --------------------

    /// <summary>The frame every service card shares: brand mark, name,
    /// status or tagline, and the actions on the right.</summary>
    private static (Grid Card, StackPanel Actions) Shell(string name, string color, string subtitle, bool connected)
    {
        var card = new Grid
        {
            Padding = new Thickness(18, 16, 18, 16),
            ColumnSpacing = 16,
            RowSpacing = 12,
            Background = Res<Brush>("VeltrioSurfaceBrush"),
            BorderBrush = Res<Brush>(connected ? "VeltrioAccentBrush" : "VeltrioBorderBrush"),
            BorderThickness = new Thickness(1),
        };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Brand mark: the service's colour, as on macOS.
        card.Children.Add(new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255,
                Convert.ToByte(color[1..3], 16), Convert.ToByte(color[3..5], 16), Convert.ToByte(color[5..7], 16))),
        });

        var texts = new StackPanel { Spacing = 3 };
        texts.Children.Add(new TextBlock
        {
            Text = name, FontFamily = Res<FontFamily>("VeltrioSerif"), FontSize = 18, FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("VeltrioTextPrimaryBrush"),
        });
        texts.Children.Add(new TextBlock
        {
            Text = subtitle,
            Style = Res<Style>(connected ? "VeltrioAccentEyebrow" : "VeltrioBody"),
            FontSize = connected ? 10 : 12,
        });
        Grid.SetColumn(texts, 1);
        card.Children.Add(texts);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(actions, 2);
        card.Children.Add(actions);
        return (card, actions);
    }

    /// <summary>A full-width row under the card's header.</summary>
    private static void AddRow(Grid card, FrameworkElement element)
    {
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(element, card.RowDefinitions.Count - 1);
        Grid.SetColumnSpan(element, 3);
        card.Children.Add(element);
    }

    private static void AddError(Grid card, string? error)
    {
        if (error is null) return;
        AddRow(card, new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Res<Brush>("VeltrioDangerBrush") });
    }

    private static void AddNote(Grid card, string text) =>
        AddRow(card, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Res<Brush>("VeltrioTextTertiaryBrush") });

    /// <summary>The code to enter on the service's site, big, with a link there.</summary>
    private static FrameworkElement CodePanel(string code, string url)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Ouvrez {url} et saisissez ce code. La connexion se fait toute seule ensuite.",
            Style = Res<Style>("VeltrioBody"), FontSize = 12,
        });
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        line.Children.Add(new TextBlock
        {
            Text = code, FontFamily = Res<FontFamily>("VeltrioMono"), FontSize = 30, FontWeight = FontWeights.Bold,
            CharacterSpacing = 200, Foreground = Res<Brush>("VeltrioAccentBrush"), IsTextSelectionEnabled = true,
        });
        line.Children.Add(Button("OUVRIR LA PAGE", ghost: true, () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url))));
        line.Children.Add(new ProgressRing { IsActive = true, Width = 18, Height = 18 });
        panel.Children.Add(line);
        return panel;
    }

    private static Button Button(string text, bool ghost, Action action, bool enabled = true)
    {
        var button = new Button
        {
            Content = text,
            Style = Res<Style>(ghost ? "VeltrioGhostButton" : "VeltrioPrimaryButton"),
            Padding = new Thickness(16, 8, 16, 8),
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => action();
        return button;
    }
}
