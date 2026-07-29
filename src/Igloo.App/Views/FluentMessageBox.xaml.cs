using System.Windows;
using System.Windows.Media;

namespace Igloo.App.Views;

public enum FluentMessageSeverity
{
    Info,
    Warning,
    Danger,
}


public partial class FluentMessageBox : Window
{
    private bool _primaryChosen;

    private FluentMessageBox() => InitializeComponent();
    public static void Show(
        string title, string message,
        FluentMessageSeverity severity = FluentMessageSeverity.Info,
        string primaryText = "OK",
        Window? owner = null)
        => Build(title, message, severity, primaryText, secondaryText: null, owner).ShowDialog();

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

        // Severity picks a STYLE for the button, never a Background.
        //
        // Assigning Background directly sets a local value, and a local value outranks
        // every trigger in the control template - so the hover and pressed states can
        // never take effect. The button then sits permanently in whatever colour was
        // assigned, which is exactly how it looked: stuck on the hover shade, and dead
        // to the mouse. Setting the style keeps the template's own state colours intact.
        var (rule, buttonStyle) = severity switch
        {
            FluentMessageSeverity.Danger => (Brush("Brush.Danger"), FindStyle("Button.Danger")),
            FluentMessageSeverity.Warning => (Brush("Brush.Warning"), FindStyle("Button.Primary")),
            _ => (Brush("Brush.Accent"), FindStyle("Button.Primary")),
        };
        dialog.SeverityRule.Background = rule;
        if (buttonStyle is not null)
            dialog.PrimaryButton.Style = buttonStyle;

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

    private static Style? FindStyle(string key) =>
        Application.Current?.TryFindResource(key) as Style;

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        _primaryChosen = true;
        Close();
    }

    private void OnSecondary(object sender, RoutedEventArgs e) => Close();
}
