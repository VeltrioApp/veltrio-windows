using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>A catalogue row: the addon and what its button says.</summary>
public sealed class CatalogEntry
{
    public required CatalogAddon Addon { get; init; }
    public string Name => Addon.Name;
    public string? Logo => Addon.Logo;
    public string? Description => Addon.Description;
    public string Version => Addon.Version is { Length: > 0 } v ? $"v{v}" : "";
    public string Meta => string.Join("   ", new[] { Addon.Capabilities, Addon.TypesLabel.ToUpperInvariant() }.Where(s => s.Length > 0));
    public bool IsInstalled => CoreService.Addons.Any(a => a.Id == Addon.Id);
    public bool Installing { get; set; }
    public string InstallLabel => Installing ? "INSTALLATION…" : IsInstalled ? "INSTALLÉ" : "INSTALLER";
    public bool CanInstall => !Installing && !IsInstalled;
}

/// <summary>Browse and install addons: Stremio's official collection, or
/// the community site, filtered by name.</summary>
public sealed partial class AddonCatalogPage : Page
{
    private bool _community;
    private IReadOnlyList<CatalogAddon> _official = Array.Empty<CatalogAddon>();

    public AddonCatalogPage()
    {
        InitializeComponent();
        foreach (var (label, community) in new[] { ("OFFICIELS", false), ("COMMUNAUTÉ", true) })
        {
            var chip = new ToggleButton { Content = label, IsChecked = community == _community, Style = (Style)Application.Current.Resources["VeltrioChip"] };
            chip.Click += async (_, _) =>
            {
                _community = community;
                foreach (var other in SourceChips.Children.OfType<ToggleButton>()) other.IsChecked = other == chip;
                await LoadAsync();
            };
            SourceChips.Children.Add(chip);
        }
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        Message.Visibility = Visibility.Collapsed;
        Loading.IsActive = true;
        LoadMore.Visibility = Visibility.Collapsed;
        try
        {
            if (_community && AddonCatalogService.CommunityPagesLoaded == 0) await AddonCatalogService.LoadMoreCommunityAsync();
            if (!_community) _official = await AddonCatalogService.OfficialAsync();
        }
        catch (Exception ex)
        {
            Message.Text = $"Impossible de charger le catalogue. {ex.Message}";
            Message.Visibility = Visibility.Visible;
        }
        Loading.IsActive = false;
        Refresh();
    }

    private void Refresh()
    {
        IEnumerable<CatalogAddon> addons = _community ? AddonCatalogService.Community : _official;
        var text = Filter.Text.Trim();
        if (text.Length > 0)
            addons = addons.Where(a => a.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                                       || (a.Description?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false));
        List.ItemsSource = addons.Select(a => new CatalogEntry { Addon = a }).ToList();
        LoadMore.Visibility = _community && AddonCatalogService.CommunityPagesLoaded < AddonCatalogService.CommunityPageCount
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => Refresh();

    private async void OnLoadMore(object sender, RoutedEventArgs e)
    {
        Loading.IsActive = true;
        LoadMore.IsEnabled = false;
        try { await AddonCatalogService.LoadMoreCommunityAsync(); }
        catch (Exception ex)
        {
            Message.Text = ex.Message;
            Message.Visibility = Visibility.Visible;
        }
        Loading.IsActive = false;
        LoadMore.IsEnabled = true;
        Refresh();
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CatalogEntry entry) return;
        var button = (Button)sender;
        button.IsEnabled = false;
        button.Content = "INSTALLATION…";
        string? error;
        try
        {
            var url = await AddonCatalogService.ResolveTransportAsync(entry.Addon);
            error = url is null ? $"Aucune adresse d'installation trouvée pour {entry.Addon.Name}." : await CoreService.InstallAddonAsync(url);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        Message.Text = error ?? "";
        Message.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        button.Content = entry.InstallLabel;
        button.IsEnabled = entry.CanInstall;
    }

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();
}
