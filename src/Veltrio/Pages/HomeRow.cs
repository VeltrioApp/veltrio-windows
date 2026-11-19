using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>A numbered catalog row that fills in on its own.</summary>
public sealed class HomeRow : INotifyPropertyChanged
{
    public string Number { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public AddonInfo Addon { get; init; } = null!;
    public CatalogInfo Catalog { get; init; } = null!;

    private IReadOnlyList<PosterItem> _items = Array.Empty<PosterItem>();
    public IReadOnlyList<PosterItem> Items { get => _items; set => Set(ref _items, value); }

    private string _state = "loading";
    /// <summary>"loading", "loaded", "empty" or an error message.</summary>
    public string State
    {
        get => _state;
        set
        {
            Set(ref _state, value);
            OnPropertyChanged(nameof(LoadingVisibility));
            OnPropertyChanged(nameof(MessageVisibility));
            OnPropertyChanged(nameof(Message));
            OnPropertyChanged(nameof(SeeAllVisibility));
        }
    }

    public Visibility LoadingVisibility => State == "loading" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MessageVisibility => State is "loading" or "loaded" ? Visibility.Collapsed : Visibility.Visible;
    public string Message => State == "empty" ? "Aucun élément." : State;
    public Visibility SeeAllVisibility => State == "loaded" && Items.Count >= 10 ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public async Task LoadAsync()
    {
        try
        {
            Items = (await CoreService.LoadCatalogAsync(Addon, Catalog)).Take(24).ToList();
            State = Items.Count == 0 ? "empty" : "loaded";
        }
        catch (Exception ex)
        {
            State = CoreService.Humanize(ex);
        }
    }
}
