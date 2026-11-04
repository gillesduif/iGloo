using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Igloo.Community.App.ViewModels;
using Igloo.Cli.Client;
using Igloo.Core.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Igloo.Community.App;

public partial class IglooApp : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = BuildHost();

        // Log unhandled exceptions so any crash leaves a diagnosable record  but
        // do NOT swallow them, and do NOT force software rendering. An earlier
        // attempt to "survive" WPF render faults that way masked nothing and made
        // the whole UI run on the CPU (slideshow-slow on real GPUs). Reverted:
        // fail loudly, keep hardware acceleration, fix the actual root cause.
        DispatcherUnhandledException += (_, args) =>
            Log.Fatal(args.Exception, "Unhandled dispatcher exception");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception,
                "Unhandled AppDomain exception (terminating: {IsTerminating})", args.IsTerminating);
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        await _host.StartAsync();
        Log.Information("Igloo starting up");

        var distrosDir = FindDistrosDirectory();

        await _host.Services.GetRequiredService<IBackendCatalog>().LoadAsync(distrosDir);

        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static IHost BuildHost() =>
        Host.CreateDefaultBuilder()
            .UseSerilog((_, cfg) => cfg
                .MinimumLevel.Debug()
                .WriteTo.File(
                    path: Path.Join(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Igloo", "logs", "igloo-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    formatProvider: CultureInfo.InvariantCulture,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"))
            .ConfigureServices(RegisterServices)
            .Build();

    [SupportedOSPlatform("windows")]
    private static void RegisterServices(IServiceCollection services)
    {
        services.AddCommunityBackend();
        services.AddSingleton(_ => CliCommandClient.ForApplication(AppContext.BaseDirectory,
            TimeSpan.FromHours(3)));
        services.Replace(ServiceDescriptor.Singleton<IIsoWorkflow>(sp => new CliIsoWorkflow(
            sp.GetRequiredService<CliCommandClient>())));
        services.AddSingleton<ICommunityReviewedInstallationWorkflow,
            CliReviewedInstallationWorkflow>();

        // ViewModels - singletons so wizard state is preserved when navigating back
        services.AddSingleton<WelcomeViewModel>();
        services.AddSingleton<PreflightViewModel>();
        services.AddSingleton<DistroSelectionViewModel>();
        services.AddSingleton<IsoAcquisitionViewModel>();
        services.AddSingleton<MigrationSetupViewModel>();
        services.AddSingleton<DiskSelectionViewModel>();
        services.AddSingleton<DirectInstallViewModel>();
        services.AddSingleton<FileStagingViewModel>();
        services.AddSingleton<UsbWriterViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
    }

    private static string FindDistrosDirectory()
    {
        var adjacent = Path.Join(AppContext.BaseDirectory, "distros");
        var directory = new DirectoryInfo(adjacent);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The adjacent iGloo provider package is unavailable.");
        Log.Information("Distros directory: {Dir} (adjacent to exe)", directory.FullName);
        return directory.FullName;
    }
}
