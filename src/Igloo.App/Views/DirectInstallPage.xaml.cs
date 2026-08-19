using System.Windows.Controls;

namespace Igloo.App.Views;

public sealed partial class DirectInstallPage
{
    public DirectInstallPage() => InitializeComponent();

    // The tail is re-read on every progress report, so the newest lines are at the
    // bottom; without this the box stays parked wherever the user last left it.
    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) =>
        LogBox.ScrollToEnd();
}
