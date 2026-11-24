using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Pages;
using Windows.System;

namespace Veltrio;

public sealed partial class MainWindow : Window
{
    private static readonly (string Label, Type Page)[] Sections =
    {
        ("ACCUEIL", typeof(HomePage)),
        ("COLLECTION", typeof(LibraryPage)),
        ("DIRECT", typeof(LivePage)),
        ("SOURCES", typeof(SourcesPage)),
    };

    private readonly List<(TextBlock Label, Border Underline)> _tabs = new();
    private int _section = -1;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Application.Current.RequestedTheme == ApplicationTheme.Light ? Colors.Black : Colors.White;
        FitToDisplay();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Veltrio.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        BuildTabs();
        Navigator.Frame = ContentFrame;
        Navigator.Window = this;
        ContentFrame.Navigated += OnNavigated;

        // Escape steps back, like the macOS app's Back links.
        var back = new KeyboardAccelerator { Key = VirtualKey.Escape };
        back.Invoked += (_, e) =>
        {
            if (ContentFrame.Content is PlayerPage) return; // the player handles its own Escape
            e.Handled = Navigator.Back();
        };
        Content.KeyboardAccelerators.Add(back);
        Content.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        Services.Updater.Changed += OnUpdaterChanged;
        Services.AppPrefs.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            Services.Updater.Changed -= OnUpdaterChanged;
            Services.AppPrefs.LanguageChanged -= OnLanguageChanged;
        };
        RefreshUpdateBanner();

        if (!Services.AppPrefs.Current.OnboardingDone)
        {
            var alreadyConfigured = Services.CoreService.Addons.Count > 0
                || Services.IptvStore.Playlists.Count > 0
                || Services.MediaServers.Connected.Any();
            if (alreadyConfigured)
            {
                Services.AppPrefs.Current.OnboardingDone = true;
                Services.AppPrefs.Current.Save();
                ShowSection(0);
            }
            else
            {
                ContentFrame.Navigate(typeof(OnboardingPage));
            }
        }
        else
        {
            ShowSection(0);
        }
    }

    /// <summary>1440×920 DIPs, scaled to the display, capped to its work
    /// area and centred; a minimum size keeps the nav bar from colliding.</summary>
    private void FitToDisplay()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = (int)Math.Min(1440 * scale, area.Width * 0.92);
        var height = (int)Math.Min(920 * scale, area.Height * 0.92);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(1040 * scale);
            presenter.PreferredMinimumHeight = (int)(640 * scale);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static string SectionLabel(int index) => index switch
    {
        0 => Services.AppPrefs.Tr("ACCUEIL", "HOME", "INICIO", "STARTSEITE", "HOME"),
        1 => Services.AppPrefs.Tr("COLLECTION", "COLLECTION", "COLECCIÓN", "SAMMLUNG", "COLLEZIONE"),
        2 => Services.AppPrefs.Tr("DIRECT", "LIVE", "EN DIRECTO", "LIVE-TV", "DIRETTA"),
        3 => Services.AppPrefs.Tr("SOURCES", "SOURCES", "FUENTES", "QUELLEN", "SORGENTI"),
        _ => "",
    };

    private void OnLanguageChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        for (var i = 0; i < _tabs.Count; i++)
            _tabs[i].Label.Text = SectionLabel(i);
    });

    private void BuildTabs()
    {
        for (var i = 0; i < Sections.Length; i++)
        {
            var index = i;
            var number = new TextBlock { Text = $"0{i + 1}", Style = (Style)Application.Current.Resources["VeltrioNumeral"], FontSize = 10 };
            var label = new TextBlock { Text = SectionLabel(i), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = 220 };
            var underline = new Border
            {
                Height = 1,
                Background = (Brush)Application.Current.Resources["VeltrioAccentBrush"],
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, -10),
            };
            var content = new Grid();
            content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { number, label } });
            content.Children.Add(underline);
            var button = new Button { Style = (Style)Application.Current.Resources["VeltrioLinkButton"], Content = content, UseSystemFocusVisuals = false };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, Sections[i].Label);
            button.Click += (_, _) => ShowSection(index);
            Tabs.Children.Add(button);
            _tabs.Add((label, underline));
        }
    }

    /// <summary>Switching section starts it fresh, with no back stack.</summary>
    public void ShowSection(int index)
    {
        _section = index;
        ContentFrame.Navigate(Sections[index].Page);
        ContentFrame.BackStack.Clear();
        UpdateTabs();
    }

    private void UpdateTabs()
    {
        for (var i = 0; i < _tabs.Count; i++)
        {
            var selected = i == _section;
            _tabs[i].Label.Foreground = (Brush)Application.Current.Resources[selected ? "VeltrioTextPrimaryBrush" : "VeltrioTextTertiaryBrush"];
            _tabs[i].Underline.Opacity = selected ? 1 : 0;
        }
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        // The player and onboarding take the whole window; everything else sits under the nav.
        var fullWindow = e.SourcePageType == typeof(PlayerPage) || e.SourcePageType == typeof(OnboardingPage);
        var chrome = fullWindow ? Visibility.Collapsed : Visibility.Visible;
        NavBar.Visibility = NavRule.Visibility = chrome;
        TitleBar.Visibility = e.SourcePageType == typeof(PlayerPage) ? Visibility.Collapsed : Visibility.Visible;
        if (chrome == Visibility.Visible && AppWindow.Presenter.Kind != AppWindowPresenterKind.Default)
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);

        var section = Array.FindIndex(Sections, s => s.Page == e.SourcePageType);
        if (section >= 0) { _section = section; UpdateTabs(); }
        RefreshUpdateBanner();
    }

    private bool _updateBannerDismissed;

    private void OnUpdaterChanged() => DispatcherQueue.TryEnqueue(RefreshUpdateBanner);

    private void RefreshUpdateBanner()
    {
        var state = Services.Updater.State;
        var show = !_updateBannerDismissed
            && state is Services.Updater.UpdateState.Available
                     or Services.Updater.UpdateState.Downloading
                     or Services.Updater.UpdateState.Ready
            && ContentFrame.Content is not PlayerPage
            && ContentFrame.Content is not OnboardingPage;
        UpdateBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        var version = Services.Updater.NewVersion;
        switch (state)
        {
            case Services.Updater.UpdateState.Available:
                UpdateBannerText.Text = $"Une mise à jour est disponible (Veltrio {version}).";
                UpdateBannerActionLabel.Text = "METTRE À JOUR";
                UpdateBannerAction.IsEnabled = true;
                UpdateBannerDismiss.Visibility = Visibility.Visible;
                break;
            case Services.Updater.UpdateState.Downloading:
                UpdateBannerText.Text = $"Téléchargement de Veltrio {version}… {Services.Updater.Progress} %";
                UpdateBannerActionLabel.Text = "TÉLÉCHARGEMENT…";
                UpdateBannerAction.IsEnabled = false;
                UpdateBannerDismiss.Visibility = Visibility.Collapsed;
                break;
            case Services.Updater.UpdateState.Ready:
                UpdateBannerText.Text = $"Veltrio {version} est prêt à installer.";
                UpdateBannerActionLabel.Text = "REDÉMARRER";
                UpdateBannerAction.IsEnabled = true;
                UpdateBannerDismiss.Visibility = Visibility.Visible;
                break;
        }
    }

    private void OnUpdateBannerAction(object sender, RoutedEventArgs e)
    {
        if (Services.Updater.State == Services.Updater.UpdateState.Available)
            _ = Services.Updater.DownloadAndInstallAsync();
        else if (Services.Updater.State == Services.Updater.UpdateState.Ready)
            Services.Updater.RestartNow();
    }

    private void OnUpdateBannerDismiss(object sender, RoutedEventArgs e)
    {
        _updateBannerDismissed = true;
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void OnWordmarkClick(object sender, RoutedEventArgs e) => ShowSection(0);

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is not SearchPage) Navigator.Go(typeof(SearchPage));
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is not SettingsPage) Navigator.Go(typeof(SettingsPage));
    }

    public void SetFullScreen(bool on) =>
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);

    public bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
}
