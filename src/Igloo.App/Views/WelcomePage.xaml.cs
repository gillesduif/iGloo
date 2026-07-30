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

    /// <summary>
    /// Puts the alpha warning in front of the user once, as a modal.
    /// </summary>
    /// <remarks>
    /// It used to be a card on this page. A banner among other banners on a page people
    /// scroll past is read once and then stops registering - which is the wrong outcome
    /// for the one message that says this tool rewrites partition tables. A modal has to
    /// be dismissed deliberately, before anything can reach a disk.
    ///
    /// Shown once per run, not once per visit: the user can navigate back to Welcome
    /// several times in a session, and a warning that reappears every time is one people
    /// learn to click through without reading.
    /// </remarks>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_warningShown)
            return;
        _warningShown = true;

        // Let the shell finish rendering first, so the dialog opens over a drawn window
        // rather than a half-painted one.
        Dispatcher.BeginInvoke(new Action(() =>
            FluentMessageBox.Show(
                "Pre-release software notice ",
                "\n• iGloo performs low level modifications to partition tables and the   boot configurations.\n\n" +
                "• Running this utility may result in data loss. Do not use this tool on a   production machine.\n\n" +
                "• Ensure you have a complete system backup before continuing.",
                FluentMessageSeverity.Warning,
                primaryText: "Continue")),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
