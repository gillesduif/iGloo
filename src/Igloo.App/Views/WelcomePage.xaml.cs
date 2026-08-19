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
    // Once per process, not once per WelcomePage instance.
    private static bool TryClaimFirstShow()
    {
        if (_warningShown)
            return false;
        _warningShown = true;
        return true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!TryClaimFirstShow())
            return;

        Dispatcher.BeginInvoke(new Action(() =>
            FluentMessageBox.Show(
                "Pre-release software notice ",
                "This software is a pre-release version. \n\nIt may contain bugs, errors or missing features. \nData loss or unexpected behavior can occur. \n\nUse this software at your own risk.",
                FluentMessageSeverity.Warning,
                primaryText: "Continue")),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
