using System.ComponentModel;
using Microsoft.UI.Xaml;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>A row of the live channel list: the channel and what's on,
/// refreshed as the guide loads and time passes.</summary>
public sealed class ChannelRow : INotifyPropertyChanged
{
    public ChannelRow(ChannelItem channel) => Channel = channel;

    public ChannelItem Channel { get; }
    public string Number => Channel.NumberLabel;
    public string Name => Channel.Name;
    public string? Logo => Channel.Logo;
    public string Initials => string.Concat(Channel.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public string NowTitle { get; private set; } = "";
    public double NowProgress { get; private set; }
    public Visibility NowVisibility => NowTitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CatchupVisibility => Channel.HasCatchup ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The channel on screen, in the player's list: an amber bar,
    /// an amber number, a lighter row.</summary>
    public bool IsCurrent { get; set; }
    public double CurrentOpacity => IsCurrent ? 1 : 0;
    public Microsoft.UI.Xaml.Media.Brush NumberBrush => new Microsoft.UI.Xaml.Media.SolidColorBrush(IsCurrent
        ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xE6, 0xA8, 0x4B) : Microsoft.UI.ColorHelper.FromArgb(0x73, 0xFF, 0xFF, 0xFF));
    public Microsoft.UI.Xaml.Media.Brush RowBackground => new Microsoft.UI.Xaml.Media.SolidColorBrush(IsCurrent
        ? Microsoft.UI.ColorHelper.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Microsoft.UI.Colors.Transparent);

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => NowTitle.Length > 0 ? $"{Number} {Name}, {NowTitle}" : $"{Number} {Name}";

    public void Refresh(DateTime now)
    {
        var current = EpgStore.Current(Channel, now);
        NowTitle = current?.Title ?? "";
        NowProgress = (current?.ProgressAt(now) ?? 0) * 100;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
