using System.Windows;
using System.Windows.Controls;
using Igloo.Community.App.ViewModels;

namespace Igloo.Community.App.Views;

public sealed partial class MigrationSetupPage : UserControl
{
    private bool _restoringPasswords;

    public MigrationSetupPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // WPF recreates this page after Back, but retains its view-model. Keep
        // the masked fields consistent with the account that will be staged.
        // Updating either box raises PasswordChanged, so restore both before
        // allowing edits to update the model again.
        _restoringPasswords = true;
        try
        {
            var vm = e.NewValue as MigrationSetupViewModel;
            PasswordBox.Password = vm?.LinuxPassword ?? string.Empty;
            PasswordConfirmBox.Password = vm?.LinuxPasswordConfirm ?? string.Empty;
        }
        finally
        {
            _restoringPasswords = false;
        }
    }

    // PasswordBox.Password is intentionally not a DependencyProperty (WPF design decision
    // to prevent passwords from lingering in the visual tree's binding infrastructure).
    // We push the value to the ViewModel manually on every keystroke instead.
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_restoringPasswords && DataContext is MigrationSetupViewModel vm)
            vm.SetPasswords(PasswordBox.Password, PasswordConfirmBox.Password);
    }


}
