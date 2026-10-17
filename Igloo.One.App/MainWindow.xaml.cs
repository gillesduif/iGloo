using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Igloo.One.App;
/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
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
            Title = $"iGloo One · {capabilities.Count} local capabilities";
        }
        catch (Igloo.Cli.Client.CliTransportException)
        {
            Title = "iGloo One · CLI unavailable";
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_backend is not null) await _backend.DisposeAsync().ConfigureAwait(true);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnMascotDemoClick(object sender, RoutedEventArgs e) =>
        new MascotDemoWindow { Owner = this }.Show();

    private void OnStepChanged(object sender, System.Windows.Data.DataTransferEventArgs e)
    {
        // The One shell has no workflow coordinator yet.
    }
}
