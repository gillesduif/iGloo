using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Preparation;
using Igloo.Core.Plugins;
using Igloo.Core.Runtime;
using Igloo.Core.Runtime.Execution;
using Igloo.Core.Services;
using Igloo.Iso;
using Igloo.Migration;
using Igloo.Preflight;
using Igloo.Preflight.CommunityPreparation;
using Igloo.Preflight.CommunityRecovery;
using Igloo.UsbWriter;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Igloo.Core.Host;

/// <summary>First-party local composition boundary. Product code receives small interfaces, never concrete services.</summary>
public static class IglooHost
{
    public static IServiceCollection AddLocalBackend(this IServiceCollection services,
        bool includeIsoAcquisition = true)
        => AddBackend(services, includeIsoAcquisition, preserveWindows: false);

    /// <summary>Community always preserves Windows and prepares dual boot.</summary>
    public static IServiceCollection AddCommunityBackend(this IServiceCollection services,
        bool includeIsoAcquisition = true)
        => AddBackend(services, includeIsoAcquisition, preserveWindows: true);

    private static IServiceCollection AddBackend(IServiceCollection services,
        bool includeIsoAcquisition, bool preserveWindows)
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
        services.AddSingleton<DirectInstallService>();
        services.AddSingleton<IDirectInstallService>(sp =>
            sp.GetRequiredService<DirectInstallService>());
        services.AddSingleton<IReviewedDirectInstallService>(sp =>
            sp.GetRequiredService<DirectInstallService>());
        services.AddSingleton(_ => new PreparedLayoutStore(
            CommunityRecoveryArtifactStore.ForCurrentUser()));
        services.AddSingleton<IReviewedInstallerInspection, ReviewedInstallerInspection>();
        services.AddSingleton<IReviewedInstallationMedia, ReviewedInstallationMedia>();
        services.AddSingleton<IReviewedInstallationSizing, ReviewedInstallationSizing>();
        services.AddSingleton<IReviewedInstallationPlanning, ReviewedInstallationPlanning>();
        services.AddSingleton<IReviewedInstallationPreparation, ReviewedInstallationPreparation>();
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
            sp.GetRequiredService<MigrationStagingCoordinator>(), preserveWindows));
        services.AddSingleton<IProviderCompatibility>(sp => new ProviderCompatibility(
            sp.GetRequiredService<DistroRegistry>(), sp.GetRequiredService<DistroLoader>()));
        services.AddSingleton<IInstallationPreparation>(sp => new InstallationPreparation(
            sp.GetRequiredService<DistroRegistry>(), sp.GetRequiredService<DistroLoader>(),
            sp.GetRequiredService<IDirectInstallService>(),
            sp.GetRequiredService<IWindowsStorageReader>(),
            sp.GetRequiredService<IPreflightChecker>()));
        services.AddSingleton<IReviewedInstallationInspection>(sp => new ReviewedInstallationInspection(
            sp.GetRequiredService<DistroRegistry>(), sp.GetRequiredService<DistroLoader>(),
            sp.GetRequiredService<PreparedLayoutStore>(),
            sp.GetRequiredService<IReviewedInstallerInspection>()));
        services.AddSingleton<IPreflightWorkflow>(sp => new PreflightWorkflow(
            sp.GetRequiredService<IPreflightChecker>(), sp.GetRequiredService<ILinuxRemovalService>()));
        services.AddSingleton<IUsbPreparation>(sp => new UsbPreparation(
            sp.GetRequiredService<IUsbWriterService>(), sp.GetRequiredService<IPartitionResizeService>(),
            sp.GetRequiredService<IWindowsStorageReader>(), sp.GetRequiredService<DistroRegistry>(),
            sp.GetRequiredService<DistroLoader>(), sp.GetRequiredService<IPreflightChecker>(),
            preserveWindows));
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
                new MigrationLayoutPlanHandler(sp.GetRequiredService<IReviewedInstallationPlanning>(),
                    sp.GetRequiredService<FileArtifactRegistry>(),
                    sp.GetRequiredService<DistroLoader>(),
                    sp.GetRequiredService<DistroRegistry>()),
            };
            if (includeIsoAcquisition)
            {
                additional.Add(new IsoOperationsHandler(sp.GetRequiredService<DistroLoader>(), journal));
                additional.Add(new IsoAcquireHandler(sp.GetRequiredService<DistroLoader>(),
                    sp.GetRequiredService<IsoAcquisitionCoordinator>(), journal));
                additional.Add(new IsoAcquireHandler(sp.GetRequiredService<DistroLoader>(),
                    sp.GetRequiredService<IsoAcquisitionCoordinator>(), journal, version: 2));
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
    int LinuxSizeGb)
{
    public bool IncludeWifiNetworks { get; init; } = true;
}

public interface IMigrationStagingWorkflow
{
    Task<FileStagingResult> StageAsync(LocalMigrationStagingInput input,
        IProgress<FileStagingProgress>? progress, CancellationToken cancellationToken = default);
}

internal sealed class MigrationStagingWorkflow(MigrationStagingCoordinator coordinator,
    bool preserveWindows) : IMigrationStagingWorkflow
{
    public async Task<FileStagingResult> StageAsync(LocalMigrationStagingInput input,
        IProgress<FileStagingProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (preserveWindows && input.InstallMode != DiskInstallMode.DualBoot)
            throw new NotSupportedException("iGloo Community always preserves Windows. Only dual-boot installation is supported.");
        using var lease = await ResourceLock.AcquireManyAsync(["migration-staging"],
            cancellationToken).ConfigureAwait(false);
        return await coordinator.StageAsync(new MigrationStagingInput(input.Files, input.User,
            input.ObservedMachine, input.TargetDisk, input.InstallMode, input.LinuxSizeGb)
            { IncludeWifiNetworks = input.IncludeWifiNetworks },
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

internal static class InstallerIsoProof
{
    internal static void RequireDeclaredSignature(DistroManifest distro,
        IDistroPlugin provider, IsoAcquisitionResult acquired)
    {
        var iso = distro.Iso;
        if ((iso.GpgSignatureUrl is not null || iso.GpgSignedDataUrl is not null ||
             iso.GpgKeyUrl is not null ||
             !string.IsNullOrWhiteSpace(iso.GpgKeyFile) ||
             !string.IsNullOrWhiteSpace(iso.GpgKeyFingerprint) ||
             provider.Metadata.IsoGpgSignatureUrl is not null ||
             provider.Metadata.IsoGpgKeyUrl is not null ||
             string.IsNullOrWhiteSpace(provider.Metadata.IsoSha256)) &&
            !acquired.GpgVerified)
            throw new InvalidDataException("The declared installer-image signature was not verified.");
    }
}

/// <summary>Read-only observation of a caller-pinned, already-created installer checkpoint.</summary>
public sealed record ReviewedInstallationInspectionResultV1(
    ReviewedInstallerInspectionV1? InstallerCreatedRoot,
    ReviewedOwnedInstallerInspectionV1? WindowsOwnedRoot);

public interface IReviewedInstallationInspection
{
    Task<ReviewedInstallationInspectionResultV1> InspectAsync(string distroId,
        PreparationCheckpointReference checkpoint, long requestedLinuxBytes,
        IsoAcquisitionResult verifiedIso, string stagingDirectory,
        CancellationToken cancellationToken = default);

    Task<ReviewedOwnedInstallerInspectionV1> InspectOwnedAsync(string distroId,
        PreparationCheckpointReference checkpoint, long requestedLinuxBytes,
        IsoAcquisitionResult verifiedIso, string stagingDirectory,
        CancellationToken cancellationToken = default);
}

internal sealed class ReviewedInstallationInspection(DistroRegistry registry, DistroLoader loader,
    PreparedLayoutStore checkpoints, IReviewedInstallerInspection inspector)
    : IReviewedInstallationInspection
{
    public async Task<ReviewedInstallationInspectionResultV1> InspectAsync(string distroId,
        PreparationCheckpointReference checkpoint, long requestedLinuxBytes,
        IsoAcquisitionResult verifiedIso, string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(verifiedIso);
        if (!registry.TryGet(distroId, out var plugin))
            throw new InvalidOperationException("The selected installer provider is unavailable.");
        if (plugin is IReviewedInstallerStorageProvider)
            return new(null, await InspectOwnedAsync(distroId, checkpoint,
                requestedLinuxBytes, verifiedIso, stagingDirectory,
                cancellationToken).ConfigureAwait(false));
        var declared = loader.LoadedDistros.SingleOrDefault(d =>
            string.Equals(d.Id, distroId, StringComparison.Ordinal));
        if (declared is null || !declared.IsAvailable ||
            !declared.SupportedInstallModes.Contains("dual-boot", StringComparer.Ordinal))
            throw new NotSupportedException("The selected provider does not support direct dual-boot installation.");
        const long minimumRootBytes = 20L * 1024 * 1024 * 1024;
        if (requestedLinuxBytes < Math.Max(minimumRootBytes,
                plugin.Metadata.MinimumRequirements.MinDiskBytes))
            throw new InvalidOperationException("The selected Linux allocation is below the provider's minimum root size.");
        var digest = verifiedIso.VerifiedSha256;
        if (!verifiedIso.Sha256Verified || digest is not { Length: 64 } ||
            !digest.All(Uri.IsHexDigit) || verifiedIso.SizeBytes <= 0 ||
            string.IsNullOrWhiteSpace(verifiedIso.LocalPath) ||
            !Path.IsPathFullyQualified(verifiedIso.LocalPath) ||
            string.IsNullOrWhiteSpace(stagingDirectory) ||
            !Path.IsPathFullyQualified(stagingDirectory))
            throw new InvalidDataException("The reviewed installer has no verified image or staged directory.");
        var providerPin = plugin.Metadata.IsoSha256;
        InstallerIsoProof.RequireDeclaredSignature(declared, plugin, verifiedIso);
        if (!string.IsNullOrWhiteSpace(providerPin) &&
            !string.Equals(providerPin, digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The acquired installer image differs from the provider pin.");

        using var lease = await ResourceLock.AcquireManyAsync(
            ["installation", "migration-staging", "recovery-state"],
            cancellationToken).ConfigureAwait(false);
        var created = checkpoints.Reopen(checkpoint);
        if (created.State != PreparationState.CreatedAndVerified ||
            created.Plan.ReviewedIntent is null ||
            !string.Equals(created.Plan.ReviewedIntent.ArtifactSha256, digest,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The exact reviewed installer checkpoint is not ready for content inspection.");
        var providerRevision = await ProviderRevision.FingerprintAsync(plugin, declared,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(created.Plan.ReviewedIntent.ProviderRevisionSha256,
                providerRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("The installer provider changed since the reviewed checkpoint was created.");
        var (manifest, _) = await UsbPreparation.ReadStagedManifestAsync(stagingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (manifest.DistroId != distroId || manifest.Hardware is null ||
            manifest.Hardware.InstallMode != "dual-boot" ||
            manifest.Hardware.TargetDiskGuid != created.Plan.TargetDisk.GptDiskGuid ||
            manifest.Hardware.TargetDiskBytes <= 0 ||
            (ulong)manifest.Hardware.TargetDiskBytes != created.Plan.TargetDisk.SizeBytes ||
            manifest.Hardware.LinuxPartitionSizeGb <= 0 ||
            checked((long)manifest.Hardware.LinuxPartitionSizeGb * 1024 * 1024 * 1024) !=
                requestedLinuxBytes)
            throw new InvalidDataException("The staged installer differs from the reviewed distribution, disk or Linux allocation.");
        if (plugin is not IReviewedInstallerConfigProvider reviewedProvider)
            throw new NotSupportedException("The selected provider has no reviewed installer config.");
        var inspection = await Task.Run(() => inspector.Inspect(checkpoint,
            plugin.GetInstallerBootSpec() with { IsoSha256 = digest },
            requestedLinuxBytes, stagingDirectory, verifiedIso.LocalPath,
            verifiedIso.SizeBytes, cancellationToken), cancellationToken).ConfigureAwait(false);
        var config = await reviewedProvider.RenderReviewedInstallerConfigAsync(manifest,
            inspection.InstallerRoot, cancellationToken).ConfigureAwait(false);
        var agent = await plugin.GetAgentPayloadAsync(cancellationToken).ConfigureAwait(false);
        var verifiedArtifacts = await UsbPreparation.RequireProviderArtifactsAsync(
            stagingDirectory, config, agent, cancellationToken).ConfigureAwait(false);
        var staged = inspection.StagedFiles.ToDictionary(file => file.RelativePath.Replace('\\', '/'),
            StringComparer.OrdinalIgnoreCase);
        var reviewedPaths = verifiedArtifacts.ProviderArtifacts
            .Append(new UsbStagedArtifactIdentity(config.FileName,
                Convert.ToHexString(SHA256.HashData(config.Contents.Span))));
        foreach (var artifact in reviewedPaths)
        {
            if (!staged.TryGetValue(artifact.RelativePath.Replace('\\', '/'), out var file) ||
                !string.Equals(file.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The reviewed provider artifacts changed during inspection.");
        }
        return new(inspection, null);
    }

    public async Task<ReviewedOwnedInstallerInspectionV1> InspectOwnedAsync(string distroId,
        PreparationCheckpointReference checkpoint, long requestedLinuxBytes,
        IsoAcquisitionResult verifiedIso, string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(verifiedIso);
        if (!registry.TryGet(distroId, out var plugin) ||
            plugin is not IReviewedInstallerStorageProvider ownedProvider)
            throw new NotSupportedException("The selected provider has no reviewed owned-root installer config.");
        var declared = loader.LoadedDistros.SingleOrDefault(d =>
            string.Equals(d.Id, distroId, StringComparison.Ordinal));
        if (declared is null || !declared.IsAvailable ||
            !declared.SupportedInstallModes.Contains("dual-boot", StringComparer.Ordinal))
            throw new NotSupportedException("The selected provider does not support direct dual-boot installation.");
        const long minimumRootBytes = 20L * 1024 * 1024 * 1024;
        if (requestedLinuxBytes < Math.Max(minimumRootBytes,
                plugin.Metadata.MinimumRequirements.MinDiskBytes))
            throw new InvalidOperationException("The selected Linux allocation is below the provider's minimum root size.");
        var digest = verifiedIso.VerifiedSha256;
        if (!verifiedIso.Sha256Verified || digest is not { Length: 64 } ||
            !digest.All(Uri.IsHexDigit) || verifiedIso.SizeBytes <= 0 ||
            string.IsNullOrWhiteSpace(verifiedIso.LocalPath) ||
            !Path.IsPathFullyQualified(verifiedIso.LocalPath) ||
            string.IsNullOrWhiteSpace(stagingDirectory) ||
            !Path.IsPathFullyQualified(stagingDirectory))
            throw new InvalidDataException("The reviewed installer has no verified image or staged directory.");
        var providerPin = plugin.Metadata.IsoSha256;
        InstallerIsoProof.RequireDeclaredSignature(declared, plugin, verifiedIso);
        if (!string.IsNullOrWhiteSpace(providerPin) &&
            !string.Equals(providerPin, digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The acquired installer image differs from the provider pin.");

        using var lease = await ResourceLock.AcquireManyAsync(
            ["installation", "migration-staging", "recovery-state"],
            cancellationToken).ConfigureAwait(false);
        var created = checkpoints.Reopen(checkpoint);
        if (created.State != PreparationState.CreatedAndVerified ||
            created.Plan.ReviewedIntent is null ||
            !created.Plan.Space.Allocations.Single(a => a.Role == PreparationRole.LinuxRoot)
                .CreateInWindows ||
            !string.Equals(created.Plan.ReviewedIntent.ArtifactSha256, digest,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The exact reviewed owned-root checkpoint is not ready for content inspection.");
        var providerRevision = await ProviderRevision.FingerprintAsync(plugin, declared,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(created.Plan.ReviewedIntent.ProviderRevisionSha256,
                providerRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("The installer provider changed since the reviewed checkpoint was created.");
        var (manifest, _) = await UsbPreparation.ReadStagedManifestAsync(stagingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (manifest.DistroId != distroId || manifest.Hardware is null ||
            manifest.Hardware.InstallMode != "dual-boot" ||
            manifest.Hardware.TargetDiskGuid != created.Plan.TargetDisk.GptDiskGuid ||
            manifest.Hardware.TargetDiskBytes <= 0 ||
            (ulong)manifest.Hardware.TargetDiskBytes != created.Plan.TargetDisk.SizeBytes ||
            manifest.Hardware.LinuxPartitionSizeGb <= 0 ||
            checked((long)manifest.Hardware.LinuxPartitionSizeGb * 1024 * 1024 * 1024) !=
                requestedLinuxBytes)
            throw new InvalidDataException("The staged installer differs from the reviewed distribution, disk or Linux allocation.");
        var bootSpec = ReviewedInstallationProviderSupport.ReviewedBootSpec(plugin) with
        { IsoSha256 = digest };
        var inspection = await Task.Run(() => inspector.InspectOwned(checkpoint,
            bootSpec, requestedLinuxBytes, stagingDirectory, verifiedIso.LocalPath,
            verifiedIso.SizeBytes, cancellationToken), cancellationToken).ConfigureAwait(false);
        var rendered = await ownedProvider.RenderDeferredReviewedInstallerConfigAsync(manifest,
            inspection.Ownership, inspection.GenerationId, cancellationToken).ConfigureAwait(false);
        if (rendered.Availability != ObservationAvailability.Available)
            throw new NotSupportedException(
                $"The reviewed owned-root provider config is unavailable: {rendered.Code}.");
        var config = rendered.Value;
        var agent = await plugin.GetAgentPayloadAsync(cancellationToken).ConfigureAwait(false);
        var verifiedArtifacts = await UsbPreparation.RequireProviderArtifactsAsync(
            stagingDirectory, config, agent, cancellationToken).ConfigureAwait(false);
        var staged = inspection.StagedFiles.ToDictionary(file => file.RelativePath.Replace('\\', '/'),
            StringComparer.OrdinalIgnoreCase);
        var reviewedPaths = verifiedArtifacts.ProviderArtifacts
            .Append(new UsbStagedArtifactIdentity(config.FileName,
                Convert.ToHexString(SHA256.HashData(config.Contents.Span))));
        foreach (var artifact in reviewedPaths)
        {
            if (!staged.TryGetValue(artifact.RelativePath.Replace('\\', '/'), out var file) ||
                !string.Equals(file.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The reviewed owned-root provider artifacts changed during inspection.");
        }
        return inspection;
    }
}

/// <summary>Private first-party path; no public destructive command is registered.</summary>
public interface IInstallationPreparation
{
    Observation<bool> PreparationAvailability { get; }

    Observation<bool> GetDistroPreparationAvailability(string distroId) => PreparationAvailability;

    Task<DirectInstallSpaceEstimate> MeasureSpaceAsync(string distroId, DiskInfo targetDisk,
        IsoAcquisitionResult verifiedIso, FileStagingRequest selectedFiles,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Direct installation space measurement is unavailable.");

    Task PrepareAsync(string distroId, DiskInfo targetDisk, long linuxSizeBytes, IsoAcquisitionResult verifiedIso,
        string stagingDirectory, Uri? stage2Url, IProgress<DirectInstallProgress>? progress,
        CancellationToken cancellationToken = default);
    Task RegisterBootEntryAsync(IProgress<DirectInstallProgress>? progress,
        CancellationToken cancellationToken = default);
}

internal sealed class InstallationPreparation(DistroRegistry registry, DistroLoader loader,
    IDirectInstallService service,
    IWindowsStorageReader storage, IPreflightChecker preflight)
    : IInstallationPreparation
{
    public Observation<bool> PreparationAvailability => service.PreparationAvailability;

    // Every shipped provider installs through the same legacy preparation path
    // that v0.2-alpha validated on hardware. The per-distribution refusals the beta
    // added here (Fedora "root binding", Mint "dedicated ESP binding") guarded
    // installer hooks that no longer exist on the product path.
    public Observation<bool> GetDistroPreparationAvailability(string distroId) => PreparationAvailability;

    public async Task<DirectInstallSpaceEstimate> MeasureSpaceAsync(string distroId,
        DiskInfo targetDisk, IsoAcquisitionResult verifiedIso, FileStagingRequest selectedFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetDisk);
        ArgumentNullException.ThrowIfNull(verifiedIso);
        ArgumentNullException.ThrowIfNull(selectedFiles);
        const string prefix = "\\\\.\\PHYSICALDRIVE";
        if (targetDisk.GptDiskGuid is not { } diskGuid || diskGuid == Guid.Empty ||
            !string.Equals(targetDisk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase) ||
            !targetDisk.DeviceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !uint.TryParse(targetDisk.DeviceId.AsSpan(prefix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out var selectedNumber) || selectedNumber > int.MaxValue)
            throw new InvalidOperationException("The selected direct install target has no exact GPT identity.");
        if (!string.Equals(selectedFiles.DistroId, distroId, StringComparison.Ordinal) ||
            !registry.TryGet(distroId, out var plugin))
            throw new InvalidOperationException("The selected migration files and installer provider do not match.");
        var declared = loader.LoadedDistros.SingleOrDefault(d =>
            string.Equals(d.Id, distroId, StringComparison.Ordinal));
        if (declared is null || !declared.IsAvailable ||
            !declared.SupportedInstallModes.Contains("dual-boot", StringComparer.Ordinal))
            throw new NotSupportedException("The selected provider does not support direct dual-boot installation.");
        var digest = verifiedIso.VerifiedSha256;
        if (!verifiedIso.Sha256Verified || digest is not { Length: 64 } ||
            !digest.All(Uri.IsHexDigit) || !Path.IsPathFullyQualified(verifiedIso.LocalPath) ||
            verifiedIso.SizeBytes <= 0 || !File.Exists(verifiedIso.LocalPath) ||
            new FileInfo(verifiedIso.LocalPath).Length != verifiedIso.SizeBytes)
            throw new InvalidDataException("The acquired installer image has no current verified identity.");
        InstallerIsoProof.RequireDeclaredSignature(declared, plugin, verifiedIso);
        if (!string.IsNullOrWhiteSpace(plugin.Metadata.IsoSha256) &&
            !string.Equals(plugin.Metadata.IsoSha256, digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The acquired installer image differs from the provider pin.");

        var diskNumber = (int)selectedNumber;
        using var lease = await ResourceLock.AcquireManyAsync(
            ["installation", "disk:" + diskNumber, "iso:" + distroId, "migration-staging"],
            cancellationToken).ConfigureAwait(false);
        RequireCurrentDisk();
        var bootSpec = plugin.GetInstallerBootSpec();
        // Selected folders and browser profiles only take seed space for providers
        // that copy them onto the installer partition; the Debian family reads them
        // from the Windows volume at first boot (alpha flow).
        var selectedBytes = bootSpec.MigrationDataOnSeed
            ? await FileStagingService.MeasureSelectedBytesAsync(selectedFiles, cancellationToken).ConfigureAwait(false)
            : 0L;
        var agents = await plugin.GetAgentPayloadAsync(cancellationToken).ConfigureAwait(false);
        // Agent archives are measured. Reserve another 64 MiB for generated config,
        // manifest, exported settings and images not yet emitted by file staging.
        // The writer still measures the complete package immediately before resize.
        const long generatedReserve = 64L * 1024 * 1024;
        var stagedBytes = checked(selectedBytes + agents.Files.Sum(file => (long)file.Contents.Length) +
            generatedReserve);
        var estimate = await service.MeasureSpaceAsync(diskNumber, verifiedIso.LocalPath,
            bootSpec with { IsoSha256 = digest }, stagedBytes,
            cancellationToken).ConfigureAwait(false);
        RequireCurrentDisk();
        return estimate;

        void RequireCurrentDisk()
        {
            var rows = storage.ReadDisks();
            if (rows.Availability != ObservationAvailability.Available)
                throw new InvalidOperationException("Direct install could not verify the current disk inventory.");
            var mismatch = UsbPreparation.InstallerTargetMismatchReason(targetDisk, selectedNumber,
                rows.Rows.Select(StorageObservationProjection.Disk).ToArray());
            if (mismatch is not null)
                throw new InvalidOperationException($"Direct install could not revalidate the selected disk ({mismatch}). Run the system check again.");
        }
    }

    public async Task PrepareAsync(string distroId, DiskInfo targetDisk, long linuxSizeBytes, IsoAcquisitionResult verifiedIso,
        string stagingDirectory, Uri? stage2Url, IProgress<DirectInstallProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetDisk);
        ArgumentNullException.ThrowIfNull(verifiedIso);
        const string prefix = "\\\\.\\PHYSICALDRIVE";
        if (targetDisk.GptDiskGuid is null ||
            !string.Equals(targetDisk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase) ||
            !targetDisk.DeviceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !uint.TryParse(targetDisk.DeviceId.AsSpan(prefix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out var selectedNumber) || selectedNumber > int.MaxValue ||
            linuxSizeBytes <= 0)
            throw new InvalidOperationException("The selected direct install target has no exact GPT identity or Linux allocation.");
        var diskNumber = (int)selectedNumber;
        if (!registry.TryGet(distroId, out var plugin))
            throw new InvalidOperationException(
                $"No installer plugin is loaded for distro '{distroId}'. " +
                "Ensure the distro's Igloo.Distro.*.dll is present in its distros/ folder.");
        var declared = loader.LoadedDistros.SingleOrDefault(d =>
            string.Equals(d.Id, distroId, StringComparison.Ordinal));
        if (declared is null || !declared.IsAvailable ||
            !declared.SupportedInstallModes.Contains("dual-boot", StringComparer.Ordinal))
            throw new NotSupportedException("The selected provider does not support direct dual-boot installation.");
        const long minimumRootBytes = 20L * 1024 * 1024 * 1024;
        if (linuxSizeBytes < Math.Max(minimumRootBytes,
                plugin.Metadata.MinimumRequirements.MinDiskBytes))
            throw new InvalidOperationException("The selected Linux allocation is below the provider's minimum root size.");
        var digest = verifiedIso.VerifiedSha256;
        if (!verifiedIso.Sha256Verified || digest is not { Length: 64 } ||
            !digest.All(Uri.IsHexDigit) || !Path.IsPathFullyQualified(verifiedIso.LocalPath) ||
            verifiedIso.SizeBytes <= 0)
            throw new InvalidDataException("The acquired installer image has no verified identity.");
        var providerPin = plugin.Metadata.IsoSha256;
        InstallerIsoProof.RequireDeclaredSignature(declared, plugin, verifiedIso);
        if (!string.IsNullOrWhiteSpace(providerPin) &&
            !string.Equals(providerPin, digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The acquired installer image differs from the provider pin.");
        using var lease = await ResourceLock.AcquireManyAsync(["installation", "disk:" + diskNumber,
            "partition-table:" + diskNumber, "migration-staging", "recovery-state"],
            cancellationToken).ConfigureAwait(false);
        var diskRows = storage.ReadDisks();
        if (diskRows.Availability != ObservationAvailability.Available)
            throw new InvalidOperationException("Direct install could not verify the current disk inventory (disk-inventory-unavailable).");
        var currentDisks = diskRows.Rows.Select(StorageObservationProjection.Disk).ToArray();
        var targetMismatch = UsbPreparation.InstallerTargetMismatchReason(targetDisk,
            selectedNumber, currentDisks);
        if (targetMismatch is not null)
            throw new InvalidOperationException($"Direct install could not revalidate the selected disk ({targetMismatch}). Run the system check again.");
        var (manifest, _) = await UsbPreparation.ReadStagedManifestAsync(stagingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (manifest.DistroId != distroId || manifest.Hardware is null ||
            manifest.Hardware.InstallMode != "dual-boot" ||
            manifest.Hardware.TargetDiskGuid != targetDisk.GptDiskGuid ||
            manifest.Hardware.TargetDiskBytes != targetDisk.TotalBytes ||
            manifest.Hardware.TargetDiskModel != targetDisk.Model ||
            manifest.Hardware.LinuxPartitionSizeGb <= 0 ||
            (long)manifest.Hardware.LinuxPartitionSizeGb * 1024 * 1024 * 1024 != linuxSizeBytes)
            throw new InvalidDataException("The staged direct installer targets a different distribution, disk or Linux allocation.");
        await UsbPreparation.RequireProviderArtifactsAsync(stagingDirectory, plugin, manifest,
            cancellationToken).ConfigureAwait(false);
        await InstallerPreflightGuard.RequireCurrentAsync(preflight, cancellationToken)
            .ConfigureAwait(false);
        await service.PrepareAsync(diskNumber, linuxSizeBytes, verifiedIso.LocalPath, stagingDirectory,
            plugin.GetInstallerBootSpec() with { IsoSha256 = digest },
            stage2Url, progress, cancellationToken).ConfigureAwait(false);
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
    /// <summary>
    /// Grows the Windows volume on <paramref name="diskNumber"/> into unallocated space
    /// that sits directly behind it, and returns the number of bytes reclaimed (0 when
    /// there was nothing to reclaim). This is a write action owned by the Community
    /// System Check, not part of read-only <see cref="IEndpointInspection"/>.
    /// </summary>
    Task<long> ReclaimAdjacentFreeSpaceAsync(uint diskNumber, CancellationToken cancellationToken = default);

    Task RemoveLinuxAsync(IReadOnlyList<LinuxInstallation> installations,
        IReadOnlyList<SeedLeftover> leftovers, bool removingAllLinux,
        IProgress<string>? progress, CancellationToken cancellationToken = default);
}

internal sealed class PreflightWorkflow(IPreflightChecker checker, ILinuxRemovalService removal)
    : IPreflightWorkflow
{
    public Task<PreflightReport> InspectAsync(CancellationToken cancellationToken = default) =>
        checker.RunAsync(cancellationToken);

    public async Task<long> ReclaimAdjacentFreeSpaceAsync(uint diskNumber, CancellationToken cancellationToken = default)
    {
        using var lease = await ResourceLock.AcquireManyAsync(
            ["disk:" + diskNumber, "partition-table:" + diskNumber], cancellationToken).ConfigureAwait(false);
        return await removal.ReclaimFreeSpaceAsync(diskNumber, ct: cancellationToken).ConfigureAwait(false);
    }

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

}

public sealed record UsbPreparationInput(UsbDriveInfo Drive, string IsoPath, string StagingDirectory,
    DiskInfo? TargetDisk, DiskInstallMode InstallMode, int LinuxSizeGb, string ExpectedIsoSha256)
{
    public string? DistroId { get; init; }
    public IsoAcquisitionResult? VerifiedIso { get; init; }
}

public interface IUsbPreparation
{
    Task<IReadOnlyList<UsbDriveInfo>> EnumerateDrivesAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(UsbPreparationInput input, IProgress<UsbWriteProgress>? progress,
        IProgress<string>? resizeProgress, CancellationToken cancellationToken = default);
}

internal sealed class UsbPreparation(IUsbWriterService writer, IPartitionResizeService resizer,
    IWindowsStorageReader storage, DistroRegistry registry, DistroLoader loader,
    IPreflightChecker preflight, bool preserveWindows)
    : IUsbPreparation
{
    public Task<IReadOnlyList<UsbDriveInfo>> EnumerateDrivesAsync(CancellationToken cancellationToken = default) =>
        writer.EnumerateDrivesAsync(cancellationToken);

    public async Task WriteAsync(UsbPreparationInput input, IProgress<UsbWriteProgress>? progress,
        IProgress<string>? resizeProgress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (preserveWindows && input.InstallMode != DiskInstallMode.DualBoot)
            throw new NotSupportedException("iGloo Community always preserves Windows. Only dual-boot installation is supported.");
        if (input.InstallMode is not (DiskInstallMode.ReplaceDisk or DiskInstallMode.DualBoot))
            throw new InvalidOperationException("The installer mode is unsupported.");
        if (input.TargetDisk is null || input.TargetDisk.GptDiskGuid is null ||
            !string.Equals(input.TargetDisk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected install target has no exact GPT identity.");
        var fatSeed = await ResolveFatSeedAsync(input, cancellationToken).ConfigureAwait(false);
        if (input.InstallMode == DiskInstallMode.DualBoot &&
            input.LinuxSizeGb <= 0)
            throw new InvalidOperationException("Dual-boot USB preparation requires an observed target disk and Linux allocation size.");
        var diskId = input.TargetDisk.DeviceId;
        const string targetPrefix = "\\\\.\\PHYSICALDRIVE";
        if (!diskId.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase) ||
            !uint.TryParse(diskId.AsSpan(targetPrefix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out var selectedNumber) ||
            selectedNumber == (uint)input.Drive.DriveIndex)
            throw new InvalidOperationException("The selected installer target disk identity is invalid.");
        int? windowsDisk = null;
        long linuxBytes = 0;
        if (input.InstallMode == DiskInstallMode.DualBoot)
        {
            windowsDisk = checked((int)selectedNumber);
            linuxBytes = checked((long)input.LinuxSizeGb * 1024 * 1024 * 1024);
        }
        var resources = new List<string> { "disk:" + input.Drive.DriveIndex,
            "partition-table:" + input.Drive.DriveIndex, "migration-staging",
            "disk:" + selectedNumber, "partition-table:" + selectedNumber };
        using var lease = await ResourceLock.AcquireManyAsync(resources,
            cancellationToken).ConfigureAwait(false);
        RequireCurrentInstallerTarget(input.TargetDisk, selectedNumber);
        await InstallerPreflightGuard.RequireCurrentAsync(preflight, cancellationToken)
            .ConfigureAwait(false);
        if (windowsDisk is { } inspectedDisk)
        {
            var available = await resizer.GetShrinkableSpaceAsync(inspectedDisk,
                cancellationToken).ConfigureAwait(false);
            if (available < linuxBytes)
                throw new InvalidOperationException("The Windows boot volume cannot provide the selected Linux allocation.");
        }
        await writer.WriteBootableFatSeedAsync(input.Drive, input.IsoPath, input.StagingDirectory,
            input.ExpectedIsoSha256, fatSeed.StagingIdentity, fatSeed.BootSpec,
            progress, cancellationToken).ConfigureAwait(false);
        RequireCurrentInstallerTarget(input.TargetDisk, selectedNumber);
        if (windowsDisk is { } resizeDisk)
        {
            await InstallerPreflightGuard.RequireCurrentAsync(preflight, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new UsbWriteProgress(UsbWritePhase.ShrinkingPartition, 0, 0,
                "Shrinking Windows partition…"));
            await resizer.ShrinkAsync(resizeDisk, linuxBytes, resizeProgress, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new UsbWriteProgress(UsbWritePhase.Complete, 0, 0,
                "USB drive and target allocation are ready."));
        }
    }

    private async Task<(InstallerBootSpec BootSpec, UsbStagingIdentity StagingIdentity)>
        ResolveFatSeedAsync(UsbPreparationInput input, CancellationToken ct)
    {
        // A provider and a verified acquisition are mandatory at the product
        // boundary. The legacy raw writer is not a supported installer route.
        if (string.IsNullOrWhiteSpace(input.DistroId))
            throw new NotSupportedException("USB installation preparation requires a selected provider.");
        if (input.DistroId is "debian" or "linuxmint-cinnamon")
            throw new NotSupportedException(input.InstallMode == DiskInstallMode.ReplaceDisk
                ? "Choose dual boot on the Windows system disk for Debian or Linux Mint. These providers have no verified USB disk-replacement recipe."
                : "Continue with the direct installation workflow for Debian or Linux Mint dual boot; this USB preparation route is unavailable.");
        if (!string.Equals(input.DistroId, "fedora-kde", StringComparison.Ordinal) ||
            input.InstallMode != DiskInstallMode.ReplaceDisk)
            throw new NotSupportedException("This provider has no verified bootable USB seed layout.");
        var declared = loader.LoadedDistros.SingleOrDefault(d =>
            string.Equals(d.Id, input.DistroId, StringComparison.Ordinal));
        if (declared is null || !declared.IsAvailable ||
            !declared.SupportedInstallModes.Contains("replace", StringComparer.Ordinal))
            throw new NotSupportedException("The selected provider does not support replace installation.");
        if (!registry.TryGet(input.DistroId, out var provider))
            throw new InvalidOperationException("The selected installer provider is unavailable.");
        const long minimumTargetBytes = 20L * 1024 * 1024 * 1024;
        if (input.TargetDisk!.TotalBytes < Math.Max(minimumTargetBytes,
                provider.Metadata.MinimumRequirements.MinDiskBytes))
            throw new InvalidOperationException("The selected disk is below the provider's minimum installation size.");
        var acquired = input.VerifiedIso;
        var pin = provider.Metadata.IsoSha256;
        if (acquired is null || !acquired.Sha256Verified || acquired.SizeBytes <= 0 ||
            acquired.VerifiedSha256 is not { Length: 64 } digest ||
            !digest.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(acquired.LocalPath) ||
            string.IsNullOrWhiteSpace(input.IsoPath) ||
            !Path.IsPathFullyQualified(acquired.LocalPath) ||
            !Path.IsPathFullyQualified(input.IsoPath) ||
            !string.Equals(Path.GetFullPath(input.IsoPath), Path.GetFullPath(acquired.LocalPath),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(input.ExpectedIsoSha256, digest, StringComparison.OrdinalIgnoreCase) ||
            pin is not { Length: 64 } || !pin.All(Uri.IsHexDigit) ||
            !string.Equals(pin, digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected provider and acquired installer image have no matching verified identity.");
        InstallerIsoProof.RequireDeclaredSignature(declared, provider, acquired);
        var stagedIdentity = await RequireStagedInstallerTargetAsync(input, provider, ct)
            .ConfigureAwait(false);
        return (provider.GetInstallerBootSpec() with { IsoSha256 = digest }, stagedIdentity);
    }

    private static async Task<UsbStagingIdentity> RequireStagedInstallerTargetAsync(
        UsbPreparationInput input, IDistroPlugin provider, CancellationToken ct)
    {
        var (manifest, manifestBytes) = await ReadStagedManifestAsync(input.StagingDirectory, ct)
            .ConfigureAwait(false);
        if (manifest.DistroId != "fedora-kde" || manifest.Hardware is null ||
            manifest.User is null || manifest.Files is null ||
            manifest.Hardware.InstallMode != "replace" ||
            manifest.Hardware.TargetDiskGuid != input.TargetDisk!.GptDiskGuid ||
            manifest.Hardware.TargetDiskBytes != input.TargetDisk.TotalBytes ||
            manifest.Hardware.TargetDiskModel != input.TargetDisk.Model)
            throw new InvalidDataException("The staged installer configuration targets a different distribution or disk.");
        if (manifest.WifiNetworks?.Any(network => !string.IsNullOrEmpty(network?.Psk)) == true)
            throw new NotSupportedException("Turn off saved Wi-Fi migration and restage before writing the USB drive. Use wired Ethernet or an open Wi-Fi network for this Fedora installation.");
        var verifiedArtifacts = await RequireProviderArtifactsAsync(input.StagingDirectory,
            provider, manifest, ct).ConfigureAwait(false);
        if (verifiedArtifacts.Config.FileName != "ks.cfg")
            throw new InvalidDataException("The selected Fedora provider has no Kickstart configuration.");
        var kickstartBytes = await ReadBoundedStagedFileAsync(
            Path.Join(input.StagingDirectory, verifiedArtifacts.Config.FileName), ct).ConfigureAwait(false);
        return new UsbStagingIdentity(
            Convert.ToHexString(SHA256.HashData(manifestBytes)),
            Convert.ToHexString(SHA256.HashData(kickstartBytes)))
        {
            ProviderArtifacts = verifiedArtifacts.ProviderArtifacts,
        };
    }

    internal sealed record VerifiedProviderArtifacts(InstallerConfig Config,
        ImmutableArray<UsbStagedArtifactIdentity> ProviderArtifacts);

    internal static async Task<VerifiedProviderArtifacts> RequireProviderArtifactsAsync(
        string stagingDirectory, IDistroPlugin provider, MigrationManifest manifest,
        CancellationToken ct)
    {
        var rendered = await provider.RenderInstallerConfigAsync(manifest, ct)
            .ConfigureAwait(false);
        var payload = await provider.GetAgentPayloadAsync(ct).ConfigureAwait(false);
        return await RequireProviderArtifactsAsync(stagingDirectory, rendered, payload, ct)
            .ConfigureAwait(false);
    }

    internal static async Task<VerifiedProviderArtifacts> RequireProviderArtifactsAsync(
        string stagingDirectory, InstallerConfig rendered, AgentPayload payload,
        CancellationToken ct)
    {
        if (!string.Equals(Path.GetFileName(rendered.FileName), rendered.FileName,
                StringComparison.Ordinal))
            throw new InvalidDataException("The selected provider has no root-level installer configuration.");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var providerArtifacts = ImmutableArray.CreateBuilder<UsbStagedArtifactIdentity>();
        await CompareArtifactAsync(rendered.FileName, rendered.Contents, false).ConfigureAwait(false);
        foreach (var extra in rendered.Extras)
            await CompareArtifactAsync(extra.RelativePath, extra.Contents, true).ConfigureAwait(false);
        foreach (var agent in payload.Files)
            await CompareArtifactAsync(Path.Join("igloo-agent", agent.RelativePath),
                agent.Contents, true).ConfigureAwait(false);

        var agentRoot = Path.Join(stagingDirectory, "igloo-agent");
        var pending = new Stack<string>();
        pending.Push(agentRoot);
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (!Directory.Exists(directory) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The staged first-boot agent directory is unavailable.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The staged first-boot agent contains a redirected entry.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else
                    observed.Add(Path.GetRelativePath(stagingDirectory, entry));
            }
        }
        if (!observed.SetEquals(expected.Where(path => path.StartsWith(
                "igloo-agent" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("The staged first-boot agent differs from the selected provider.");
        return new(rendered, providerArtifacts.ToImmutable());

        async Task CompareArtifactAsync(string relativePath, ReadOnlyMemory<byte> contents,
            bool providerArtifact)
        {
            ct.ThrowIfCancellationRequested();
            var segments = relativePath.Split(['/', '\\']);
            if (segments.Length == 0 || segments.Any(segment =>
                    string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
                Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
                throw new InvalidDataException("The selected provider contains an invalid installer artifact path.");
            var portable = Path.Join(segments);
            if (!expected.Add(portable) || contents.IsEmpty)
                throw new InvalidDataException("The selected provider contains an empty or duplicate installer artifact.");
            var staged = await ReadBoundedStagedFileAsync(Path.Join(stagingDirectory, portable), ct)
                .ConfigureAwait(false);
            if (!staged.AsSpan().SequenceEqual(contents.Span))
                throw new InvalidDataException("The staged installer or first-boot agent differs from the selected provider.");
            if (providerArtifact)
                providerArtifacts.Add(new UsbStagedArtifactIdentity(
                    portable.Replace('\\', '/'),
                    Convert.ToHexString(SHA256.HashData(contents.Span))));
        }
    }

    internal static async Task<(MigrationManifest Manifest, byte[] Bytes)> ReadStagedManifestAsync(
        string stagingDirectory, CancellationToken ct)
    {
        var manifestBytes = await ReadBoundedStagedFileAsync(
            Path.Join(stagingDirectory, "migration-manifest.json"), ct).ConfigureAwait(false);
        MigrationManifest manifest;
        try
        {
            using var document = JsonDocument.Parse(manifestBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var schemaVersion) || schemaVersion != 1)
                throw new JsonException("The staged migration manifest has no supported schema version.");
            manifest = JsonSerializer.Deserialize<MigrationManifest>(manifestBytes) ??
                throw new JsonException("The staged migration manifest is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The staged migration manifest is invalid.", error);
        }
        if (manifest.SchemaVersion != 1 || manifest.Hardware is null ||
            manifest.User is null || manifest.Files is null)
            throw new InvalidDataException("The staged migration manifest is incomplete.");
        return (manifest, manifestBytes);
    }

    private static async Task<byte[]> ReadBoundedStagedFileAsync(string path, CancellationToken ct)
    {
        const int maxBytes = 8 * 1024 * 1024;
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > maxBytes ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A required staged installer file is unavailable.");
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
            FileShare.Read, 16 * 1024, useAsync: true);
        if (stream.Length is <= 0 or > maxBytes)
            throw new InvalidDataException("A staged installer file changed size.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }

    private void RequireCurrentInstallerTarget(DiskInfo selected, uint number)
    {
        var diskRows = storage.ReadDisks();
        if (diskRows.Availability != ObservationAvailability.Available)
            throw new InvalidOperationException("The current installer target inventory is unavailable.");
        var observedDisks = diskRows.Rows.Select(StorageObservationProjection.Disk).ToArray();
        if (!SameInstallerTarget(selected, number, observedDisks))
            throw new InvalidOperationException("The selected installer target changed; run the system check again.");
    }

    internal static bool SameInstallerTarget(DiskInfo selected, uint number,
        IReadOnlyList<DiskObservation> current)
        => InstallerTargetMismatchReason(selected, number, current) is null;

    internal static string? InstallerTargetMismatchReason(DiskInfo selected, uint number,
        IReadOnlyList<DiskObservation> current)
    {
        if (selected.GptDiskGuid is not { } guid || guid == Guid.Empty ||
            selected.TotalBytes <= 0 ||
            !string.Equals(selected.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(selected.DeviceId, @"\\.\PHYSICALDRIVE" + number.ToString(CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase))
            return "selected-disk-identity-incomplete";
        var observed = InstallerDiskIdentity.Observe(current, number);
        if (observed.Availability != ObservationAvailability.Available) return observed.Code;
        var disk = observed.Value;
        if (disk.GptGuid.Value != guid) return "gpt-disk-guid-changed";
        if (disk.Size.Value != (ulong)selected.TotalBytes)
            return "disk-size-changed-or-unavailable";
        return null;
    }
}

internal static class InstallerPreflightGuard
{
    internal static async Task RequireCurrentAsync(IPreflightChecker checker, CancellationToken ct)
    {
        var report = await checker.RunAsync(ct).ConfigureAwait(false);
        if (!report.IsUefi || report.BitLocker != BitLockerState.NotEncrypted ||
            report.Findings.Any(finding => finding.Severity == FindingSeverity.Blocker))
            throw new InvalidOperationException(
                "The current Windows system check blocks installation preparation. Run the system check again.");
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
        if (systemDrive is not { Length: 2 } || !char.IsAsciiLetter(systemDrive[0]) ||
            systemDrive[1] != ':')
            throw new ArgumentException("The Windows system drive must be an exact drive letter.", nameof(systemDrive));
        var start = new ProcessStartInfo(FindNativeExe("manage-bde.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-off");
        start.ArgumentList.Add(systemDrive);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("BitLocker control could not start.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    public void RestartToFirmware() => Process.Start(new ProcessStartInfo(
        FindNativeExe("shutdown.exe"), "/r /fw /t 10")
    { UseShellExecute = false, CreateNoWindow = true });

    public void RestartToInstaller() => Process.Start(new ProcessStartInfo(
        FindNativeExe("shutdown.exe"),
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
