using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Igloo.Community.App.ViewModels;
using Igloo.Community.App.Views;
using Igloo.Core.Host;
using Xunit;

namespace Igloo.Community.App.Tests;

[CollectionDefinition("Community WPF application", DisableParallelization = true)]
public sealed class CommunityWpfApplicationTestGroup { }

[Collection("Community WPF application")]
public sealed class MigrationSetupPageTests
{
    [Fact]
    public void Recreating_configuration_restores_masked_passwords_and_keeps_edits_in_sync()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        CheckPasswordFields(application);
                    }
#pragma warning disable CA1031 // Marshal an assertion failure from the STA test thread.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        failure = ex;
                    }
                    finally
                    {
                        application.Shutdown();
                    }
                }));
                // Use Application's own message loop so queued shutdown ends
                // that loop and clears Application.Current before returning.
                application.Run();
            }
#pragma warning disable CA1031 // Marshal an assertion failure from the STA test thread.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The WPF configuration check timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void CheckPasswordFields(Application application)
    {
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/iGloo;component/Themes/IglooTheme.xaml"),
        });
        application.Resources["BoolToVis"] = new BooleanToVisibilityConverter();
        var setup = new MigrationSetupViewModel(new Profile(), []);
        setup.SetPasswords("temporary-passphrase", "temporary-passphrase");

        var page = new MigrationSetupPage { DataContext = setup };
        var password = Assert.IsType<PasswordBox>(page.FindName("PasswordBox"));
        var confirmation = Assert.IsType<PasswordBox>(page.FindName("PasswordConfirmBox"));
        Assert.Equal(setup.LinuxPassword, password.Password);
        Assert.Equal(setup.LinuxPasswordConfirm, confirmation.Password);
        Assert.True(setup.IsPasswordMatch);

        password.Password = "another-passphrase";
        Assert.Equal("another-passphrase", setup.LinuxPassword);
        Assert.Equal("temporary-passphrase", setup.LinuxPasswordConfirm);
        Assert.False(setup.IsPasswordMatch);
        confirmation.Password = password.Password;
        Assert.True(setup.IsPasswordMatch);

        // A second page is what the wizard creates after leaving and
        // revisiting Configuration through its data templates.
        var revisited = new MigrationSetupPage { DataContext = setup };
        Assert.Equal("another-passphrase",
            Assert.IsType<PasswordBox>(revisited.FindName("PasswordBox")).Password);
        Assert.Equal("another-passphrase",
            Assert.IsType<PasswordBox>(revisited.FindName("PasswordConfirmBox")).Password);

        var emptySetup = new MigrationSetupViewModel(new Profile(), []);
        revisited.DataContext = emptySetup;
        Assert.Empty(Assert.IsType<PasswordBox>(revisited.FindName("PasswordBox")).Password);
        Assert.Empty(Assert.IsType<PasswordBox>(revisited.FindName("PasswordConfirmBox")).Password);
        Assert.False(emptySetup.CanProceed);
        Assert.Equal("another-passphrase", setup.LinuxPassword);
    }

    private sealed class Profile : IUserProfileInspection
    {
        public IReadOnlyList<AppSuggestion> GetSuggestedPackages() => [];
        public string? FindAccountPicture() => null;
        public string? FlatpakForBrowser(string browserName) => null;
    }
}
