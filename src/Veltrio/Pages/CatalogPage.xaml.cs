using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>"See all": a catalog as a poster grid, paged with skip=.</summary>
public sealed partial class CatalogPage : Page
{
    private readonly ObservableCollection<PosterItem> _items = new();
    private CatalogRequest? _request;

    public CatalogPage()
    {
        InitializeComponent();
        PosterGrid.ItemsSource = _items;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not CatalogRequest request || _request is not null) return;
        _request = request;
        Eyebrow.Text = $"{request.Addon.Name.ToUpperInvariant()} · {CoreService.TypeLabel(request.Catalog.Type).ToUpperInvariant()}";
        TitleText.Text = request.Catalog.Name;
        await LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        if (_request is null) return;
        Loading.IsActive = true;
        LoadMore.Visibility = Visibility.Collapsed;
        var page = await CoreService.LoadCatalogPageAsync(_request.Addon, _request.Catalog, _items.Count);
        var known = _items.Select(i => i.Id).ToHashSet();
        var fresh = page.Where(p => known.Add(p.Id)).ToList();
        foreach (var item in fresh) _items.Add(item);
        Loading.IsActive = false;
        // Addons page by 100 or less; a page with nothing new is the end.
        LoadMore.Visibility = fresh.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnLoadMore(object sender, RoutedEventArgs e) => await LoadPageAsync();

    private void OnPosterClick(object sender, ItemClickEventArgs e) => Navigator.OpenTitle((PosterItem)e.ClickedItem);

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();
}
