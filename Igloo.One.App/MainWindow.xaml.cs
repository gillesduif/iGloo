using System.Windows;
using Igloo.Mascot.Core;

namespace Igloo.One.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515:Consider making public types internal",
    Justification = "WPF XAML-generated partial class is public.")]
public partial class MainWindow : Window
{
    private OneBackendClient? _backend;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _backend = OneBackendClient.Create();
            var capabilities = await _backend.ListCapabilitiesAsync().ConfigureAwait(true);
            CapabilityStatus.Text = $"Local capabilities discovered: {capabilities.Count}.";
        }
        catch (Igloo.Cli.Client.CliTransportException)
        {
            CapabilityStatus.Text = "Local capabilities are unavailable right now.";
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_backend is not null)
            await _backend.DisposeAsync().ConfigureAwait(true);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnMascotDemoClick(object sender, RoutedEventArgs e) =>
        new MascotDemoWindow { Owner = this }.Show();

    private void OnWaveClick(object sender, RoutedEventArgs e) => MainAvatar.Trigger(MascotAction.Wave);
}
