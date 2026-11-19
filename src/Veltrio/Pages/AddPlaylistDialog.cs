using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>Adding an IPTV source: an M3U URL or an Xtream Codes account,
/// as in the macOS add sheet. A provider's get.php M3U link carries the
/// Xtream credentials, so it's added as an Xtream account (films, series
/// and guide come with it).</summary>
public static class AddPlaylistDialog
{
    /// <summary>Shows the dialog; true once a source was added.</summary>
    public static async Task<bool> ShowAsync(XamlRoot root)
    {
        var mode = 0; // 0 M3U URL, 1 Xtream, 2 local file
        var name = new TextBox { Header = "Nom (facultatif)", PlaceholderText = "Mon fournisseur" };
        var m3u = new TextBox { Header = "URL de la playlist M3U", PlaceholderText = "https://…/playlist.m3u" };
        var host = new TextBox { Header = "Serveur", PlaceholderText = "http://fournisseur.tv:8080" };
        var user = new TextBox { Header = "Utilisateur" };
        var password = new PasswordBox { Header = "Mot de passe" };
        var format = new ComboBox { Header = "Format du direct", ItemsSource = new[] { "MPEG-TS (.ts)", "HLS (.m3u8)" }, SelectedIndex = 0, MinWidth = 220 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioDangerBrush"], Visibility = Visibility.Collapsed };
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 };

        var file = new TextBox { Header = "Fichier M3U", PlaceholderText = @"C:\…\chaines.m3u", MinWidth = 300 };
        var browse = new Button { Content = "Choisir…", VerticalAlignment = VerticalAlignment.Bottom };
        browse.Click += async (_, _) =>
        {
            if (await PickFileAsync() is { } path) file.Text = path;
        };
        var filePanel = new Grid { ColumnSpacing = 10, Visibility = Visibility.Collapsed };
        filePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filePanel.Children.Add(file);
        Grid.SetColumn(browse, 1);
        filePanel.Children.Add(browse);

        var m3uPanel = new StackPanel { Spacing = 12, Children = { m3u } };
        var xtreamPanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed, Children = { host, user, password, format } };

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void Mode(int value)
        {
            mode = value;
            m3uPanel.Visibility = value == 0 ? Visibility.Visible : Visibility.Collapsed;
            xtreamPanel.Visibility = value == 1 ? Visibility.Visible : Visibility.Collapsed;
            filePanel.Visibility = value == 2 ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = value switch
            {
                1 => "Utilise l'API Xtream Codes (player_api.php) : chaînes, films, séries, guide TV et replay. Le mot de passe reste sur cet appareil, chiffré.",
                2 => "Un fichier .m3u ou .m3u8 sur cet ordinateur, relu à chaque chargement : pratique pour tester ou garder sa propre liste.",
                _ => "Toute URL qui renvoie une playlist M3U. Un lien get.php?username=…&password=… de fournisseur est ajouté comme compte Xtream.",
            };
            foreach (var chip in chips.Children.OfType<ToggleButton>()) chip.IsChecked = (int)chip.Tag == value;
        }
        foreach (var (label, value) in new[] { ("PLAYLIST M3U", 0), ("XTREAM CODES", 1), ("FICHIER LOCAL", 2) })
        {
            var chip = new ToggleButton { Content = label, Tag = value, Style = (Style)Application.Current.Resources["VeltrioChip"] };
            chip.Click += (_, _) => Mode(value);
            chips.Children.Add(chip);
        }
        Mode(0);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Ajouter une source IPTV",
            PrimaryButtonText = "Ajouter",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
            Content = new ScrollViewer
            {
                Content = new StackPanel { Spacing = 14, MinWidth = 440, Children = { chips, hint, name, m3uPanel, xtreamPanel, filePanel, error } },
            },
        };

        var added = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            error.Visibility = Visibility.Collapsed;
            string? message;
            if (mode == 0 && XtreamFromM3u(m3u.Text) is { } fromLink)
                message = await IptvStore.AddXtreamAsync(name.Text, fromLink.Host, fromLink.User, fromLink.Password, fromLink.Output);
            else if (mode == 1)
                message = await IptvStore.AddXtreamAsync(name.Text, host.Text, user.Text, password.Password, format.SelectedIndex == 1 ? "m3u8" : "ts");
            else if (mode == 2)
                message = IptvStore.AddLocalFile(name.Text, file.Text);
            else
                message = IptvStore.AddM3u(name.Text, m3u.Text);
            dialog.IsPrimaryButtonEnabled = true;
            if (message is null)
            {
                added = true;
            }
            else
            {
                error.Text = message;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
            deferral.Complete();
        };
        await dialog.ShowAsync();
        return added;
    }

    private static async Task<string?> PickFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        // Unpackaged desktop apps hand the picker their window.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(Navigator.Window));
        picker.FileTypeFilter.Add(".m3u");
        picker.FileTypeFilter.Add(".m3u8");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>http://host:port/get.php?username=U&password=P&type=m3u_plus&output=ts</summary>
    internal static (string Host, string User, string Password, string Output)? XtreamFromM3u(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || !uri.AbsolutePath.EndsWith("/get.php", StringComparison.OrdinalIgnoreCase))
            return null;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var user = query["username"];
        var password = query["password"];
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password)) return null;
        var output = query["output"] is "m3u8" or "hls" ? "m3u8" : "ts";
        return ($"{uri.Scheme}://{uri.Authority}", user, password, output);
    }
}
