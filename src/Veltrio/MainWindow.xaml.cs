using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Veltrio.Pages;

namespace Veltrio;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(PlaceholderPage), "Settings");
            return;
        }
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        switch (tag)
        {
            case "home": ContentFrame.Navigate(typeof(HomePage)); break;
            case "library": ContentFrame.Navigate(typeof(PlaceholderPage), "Collection"); break;
            case "live": ContentFrame.Navigate(typeof(PlaceholderPage), "Live"); break;
            case "sources": ContentFrame.Navigate(typeof(PlaceholderPage), "Sources"); break;
        }
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ContentFrame.Navigate(typeof(PlaceholderPage), $"Search · {args.QueryText}");
    }
}
