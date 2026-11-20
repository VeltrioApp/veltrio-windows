using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Veltrio.Pages;

/// <summary>The card every service shows in Settings and Sources (debrid,
/// accounts, media servers): brand mark, name, status, actions, then rows.</summary>
public static class ServiceCard
{
    public static T Res<T>(string key) => (T)Application.Current.Resources[key];

    /// <summary>The frame every service card shares: brand mark, name,
    /// status or tagline, and the actions on the right.</summary>
    public static (Grid Card, StackPanel Actions) Shell(string name, string color, string subtitle, bool connected)
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
    public static void AddRow(Grid card, FrameworkElement element)
    {
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(element, card.RowDefinitions.Count - 1);
        Grid.SetColumnSpan(element, 3);
        card.Children.Add(element);
    }

    public static void AddError(Grid card, string? error)
    {
        if (error is null) return;
        AddRow(card, new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Res<Brush>("VeltrioDangerBrush") });
    }

    public static void AddNote(Grid card, string text) =>
        AddRow(card, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Res<Brush>("VeltrioTextTertiaryBrush") });

    /// <summary>The code to enter on the service's site, big, with a link there.</summary>
    public static FrameworkElement CodePanel(string code, string url)
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
        line.Children.Add(ActionButton("OUVRIR LA PAGE", ghost: true, () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url))));
        line.Children.Add(new ProgressRing { IsActive = true, Width = 18, Height = 18 });
        panel.Children.Add(line);
        return panel;
    }

    public static Button ActionButton(string text, bool ghost, Action action, bool enabled = true)
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
