using System.Windows;
using System.Windows.Media;

namespace Igloo.App.Views;

/// <summary>How serious the message is; drives the accent colour and the default button.</summary>
public enum FluentMessageSeverity
{
    Info,
    Warning,

    /// <summary>Irreversible and destructive - partitions, boot configuration, data.</summary>
    Danger,
}

/// <summary>
/// The application's own modal dialog, in place of Win32's <see cref="MessageBox"/>.
/// </summary>
/// <remarks>
/// A system MessageBox is a light-themed window that appears mid-wizard looking like a
/// different application - and it appears on exactly the screens where trust matters
/// most, since those are the destructive confirmations. This keeps the same blocking
/// semantics and a comparable call shape, so replacing a call site is a one-line change.
///
/// On a Danger prompt the default button is deliberately the SAFE one: a reflexive Enter
/// or Space cancels rather than deletes a partition. The destructive action always has to
/// be chosen on purpose.
/// </remarks>
public partial class FluentMessageBox : Window
{
    private bool _primaryChosen;

    private FluentMessageBox() => InitializeComponent();

    /// <summary>Shows a modal message with a single dismissing button.</summary>
    public static void Show(
        string title, string message,
        FluentMessageSeverity severity = FluentMessageSeverity.Info,
        string primaryText = "OK",
        Window? owner = null)
        => Build(title, message, severity, primaryText, secondaryText: null, owner).ShowDialog();

    /// <summary>
    /// Shows a modal confirmation. Returns true only when the user picks the primary action.
    /// </summary>
    public static bool Confirm(
        string title, string message,
        FluentMessageSeverity severity = FluentMessageSeverity.Warning,
        string primaryText = "Continue",
        string secondaryText = "Cancel",
        Window? owner = null)
    {
        var dialog = Build(title, message, severity, primaryText, secondaryText, owner);
        dialog.ShowDialog();
        return dialog._primaryChosen;
    }

    private static FluentMessageBox Build(
        string title, string message, FluentMessageSeverity severity,
        string primaryText, string? secondaryText, Window? owner)
    {
        var dialog = new FluentMessageBox
        {
            // Owner drives CenterOwner placement and keeps the dialog above the wizard.
            // Falls back to the main window so callers rarely have to pass one; a null
            // owner would centre on screen and could surface behind the app.
            Owner = owner ?? Application.Current?.MainWindow,
        };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.PrimaryButton.Content = primaryText;

        if (secondaryText is not null)
        {
            dialog.SecondaryButton.Content = secondaryText;
            dialog.SecondaryButton.Visibility = Visibility.Visible;
        }

        var (rule, primaryFill) = severity switch
        {
            FluentMessageSeverity.Danger => (Brush("Brush.Danger"), Brush("Brush.Danger")),
            FluentMessageSeverity.Warning => (Brush("Brush.Warning"), Brush("Brush.Accent")),
            _ => (Brush("Brush.Accent"), Brush("Brush.Accent")),
        };
        dialog.SeverityRule.Background = rule;
        dialog.PrimaryButton.Background = primaryFill;

        // Keyboard safety: on a destructive prompt, Enter must not confirm. The safe
        // option takes the default and the focus, so the dangerous one requires a
        // deliberate click or an explicit Tab to reach.
        if (severity == FluentMessageSeverity.Danger && secondaryText is not null)
        {
            dialog.PrimaryButton.IsDefault = false;
            dialog.SecondaryButton.IsDefault = true;
            dialog.SecondaryButton.Focus();
        }

        return dialog;
    }

    private static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.SlateGray;

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        _primaryChosen = true;
        Close();
    }

    private void OnSecondary(object sender, RoutedEventArgs e) => Close();
}
