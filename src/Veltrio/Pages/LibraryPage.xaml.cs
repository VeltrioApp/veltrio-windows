using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>Collection: saved titles and everything started, with the
/// same filters as macOS. Right-click for watched / remove.</summary>
public sealed partial class LibraryPage : Page
{
    private static readonly (string Key, string Label)[] Filters =
    {
        ("all", "TOUT"), ("progress", "EN COURS"), ("saved", "ENREGISTRÉS"), ("watched", "VUS"),
    };
    private static readonly (string Key, string Label)[] Types =
    {
        ("all", "TOUS LES TYPES"), ("movie", "FILMS"), ("series", "SÉRIES"),
    };

    private string _filter = "all";
    private string _type = "all";

    public LibraryPage()
    {
        InitializeComponent();
        BuildChips(FilterChips, Filters, () => _filter, v => _filter = v);
        BuildChips(TypeChips, Types, () => _type, v => _type = v);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => Refresh();

    private void BuildChips(Panel panel, (string Key, string Label)[] options, Func<string> get, Action<string> set)
    {
        foreach (var (key, label) in options)
        {
            var chip = new ToggleButton { Content = label, Tag = key, IsChecked = key == get(), Style = (Style)Application.Current.Resources["VeltrioChip"] };
            chip.Click += (_, _) =>
            {
                set(key);
                foreach (var other in panel.Children.OfType<ToggleButton>()) other.IsChecked = (string)other.Tag == key;
                Refresh();
            };
            panel.Children.Add(chip);
        }
    }

    private void Refresh()
    {
        var all = WatchStore.LibraryItems;
        var items = all
            .Where(e => _type == "all" || e.Type == _type)
            .Where(e => _filter switch
            {
                "progress" => e.IsStarted && !e.Watched,
                "saved" => e.InLibrary,
                "watched" => e.Watched,
                _ => true,
            })
            .ToList();
        Eyebrow.Text = $"N° {all.Count} DANS VOTRE COLLECTION";
        Items.ItemsSource = items;
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (all.Count > 0)
        {
            EmptyTitle.Text = "Rien ne correspond.";
            EmptyBody.Text = "Aucun titre ne correspond à ces filtres.";
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (!Navigator.OpenEntry((WatchEntry)e.ClickedItem))
            EmptyBody.Text = "Aucune source installée ne fournit la fiche de ce titre.";
    }

    private void OnItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not WatchEntry entry) return;
        var menu = new MenuFlyout();
        var watched = new MenuFlyoutItem { Text = entry.Watched ? "Marquer comme non vu" : "Marquer comme vu" };
        watched.Click += (_, _) => { WatchStore.MarkWatched(entry.Type, entry.ContentId, !entry.Watched); Refresh(); };
        menu.Items.Add(watched);
        if (entry.InLibrary)
        {
            var unsave = new MenuFlyoutItem { Text = "Retirer de la collection" };
            unsave.Click += (_, _) =>
            {
                WatchStore.ToggleLibrary(entry.Type, entry.ContentId, entry.Name, null, null, null);
                Refresh();
            };
            menu.Items.Add(unsave);
        }
        var forget = new MenuFlyoutItem { Text = "Supprimer de l'historique" };
        forget.Click += (_, _) => { WatchStore.Remove(entry.Type, entry.ContentId); Refresh(); };
        menu.Items.Add(forget);
        menu.ShowAt((FrameworkElement)e.OriginalSource, e.GetPosition((FrameworkElement)e.OriginalSource));
    }
}
