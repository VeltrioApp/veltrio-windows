using System.Reflection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>Settings: debrid accounts, where data lives, playback notes,
/// versions.</summary>
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
        BuildDebridCards();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => DebridService.Changed -= OnDebridChanged;

    private void OnDebridChanged() => DispatcherQueue.TryEnqueue(BuildDebridCards);

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
        var card = new Grid
        {
            Padding = new Thickness(18, 16, 18, 16),
            ColumnSpacing = 16,
            RowSpacing = 12,
            Background = Res<Brush>("VeltrioSurfaceBrush"),
            BorderBrush = Res<Brush>(account.IsConnected ? "VeltrioAccentBrush" : "VeltrioBorderBrush"),
            BorderThickness = new Thickness(1),
        };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Brand mark: the provider's colour, as on macOS.
        card.Children.Add(new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255,
                Convert.ToByte(account.Color[1..3], 16), Convert.ToByte(account.Color[3..5], 16), Convert.ToByte(account.Color[5..7], 16))),
        });

        var texts = new StackPanel { Spacing = 3 };
        texts.Children.Add(new TextBlock
        {
            Text = account.Name, FontFamily = Res<FontFamily>("VeltrioSerif"), FontSize = 18, FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("VeltrioTextPrimaryBrush"),
        });
        texts.Children.Add(new TextBlock
        {
            Text = account.IsConnected ? Status(account) : account.Tagline,
            Style = Res<Style>(account.IsConnected ? "VeltrioAccentEyebrow" : "VeltrioBody"),
            FontSize = account.IsConnected ? 10 : 12,
        });
        Grid.SetColumn(texts, 1);
        card.Children.Add(texts);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(actions, 2);
        card.Children.Add(actions);

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
                card.Children.Add(CodePanel(account));
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
            Grid.SetRow(row, 1);
            Grid.SetColumnSpan(row, 3);
            card.Children.Add(row);
        }

        if (account.Error is not null)
        {
            card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var error = new TextBlock { Text = account.Error, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Res<Brush>("VeltrioDangerBrush") };
            Grid.SetRow(error, 2);
            Grid.SetColumnSpan(error, 3);
            card.Children.Add(error);
        }
        return card;
    }

    /// <summary>The code to enter on the provider's site, big, with a link there.</summary>
    private static FrameworkElement CodePanel(DebridAccount account)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Ouvrez {account.PendingUrl} et saisissez ce code. La connexion se fait toute seule ensuite.",
            Style = Res<Style>("VeltrioBody"), FontSize = 12,
        });
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        line.Children.Add(new TextBlock
        {
            Text = account.PendingCode, FontFamily = Res<FontFamily>("VeltrioMono"), FontSize = 30, FontWeight = FontWeights.Bold,
            CharacterSpacing = 200, Foreground = Res<Brush>("VeltrioAccentBrush"), IsTextSelectionEnabled = true,
        });
        line.Children.Add(Button("OUVRIR LA PAGE", ghost: true, () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(account.PendingUrl!))));
        line.Children.Add(new ProgressRing { IsActive = true, Width = 18, Height = 18 });
        panel.Children.Add(line);
        Grid.SetRow(panel, 1);
        Grid.SetColumnSpan(panel, 3);
        return panel;
    }

    private static string Status(DebridAccount a)
    {
        var parts = new List<string> { "CONNECTÉ" };
        if (!string.IsNullOrEmpty(a.User)) parts.Add(a.User!.ToUpperInvariant());
        if (a.Premium is { } premium) parts.Add(premium ? "PREMIUM" : "COMPTE GRATUIT");
        return string.Join(" · ", parts);
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
