using System.Windows;
using System.Windows.Controls;
using Igloo.App.ViewModels;

namespace Igloo.App.Views;

public sealed partial class MigrationSetupPage : UserControl
{
    public MigrationSetupPage()
    {
        InitializeComponent();
    }

    // PasswordBox.Password is intentionally not a DependencyProperty (WPF design decision
    // to prevent passwords from lingering in the visual tree's binding infrastructure).
    // We push the value to the ViewModel manually on every keystroke instead.
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MigrationSetupViewModel vm)
            vm.SetPasswords(PasswordBox.Password, PasswordConfirmBox.Password);
    }
}
