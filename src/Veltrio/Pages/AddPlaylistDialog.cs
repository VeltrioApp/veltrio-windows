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
        var xtream = false;
        var name = new TextBox { Header = "Nom (facultatif)", PlaceholderText = "Mon fournisseur" };
        var m3u = new TextBox { Header = "URL de la playlist M3U", PlaceholderText = "https://…/playlist.m3u" };
        var host = new TextBox { Header = "Serveur", PlaceholderText = "http://fournisseur.tv:8080" };
        var user = new TextBox { Header = "Utilisateur" };
        var password = new PasswordBox { Header = "Mot de passe" };
        var format = new ComboBox { Header = "Format du direct", ItemsSource = new[] { "MPEG-TS (.ts)", "HLS (.m3u8)" }, SelectedIndex = 0, MinWidth = 220 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioDangerBrush"], Visibility = Visibility.Collapsed };
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 };

        var m3uPanel = new StackPanel { Spacing = 12, Children = { m3u } };
        var xtreamPanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed, Children = { host, user, password, format } };

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void Mode(bool isXtream)
        {
            xtream = isXtream;
            m3uPanel.Visibility = isXtream ? Visibility.Collapsed : Visibility.Visible;
            xtreamPanel.Visibility = isXtream ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = isXtream
                ? "Utilise l'API Xtream Codes (player_api.php) : chaînes, films, séries, guide TV et replay. Le mot de passe reste sur cet appareil, chiffré."
                : "Toute URL qui renvoie une playlist M3U. Un lien get.php?username=…&password=… de fournisseur est ajouté comme compte Xtream.";
            foreach (var chip in chips.Children.OfType<ToggleButton>()) chip.IsChecked = (bool)chip.Tag == isXtream;
        }
        foreach (var (label, isXtream) in new[] { ("PLAYLIST M3U", false), ("XTREAM CODES", true) })
        {
            var chip = new ToggleButton { Content = label, Tag = isXtream, Style = (Style)Application.Current.Resources["VeltrioChip"] };
            chip.Click += (_, _) => Mode(isXtream);
            chips.Children.Add(chip);
        }
        Mode(false);

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
                Content = new StackPanel { Spacing = 14, MinWidth = 440, Children = { chips, hint, name, m3uPanel, xtreamPanel, error } },
            },
        };

        var added = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            error.Visibility = Visibility.Collapsed;
            string? message;
            if (!xtream && XtreamFromM3u(m3u.Text) is { } fromLink)
                message = await IptvStore.AddXtreamAsync(name.Text, fromLink.Host, fromLink.User, fromLink.Password, fromLink.Output);
            else if (xtream)
                message = await IptvStore.AddXtreamAsync(name.Text, host.Text, user.Text, password.Password, format.SelectedIndex == 1 ? "m3u8" : "ts");
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
