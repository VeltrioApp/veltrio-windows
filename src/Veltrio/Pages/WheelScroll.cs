using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Veltrio.Pages;

/// <summary>For a horizontal row of posters inside a page that scrolls
/// vertically: a plain mouse wheel scrolls the page instead of being caught
/// by the row (which turns it into a sideways scroll). Shift + wheel, a
/// tilt wheel or a touchpad still scroll the row.
/// <c>local:WheelScroll.PassToPage="True"</c> on the ListView.</summary>
public static class WheelScroll
{
    public static readonly DependencyProperty PassToPageProperty = DependencyProperty.RegisterAttached(
        "PassToPage", typeof(bool), typeof(WheelScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetPassToPage(DependencyObject o) => (bool)o.GetValue(PassToPageProperty);
    public static void SetPassToPage(DependencyObject o, bool value) => o.SetValue(PassToPageProperty, value);

    private static readonly PointerEventHandler Handler = OnWheel;

    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not ListViewBase list) return;
        list.Loaded -= OnListLoaded;
        if ((bool)e.NewValue) list.Loaded += OnListLoaded;
    }

    /// <summary>The wheel has to be caught under the row's own ScrollViewer,
    /// which would otherwise handle it: on the items panel, there once the
    /// list has loaded.</summary>
    private static void OnListLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListViewBase { ItemsPanelRoot: { } panel }) return;
        panel.RemoveHandler(UIElement.PointerWheelChangedEvent, Handler);
        panel.AddHandler(UIElement.PointerWheelChangedEvent, Handler, handledEventsToo: false);
    }

    private static void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var element = (UIElement)sender;
        var point = e.GetCurrentPoint(element);
        if (point.Properties.IsHorizontalMouseWheel
            || (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0) return;
        if (PageScroller(element) is not { } page) return;
        page.ChangeView(null, page.VerticalOffset - point.Properties.MouseWheelDelta, null, false);
        e.Handled = true;
    }

    /// <summary>The nearest ScrollViewer above the row that scrolls
    /// vertically (the row's own one only scrolls sideways).</summary>
    private static ScrollViewer? PageScroller(DependencyObject from)
    {
        for (var node = VisualTreeHelper.GetParent(from); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer { VerticalScrollMode: not ScrollMode.Disabled, ScrollableHeight: > 0 } viewer) return viewer;
        return null;
    }
}
