using System.Windows;
using System.Windows.Controls;

namespace Igloo.App.Views;

public sealed partial class WelcomePage : UserControl
{
    private static bool _warningShown;

    public WelcomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_warningShown)
            return;
        _warningShown = true;

        Dispatcher.BeginInvoke(new Action(() =>
            FluentMessageBox.Show(
                "Pre-release software notice ",
                "This software is a pre-release version. \n\nIt may contain bugs, errors, or missing features. \nData loss or unexpected behavior can occur. \n\nUse this software at your own risk.\nWe do not provide standard support for this version.",
                FluentMessageSeverity.Warning,
                primaryText: "Continue")),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
