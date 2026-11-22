using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>First-launch 3-step onboarding flow for Windows: Stremio sign-in
/// and full import first (or quick Cinemeta setup), optional addon URL, and
/// ready summary. Always skippable.</summary>
public sealed class OnboardingPage : Page
{
    private int _step;
    private string _email = "";
    private string _password = "";
    private string _customAddonUrl = "";
    private string? _addonMessage;
    private string? _addonError;
    private bool _installingAddon;
    private bool _finishing;

    private readonly TextBlock _stepCounter;
    private readonly StackPanel _body;
    private readonly Button _backButton;
    private readonly Button _nextButton;
    private readonly TextBlock _nextLabel;

    public OnboardingPage()
    {
        Background = Res<Brush>("VeltrioBackgroundBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var header = new Grid
        {
            Background = Res<Brush>("VeltrioBackgroundAltBrush"),
            Padding = new Thickness(48, 22, 48, 18),
        };
        var brandRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        brandRow.Children.Add(new TextBlock
        {
            Text = "Veltrio",
            FontFamily = Res<FontFamily>("VeltrioSerif"),
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = Res<Brush>("VeltrioTextPrimaryBrush"),
        });
        _stepCounter = new TextBlock
        {
            Text = "01 / 03",
            FontFamily = Res<FontFamily>("VeltrioMono"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("VeltrioAccentTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        brandRow.Children.Add(_stepCounter);
        header.Children.Add(brandRow);

        var skipBtn = new Button
        {
            Style = Res<Style>("VeltrioLinkButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = "PASSER",
                FontFamily = Res<FontFamily>("VeltrioMono"),
                FontSize = 11,
                CharacterSpacing = 180,
                Foreground = Res<Brush>("VeltrioTextTertiaryBrush"),
            },
        };
        skipBtn.Click += (_, _) => Finish(skipCinemeta: true);
        header.Children.Add(skipBtn);

        var headerWrap = new StackPanel();
        headerWrap.Children.Add(header);
        headerWrap.Children.Add(new Border { Style = Res<Style>("VeltrioRule") });
        Grid.SetRow(headerWrap, 0);
        root.Children.Add(headerWrap);

        // Scrollable body
        _body = new StackPanel
        {
            Spacing = 22,
            MaxWidth = 700,
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(48, 36, 48, 36),
        };
        var scroll = new ScrollViewer { Content = _body };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // Footer
        var footer = new Grid
        {
            Background = Res<Brush>("VeltrioBackgroundAltBrush"),
            Padding = new Thickness(48, 18, 48, 22),
        };
        _backButton = new Button
        {
            Style = Res<Style>("VeltrioGhostButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new TextBlock { Text = "RETOUR" },
        };
        _backButton.Click += (_, _) => { if (_step > 0) { _step--; Render(); } };
        footer.Children.Add(_backButton);

        _nextLabel = new TextBlock { Text = "CONTINUER" };
        _nextButton = new Button
        {
            Style = Res<Style>("VeltrioPrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Content = _nextLabel,
        };
        _nextButton.Click += async (_, _) =>
        {
            if (_step < 2) { _step++; Render(); }
            else await FinishAsync(skipCinemeta: false);
        };
        footer.Children.Add(_nextButton);

        var footerWrap = new StackPanel();
        footerWrap.Children.Add(new Border { Style = Res<Style>("VeltrioRule") });
        footerWrap.Children.Add(footer);
        Grid.SetRow(footerWrap, 2);
        root.Children.Add(footerWrap);

        Content = root;

        Loaded += (_, _) => { StremioAccountService.Changed += OnStremioChanged; Render(); };
        Unloaded += (_, _) => StremioAccountService.Changed -= OnStremioChanged;
    }

    private void OnStremioChanged() => DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        _stepCounter.Text = $"0{_step + 1} / 03";
        _backButton.Visibility = _step > 0 ? Visibility.Visible : Visibility.Collapsed;
        _nextLabel.Text = _step < 2 ? "CONTINUER" : (_finishing ? "PRÉPARATION…" : "COMMENCER À REGARDER");
        _nextButton.IsEnabled = !_finishing;

        _body.Children.Clear();
        switch (_step)
        {
            case 0: RenderWelcome(); break;
            case 1: RenderSources(); break;
            default: RenderReady(); break;
        }
    }

    private void RenderWelcome()
    {
        _body.Children.Add(new TextBlock { Text = "01 — BIENVENUE", Style = Res<Style>("VeltrioAccentEyebrow") });
        _body.Children.Add(new TextBlock
        {
            Text = "Toutes vos sources, un seul lecteur natif.",
            Style = Res<Style>("VeltrioPageTitle"),
            TextWrapping = TextWrapping.Wrap,
        });
        _body.Children.Add(new TextBlock
        {
            Text = "Connectez votre compte Stremio pour importer vos addons et votre bibliothèque en un geste, ou démarrez à zéro.",
            Style = Res<Style>("VeltrioBody"),
            TextWrapping = TextWrapping.Wrap,
        });

        // Primary card: Stremio sign-in & import
        var stremioCard = Card(accentBorder: true);
        var stremioHeader = new Grid();
        stremioHeader.Children.Add(new TextBlock
        {
            Text = "Se connecter à Stremio",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = Res<Brush>("VeltrioTextPrimaryBrush"),
        });
        if (StremioAccountService.IsConnected)
        {
            stremioHeader.Children.Add(new TextBlock
            {
                Text = "CONNECTÉ",
                FontFamily = Res<FontFamily>("VeltrioMono"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Res<Brush>("VeltrioSuccessBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        stremioCard.Children.Add(stremioHeader);

        if (StremioAccountService.IsConnected)
        {
            stremioCard.Children.Add(new TextBlock
            {
                Text = $"Connecté en tant que {StremioAccountService.Email ?? "Stremio"} — vos addons et votre bibliothèque peuvent être importés en un clic.",
                Style = Res<Style>("VeltrioBody"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            var importBtn = new Button
            {
                Style = Res<Style>("VeltrioPrimaryButton"),
                IsEnabled = !StremioAccountService.IsBusy && !StremioAccountService.IsInstallingAddons,
                Content = new TextBlock { Text = StremioAccountService.IsBusy || StremioAccountService.IsInstallingAddons ? "IMPORTATION…" : "IMPORTER ADDONS ET BIBLIOTHÈQUE" },
            };
            importBtn.Click += async (_, _) =>
            {
                await StremioAccountService.InstallAccountAddonsAsync();
                await StremioAccountService.ImportLibraryAsync();
            };
            stremioCard.Children.Add(importBtn);
        }
        else
        {
            stremioCard.Children.Add(new TextBlock
            {
                Text = "Importe automatiquement vos addons installés, votre collection et votre historique.",
                Style = Res<Style>("VeltrioBody"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });

            var fields = new Grid { ColumnSpacing = 12 };
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var emailBox = new TextBox { PlaceholderText = "E-mail", Text = _email };
            emailBox.TextChanged += (_, _) => _email = emailBox.Text;
            Grid.SetColumn(emailBox, 0);
            fields.Children.Add(emailBox);

            var pwdBox = new PasswordBox { PlaceholderText = "Mot de passe", Password = _password };
            pwdBox.PasswordChanged += (_, _) => _password = pwdBox.Password;
            Grid.SetColumn(pwdBox, 1);
            fields.Children.Add(pwdBox);
            stremioCard.Children.Add(fields);

            var signInBtn = new Button
            {
                Style = Res<Style>("VeltrioPrimaryButton"),
                IsEnabled = !StremioAccountService.IsBusy && !StremioAccountService.IsInstallingAddons,
                Content = new TextBlock { Text = StremioAccountService.IsBusy || StremioAccountService.IsInstallingAddons ? "CONNEXION…" : "SE CONNECTER ET IMPORTER" },
            };
            signInBtn.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_email) || string.IsNullOrEmpty(_password)) return;
                await StremioAccountService.SignInAsync(_email.Trim(), _password);
                if (StremioAccountService.IsConnected)
                {
                    _password = "";
                    await StremioAccountService.InstallAccountAddonsAsync();
                    await StremioAccountService.ImportLibraryAsync();
                }
            };
            stremioCard.Children.Add(signInBtn);
        }

        if (StremioAccountService.AddonResult is { } ar)
            stremioCard.Children.Add(new TextBlock { Text = ar, FontSize = 12, Foreground = Res<Brush>("VeltrioSuccessBrush"), TextWrapping = TextWrapping.Wrap });
        if (StremioAccountService.LastResult is { } lr)
            stremioCard.Children.Add(new TextBlock { Text = lr, FontSize = 12, Foreground = Res<Brush>("VeltrioTextSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
        if (StremioAccountService.Error is { } err)
            stremioCard.Children.Add(new TextBlock { Text = err, FontSize = 12, Foreground = Res<Brush>("VeltrioDangerBrush"), TextWrapping = TextWrapping.Wrap });

        _body.Children.Add(WrapCard(stremioCard, accentBorder: true));

        // Language selector card
        var langCard = Card(accentBorder: false);
        langCard.Children.Add(new TextBlock { Text = AppPrefs.Tr("LANGUE DE L'APPLICATION", "APP LANGUAGE"), Style = Res<Style>("VeltrioEyebrow") });
        var langRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (code, label) in new[] { ("fr", "FRANÇAIS"), ("en", "ENGLISH") })
        {
            var selected = AppPrefs.Current.AppLanguage == code || (string.IsNullOrEmpty(AppPrefs.Current.AppLanguage) && (code == "fr") == AppPrefs.IsFrench);
            var chip = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = label,
                IsChecked = selected,
                Style = Res<Style>("VeltrioChip"),
            };
            chip.Click += (_, _) =>
            {
                AppPrefs.SetAppLanguage(code);
                Render();
            };
            langRow.Children.Add(chip);
        }
        langCard.Children.Add(langRow);
        _body.Children.Add(WrapCard(langCard, accentBorder: false));
    }

    private void RenderSources()
    {
        _body.Children.Add(new TextBlock { Text = "02 — SOURCES", Style = Res<Style>("VeltrioAccentEyebrow") });
        _body.Children.Add(new TextBlock
        {
            Text = "Connectez ce que vous utilisez déjà.",
            Style = Res<Style>("VeltrioPageTitle"),
            TextWrapping = TextWrapping.Wrap,
        });
        _body.Children.Add(new TextBlock
        {
            Text = "Collez un lien d'addon Stremio maintenant, ou passez cette étape pour ajouter vos playlists IPTV, serveurs Plex / Jellyfin / Emby et comptes débrideurs plus tard dans Sources.",
            Style = Res<Style>("VeltrioBody"),
            TextWrapping = TextWrapping.Wrap,
        });

        var addonCard = Card(accentBorder: false);
        addonCard.Children.Add(new TextBlock
        {
            Text = "Ajouter un addon Stremio",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("VeltrioTextPrimaryBrush"),
        });

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var urlBox = new TextBox { PlaceholderText = "https://example.com/manifest.json", Text = _customAddonUrl };
        urlBox.TextChanged += (_, _) => _customAddonUrl = urlBox.Text;
        Grid.SetColumn(urlBox, 0);
        row.Children.Add(urlBox);

        var installBtn = new Button
        {
            Style = Res<Style>("VeltrioGhostButton"),
            IsEnabled = !_installingAddon,
            Content = new TextBlock { Text = _installingAddon ? "INSTALLATION…" : "INSTALLER" },
        };
        installBtn.Click += async (_, _) =>
        {
            var trimmed = _customAddonUrl.Trim();
            if (string.IsNullOrEmpty(trimmed)) return;
            _installingAddon = true;
            _addonMessage = null;
            _addonError = null;
            Render();
            var err = await CoreService.InstallAddonAsync(trimmed);
            _installingAddon = false;
            if (err is null)
            {
                _addonMessage = "Addon installé.";
                _customAddonUrl = "";
            }
            else
            {
                _addonError = err;
            }
            Render();
        };
        Grid.SetColumn(installBtn, 1);
        row.Children.Add(installBtn);
        addonCard.Children.Add(row);

        if (_addonMessage is not null)
            addonCard.Children.Add(new TextBlock { Text = _addonMessage, FontSize = 12, Foreground = Res<Brush>("VeltrioSuccessBrush") });
        if (_addonError is not null)
            addonCard.Children.Add(new TextBlock { Text = _addonError, FontSize = 12, Foreground = Res<Brush>("VeltrioDangerBrush") });

        _body.Children.Add(WrapCard(addonCard, accentBorder: false));

        // Optional TMDB API key + metadata language
        var tmdbCard = new StackPanel { Spacing = 10 };
        tmdbCard.Children.Add(new TextBlock
        {
            Text = "Résumés et titres dans votre langue (TMDB)",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("VeltrioTextPrimaryBrush"),
        });
        tmdbCard.Children.Add(new TextBlock
        {
            Text = "Optionnel : ajoutez une clé API gratuite TMDB (themoviedb.org/settings/api) pour traduire les fiches Cinemeta.",
            Style = Res<Style>("VeltrioBody"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        var langRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (code, label) in new[] { ("fr-FR", "FR"), ("en-US", "EN"), ("es-ES", "ES"), ("de-DE", "DE") })
        {
            var chip = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = label,
                IsChecked = TmdbService.Language == code,
                Style = Res<Style>("VeltrioChip"),
            };
            var c = code;
            chip.Click += (_, _) => { TmdbService.SetLanguage(c); Render(); };
            langRow.Children.Add(chip);
        }
        tmdbCard.Children.Add(langRow);

        if (TmdbService.IsConnected)
        {
            tmdbCard.Children.Add(new TextBlock { Text = "Clé TMDB connectée ✓", FontSize = 12, Foreground = Res<Brush>("VeltrioSuccessBrush") });
        }
        else
        {
            var tmdbRow = new Grid { ColumnSpacing = 12 };
            tmdbRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tmdbRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var keyBox = new PasswordBox { PlaceholderText = "Clé API ou jeton TMDB (optionnel)" };
            Grid.SetColumn(keyBox, 0);
            tmdbRow.Children.Add(keyBox);
            var saveKeyBtn = new Button
            {
                Style = Res<Style>("VeltrioGhostButton"),
                IsEnabled = !TmdbService.IsBusy,
                Content = new TextBlock { Text = TmdbService.IsBusy ? "VÉRIFICATION…" : "ENREGISTRER" },
            };
            saveKeyBtn.Click += async (_, _) =>
            {
                await TmdbService.ConnectAsync(keyBox.Password);
                Render();
            };
            Grid.SetColumn(saveKeyBtn, 1);
            tmdbRow.Children.Add(saveKeyBtn);
            tmdbCard.Children.Add(tmdbRow);
        }
        if (TmdbService.Error is not null)
            tmdbCard.Children.Add(new TextBlock { Text = TmdbService.Error, FontSize = 12, Foreground = Res<Brush>("VeltrioDangerBrush") });
        _body.Children.Add(WrapCard(tmdbCard, accentBorder: false));
    }

    private void RenderReady()
    {
        _body.Children.Add(new TextBlock { Text = "03 — PRÊT", Style = Res<Style>("VeltrioAccentEyebrow") });
        _body.Children.Add(new TextBlock
        {
            Text = "Lumière tamisée. Tout est prêt.",
            Style = Res<Style>("VeltrioPageTitle"),
            TextWrapping = TextWrapping.Wrap,
        });
        _body.Children.Add(new TextBlock
        {
            Text = "Tout peut être ajusté à tout moment dans Sources et Réglages.",
            Style = Res<Style>("VeltrioBody"),
            TextWrapping = TextWrapping.Wrap,
        });

        var summary = Card(accentBorder: false);
        summary.Children.Add(SummaryRow(AppPrefs.Tr("Catalogue", "Catalog"), TmdbService.IsConnected ? AppPrefs.Tr("TMDB activé", "TMDB enabled") : AppPrefs.Tr("Aucun", "None")));
        summary.Children.Add(new Border { Style = Res<Style>("VeltrioRule") });
        summary.Children.Add(SummaryRow(AppPrefs.Tr("Addons installés", "Installed addons"), CoreService.Addons.Count.ToString()));
        summary.Children.Add(new Border { Style = Res<Style>("VeltrioRule") });
        summary.Children.Add(SummaryRow(AppPrefs.Tr("Compte Stremio", "Stremio account"), StremioAccountService.IsConnected ? (StremioAccountService.Email ?? AppPrefs.Tr("Connecté", "Connected")) : AppPrefs.Tr("Non connecté", "Not signed in")));
        _body.Children.Add(WrapCard(summary, accentBorder: false));
    }

    private void Finish(bool skipCinemeta) => _ = FinishAsync();

    private Task FinishAsync()
    {
        AppPrefs.Current.OnboardingDone = true;
        AppPrefs.Current.Save();
        Navigator.Window?.ShowSection(0);
        return Task.CompletedTask;
    }

    private static StackPanel Card(bool accentBorder) => new() { Spacing = 14 };

    private static Border WrapCard(StackPanel inner, bool accentBorder) => new()
    {
        Background = Res<Brush>("VeltrioSurfaceBrush"),
        BorderBrush = Res<Brush>(accentBorder ? "VeltrioAccentBrush" : "VeltrioHairlineBrush"),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(22),
        Child = inner,
    };

    private static Grid SummaryRow(string label, string value)
    {
        var g = new Grid();
        g.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Res<Brush>("VeltrioTextSecondaryBrush") });
        g.Children.Add(new TextBlock
        {
            Text = value,
            FontFamily = Res<FontFamily>("VeltrioMono"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("VeltrioTextPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        return g;
    }

    private static T Res<T>(string key) => (T)Application.Current.Resources[key];
}
