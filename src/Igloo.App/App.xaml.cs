using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using Igloo.App.ViewModels;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Igloo.Core.Services;
using Igloo.Iso;
using Igloo.Migration;
using Igloo.Preflight;
using Igloo.UsbWriter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Igloo.App;

/// <summary>
/// Application entry point. Bootstraps the generic host, configures Serilog,
/// registers all services and view-models, then shows the main window.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = BuildHost();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled dispatcher exception");
            Log.CloseAndFlush();
        };

        await _host.StartAsync();
        Log.Information("Igloo starting up");

        var distrosDir = FindDistrosDirectory();

        // Load distro catalog (JSON manifests → DistroLoader.LoadedDistros).
        _host.Services.GetRequiredService<DistroLoader>().Load(distrosDir);

        // Load distro plugin DLLs (IDistroPlugin implementations → DistroRegistry).
        await _host.Services.GetRequiredService<DistroRegistry>().LoadAsync(distrosDir);

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
                    path: Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Igloo", "logs", "igloo-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"))
            .ConfigureServices(RegisterServices)
            .Build();

    [SupportedOSPlatform("windows")]
    private static void RegisterServices(IServiceCollection services)
    {
        // HTTP client used for ISO downloads (large files — no timeout).
        services.AddHttpClient("iso", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("iGloo/0.0.1-alpha");
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        // Core services
        services.AddSingleton<IPreflightChecker, WindowsPreflightChecker>();
        services.AddSingleton<IPartitionResizeService, PartitionResizeService>();
        services.AddSingleton<IDirectInstallService, DirectInstallService>();
        services.AddSingleton<IIsoAcquisitionService, IsoAcquisitionService>();
        services.AddSingleton<IFileStagingService, FileStagingService>();
        services.AddSingleton<IUsbWriterService, UsbWriterService>();
        services.AddSingleton<ManifestGeneratorService>();
        services.AddSingleton<DistroLoader>();
        services.AddSingleton<DistroRegistry>();

        // ViewModels — singletons so wizard state is preserved when navigating back
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

    /// <summary>
    /// In a published app <c>distros/</c> sits next to the exe.
    /// During development (<c>dotnet run</c>) we walk up from the bin directory
    /// until we find the repo-root <c>distros/</c> folder.
    /// </summary>
    private static string FindDistrosDirectory()
    {
        var adjacent = Path.Combine(AppContext.BaseDirectory, "distros");
        if (Directory.Exists(adjacent))
        {
            Log.Information("Distros directory: {Dir} (adjacent to exe)", adjacent);
            return adjacent;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "distros");
            if (Directory.Exists(candidate))
            {
                Log.Information("Distros directory: {Dir} (found via parent walk)", candidate);
                return candidate;
            }
            dir = dir.Parent;
        }

        Log.Warning("Distros directory not found — searched from {Base}. " +
                    "Expected 'distros/' adjacent to the exe or in a parent directory.",
                    AppContext.BaseDirectory);
        return adjacent; // fallback — produces empty catalog
    }
}
