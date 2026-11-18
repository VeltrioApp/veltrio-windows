using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>Home: Cinemeta's catalogs as poster rows, loaded through the
/// Rust core — the first screen to prove the core works on Windows.</summary>
public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            CoreVersionText.Text = $"Core {CoreService.Version}";
            Rows.ItemsSource = await CoreService.LoadHomeAsync();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            Loading.IsActive = false;
            Loading.Visibility = Visibility.Collapsed;
        }
    }
}
