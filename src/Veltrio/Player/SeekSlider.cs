using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Veltrio.Player;

/// <summary>The macOS player's thin slider: a 4 px rail that thickens to
/// 8 px with a knob on hover, the played part in amber, chapter marks
/// painted into the rail. Reports where the pointer hovers, so the page can
/// show a time and chapter tooltip, and the value once a drag ends.</summary>
public sealed class SeekSlider : Grid
{
    private readonly Border _rail;
    private readonly Border _fill;
    private readonly Ellipse _knob;
    private readonly Canvas _marks = new() { IsHitTestVisible = false };
    private bool _hovering;
    private bool _dragging;
    private double _value;
    private double _maximum = 1;
    private IReadOnlyList<double> _chapters = Array.Empty<double>();

    /// <summary>The pointer's position on the rail as a value, or null when it leaves.</summary>
    public event Action<double?>? Hovered;
    /// <summary>A drag moved the value (not committed yet).</summary>
    public event Action<double>? Dragging;
    /// <summary>A click or drag ended here.</summary>
    public event Action<double>? Committed;

    public bool IsDragging => _dragging;

    public double Maximum
    {
        get => _maximum;
        set { _maximum = Math.Max(value, 0.000001); Layout(); }
    }

    /// <summary>Set from outside; ignored while the user drags.</summary>
    public double Value
    {
        get => _value;
        set
        {
            if (_dragging) return;
            _value = Math.Clamp(value, 0, _maximum);
            Layout();
        }
    }

    /// <summary>Chapter starts, in the same unit as the value.</summary>
    public IReadOnlyList<double> Chapters
    {
        get => _chapters;
        set { _chapters = value; Layout(); }
    }

    public SeekSlider()
    {
        Height = 24;
        Background = new SolidColorBrush(Colors.Transparent);
        _rail = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _fill = new Border
        {
            Background = (Brush)Application.Current.Resources["VeltrioAccentBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
        };
        _knob = new Ellipse
        {
            Fill = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        Children.Add(_rail);
        Children.Add(_fill);
        Children.Add(_marks);
        Children.Add(_knob);
        SizeChanged += (_, _) => Layout();
        PointerEntered += (_, _) => { _hovering = true; Layout(); };
        PointerExited += (_, e) =>
        {
            _hovering = false;
            if (!_dragging) Hovered?.Invoke(null);
            Layout();
        };
        PointerMoved += OnPointerMoved;
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => EndDrag();
    }

    private double ValueAt(PointerRoutedEventArgs e) =>
        ActualWidth > 0 ? Math.Clamp(e.GetCurrentPoint(this).Position.X / ActualWidth, 0, 1) * _maximum : 0;

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var at = ValueAt(e);
        Hovered?.Invoke(at);
        if (!_dragging) return;
        _value = at;
        Layout();
        Dragging?.Invoke(at);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        CapturePointer(e.Pointer);
        _value = ValueAt(e);
        Layout();
        Dragging?.Invoke(_value);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        Committed?.Invoke(_value);
        if (!_hovering) Hovered?.Invoke(null);
        Layout();
    }

    private void Layout()
    {
        var active = _hovering || _dragging;
        var height = active ? 8 : 4;
        var width = ActualWidth;
        var fraction = _maximum > 0 ? Math.Clamp(_value / _maximum, 0, 1) : 0;
        _rail.Height = _fill.Height = height;
        _rail.CornerRadius = _fill.CornerRadius = new CornerRadius(height / 2.0);
        _fill.Width = Math.Max(height, fraction * width);
        var knob = active ? 14 : 0;
        _knob.Width = _knob.Height = knob;
        _knob.Margin = new Thickness(Math.Max(0, fraction * width - knob / 2.0), 0, 0, 0);

        _marks.Children.Clear();
        if (width <= 0) return;
        foreach (var start in _chapters)
        {
            if (start <= 0 || start >= _maximum) continue;
            var mark = new Rectangle
            {
                Width = 2, Height = height + 4,
                Fill = new SolidColorBrush(ColorHelper.FromArgb(0xA6, 0xFF, 0xFF, 0xFF)),
            };
            Canvas.SetLeft(mark, start / _maximum * width - 1);
            Canvas.SetTop(mark, (ActualHeight - mark.Height) / 2);
            _marks.Children.Add(mark);
        }
    }
}
