using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Igloo.Core.Runtime;
using Igloo.Core.Runtime.Execution;
using Igloo.Core.Services;
using Igloo.Iso;
using Igloo.Migration;
using Igloo.Preflight;
using Igloo.UsbWriter;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Igloo.Core.Host;

/// <summary>First-party local composition boundary. Product code receives small interfaces, never concrete services.</summary>
public static class IglooHost
{
    public static IServiceCollection AddLocalBackend(this IServiceCollection services,
        bool includeIsoAcquisition = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpClient("iso", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("iGloo/0.0.1-alpha");
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton<IWindowsStorageReader, WindowsStorageReader>();
        services.AddSingleton<IWindowsBitLockerReader, WindowsBitLockerReader>();
        services.AddSingleton<IWindowsBcdReader, WindowsBcdReader>();
        services.AddSingleton<IWindowsFirmwareReader, WindowsFirmwareReader>();
        services.AddSingleton<IPreflightChecker, WindowsPreflightChecker>();
        services.AddSingleton<ILinuxRemovalService, LinuxRemovalService>();
        services.AddSingleton<IPartitionResizeService, PartitionResizeService>();
        services.AddSingleton<IDirectInstallService, DirectInstallService>();
        services.AddSingleton<IIsoAcquisitionService, IsoAcquisitionService>();
        services.AddSingleton<IsoAcquisitionCoordinator>();
        services.AddSingleton<MigrationStagingCoordinator>();
        services.AddSingleton<IFileStagingService, FileStagingService>();
        services.AddSingleton<IUsbWriterService, UsbWriterService>();
        services.AddSingleton<DistroLoader>();
        services.AddSingleton<DistroRegistry>();
        services.AddSingleton(sp => new BackendCatalog(sp.GetRequiredService<DistroLoader>(),
            sp.GetRequiredService<DistroRegistry>()));
        services.AddSingleton<IBackendCatalog>(sp => sp.GetRequiredService<BackendCatalog>());
        services.AddSingleton<IProviderCatalog>(sp => sp.GetRequiredService<BackendCatalog>());
        services.AddSingleton<IOperationJournal>(_ => new FileOperationJournal(FileOperationJournal.DefaultDirectory));
        services.AddSingleton<IAdvisoryPlanJournal>(_ => new FileAdvisoryPlanJournal(
            FileAdvisoryPlanJournal.DefaultDirectory));
        services.AddSingleton(_ => new FileArtifactRegistry(FileArtifactRegistry.DefaultDirectory,
            IsoAcquisitionService.DefaultCacheDirectory));
        services.AddSingleton<ICanonicalMigrationTargetSource, WindowsCanonicalMigrationTargetSource>();
        services.AddSingleton(sp => new FileMigrationTargetRegistry(
            FileMigrationTargetRegistry.DefaultDirectory,
            sp.GetRequiredService<ICanonicalMigrationTargetSource>()));
        services.AddSingleton<IAllocationObservationSource, WindowsAllocationObservationSource>();
        services.AddSingleton(sp => new FileMigrationAllocationRegistry(
            FileMigrationAllocationRegistry.DefaultDirectory,
            sp.GetRequiredService<FileMigrationTargetRegistry>(),
            sp.GetRequiredService<IAllocationObservationSource>()));
        services.AddSingleton<IEndpointInspection>(sp => sp.GetRequiredService<IPreflightWorkflow>());
        services.AddSingleton<IIsoWorkflow>(sp => new IsoWorkflow(sp.GetRequiredService<IsoAcquisitionCoordinator>()));
        services.AddSingleton<IMigrationStagingWorkflow>(sp => new MigrationStagingWorkflow(
            sp.GetRequiredService<MigrationStagingCoordinator>()));
        services.AddSingleton<IProviderCompatibility>(sp => new ProviderCompatibility(
            sp.GetRequiredService<DistroRegistry>(), sp.GetRequiredService<DistroLoader>()));
        services.AddSingleton<IInstallationPreparation>(sp => new InstallationPreparation(
            sp.GetRequiredService<DistroRegistry>(), sp.GetRequiredService<IDirectInstallService>()));
        services.AddSingleton<IPreflightWorkflow>(sp => new PreflightWorkflow(
            sp.GetRequiredService<IPreflightChecker>(), sp.GetRequiredService<ILinuxRemovalService>(),
            sp.GetRequiredService<ILogger<PreflightWorkflow>>()));
        services.AddSingleton<IUsbPreparation>(sp => new UsbPreparation(
            sp.GetRequiredService<IUsbWriterService>(), sp.GetRequiredService<IPartitionResizeService>()));
        services.AddSingleton<IWindowsMaintenance>(_ => new WindowsMaintenance());
        services.AddSingleton<IUserProfileInspection>(sp => new UserProfileInspection(
            sp.GetRequiredService<ILogger<UserProfileInspection>>()));
        services.AddSingleton<ICommandDispatcher>(sp =>
        {
            var journal = sp.GetRequiredService<IOperationJournal>();
            var additional = new List<ICommandHandler>
            {
                new OperationStatusHandler(journal, false),
                new OperationStatusHandler(journal, true),
                new ArtifactStatusHandler(sp.GetRequiredService<FileArtifactRegistry>()),
                new MigrationTargetBindHandler(sp.GetRequiredService<IAdvisoryPlanJournal>(),
                    sp.GetRequiredService<IPreflightChecker>(),
                    sp.GetRequiredService<DistroLoader>(),
                    sp.GetRequiredService<DistroRegistry>(),
                    sp.GetRequiredService<ICanonicalMigrationTargetSource>(),
                    sp.GetRequiredService<FileMigrationTargetRegistry>()),
                new MigrationTargetAllocateHandler(sp.GetRequiredService<IAdvisoryPlanJournal>(),
                    sp.GetRequiredService<FileMigrationTargetRegistry>(),
                    sp.GetRequiredService<FileMigrationAllocationRegistry>(),
                    sp.GetRequiredService<IAllocationObservationSource>(),
                    sp.GetRequiredService<IPreflightChecker>(),
                    sp.GetRequiredService<DistroLoader>(),
                    sp.GetRequiredService<DistroRegistry>()),
            };
            if (includeIsoAcquisition)
            {
                additional.Add(new IsoAcquireHandler(sp.GetRequiredService<DistroLoader>(),
                    sp.GetRequiredService<IsoAcquisitionCoordinator>(), journal));
                additional.Add(new IsoAcquireHandler(sp.GetRequiredService<DistroLoader>(),
                    sp.GetRequiredService<IsoAcquisitionCoordinator>(), journal,
                    sp.GetRequiredService<FileArtifactRegistry>(), artifactMode: true));
            }
            return new CommandDispatcher(sp.GetRequiredService<IPreflightChecker>(),
                sp.GetRequiredService<DistroLoader>(), sp.GetRequiredService<DistroRegistry>(), additional,
                sp.GetRequiredService<IAdvisoryPlanJournal>());
        });
        return services;
    }

    public static async Task<ILocalBackend> CreateAsync(string distrosDirectory,
        CancellationToken cancellationToken = default)
        => await CreateCoreAsync(distrosDirectory, includeIsoAcquisition: true,
            cancellationToken).ConfigureAwait(false);

    public static async Task<ILocalBackend> CreateReadOnlyAsync(string distrosDirectory,
        CancellationToken cancellationToken = default)
        => await CreateCoreAsync(distrosDirectory, includeIsoAcquisition: false,
            cancellationToken).ConfigureAwait(false);

    private static async Task<ILocalBackend> CreateCoreAsync(string distrosDirectory,
        bool includeIsoAcquisition, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalBackend(includeIsoAcquisition);
        var provider = services.BuildServiceProvider();
        try
        {
            await provider.GetRequiredService<IBackendCatalog>().LoadAsync(distrosDirectory,
                cancellationToken).ConfigureAwait(false);
            return new LocalBackend(provider, provider.GetRequiredService<ICommandDispatcher>(),
                provider.GetRequiredService<IEndpointInspection>(),
                provider.GetRequiredService<IProviderCompatibility>());
        }
        catch
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

public interface ILocalBackend : IAsyncDisposable
{
    ICommandDispatcher Commands { get; }
    IEndpointInspection Inspection { get; }
    IProviderCompatibility Providers { get; }
}

public interface IBackendCatalog
{
    Task LoadAsync(string distrosDirectory, CancellationToken cancellationToken = default);
}

public interface IProviderCatalog
{
    IReadOnlyList<DistroManifest> LoadedDistros { get; }
}

internal sealed class BackendCatalog(DistroLoader loader, DistroRegistry registry)
    : IBackendCatalog, IProviderCatalog
{
    public IReadOnlyList<DistroManifest> LoadedDistros => loader.LoadedDistros;
    public async Task LoadAsync(string distrosDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distrosDirectory);
        loader.Load(distrosDirectory);
        await registry.LoadAsync(distrosDirectory, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class LocalBackend(ServiceProvider provider, ICommandDispatcher commands,
    IEndpointInspection inspection, IProviderCompatibility providers) : ILocalBackend
{
    public ICommandDispatcher Commands => commands;
    public IEndpointInspection Inspection => inspection;
    public IProviderCompatibility Providers => providers;
    public ValueTask DisposeAsync() => provider.DisposeAsync();
}

public interface IIsoWorkflow
{
    Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
        IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default);
}

internal sealed class IsoWorkflow(IsoAcquisitionCoordinator coordinator) : IIsoWorkflow
{
    public Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
        IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default) =>
        coordinator.AcquireAsync(distro, progress, cancellationToken);
}

public sealed record LocalMigrationStagingInput(FileStagingRequest Files, UserSetup User,
    PreflightReport ObservedMachine, DiskInfo? TargetDisk, DiskInstallMode InstallMode,
    int LinuxSizeGb);

public interface IMigrationStagingWorkflow
{
    Task<FileStagingResult> StageAsync(LocalMigrationStagingInput input,
        IProgress<FileStagingProgress>? progress, CancellationToken cancellationToken = default);
}

internal sealed class MigrationStagingWorkflow(MigrationStagingCoordinator coordinator) : IMigrationStagingWorkflow
{
    public async Task<FileStagingResult> StageAsync(LocalMigrationStagingInput input,
        IProgress<FileStagingProgress>? progress, CancellationToken cancellationToken = default)
    {
        using var lease = await ResourceLock.AcquireManyAsync(["migration-staging"],
            cancellationToken).ConfigureAwait(false);
        return await coordinator.StageAsync(new MigrationStagingInput(input.Files, input.User,
            input.ObservedMachine, input.TargetDisk, input.InstallMode, input.LinuxSizeGb),
            progress, cancellationToken).ConfigureAwait(false);
    }
}

public interface IProviderCompatibility
{
    bool IsLoaded(string distroId);
    IReadOnlyList<PreflightFinding> Evaluate(string distroId, PreflightReport report);
    Task<ProviderInspection?> InspectAsync(string distroId, CancellationToken cancellationToken = default);
}

public sealed record ProviderInspection(string DistroId, long MinimumDiskBytes,
    string ImplementationHash);

internal sealed class ProviderCompatibility(DistroRegistry registry, DistroLoader loader) : IProviderCompatibility
{
    private readonly DistroCompatibilityEvaluator _evaluator = new(registry, loader);
    public bool IsLoaded(string distroId) => registry.TryGet(distroId, out _);
    public IReadOnlyList<PreflightFinding> Evaluate(string distroId, PreflightReport report) =>
        _evaluator.Evaluate(distroId, report);

    public async Task<ProviderInspection?> InspectAsync(string distroId,
        CancellationToken cancellationToken = default)
    {
        if (!registry.TryGet(distroId, out var plugin)) return null;
        var manifest = loader.LoadedDistros.SingleOrDefault(d => string.Equals(d.Id, distroId,
            StringComparison.OrdinalIgnoreCase));
        return new ProviderInspection(plugin.Id, plugin.Metadata.MinimumRequirements.MinDiskBytes,
            await ProviderRevision.FingerprintAsync(plugin, manifest, cancellationToken)
                .ConfigureAwait(false));
    }
}

/// <summary>Private first-party path; no public destructive command is registered.</summary>
public interface IInstallationPreparation
{
    Task PrepareAsync(string distroId, int diskNumber, long linuxSizeBytes, string isoPath,
        string stagingDirectory, Uri? stage2Url, IProgress<DirectInstallProgress>? progress,
        CancellationToken cancellationToken = default);
    Task RegisterBootEntryAsync(IProgress<DirectInstallProgress>? progress,
        CancellationToken cancellationToken = default);
}

internal sealed class InstallationPreparation(DistroRegistry registry, IDirectInstallService service)
    : IInstallationPreparation
{
    public async Task PrepareAsync(string distroId, int diskNumber, long linuxSizeBytes, string isoPath,
        string stagingDirectory, Uri? stage2Url, IProgress<DirectInstallProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!registry.TryGet(distroId, out var plugin))
            throw new InvalidOperationException(
                $"No installer plugin is loaded for distro '{distroId}'. " +
                "Ensure the distro's Igloo.Distro.*.dll is present in its distros/ folder.");
        using var lease = await ResourceLock.AcquireManyAsync(["installation", "disk:" + diskNumber,
            "partition-table:" + diskNumber, "migration-staging", "recovery-state"],
            cancellationToken).ConfigureAwait(false);
        await service.PrepareAsync(diskNumber, linuxSizeBytes, isoPath, stagingDirectory,
            plugin.GetInstallerBootSpec(), stage2Url, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task RegisterBootEntryAsync(IProgress<DirectInstallProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        using var lease = await ResourceLock.AcquireManyAsync(
            ["boot-configuration", "installation", "recovery-state"],
            cancellationToken).ConfigureAwait(false);
        await service.RegisterBootEntryAsync(progress, cancellationToken).ConfigureAwait(false);
    }
}

public interface IEndpointInspection
{
    Task<PreflightReport> InspectAsync(CancellationToken cancellationToken = default);
}

public interface IPreflightWorkflow : IEndpointInspection
{
    Task<PreflightReport> InspectAndCleanupAsync(CancellationToken cancellationToken = default);
    Task RemoveLinuxAsync(IReadOnlyList<LinuxInstallation> installations,
        IReadOnlyList<SeedLeftover> leftovers, bool removingAllLinux,
        IProgress<string>? progress, CancellationToken cancellationToken = default);
}

internal sealed class PreflightWorkflow(IPreflightChecker checker, ILinuxRemovalService removal,
    ILogger<PreflightWorkflow> logger) : IPreflightWorkflow
{
    public Task<PreflightReport> InspectAsync(CancellationToken cancellationToken = default) =>
        checker.RunAsync(cancellationToken);

    public async Task RemoveLinuxAsync(IReadOnlyList<LinuxInstallation> installations,
        IReadOnlyList<SeedLeftover> leftovers, bool removingAllLinux,
        IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        var disks = installations.Select(i => i.DiskNumber)
            .Concat(leftovers.Select(s => s.DiskNumber)).Distinct().ToArray();
        using var lease = await ResourceLock.AcquireManyAsync(disks.SelectMany(d => new[]
            { "disk:" + d, "partition-table:" + d }).Concat(["boot-configuration", "recovery-state"]),
            cancellationToken).ConfigureAwait(false);
        await removal.RemoveAsync(installations, leftovers, removingAllLinux, progress,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PreflightReport> InspectAndCleanupAsync(CancellationToken cancellationToken = default)
    {
        var report = await checker.RunAsync(cancellationToken).ConfigureAwait(false);
        if (report.SeedLeftovers.Count > 0)
        {
            try
            {
                await RemoveLinuxAsync([], report.SeedLeftovers, removingAllLinux: false,
                    progress: null, cancellationToken).ConfigureAwait(false);
                report = await checker.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Auto-cleanup of leftover installer partitions failed (non-fatal)");
            }
        }
        try
        {
            var windowsDisk = report.Disks.FirstOrDefault(d => d.Partitions.Any(p => p.IsBoot));
            if (windowsDisk is not null && TryDiskNumber(windowsDisk.DeviceId, out var diskNumber))
            {
                using var lease = await ResourceLock.AcquireManyAsync(["disk:" + diskNumber,
                    "partition-table:" + diskNumber], cancellationToken).ConfigureAwait(false);
                var bytes = await removal.ReclaimFreeSpaceAsync(diskNumber,
                    ct: cancellationToken).ConfigureAwait(false);
                if (bytes > 0)
                {
                    logger.LogInformation("Reclaimed {Bytes} bytes on disk {Disk}", bytes, diskNumber);
                    report = await checker.RunAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not reclaim adjacent free space (non-fatal)");
        }
        return report;
    }

    private static bool TryDiskNumber(string deviceId, out uint number)
    {
        const string marker = "PHYSICALDRIVE";
        var at = deviceId.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        number = 0;
        return at >= 0 && uint.TryParse(deviceId[(at + marker.Length)..], out number);
    }
}

public sealed record UsbPreparationInput(UsbDriveInfo Drive, string IsoPath, string StagingDirectory,
    DiskInfo? TargetDisk, DiskInstallMode InstallMode, int LinuxSizeGb);

public interface IUsbPreparation
{
    Task<IReadOnlyList<UsbDriveInfo>> EnumerateDrivesAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(UsbPreparationInput input, IProgress<UsbWriteProgress>? progress,
        IProgress<string>? resizeProgress, CancellationToken cancellationToken = default);
}

internal sealed class UsbPreparation(IUsbWriterService writer, IPartitionResizeService resizer)
    : IUsbPreparation
{
    public Task<IReadOnlyList<UsbDriveInfo>> EnumerateDrivesAsync(CancellationToken cancellationToken = default) =>
        writer.EnumerateDrivesAsync(cancellationToken);

    public async Task WriteAsync(UsbPreparationInput input, IProgress<UsbWriteProgress>? progress,
        IProgress<string>? resizeProgress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var resources = new List<string> { "disk:" + input.Drive.DriveIndex,
            "partition-table:" + input.Drive.DriveIndex, "migration-staging" };
        if (input.InstallMode == DiskInstallMode.DualBoot && input.TargetDisk is not null)
        {
            const string diskPrefix = "\\\\.\\PHYSICALDRIVE";
            var windowsDisk = int.Parse(input.TargetDisk.DeviceId.AsSpan()[diskPrefix.Length..],
                CultureInfo.InvariantCulture);
            resources.Add("disk:" + windowsDisk);
            resources.Add("partition-table:" + windowsDisk);
        }
        using var lease = await ResourceLock.AcquireManyAsync(resources,
            cancellationToken).ConfigureAwait(false);
        if (input.InstallMode == DiskInstallMode.DualBoot && input.TargetDisk is not null && input.LinuxSizeGb > 0)
        {
            const string prefix = "\\\\.\\PHYSICALDRIVE";
            var diskNumber = int.Parse(input.TargetDisk.DeviceId.AsSpan()[prefix.Length..], CultureInfo.InvariantCulture);
            var linuxBytes = (long)input.LinuxSizeGb * 1024 * 1024 * 1024;
            await resizer.ShrinkAsync(diskNumber, linuxBytes, resizeProgress, cancellationToken)
                .ConfigureAwait(false);
        }
        await writer.WriteAsync(input.Drive, input.IsoPath, input.StagingDirectory, progress,
            cancellationToken).ConfigureAwait(false);
    }
}

public interface IWindowsMaintenance
{
    Task<int> StartBitLockerDecryptionAsync(string systemDrive, CancellationToken cancellationToken = default);
    void RestartToFirmware();
    void RestartToInstaller();
}

internal sealed class WindowsMaintenance : IWindowsMaintenance
{
    public async Task<int> StartBitLockerDecryptionAsync(string systemDrive,
        CancellationToken cancellationToken = default)
    {
        using var process = Process.Start(new ProcessStartInfo(FindNativeExe("manage-bde.exe"),
            $"-off {systemDrive}") { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("BitLocker control could not start.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    public void RestartToFirmware() => Process.Start(new ProcessStartInfo(
        FindNativeExe("shutdown.exe"), "/r /fw /t 10")
    { UseShellExecute = false, CreateNoWindow = true });

    public void RestartToInstaller() => Process.Start(new ProcessStartInfo("shutdown.exe",
        "/r /t 10 /c \"iGloo is restarting to install Linux. Save any open work now.\"")
    { UseShellExecute = false, CreateNoWindow = true });

    private static string FindNativeExe(string exeName)
    {
        var winRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var sysnative = Path.Join(winRoot, "Sysnative", exeName);
        return File.Exists(sysnative) ? sysnative : Path.Join(winRoot, "System32", exeName);
    }
}

public interface IUserProfileInspection
{
    IReadOnlyList<AppSuggestion> GetSuggestedPackages();
    string? FindAccountPicture();
    string? FlatpakForBrowser(string browserName);
}

public sealed record AppSuggestion(string WindowsDisplayName, string LinuxAppName,
    string? FlatpakId, string? NativePackage);

internal sealed class UserProfileInspection(ILogger<UserProfileInspection> logger) : IUserProfileInspection
{
    public IReadOnlyList<AppSuggestion> GetSuggestedPackages() => WindowsAppScanner.Scan()
        .Select(s => new AppSuggestion(s.WindowsDisplayName, s.LinuxAppName,
            s.FlatpakId, s.NativePackage)).ToArray();
    public string? FindAccountPicture() => AccountPictureReader.TryFindAccountPicture(logger);
    public string? FlatpakForBrowser(string browserName) => WindowsAppScanner.FlatpakFor(browserName);
}
