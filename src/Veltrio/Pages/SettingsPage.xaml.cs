using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>Settings: what Windows has so far — where data lives,
/// playback notes, versions.</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataFolder.Text = Storage.Folder;
        var app = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        string core;
        try { core = CoreService.Version; }
        catch { core = "indisponible"; }
        Versions.Text = $"Veltrio {app} · core {core} · libmpv · Windows App SDK";
    }

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnOpenSources(object sender, RoutedEventArgs e) => Navigator.Window.ShowSection(3);

    private async void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Storage.Folder);
        await Windows.System.Launcher.LaunchFolderPathAsync(Storage.Folder);
    }
}
