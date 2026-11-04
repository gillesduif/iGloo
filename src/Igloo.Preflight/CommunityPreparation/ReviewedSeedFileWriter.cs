using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityRecovery;

namespace Igloo.Preflight.CommunityPreparation;

// A file-copy readback on the separately prepared Linux ESP. It does not
// establish an installed bootloader, a boot entry, or permission to register one.
internal sealed record ReviewedLinuxEspFileCopyReceiptV1(Guid GenerationId,
    CanonicalDiskIdentityV1 TargetDisk, CanonicalVolumeIdentityV1 LinuxEsp,
    CanonicalFileIdentityV1 File);

// Exact content/readback binding only. GRUB's compiled prefix and firmware
// execution still require a disposable boot result before registration opens.
internal sealed record ReviewedLinuxEspBootBindingV1(ReviewedInstallerHandoffV1 Storage,
    PreparationCheckpointReference SeedCheckpoint, CanonicalFileIdentityV1 Shim,
    CanonicalFileIdentityV1 Grub, CanonicalFileIdentityV1 Config,
    CanonicalFileIdentityV1? CompiledPrefixConfig = null);

#pragma warning disable CA1032 // This interruption must retain both the original and restoration failures.
internal sealed class ReviewedLinuxEspRestorationException(Exception original, Exception restoration)
    : InvalidOperationException("The reviewed Linux ESP copy was interrupted and exact file restoration could not be verified. Inspect the target before retrying.",
        new AggregateException(original, restoration));
internal sealed class ReviewedSeedRestorationException(Exception original, Exception restoration)
    : InvalidOperationException("The reviewed installer seed copy was interrupted and exact file restoration could not be verified. Inspect the target before retrying.",
        new AggregateException(original, restoration));
#pragma warning restore CA1032

// One exact file copy into a receipted OEMDRV, ISO, or prepared Linux ESP volume.
// Production callers must enter through the closed reviewed execution gate and
// persist a complete receipt after independent volume-GUID readback.
// An interrupted copy requires exact-volume cleanup before another attempt.
internal sealed class ReviewedSeedFileWriter
{
    internal const string LinuxEspGrubConfigPath = PreparedLayoutRules.GrubConfigPath;
    internal const string LinuxEspCompiledPrefixConfigPath =
        PreparedLayoutRules.DebianFamilyGrubConfigPath;
    internal const string LinuxEspFedoraPrefixConfigPath =
        PreparedLayoutRules.FedoraGrubConfigPath;
    private readonly ReviewedPartitionCreationService _reviewed;
    private readonly Func<Guid, string> _volumeRoot;
    private readonly Func<Guid, string, Observation<CanonicalFileIdentityV1>> _readFile;

    internal ReviewedSeedFileWriter(ReviewedPartitionCreationService reviewed) : this(reviewed,
        volume => @"\\?\Volume{" + volume.ToString("D") + @"}\",
        WindowsRecoveryPathReader.ReadFileIdentity) { }

    internal ReviewedSeedFileWriter(ReviewedPartitionCreationService reviewed,
        Func<Guid, string> volumeRoot,
        Func<Guid, string, Observation<CanonicalFileIdentityV1>> readFile)
    {
        _reviewed = reviewed ?? throw new ArgumentNullException(nameof(reviewed));
        _volumeRoot = volumeRoot;
        _readFile = readFile;
    }

    // Internal reviewed copy transition. The public installer path remains
    // closed before this method; its file-backed fixture exercises the same
    // exact-volume copy/readback primitives without authorizing physical use.
    internal Task<PreparationCheckpointReference> CopyAndPersistPinnedExternalSeedAsync(
        ReviewedContentHandoffV1 content, InstallerBootSpec bootSpec, long requestedLinuxBytes,
        string pinnedIsoPath, string pinnedKernelPath, string pinnedRawInitrdPath,
        CancellationToken ct = default) => CopyAndPersistPinnedSeedAsync(content, bootSpec,
            requestedLinuxBytes, pinnedIsoPath, pinnedKernelPath, pinnedRawInitrdPath, ct);

    internal async Task<PreparationCheckpointReference> CopyAndPersistPinnedSeedAsync(
        ReviewedContentHandoffV1 content, InstallerBootSpec bootSpec, long requestedLinuxBytes,
        string pinnedIsoPath, string? pinnedKernelPath, string? pinnedRawInitrdPath,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(bootSpec);
        RequirePinnedMediaRecipe(content, bootSpec, pinnedKernelPath,
            pinnedRawInitrdPath);
        var storage = _reviewed.ObserveInstallerHandoff(content.Storage.Checkpoint, ct);
        var actual = _reviewed.ObserveContentHandoff(storage.Checkpoint, storage,
            bootSpec, requestedLinuxBytes,
            content.StagingDirectory, ct);
        if (storage != content.Storage || actual.StagedBytes != content.StagedBytes ||
            actual.Sizing != content.Sizing ||
            !string.Equals(actual.StagingDirectory, content.StagingDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.BootSpecSha256, content.BootSpecSha256,
                StringComparison.Ordinal) ||
            !actual.StagedFiles.SequenceEqual(content.StagedFiles))
            throw new InvalidDataException("The reviewed content changed before seed copying.");
        var boot = DirectInstallService.BuildReviewedBootPayload(actual, bootSpec, ct);
        var checkpoint = _reviewed.ReopenCreatedCheckpoint(storage.Checkpoint);
        var intent = checkpoint.Plan.ReviewedIntent ??
            throw new InvalidDataException("The reviewed seed has no media intent.");
        if (intent.Media.FullIsoBytes is 0 or > long.MaxValue ||
            intent.Media.KernelBytes is 0 or > long.MaxValue ||
            intent.Media.InitrdBytes is 0 or > long.MaxValue ||
            !string.Equals(bootSpec.IsoSha256, intent.ArtifactSha256,
                StringComparison.OrdinalIgnoreCase) ||
            actual.StagedFiles.Any(file => file.SizeBytes < 0 ||
                file.SizeBytes == 0 && !ReviewedSeedPathRules.AllowsEmptySelectedData(
                    "\\" + file.RelativePath.Replace('/', '\\'))))
            throw new InvalidDataException("The reviewed seed sources have incomplete measured sizes.");

        // Validate all external sources and the selected-media/agent identity
        // inventory before the first OEMDRV write. Every copy repeats its pin.
        using (var iso = new ReviewedPinnedIsoSource(pinnedIsoPath,
            (long)intent.Media.FullIsoBytes, intent.ArtifactSha256, ct))
        {
            _ = FindInstallerEfiPair(iso);
            foreach (var extra in bootSpec.ExtraIsoFiles)
                _ = iso.HashMember(extra.IsoRelativePath,
                    checked((long)intent.Media.Stage2Bytes), ct);
            if (pinnedKernelPath is null)
            {
                var kernelMember = iso.FirstAvailable(bootSpec.KernelIsoPaths) ??
                    throw new InvalidDataException("The pinned ISO has no declared installer kernel.");
                var initrdMember = iso.FirstAvailable(bootSpec.InitrdIsoPaths) ??
                    throw new InvalidDataException("The pinned ISO has no declared installer initrd.");
                _ = iso.HashMember(kernelMember, (long)intent.Media.KernelBytes, ct);
                _ = iso.HashMember(initrdMember, (long)intent.Media.InitrdBytes, ct);
            }
            iso.VerifyPin(ct);
        }
        if (pinnedKernelPath is not null)
        {
            var kernel = new FileInfo(pinnedKernelPath);
            if (!kernel.Exists || (kernel.Attributes & FileAttributes.ReparsePoint) != 0 ||
                kernel.Length != (long)intent.Media.KernelBytes ||
                !string.Equals(HashFile(kernel.FullName, ct), bootSpec.KernelSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The reviewed kernel source changed before seed copying.");
        }
        _ = MeasurePinnedInstallerInitrd(boot, bootSpec, pinnedIsoPath,
            pinnedRawInitrdPath, (long)intent.Media.FullIsoBytes,
            (long)intent.Media.InitrdBytes, ct);
        var agentPaths = actual.StagedFiles.Select(file => file.RelativePath.Replace('\\', '/'))
            .Where(path => path.StartsWith("igloo-agent/", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (agentPaths.Length == 0)
            throw new InvalidDataException("The reviewed seed has no first-boot agent inventory.");
        _ = await SeedIdentityWriter.BuildBytesAsync(actual.StagingDirectory,
            storage.TargetDisk.GptDiskGuid, storage.Payload.PartitionGuid, agentPaths, ct)
            .ConfigureAwait(false);

        var payloadPaths = actual.StagedFiles.Select(file =>
            "\\" + file.RelativePath.Replace('/', '\\')).ToList();
        payloadPaths.AddRange([@"\EFI\BOOT\BOOTX64.EFI", @"\EFI\BOOT\grubx64.efi",
            @"\igloo-boot\shimx64.efi", @"\igloo-boot\grubx64.efi",
            @"\igloo-boot\linux", @"\igloo-boot\initrd",
            @"\igloo-seed-identity.json"]);
        payloadPaths.AddRange(bootSpec.ExtraIsoFiles.Select(extra =>
            "\\" + extra.OemDrvRelativePath.Replace('/', '\\')));
        payloadPaths.AddRange(DirectInstallService.ReviewedGrubCfgDirs.Select(dir =>
            "\\" + dir + @"\grub.cfg"));
        var isoPaths = new List<string>();
        if (bootSpec.CopyFullIsoToVolume)
        {
            if (string.IsNullOrWhiteSpace(bootSpec.IsoVolumeFileName))
                throw new InvalidDataException("The reviewed ISO copy has no declared filename.");
            var isoName = "\\" + bootSpec.IsoVolumeFileName;
            if (actual.Sizing.IsoOnOwnPartition) isoPaths.Add(isoName);
            else payloadPaths.Add(isoName);
        }
        PreparedLayoutStore.RequireReviewedSeedReceiptCapacity(checkpoint,
            storage.Payload, storage.LinuxEsp, storage.Iso, actual.BootSpecSha256,
            payloadPaths, isoPaths);
        RequireEmptyDestinations(storage.Payload.VolumeGuid, payloadPaths, ct);
        if (isoPaths.Count > 0)
            RequireEmptyDestinations(storage.Iso!.VolumeGuid, isoPaths, ct);

        var payloadRoot = _volumeRoot(storage.Payload.VolumeGuid);
        if (string.IsNullOrWhiteSpace(payloadRoot) || !Path.IsPathFullyQualified(payloadRoot))
            throw new InvalidDataException("The reviewed payload has no exact volume root.");
        var payloadDirectories = CaptureReviewedDirectories(payloadRoot, payloadPaths);
        string? isoRoot = null;
        (string Path, bool Existed)[] isoDirectories = [];
        if (isoPaths.Count > 0)
        {
            isoRoot = _volumeRoot(storage.Iso!.VolumeGuid);
            if (string.IsNullOrWhiteSpace(isoRoot) || !Path.IsPathFullyQualified(isoRoot))
                throw new InvalidDataException("The reviewed ISO has no exact volume root.");
            isoDirectories = CaptureReviewedDirectories(isoRoot, isoPaths);
        }
        var payloadFiles = ImmutableArray.CreateBuilder<CanonicalFileIdentityV1>();
        var isoFiles = ImmutableArray.CreateBuilder<CanonicalFileIdentityV1>();
        try
        {
            if (bootSpec.CopyFullIsoToVolume)
            {
                var copiedIso = CopyReviewedIso(actual, bootSpec, pinnedIsoPath, ct);
                if (actual.Sizing.IsoOnOwnPartition) isoFiles.Add(copiedIso);
                else payloadFiles.Add(copiedIso);
            }
            foreach (var file in actual.StagedFiles)
                payloadFiles.Add(CopyStaged(actual, file.RelativePath, ct));
            _ = CopyPinnedIsoBootFiles(actual, bootSpec, pinnedIsoPath,
                file => payloadFiles.Add(file), ct);
            payloadFiles.Add(CopyPinnedInstallerKernel(actual, bootSpec,
                pinnedIsoPath, pinnedKernelPath, ct));
            payloadFiles.Add(CopyPinnedInstallerInitrd(actual, bootSpec,
                pinnedIsoPath, pinnedRawInitrdPath, ct));

            var grubBytes = Encoding.ASCII.GetBytes(boot.GrubConfig);
            var temporaryGrub = Path.Join(Path.GetTempPath(),
                "igloo-reviewed-grub-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllBytesAsync(temporaryGrub, grubBytes, ct).ConfigureAwait(false);
                var digest = Convert.ToHexString(SHA256.HashData(grubBytes));
                foreach (var dir in DirectInstallService.ReviewedGrubCfgDirs)
                    payloadFiles.Add(CopyNew(storage, PreparationRole.Payload,
                        "\\" + dir + @"\grub.cfg", temporaryGrub,
                        grubBytes.Length, digest, ct));
            }
            finally
            {
                if (File.Exists(temporaryGrub)) File.Delete(temporaryGrub);
            }

            var seedRoot = _volumeRoot(storage.Payload.VolumeGuid);
            if (string.IsNullOrWhiteSpace(seedRoot) || !Path.IsPathFullyQualified(seedRoot))
                throw new InvalidDataException("The reviewed payload has no exact seed root.");
            var identityBytes = await SeedIdentityWriter.BuildBytesAsync(seedRoot,
                storage.TargetDisk.GptDiskGuid, storage.Payload.PartitionGuid, agentPaths, ct)
                .ConfigureAwait(false);
            var temporaryIdentity = Path.Join(Path.GetTempPath(),
                "igloo-reviewed-identity-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllBytesAsync(temporaryIdentity, identityBytes, ct)
                    .ConfigureAwait(false);
                payloadFiles.Add(CopyNew(storage, PreparationRole.Payload,
                    @"\igloo-seed-identity.json", temporaryIdentity,
                    identityBytes.Length, Convert.ToHexString(SHA256.HashData(identityBytes)), ct));
            }
            finally
            {
                if (File.Exists(temporaryIdentity)) File.Delete(temporaryIdentity);
            }
            var receipt = new ReviewedInstallerSeedReceiptV1(storage.GenerationId,
                storage.Payload, storage.LinuxEsp, storage.Iso, actual.BootSpecSha256,
                payloadFiles.ToImmutable(), isoFiles.ToImmutable());
            return _reviewed.VerifyAndPersistReviewedSeedContent(actual, boot, receipt,
                bootSpec, requestedLinuxBytes,
                pinnedIsoPath, pinnedRawInitrdPath, _readFile, ct);
        }
        catch (Exception original)
        {
            try
            {
                if (_reviewed.ReopenCreatedCheckpoint(storage.Checkpoint).State !=
                    PreparationState.CreatedAndVerified)
                    throw new InvalidOperationException(
                        "The reviewed seed checkpoint changed before file restoration.");
                RestoreReviewedSeedFiles(storage, payloadRoot, isoRoot,
                    payloadPaths, isoPaths, payloadDirectories, isoDirectories,
                    payloadFiles, isoFiles);
            }
            catch (Exception restoration)
            { throw new ReviewedSeedRestorationException(original, restoration); }
            throw;
        }
    }

    private void RequireEmptyDestinations(Guid volumeGuid, IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        var root = _volumeRoot(volumeGuid);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidDataException("The reviewed seed has no exact volume root.");
        RequirePlainDirectory(root);
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var segments = SafeSegments(path);
            var safe = "\\" + string.Join('\\', segments);
            if (!normalized.Add(safe) ||
                _readFile(volumeGuid, safe).Availability != ObservationAvailability.Absent)
                throw new InvalidOperationException("A reviewed seed destination is occupied or ambiguous.");
            RequireNoInterruptedCopy(root, segments);
        }
    }

    private static void RequireNoInterruptedCopy(string root, string[] segments)
    {
        var parent = root;
        foreach (var segment in segments.SkipLast(1))
        {
            parent = Path.Join(parent, segment);
            if (!ObservePlainDirectoryIfPresent(parent)) return;
        }
        // A previous process can die after creating its same-volume temporary
        // file. A fresh attempt must not hide that unresolved copy with a new
        // temporary name or a successful final-path receipt.
        var prefix = segments[^1] + ".igloo-copy-";
        if (Directory.EnumerateFileSystemEntries(parent, prefix + "*",
                SearchOption.TopDirectoryOnly).Any(entry =>
                Path.GetFileName(entry).StartsWith(prefix,
                    StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "An interrupted reviewed copy remains at the destination. Inspect the exact volume before retrying.");
    }

    internal CanonicalFileIdentityV1 CopyStaged(ReviewedContentHandoffV1 content,
        string stagedRelativePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.StagedFiles.IsDefaultOrEmpty ||
            !Path.IsPathFullyQualified(content.StagingDirectory) ||
            string.IsNullOrWhiteSpace(stagedRelativePath))
            throw new InvalidDataException("The reviewed source staging handoff is incomplete.");
        var segments = SafeSegments("\\" + stagedRelativePath.Replace('/', '\\'));
        var portable = string.Join('/', segments);
        var matches = content.StagedFiles.Where(file =>
            string.Equals(file.RelativePath.Replace('\\', '/'), portable,
                StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("The reviewed staged file has no unique source receipt.");
        var source = Path.Join(content.StagingDirectory, Path.Join(segments));
        return CopyNew(content.Storage, PreparationRole.Payload, "\\" + string.Join('\\', segments),
            source, matches[0].SizeBytes, matches[0].Sha256, ct);
    }

    internal ReviewedLinuxEspFileCopyReceiptV1 CopyExactLinuxEspFile(
        ReviewedInstallerHandoffV1 storage, string relativePath, string sourcePath,
        long expectedLength, string expectedSha256, CancellationToken ct = default)
    {
        RequireLinuxEspPath(relativePath);
        var copied = CopyNew(storage, PreparationRole.LinuxEsp, relativePath, sourcePath,
            expectedLength, expectedSha256, ct);
        return new(storage.GenerationId, storage.TargetDisk, storage.LinuxEsp, copied);
    }

    internal ReviewedLinuxEspFileCopyReceiptV1 ReobserveLinuxEspFileCopy(
        ReviewedInstallerHandoffV1 storage, ReviewedLinuxEspFileCopyReceiptV1 receipt,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(receipt);
        RequireLinuxEspPath(receipt.File.RelativePath);
        if (receipt.GenerationId != storage.GenerationId ||
            receipt.TargetDisk != storage.TargetDisk ||
            receipt.LinuxEsp != storage.LinuxEsp ||
            receipt.File.VolumeGuid != storage.LinuxEsp.VolumeGuid ||
            receipt.File.Length <= 0 || receipt.File.Sha256 is not { Length: 64 } ||
            !receipt.File.Sha256.All(Uri.IsHexDigit) ||
            _reviewed.ObserveInstallerHandoff(storage.Checkpoint, ct) != storage)
            throw new InvalidDataException("The Linux ESP copy receipt changed or lost its storage binding.");
        var observed = _readFile(storage.LinuxEsp.VolumeGuid, receipt.File.RelativePath);
        if (observed.Availability != ObservationAvailability.Available || observed.Value != receipt.File ||
            _reviewed.ObserveInstallerHandoff(storage.Checkpoint, ct) != storage)
            throw new InvalidDataException("The Linux ESP copy differs from its exact-volume readback.");
        return receipt;
    }

    internal ReviewedLinuxEspBootBindingV1 BindReviewedLinuxEspBootFiles(
        ReviewedContentHandoffV1 content, PreparationCheckpointReference stagedReference,
        InstallerBootSpec bootSpec,
        string pinnedIsoPath, string? pinnedRawInitrdPath,
        ReviewedLinuxEspFileCopyReceiptV1 shim,
        ReviewedLinuxEspFileCopyReceiptV1 grub,
        ReviewedLinuxEspFileCopyReceiptV1 config,
        ReviewedLinuxEspFileCopyReceiptV1? compiledPrefixConfig = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(stagedReference);
        ArgumentNullException.ThrowIfNull(bootSpec);
        var storage = content.Storage;
        var seed = _reviewed.VerifyPersistedReviewedSeed(stagedReference, storage,
            content, bootSpec, pinnedIsoPath, pinnedRawInitrdPath, _readFile, ct);
        var boot = DirectInstallService.BuildReviewedBootPayload(content, bootSpec, ct);
        if (boot.Storage != storage || boot.BootSpecSha256 != seed.BootSpecSha256 ||
            shim.File.RelativePath != PreparedLayoutRules.ShimPath ||
            grub.File.RelativePath != PreparedLayoutRules.GrubPath ||
            config.File.RelativePath != LinuxEspGrubConfigPath)
            throw new InvalidDataException("The reviewed Linux ESP boot files have no exact loader and config paths.");
        if (boot.LinuxEspCompiledPrefixConfigPath is { } requiredPrefixPath
            ? compiledPrefixConfig?.File.RelativePath != requiredPrefixPath
            : compiledPrefixConfig is not null)
            throw new InvalidDataException("The reviewed Linux ESP compiled-prefix config is absent or unexpected.");
        var sourceFiles = seed.PayloadFiles.ToDictionary(file => file.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        RequireSource(@"\igloo-boot\shimx64.efi", shim.File);
        RequireSource(@"\igloo-boot\grubx64.efi", grub.File);
        var configBytes = Encoding.ASCII.GetBytes(boot.LinuxEspGrubConfig);
        if (config.File.Length != configBytes.Length ||
            !string.Equals(config.File.Sha256,
                Convert.ToHexString(SHA256.HashData(configBytes)), StringComparison.Ordinal) ||
            compiledPrefixConfig is { } prefix && prefix.File !=
                (config.File with { RelativePath = boot.LinuxEspCompiledPrefixConfigPath! }))
            throw new InvalidDataException("The reviewed Linux ESP config differs from the bound installer boot recipe.");
        ReobserveLinuxEspFileCopy(storage, shim, ct);
        ReobserveLinuxEspFileCopy(storage, grub, ct);
        ReobserveLinuxEspFileCopy(storage, config, ct);
        if (compiledPrefixConfig is not null)
            ReobserveLinuxEspFileCopy(storage, compiledPrefixConfig, ct);
        if (_reviewed.ObserveInstallerHandoff(storage.Checkpoint, ct) != storage)
            throw new InvalidOperationException("The reviewed storage changed during Linux ESP boot file readback.");
        return new(storage, stagedReference, shim.File, grub.File, config.File,
            compiledPrefixConfig?.File);

        void RequireSource(string path, CanonicalFileIdentityV1 copy)
        {
            if (!sourceFiles.TryGetValue(path, out var source) ||
                source.VolumeGuid != storage.Payload.VolumeGuid ||
                source.Length != copy.Length ||
                !string.Equals(source.Sha256, copy.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("A reviewed Linux ESP loader differs from its pinned seed source.");
        }
    }

    // Keep the reviewed handoff in the existing seed and boot-plan path. The
    // public installer gate remains closed; this method only copies to a
    // receipted exact Linux ESP and produces a blocked registration plan.
    internal Observation<CommunityBootRegistrationPlan> CopyAndPlanReviewedLinuxEspBoot(
        ReviewedContentHandoffV1 content, PreparationCheckpointReference stagedReference,
        InstallerBootSpec bootSpec, long requestedLinuxBytes,
        string pinnedIsoPath, string? pinnedRawInitrdPath,
        Func<BootRegistrationEvidence> captureBootEvidence, Guid newBcdId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(content.Storage);
        ArgumentNullException.ThrowIfNull(stagedReference);
        ArgumentNullException.ThrowIfNull(bootSpec);
        ArgumentNullException.ThrowIfNull(captureBootEvidence);
        if (newBcdId == Guid.Empty)
            throw new InvalidDataException("The reviewed boot object identity is unavailable.");

        var storage = _reviewed.ObserveInstallerHandoff(content.Storage.Checkpoint, ct);
        if (storage != content.Storage)
            throw new InvalidOperationException("The reviewed storage changed before Linux ESP preparation.");
        var actual = _reviewed.ObserveContentHandoff(storage.Checkpoint, storage,
            bootSpec, requestedLinuxBytes, content.StagingDirectory, ct);
        if (actual.Storage != content.Storage ||
            actual.StagedBytes != content.StagedBytes || actual.Sizing != content.Sizing ||
            actual.BootSpecSha256 != content.BootSpecSha256 ||
            !string.Equals(actual.StagingDirectory, content.StagingDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            !actual.StagedFiles.SequenceEqual(content.StagedFiles))
            throw new InvalidDataException("The reviewed content changed before Linux ESP preparation.");
        var seed = _reviewed.VerifyPersistedReviewedSeed(stagedReference, storage,
            actual, bootSpec, pinnedIsoPath, pinnedRawInitrdPath, _readFile, ct);
        var boot = DirectInstallService.BuildReviewedBootPayload(actual, bootSpec, ct);
        var checkpoint = _reviewed.ReopenCreatedCheckpoint(storage.Checkpoint);
        var intent = checkpoint.Plan.ReviewedIntent!;
        var windowsBootVolume = intent.ShrinkSource with
            { SizeBytes = intent.ResultingSourceBytes };
        var linuxEspPartitionNumber = _reviewed.ObserveLinuxEspPartitionNumber(storage, ct);
        var prewrite = captureBootEvidence() ??
            throw new InvalidDataException("The reviewed boot evidence is unavailable.");
        if (prewrite.Binding.Availability != ObservationAvailability.Available ||
            prewrite.Binding.Value.TargetVolume != storage.LinuxEsp ||
            prewrite.Binding.Value.WindowsVolume != windowsBootVolume ||
            prewrite.TargetPartitionNumber != linuxEspPartitionNumber)
            throw new InvalidDataException("The boot evidence differs from the reviewed Windows boot volume or exact Linux ESP partition.");

        // A failed read-only BCD/firmware/RTC plan must not leave newly copied
        // files on the Linux ESP. These projected identities only check whether
        // the prewrite snapshot can form a plan; the returned plan is still
        // built from actual exact-volume receipts after the copies.
        var configBytes = Encoding.ASCII.GetBytes(boot.LinuxEspGrubConfig);
        var configSha256 = Convert.ToHexString(SHA256.HashData(configBytes));
        var sourceShim = RequireSeedLoader(@"\igloo-boot\shimx64.efi");
        var sourceGrub = RequireSeedLoader(@"\igloo-boot\grubx64.efi");
        CanonicalFileIdentityV1 Project(CanonicalFileIdentityV1 source, string path) =>
            source with { VolumeGuid = storage.LinuxEsp.VolumeGuid, RelativePath = path };
        var projectedConfig = new CanonicalFileIdentityV1(storage.LinuxEsp.VolumeGuid,
            LinuxEspGrubConfigPath, configBytes.Length, configSha256);
        var projected = new ReviewedLinuxEspBootBindingV1(storage, stagedReference,
            Project(sourceShim, PreparedLayoutRules.ShimPath),
            Project(sourceGrub, PreparedLayoutRules.GrubPath), projectedConfig,
            boot.LinuxEspCompiledPrefixConfigPath is { } projectedPrefix
                ? projectedConfig with { RelativePath = projectedPrefix } : null);
        var preflight = CommunityBootRegistrationPlan.CreateReviewed(prewrite, newBcdId,
            projected);
        if (preflight.Availability != ObservationAvailability.Available)
            return preflight;

        var paths = new List<string> { PreparedLayoutRules.ShimPath,
            PreparedLayoutRules.GrubPath, LinuxEspGrubConfigPath };
        if (boot.LinuxEspCompiledPrefixConfigPath is { } prefixPath)
            paths.Add(prefixPath);
        RequireEmptyDestinations(storage.LinuxEsp.VolumeGuid, paths, ct);
        var espRoot = _volumeRoot(storage.LinuxEsp.VolumeGuid);
        if (string.IsNullOrWhiteSpace(espRoot) || !Path.IsPathFullyQualified(espRoot))
            throw new InvalidDataException("The reviewed Linux ESP has no exact volume root.");
        var prewriteDirectories = CaptureReviewedDirectories(espRoot, paths);
        var seedRoot = _volumeRoot(storage.Payload.VolumeGuid);
        if (string.IsNullOrWhiteSpace(seedRoot) || !Path.IsPathFullyQualified(seedRoot))
            throw new InvalidDataException("The reviewed seed has no exact volume root.");
        RequirePlainDirectory(seedRoot);
        var copied = new List<ReviewedLinuxEspFileCopyReceiptV1>();
        Observation<CommunityBootRegistrationPlan> plan;
        try
        {
            var shim = CopyLoader(@"\igloo-boot\shimx64.efi", PreparedLayoutRules.ShimPath);
            copied.Add(shim);
            var grub = CopyLoader(@"\igloo-boot\grubx64.efi", PreparedLayoutRules.GrubPath);
            copied.Add(grub);

            var temporaryConfig = Path.Join(Path.GetTempPath(),
                "igloo-reviewed-esp-config-" + Guid.NewGuid().ToString("N") + ".tmp");
            ReviewedLinuxEspFileCopyReceiptV1 config;
            ReviewedLinuxEspFileCopyReceiptV1? compiledPrefixConfig = null;
            try
            {
                using (var output = new FileStream(temporaryConfig, FileMode.CreateNew,
                           FileAccess.Write, FileShare.None))
                {
                    output.Write(configBytes);
                    output.Flush(flushToDisk: true);
                }
                config = CopyExactLinuxEspFile(storage, LinuxEspGrubConfigPath,
                    temporaryConfig, configBytes.Length, configSha256, ct);
                copied.Add(config);
                if (boot.LinuxEspCompiledPrefixConfigPath is { } requiredPrefix)
                {
                    compiledPrefixConfig = CopyExactLinuxEspFile(storage, requiredPrefix,
                        temporaryConfig, configBytes.Length, configSha256, ct);
                    copied.Add(compiledPrefixConfig);
                }
            }
            finally
            {
                if (File.Exists(temporaryConfig)) File.Delete(temporaryConfig);
            }

            var current = captureBootEvidence() ??
                throw new InvalidDataException("The reviewed boot evidence became unavailable.");
            if (!RecoverySnapshotSerialization.ValueEqual(prewrite, current))
                throw new InvalidOperationException("The Windows boot evidence changed during Linux ESP preparation.");
            if (_reviewed.ObserveLinuxEspPartitionNumber(storage, ct) != linuxEspPartitionNumber)
                throw new InvalidOperationException("The reviewed Linux ESP partition number changed during boot file copying.");
            var binding = BindReviewedLinuxEspBootFiles(actual, stagedReference,
                bootSpec, pinnedIsoPath, pinnedRawInitrdPath, shim, grub, config,
                compiledPrefixConfig, ct);
            plan = CommunityBootRegistrationPlan.CreateReviewed(current, newBcdId, binding);
        }
        catch (Exception original)
        {
            try { RestoreReviewedLinuxEspFiles(storage, espRoot, paths,
                prewriteDirectories, copied); }
            catch (Exception restoration)
            { throw new ReviewedLinuxEspRestorationException(original, restoration); }
            throw;
        }
        if (plan.Availability == ObservationAvailability.Available) return plan;
        try { RestoreReviewedLinuxEspFiles(storage, espRoot, paths,
            prewriteDirectories, copied); }
        catch (Exception restoration)
        { throw new ReviewedLinuxEspRestorationException(
            new InvalidOperationException($"The reviewed boot plan is unavailable: {plan.Code}."),
            restoration); }
        return plan;

        ReviewedLinuxEspFileCopyReceiptV1 CopyLoader(string seedPath, string espPath)
        {
            var source = RequireSeedLoader(seedPath);
            var sourcePath = Path.Join(seedRoot, seedPath.TrimStart('\\').Replace('\\', Path.DirectorySeparatorChar));
            return CopyExactLinuxEspFile(storage, espPath, sourcePath,
                source.Length, source.Sha256, ct);
        }

        CanonicalFileIdentityV1 RequireSeedLoader(string seedPath)
        {
            var source = seed.PayloadFiles.SingleOrDefault(file => file.RelativePath == seedPath);
            if (source is null || source.VolumeGuid != storage.Payload.VolumeGuid ||
                source.Length <= 0 || source.Sha256 is not { Length: 64 } ||
                !source.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("The reviewed OEMDRV loader receipt is unavailable.");
            return source;
        }
    }

    private static (string Path, bool Existed)[] CaptureReviewedDirectories(
        string root, IReadOnlyList<string> paths)
    {
        RequirePlainDirectory(root);
        var captured = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var directory = root;
            foreach (var segment in SafeSegments(path).SkipLast(1))
            {
                directory = Path.Join(directory, segment);
                if (captured.ContainsKey(directory)) continue;
                captured.Add(directory, ObservePlainDirectoryIfPresent(directory));
            }
        }
        return captured.Select(pair => (Path: pair.Key, Existed: pair.Value)).ToArray();
    }

    private static bool ObservePlainDirectoryIfPresent(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            (attributes & FileAttributes.Directory) == 0)
            throw new InvalidDataException("A reviewed Linux ESP parent path is a file or redirected.");
        return true;
    }

    private void RestoreReviewedSeedFiles(ReviewedInstallerHandoffV1 storage,
        string payloadRoot, string? isoRoot,
        List<string> payloadPaths, List<string> isoPaths,
        IReadOnlyList<(string Path, bool Existed)> payloadDirectories,
        IReadOnlyList<(string Path, bool Existed)> isoDirectories,
        IEnumerable<CanonicalFileIdentityV1> payloadFiles,
        IEnumerable<CanonicalFileIdentityV1> isoFiles)
    {
        RestoreVolume(storage.Payload, payloadRoot, payloadPaths,
            payloadDirectories, payloadFiles);
        if (isoPaths.Count > 0)
        {
            if (storage.Iso is null || isoRoot is null)
                throw new InvalidDataException("The reviewed ISO restoration volume is unavailable.");
            RestoreVolume(storage.Iso, isoRoot, isoPaths, isoDirectories, isoFiles);
        }

        void RestoreVolume(CanonicalVolumeIdentityV1 volume, string root,
            IReadOnlyList<string> expectedAbsentPaths,
            IReadOnlyList<(string Path, bool Existed)> prewriteDirectories,
            IEnumerable<CanonicalFileIdentityV1> copied)
        {
            if (_reviewed.ObserveInstallerHandoff(storage.Checkpoint) != storage ||
                !string.Equals(_volumeRoot(volume.VolumeGuid), root,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.IsPathFullyQualified(root))
                throw new InvalidOperationException("The reviewed seed volume changed before restoration.");
            RequirePlainDirectory(root);
            foreach (var receipt in copied.Reverse())
            {
                if (receipt.VolumeGuid != volume.VolumeGuid ||
                    !expectedAbsentPaths.Contains(receipt.RelativePath,
                        StringComparer.OrdinalIgnoreCase) ||
                    _reviewed.ObserveInstallerHandoff(storage.Checkpoint) != storage)
                    throw new InvalidDataException("A seed copy receipt is outside this restoration attempt.");
                var observed = _readFile(volume.VolumeGuid, receipt.RelativePath);
                if (observed.Availability == ObservationAvailability.Absent) continue;
                if (observed.Availability != ObservationAvailability.Available ||
                    observed.Value != receipt)
                    throw new InvalidDataException("A seed file changed after its copy receipt; deletion is blocked.");
                var segments = SafeSegments(receipt.RelativePath);
                var parent = root;
                foreach (var segment in segments.SkipLast(1))
                {
                    parent = Path.Join(parent, segment);
                    RequirePlainDirectory(parent);
                }
                var destination = Path.Join(parent, segments[^1]);
                var file = new FileInfo(destination);
                if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A seed file became unavailable or redirected before restoration.");
                File.Delete(destination);
                if (_readFile(volume.VolumeGuid, receipt.RelativePath).Availability !=
                    ObservationAvailability.Absent)
                    throw new InvalidOperationException("A deleted seed file remains present after restoration.");
            }
            if (_reviewed.ObserveInstallerHandoff(storage.Checkpoint) != storage ||
                expectedAbsentPaths.Any(path => _readFile(volume.VolumeGuid, path).Availability !=
                    ObservationAvailability.Absent))
                throw new InvalidOperationException("The reviewed seed copy left an unreceipted or changed file.");
            foreach (var (path, existed) in prewriteDirectories.OrderByDescending(
                directory => directory.Path.Length))
            {
                if (existed)
                {
                    RequirePlainDirectory(path);
                    continue;
                }
                if (!ObservePlainDirectoryIfPresent(path)) continue;
                Directory.Delete(path, recursive: false);
                if (ObservePlainDirectoryIfPresent(path))
                    throw new InvalidOperationException("A created seed directory remains after restoration.");
            }
            if (_reviewed.ObserveInstallerHandoff(storage.Checkpoint) != storage)
                throw new InvalidOperationException("The reviewed storage changed during seed restoration.");
        }
    }

    // This only restores the bounded set of files created by this copy attempt.
    // A partial, unreceipted file or changed target remains explicitly unresolved;
    // the public preparation gate stays closed until durable crash recovery exists.
    private void RestoreReviewedLinuxEspFiles(ReviewedInstallerHandoffV1 storage,
        string expectedRoot,
        IReadOnlyList<string> expectedAbsentPaths,
        IReadOnlyList<(string Path, bool Existed)> prewriteDirectories,
        IReadOnlyList<ReviewedLinuxEspFileCopyReceiptV1> copied)
    {
        if (_reviewed.ObserveInstallerHandoff(storage.Checkpoint) != storage)
            throw new InvalidOperationException("The reviewed Linux ESP changed before file restoration.");
        var root = _volumeRoot(storage.LinuxEsp.VolumeGuid);
        if (!string.Equals(root, expectedRoot, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidDataException("The reviewed Linux ESP has no exact volume root for restoration.");
        RequirePlainDirectory(root);
        foreach (var receipt in copied.Reverse())
        {
            if (receipt.GenerationId != storage.GenerationId ||
                receipt.TargetDisk != storage.TargetDisk ||
                receipt.LinuxEsp != storage.LinuxEsp ||
                receipt.File.VolumeGuid != storage.LinuxEsp.VolumeGuid ||
                !expectedAbsentPaths.Contains(receipt.File.RelativePath,
                    StringComparer.Ordinal))
                throw new InvalidDataException("A Linux ESP copy receipt is outside this restoration attempt.");
            if (_reviewed.ObserveInstallerHandoff(storage.Checkpoint) != storage)
                throw new InvalidOperationException("The reviewed Linux ESP changed during file restoration.");
            var observed = _readFile(storage.LinuxEsp.VolumeGuid, receipt.File.RelativePath);
            if (observed.Availability == ObservationAvailability.Absent) continue;
            if (observed.Availability != ObservationAvailability.Available ||
                observed.Value != receipt.File)
                throw new InvalidDataException("A Linux ESP file changed after its copy receipt; deletion is blocked.");
            var segments = SafeSegments(receipt.File.RelativePath);
            var parent = root;
            foreach (var segment in segments.SkipLast(1))
            {
                parent = Path.Join(parent, segment);
                RequirePlainDirectory(parent);
            }
            var destination = Path.Join(parent, segments[^1]);
            var file = new FileInfo(destination);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A Linux ESP file became unavailable or redirected before restoration.");
            File.Delete(destination);
            if (_readFile(storage.LinuxEsp.VolumeGuid, receipt.File.RelativePath)
                .Availability != ObservationAvailability.Absent)
                throw new InvalidOperationException("A deleted Linux ESP file remains present after restoration.");
        }
        if (_reviewed.ObserveInstallerHandoff(storage.Checkpoint) != storage ||
            expectedAbsentPaths.Any(path =>
                _readFile(storage.LinuxEsp.VolumeGuid, path).Availability !=
                    ObservationAvailability.Absent))
            throw new InvalidOperationException("The reviewed Linux ESP copy left an unreceipted or changed file.");
        foreach (var (path, existed) in prewriteDirectories.OrderByDescending(
            directory => directory.Path.Length))
        {
            if (existed)
            {
                RequirePlainDirectory(path);
                continue;
            }
            if (!ObservePlainDirectoryIfPresent(path)) continue;
            Directory.Delete(path, recursive: false);
            if (ObservePlainDirectoryIfPresent(path))
                throw new InvalidOperationException("A created Linux ESP directory remains after restoration.");
        }
        if (_reviewed.ObserveInstallerHandoff(storage.Checkpoint) != storage)
            throw new InvalidOperationException("The reviewed Linux ESP changed during directory restoration.");
    }

    private static void RequireLinuxEspPath(string relativePath)
    {
        // The installer GRUB config targets OEMDRV. An installed-system config
        // needs a separate post-install binding and is deliberately excluded.
        if (relativePath is not (PreparedLayoutRules.ShimPath or PreparedLayoutRules.GrubPath or
            LinuxEspGrubConfigPath or LinuxEspCompiledPrefixConfigPath or
            LinuxEspFedoraPrefixConfigPath))
            throw new InvalidDataException("The Linux ESP copy has no declared iGloo boot path.");
    }

    internal CanonicalFileIdentityV1 CopyReviewedIso(ReviewedContentHandoffV1 content,
        InstallerBootSpec bootSpec, string isoPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(bootSpec);
        var checkpoint = _reviewed.ReopenCreatedCheckpoint(content.Storage.Checkpoint);
        var intent = checkpoint.Plan.ReviewedIntent;
        if (checkpoint.State != PreparationState.CreatedAndVerified ||
            checkpoint.Plan.GenerationId != content.Storage.GenerationId ||
            content.Storage.Checkpoint.GenerationId != checkpoint.Plan.GenerationId ||
            checkpoint.Plan.TargetDisk != content.Storage.TargetDisk ||
            intent is null || PreparedLayoutRules.VerifyReviewedIntent(checkpoint.Plan).Availability !=
                ObservationAvailability.Available ||
            !bootSpec.CopyFullIsoToVolume ||
            !string.Equals(content.BootSpecSha256,
                ReviewedPartitionCreationService.HashBootSpec(bootSpec), StringComparison.Ordinal) ||
            !string.Equals(bootSpec.IsoSha256, intent.ArtifactSha256,
                StringComparison.OrdinalIgnoreCase) ||
            bootSpec.IsoVolumeFileName is null ||
            intent.Media.FullIsoBytes is 0 or > long.MaxValue ||
            content.Sizing.IsoOnOwnPartition != (content.Storage.Iso is not null))
            throw new InvalidDataException("The reviewed ISO is not bound to the created storage and media pin.");
        var segments = SafeSegments("\\" + bootSpec.IsoVolumeFileName.Replace('/', '\\'));
        if (segments.Length != 1)
            throw new InvalidDataException("The reviewed ISO filename is unsafe.");
        return CopyNew(content.Storage,
            content.Sizing.IsoOnOwnPartition ? PreparationRole.Iso : PreparationRole.Payload,
            "\\" + segments[0], isoPath, checked((long)intent.Media.FullIsoBytes),
            intent.ArtifactSha256, ct);
    }

    internal IReadOnlyList<CanonicalFileIdentityV1> CopyPinnedIsoBootFiles(
        ReviewedContentHandoffV1 content, InstallerBootSpec bootSpec, string isoPath,
        Action<CanonicalFileIdentityV1>? onCopied = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(bootSpec);
        var checkpoint = _reviewed.ReopenCreatedCheckpoint(content.Storage.Checkpoint);
        var intent = checkpoint.Plan.ReviewedIntent;
        if (checkpoint.State != PreparationState.CreatedAndVerified ||
            checkpoint.Plan.GenerationId != content.Storage.GenerationId ||
            content.Storage.Checkpoint.GenerationId != checkpoint.Plan.GenerationId ||
            checkpoint.Plan.TargetDisk != content.Storage.TargetDisk ||
            intent is null || intent.Media.FullIsoBytes is 0 or > long.MaxValue ||
            PreparedLayoutRules.VerifyReviewedIntent(checkpoint.Plan).Availability !=
                ObservationAvailability.Available ||
            !string.Equals(bootSpec.IsoSha256, intent.ArtifactSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(content.BootSpecSha256,
                ReviewedPartitionCreationService.HashBootSpec(bootSpec), StringComparison.Ordinal))
            throw new InvalidDataException("The EFI source has no exact reviewed ISO and storage pin.");
        using var source = new ReviewedPinnedIsoSource(isoPath,
            checked((long)intent.Media.FullIsoBytes), intent.ArtifactSha256, ct);
        // These copies stage the installer on OEMDRV; they do not write the
        // dedicated installed-system Linux ESP.
        var selected = FindInstallerEfiPair(source);
        if (bootSpec.ExtraIsoFiles.Count > 1 ||
            (bootSpec.ExtraIsoFiles.Count == 1 && intent.Media.Stage2Bytes is 0 or > long.MaxValue))
            throw new InvalidDataException("The reviewed ISO stage2 inventory is unsupported.");

        var copied = new List<CanonicalFileIdentityV1>();
        CopyMember(selected.Shim, @"\EFI\BOOT\BOOTX64.EFI", null);
        CopyMember(selected.Grub, @"\EFI\BOOT\grubx64.efi", null);
        CopyMember(selected.Shim, @"\igloo-boot\shimx64.efi", null);
        CopyMember(selected.Grub, @"\igloo-boot\grubx64.efi", null);
        foreach (var extra in bootSpec.ExtraIsoFiles)
        {
            if (string.IsNullOrWhiteSpace(extra.IsoRelativePath) ||
                string.IsNullOrWhiteSpace(extra.OemDrvRelativePath))
                throw new InvalidDataException("The reviewed stage2 path is absent.");
            CopyMember(extra.IsoRelativePath,
                "\\" + extra.OemDrvRelativePath.Replace('/', '\\'),
                checked((long)intent.Media.Stage2Bytes));
        }
        source.VerifyPin(ct);
        return copied;

        void CopyMember(string isoMember, string destination, long? expectedBytes)
        {
            ct.ThrowIfCancellationRequested();
            var extracted = source.Extract(isoMember, expectedBytes, ct);
            try
            {
                var file = CopyNew(content.Storage, PreparationRole.Payload,
                    destination, extracted.TemporaryPath, extracted.SizeBytes,
                    extracted.Sha256, ct);
                copied.Add(file);
                onCopied?.Invoke(file);
            }
            finally
            {
                File.Delete(extracted.TemporaryPath);
            }
        }
    }

    internal static (string Shim, string Grub) FindInstallerEfiPair(
        ReviewedPinnedIsoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // The firmware loader and GRUB must be a pair from one ISO directory.
        var pairs = new (string Shim, string Grub)[]
        {
            ("EFI/fedora/shimx64.efi", "EFI/fedora/grubx64.efi"),
            ("EFI/debian/shimx64.efi", "EFI/debian/grubx64.efi"),
            ("EFI/ubuntu/shimx64.efi", "EFI/ubuntu/grubx64.efi"),
            ("EFI/BOOT/shimx64.efi", "EFI/BOOT/grubx64.efi"),
            ("EFI/BOOT/BOOTX64.EFI", "EFI/BOOT/grubx64.efi"),
        };
        var selected = source.FirstAvailablePair(pairs);
        if (selected is null)
            throw new InvalidDataException("The pinned ISO lacks a supported shim and GRUB pair.");
        return selected.Value;
    }

    internal CanonicalFileIdentityV1 CopyPinnedKernel(ReviewedContentHandoffV1 content,
        InstallerBootSpec bootSpec, string kernelPath, CancellationToken ct = default)
    {
        RequirePinnedExternalMedia(content, bootSpec);
        var checkpoint = _reviewed.ReopenCreatedCheckpoint(content.Storage.Checkpoint);
        var expectedLength = checkpoint.Plan.ReviewedIntent?.Media.KernelBytes;
        var source = new FileInfo(kernelPath);
        if (!source.Exists || (source.Attributes & FileAttributes.ReparsePoint) != 0 ||
            expectedLength is null or 0 or > long.MaxValue ||
            source.Length != (long)expectedLength)
            throw new InvalidDataException("The pinned installer kernel is unavailable.");
        return CopyNew(content.Storage, PreparationRole.Payload, @"\igloo-boot\linux",
            kernelPath, source.Length, bootSpec.KernelSha256!, ct);
    }

    internal CanonicalFileIdentityV1 CopyPinnedInstallerKernel(
        ReviewedContentHandoffV1 content, InstallerBootSpec bootSpec,
        string pinnedIsoPath, string? pinnedKernelPath, CancellationToken ct = default)
    {
        if (pinnedKernelPath is not null)
            return CopyPinnedKernel(content, bootSpec, pinnedKernelPath, ct);
        RequirePinnedMediaRecipe(content, bootSpec, null, null);
        var intent = _reviewed.ReopenCreatedCheckpoint(content.Storage.Checkpoint)
            .Plan.ReviewedIntent!;
        if (intent.Media.FullIsoBytes is 0 or > long.MaxValue ||
            intent.Media.KernelBytes is 0 or > long.MaxValue)
            throw new InvalidDataException("The ISO-contained installer kernel has no measured size.");
        using var source = new ReviewedPinnedIsoSource(pinnedIsoPath,
            (long)intent.Media.FullIsoBytes, intent.ArtifactSha256, ct);
        var member = source.FirstAvailable(bootSpec.KernelIsoPaths) ??
            throw new InvalidDataException("The pinned ISO has no declared installer kernel.");
        var extracted = source.Extract(member, (long)intent.Media.KernelBytes, ct);
        try
        {
            source.VerifyPin(ct);
            var copied = CopyNew(content.Storage, PreparationRole.Payload,
                @"\igloo-boot\linux", extracted.TemporaryPath,
                extracted.SizeBytes, extracted.Sha256, ct);
            source.VerifyPin(ct);
            return copied;
        }
        finally { File.Delete(extracted.TemporaryPath); }
    }

    internal CanonicalFileIdentityV1 CopyInjectedPinnedInitrd(ReviewedContentHandoffV1 content,
        InstallerBootSpec bootSpec, string rawInitrdPath, CancellationToken ct = default)
    {
        RequirePinnedExternalMedia(content, bootSpec);
        if (bootSpec.ConfigDelivery != ConfigDelivery.InjectIntoInitrd)
            throw new InvalidDataException("The installer has no reviewed initrd injection recipe.");
        var boot = DirectInstallService.BuildReviewedBootPayload(content, bootSpec, ct);
        var checkpoint = _reviewed.ReopenCreatedCheckpoint(content.Storage.Checkpoint);
        var rawLength = checkpoint.Plan.ReviewedIntent?.Media.InitrdBytes;
        if (rawLength is null or 0 or > long.MaxValue)
            throw new InvalidDataException("The reviewed initrd has no measured raw source size.");
        return WithPinnedInjectedInitrd(boot, bootSpec, rawInitrdPath, (long)rawLength,
            (temporary, size, digest) => CopyNew(content.Storage, PreparationRole.Payload,
                @"\igloo-boot\initrd", temporary, size, digest, ct), ct);
    }

    internal CanonicalFileIdentityV1 CopyPinnedInstallerInitrd(
        ReviewedContentHandoffV1 content, InstallerBootSpec bootSpec,
        string pinnedIsoPath, string? pinnedRawInitrdPath, CancellationToken ct = default)
    {
        if (pinnedRawInitrdPath is null)
            RequirePinnedMediaRecipe(content, bootSpec, null, null);
        else
            RequirePinnedExternalMedia(content, bootSpec);
        if ((bootSpec.KernelUrl is null) != (pinnedRawInitrdPath is null))
            throw new InvalidDataException("The installer initrd source differs from the selected provider recipe.");
        var intent = _reviewed.ReopenCreatedCheckpoint(content.Storage.Checkpoint)
            .Plan.ReviewedIntent!;
        if (intent.Media.InitrdBytes is 0 or > long.MaxValue ||
            intent.Media.FullIsoBytes is 0 or > long.MaxValue)
            throw new InvalidDataException("The installer initrd has no measured source size.");
        if (pinnedRawInitrdPath is not null)
        {
            if (bootSpec.ConfigDelivery == ConfigDelivery.InjectIntoInitrd)
                return CopyInjectedPinnedInitrd(content, bootSpec,
                    pinnedRawInitrdPath, ct);
            var derived = MeasurePinnedInstallerInitrd(
                DirectInstallService.BuildReviewedBootPayload(content, bootSpec, ct),
                bootSpec, pinnedIsoPath, pinnedRawInitrdPath,
                (long)intent.Media.FullIsoBytes, (long)intent.Media.InitrdBytes, ct);
            return CopyNew(content.Storage, PreparationRole.Payload,
                @"\igloo-boot\initrd", pinnedRawInitrdPath,
                derived.SizeBytes, derived.Sha256, ct);
        }
        using var source = new ReviewedPinnedIsoSource(pinnedIsoPath,
            (long)intent.Media.FullIsoBytes, intent.ArtifactSha256, ct);
        var member = source.FirstAvailable(bootSpec.InitrdIsoPaths) ??
            throw new InvalidDataException("The pinned ISO has no declared installer initrd.");
        var extracted = source.Extract(member, (long)intent.Media.InitrdBytes, ct);
        try
        {
            source.VerifyPin(ct);
            var copied = bootSpec.ConfigDelivery == ConfigDelivery.InjectIntoInitrd
                ? WithMeasuredInjectedInitrd(
                    DirectInstallService.BuildReviewedBootPayload(content, bootSpec, ct),
                    extracted.TemporaryPath, extracted.SizeBytes, extracted.Sha256,
                    (temporary, size, digest) => CopyNew(content.Storage,
                        PreparationRole.Payload, @"\igloo-boot\initrd",
                        temporary, size, digest, ct), ct)
                : CopyNew(content.Storage, PreparationRole.Payload,
                    @"\igloo-boot\initrd", extracted.TemporaryPath,
                    extracted.SizeBytes, extracted.Sha256, ct);
            source.VerifyPin(ct);
            return copied;
        }
        finally { File.Delete(extracted.TemporaryPath); }
    }

    internal static (long SizeBytes, string Sha256) MeasurePinnedInstallerInitrd(
        ReviewedBootPayloadV1 boot, InstallerBootSpec bootSpec,
        string pinnedIsoPath, string? pinnedRawInitrdPath,
        long expectedIsoBytes, long expectedRawBytes, CancellationToken ct = default)
    {
        if (pinnedRawInitrdPath is not null)
        {
            if (bootSpec.ConfigDelivery == ConfigDelivery.InjectIntoInitrd)
                return MeasurePinnedInjectedInitrd(boot, bootSpec,
                    pinnedRawInitrdPath, expectedRawBytes, ct);
            if (expectedRawBytes <= 0 ||
                bootSpec.InitrdSha256 is not { Length: 64 } ||
                !bootSpec.InitrdSha256.All(Uri.IsHexDigit) ||
                !Path.IsPathFullyQualified(pinnedRawInitrdPath))
                throw new InvalidDataException("The external installer initrd has no exact media pin.");
            var raw = new FileInfo(pinnedRawInitrdPath);
            if (!raw.Exists || (raw.Attributes & FileAttributes.ReparsePoint) != 0 ||
                raw.Length != expectedRawBytes ||
                !string.Equals(HashFile(raw.FullName, ct), bootSpec.InitrdSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The external installer initrd changed.");
            return (raw.Length, bootSpec.InitrdSha256.ToUpperInvariant());
        }
        using var source = new ReviewedPinnedIsoSource(pinnedIsoPath,
            expectedIsoBytes, bootSpec.IsoSha256 ?? "", ct);
        var member = source.FirstAvailable(bootSpec.InitrdIsoPaths) ??
            throw new InvalidDataException("The pinned ISO has no declared installer initrd.");
        var measured = source.HashMember(member, expectedRawBytes, ct);
        if (bootSpec.ConfigDelivery != ConfigDelivery.InjectIntoInitrd)
        {
            source.VerifyPin(ct);
            return (measured.SizeBytes, measured.Sha256);
        }
        var extracted = source.Extract(member, expectedRawBytes, ct);
        try
        {
            var derived = WithMeasuredInjectedInitrd(boot, extracted.TemporaryPath,
                expectedRawBytes, measured.Sha256,
                (_, size, digest) => (size, digest), ct);
            source.VerifyPin(ct);
            return derived;
        }
        finally { File.Delete(extracted.TemporaryPath); }
    }

    // A copied initrd receipt is source-bound only if its complete bytes equal
    // the pinned raw installer initrd followed by the exact reviewed CPIO.
    internal static (long SizeBytes, string Sha256) MeasurePinnedInjectedInitrd(
        ReviewedBootPayloadV1 boot, InstallerBootSpec bootSpec, string rawInitrdPath,
        long expectedRawBytes, CancellationToken ct = default) =>
        WithPinnedInjectedInitrd(boot, bootSpec, rawInitrdPath, expectedRawBytes,
            (_, size, digest) => (size, digest), ct);

    private static T WithPinnedInjectedInitrd<T>(ReviewedBootPayloadV1 boot,
        InstallerBootSpec bootSpec, string rawInitrdPath, long expectedRawBytes,
        Func<string, long, string, T> consume, CancellationToken ct) =>
        WithMeasuredInjectedInitrd(boot, rawInitrdPath, expectedRawBytes,
            bootSpec.InitrdSha256 ?? "", consume, ct);

    private static T WithMeasuredInjectedInitrd<T>(ReviewedBootPayloadV1 boot,
        string rawInitrdPath, long expectedRawBytes, string expectedRawSha256,
        Func<string, long, string, T> consume, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(boot);
        if (boot.InitrdFiles.IsDefaultOrEmpty || expectedRawBytes <= 0 ||
            expectedRawSha256 is not { Length: 64 } ||
            !expectedRawSha256.All(Uri.IsHexDigit) ||
            !Path.IsPathFullyQualified(rawInitrdPath))
            throw new InvalidDataException("The reviewed initrd has no bound raw source or installer config.");
        var temporary = Path.Join(Path.GetTempPath(),
            "igloo-reviewed-initrd-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var raw = new FileInfo(rawInitrdPath);
            if (!raw.Exists || (raw.Attributes & FileAttributes.ReparsePoint) != 0 ||
                raw.Length != expectedRawBytes ||
                !string.Equals(HashFile(raw.FullName, ct), expectedRawSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded installer initrd differs from its pin.");
            File.Copy(raw.FullName, temporary);
            if (!string.Equals(HashFile(temporary, ct), expectedRawSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installer initrd changed while making the injection source.");
            DirectInstallService.AppendFilesToInitrd(temporary, boot.InitrdFiles
                .Select(file => (file.Name, file.Contents.ToArray())).ToArray());
            var result = new FileInfo(temporary);
            var digest = HashFile(temporary, ct);
            return consume(temporary, result.Length, digest);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RequirePinnedMediaRecipe(ReviewedContentHandoffV1 content,
        InstallerBootSpec bootSpec, string? pinnedKernelPath, string? pinnedRawInitrdPath)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(bootSpec);
        DirectInstallService.ValidateExternalInstallerMedia(bootSpec);
        var external = bootSpec.KernelUrl is not null && bootSpec.InitrdUrl is not null;
        if (!string.Equals(content.BootSpecSha256,
                ReviewedPartitionCreationService.HashBootSpec(bootSpec), StringComparison.Ordinal) ||
            external != (pinnedKernelPath is not null && pinnedRawInitrdPath is not null) ||
            (external && (!Path.IsPathFullyQualified(pinnedKernelPath!) ||
                !Path.IsPathFullyQualified(pinnedRawInitrdPath!))) ||
            (!external && (pinnedKernelPath is not null ||
                pinnedRawInitrdPath is not null ||
                bootSpec.KernelIsoPaths.Count == 0 ||
                bootSpec.InitrdIsoPaths.Count == 0)))
            throw new InvalidDataException("The installer media sources differ from the reviewed boot recipe.");
    }

    private static void RequirePinnedExternalMedia(ReviewedContentHandoffV1 content,
        InstallerBootSpec bootSpec)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(bootSpec);
        DirectInstallService.ValidateExternalInstallerMedia(bootSpec);
        if (bootSpec.KernelUrl is null || bootSpec.InitrdUrl is null ||
            !string.Equals(content.BootSpecSha256,
                ReviewedPartitionCreationService.HashBootSpec(bootSpec), StringComparison.Ordinal))
            throw new InvalidDataException("The external installer media differ from the reviewed boot recipe.");
    }

    internal CanonicalFileIdentityV1 CopyNew(ReviewedInstallerHandoffV1 storage,
        PreparationRole role, string relativePath, string sourcePath,
        long expectedLength, string expectedSha256, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(storage);
        if (role is not (PreparationRole.Payload or PreparationRole.Iso or PreparationRole.LinuxEsp) ||
            (role == PreparationRole.Iso && storage.Iso is null) ||
            !CanonicalRecoveryIdentity.IsValid(storage.Payload) ||
            !CanonicalRecoveryIdentity.IsValid(storage.LinuxEsp) ||
            storage.Payload.Disk != storage.TargetDisk ||
            storage.LinuxEsp.Disk != storage.TargetDisk ||
            (expectedLength < 0 || expectedLength == 0 &&
                (role != PreparationRole.Payload ||
                 !ReviewedSeedPathRules.AllowsEmptySelectedData(relativePath))) ||
            expectedSha256 is not { Length: 64 } ||
            !expectedSha256.All(Uri.IsHexDigit) ||
            !Path.IsPathFullyQualified(sourcePath))
            throw new InvalidDataException("The reviewed seed file has no exact volume or source binding.");
        if (role == PreparationRole.LinuxEsp)
            RequireLinuxEspPath(relativePath);
        var destinationVolume = role switch
        {
            PreparationRole.Payload => storage.Payload,
            PreparationRole.Iso => storage.Iso!,
            PreparationRole.LinuxEsp => storage.LinuxEsp,
            _ => throw new InvalidDataException("The reviewed destination role is unsupported."),
        };
        if (destinationVolume.Disk != storage.TargetDisk ||
            !CanonicalRecoveryIdentity.IsValid(destinationVolume) ||
            destinationVolume.SizeBytes > long.MaxValue ||
            expectedLength > (long)destinationVolume.SizeBytes)
            throw new InvalidDataException("The reviewed seed file exceeds its exact destination volume.");
        var segments = SafeSegments(relativePath);
        var relative = "\\" + string.Join('\\', segments);
        var source = new FileInfo(sourcePath);
        if (!source.Exists || (source.Attributes & FileAttributes.ReparsePoint) != 0 ||
            source.Length != expectedLength ||
            !string.Equals(HashFile(source.FullName, ct), expectedSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reviewed seed source changed before copy.");

        if (_reviewed.ObserveInstallerHandoff(storage.Checkpoint, ct) != storage)
            throw new InvalidOperationException("The exact reviewed storage changed before seed copy.");

        var before = _readFile(destinationVolume.VolumeGuid, relative);
        if (before.Availability != ObservationAvailability.Absent)
            throw new InvalidOperationException("The reviewed seed destination is occupied or cannot be proven empty.");
        var root = _volumeRoot(destinationVolume.VolumeGuid);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidDataException("The reviewed volume has no exact writable path.");
        RequirePlainDirectory(root);
        var parent = root;
        foreach (var segment in segments.SkipLast(1))
        {
            parent = Path.Join(parent, segment);
            Directory.CreateDirectory(parent);
            RequirePlainDirectory(parent);
        }
        RequireNoInterruptedCopy(root, segments);
        var destination = Path.Join(parent, segments[^1]);
        // Introduce the final path only after a complete, independently
        // read-back same-volume copy. A canceled stream has no final path.
        var temporaryRelative = relative + ".igloo-copy-" + Guid.NewGuid().ToString("N") + ".tmp";
        _ = SafeSegments(temporaryRelative);
        var temporary = Path.Join(parent, Path.GetFileName(temporaryRelative));
        var temporaryCreated = false;
        var moved = false;
        try
        {
            using (var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read,
                       FileShare.Read, 1024 * 1024))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 1024 * 1024))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                temporaryCreated = true;
                var buffer = new byte[1024 * 1024];
                long copied = 0;
                int count;
                while ((count = input.Read(buffer)) != 0)
                {
                    ct.ThrowIfCancellationRequested();
                    copied = checked(copied + count);
                    if (copied > expectedLength)
                        throw new InvalidDataException("The reviewed seed source grew during copy.");
                    hash.AppendData(buffer, 0, count);
                    output.Write(buffer, 0, count);
                }
                output.Flush(flushToDisk: true);
                if (copied != expectedLength ||
                    !string.Equals(Convert.ToHexString(hash.GetHashAndReset()),
                        expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The reviewed seed source changed during copy.");
            }
            var temporaryExpected = new CanonicalFileIdentityV1(
                destinationVolume.VolumeGuid, temporaryRelative, expectedLength,
                expectedSha256.ToUpperInvariant());
            var temporaryObserved = _readFile(destinationVolume.VolumeGuid,
                temporaryRelative);
            if (temporaryObserved.Availability != ObservationAvailability.Available ||
                temporaryObserved.Value != temporaryExpected)
                throw new InvalidDataException("The reviewed temporary copy failed exact-volume readback.");
            if (_reviewed.ObserveInstallerHandoff(storage.Checkpoint, ct) != storage ||
                _readFile(destinationVolume.VolumeGuid, relative).Availability !=
                    ObservationAvailability.Absent)
                throw new InvalidOperationException("The reviewed destination changed before the completed file rename.");
            File.Move(temporary, destination, overwrite: false);
            moved = true;
            var after = _readFile(destinationVolume.VolumeGuid, relative);
            var expected = new CanonicalFileIdentityV1(destinationVolume.VolumeGuid, relative,
                expectedLength, expectedSha256.ToUpperInvariant());
            if (after.Availability != ObservationAvailability.Available || after.Value != expected)
                throw new InvalidDataException("The reviewed seed file did not match independent volume readback.");
            if (_reviewed.ObserveInstallerHandoff(storage.Checkpoint, ct) != storage)
                throw new InvalidOperationException("The exact reviewed storage changed during seed copy.");
            return expected;
        }
        catch (Exception original)
        {
            if (temporaryCreated)
                try
                {
                    if (moved)
                        CleanupReviewedCompletedCopy(storage, destinationVolume,
                            root, parent, destination, relative, expectedLength,
                            expectedSha256);
                    else
                        CleanupReviewedTemporary(storage, destinationVolume,
                            root, parent, temporary, temporaryRelative);
                }
                catch (Exception restoration)
                {
                    if (role == PreparationRole.LinuxEsp)
                        throw new ReviewedLinuxEspRestorationException(original, restoration);
                    throw new ReviewedSeedRestorationException(original, restoration);
                }
            throw;
        }
    }

    private void CleanupReviewedTemporary(ReviewedInstallerHandoffV1 storage,
        CanonicalVolumeIdentityV1 volume, string expectedRoot, string parent,
        string temporary, string temporaryRelative)
    {
        if (_reviewed.ObserveInstallerHandoff(storage.Checkpoint) != storage ||
            !string.Equals(_volumeRoot(volume.VolumeGuid), expectedRoot,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The reviewed volume changed before temporary-copy cleanup.");
        var observed = _readFile(volume.VolumeGuid, temporaryRelative);
        if (observed.Availability == ObservationAvailability.Absent) return;
        if (observed.Availability != ObservationAvailability.Available ||
            observed.Value.VolumeGuid != volume.VolumeGuid ||
            observed.Value.RelativePath != temporaryRelative)
            throw new InvalidDataException("The reviewed temporary file has no exact-volume identity for cleanup.");
        RequirePlainDirectory(expectedRoot);
        RequirePlainDirectory(parent);
        var file = new FileInfo(temporary);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The reviewed temporary file became unavailable or redirected.");
        File.Delete(temporary);
        if (_readFile(volume.VolumeGuid, temporaryRelative).Availability !=
            ObservationAvailability.Absent)
            throw new InvalidOperationException("The reviewed temporary file remains after cleanup.");
    }

    private void CleanupReviewedCompletedCopy(ReviewedInstallerHandoffV1 storage,
        CanonicalVolumeIdentityV1 volume, string expectedRoot, string parent,
        string destination, string relative, long length, string sha256)
    {
        if (_reviewed.ObserveInstallerHandoff(storage.Checkpoint) != storage ||
            !string.Equals(_volumeRoot(volume.VolumeGuid), expectedRoot,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The reviewed volume changed before completed-copy cleanup.");
        var observed = _readFile(volume.VolumeGuid, relative);
        if (observed.Availability == ObservationAvailability.Absent) return;
        var expected = new CanonicalFileIdentityV1(volume.VolumeGuid, relative,
            length, sha256.ToUpperInvariant());
        if (observed.Availability != ObservationAvailability.Available || observed.Value != expected)
            throw new InvalidDataException("The completed seed file changed before cleanup; deletion is blocked.");
        RequirePlainDirectory(expectedRoot);
        RequirePlainDirectory(parent);
        var file = new FileInfo(destination);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The completed seed file became unavailable or redirected.");
        File.Delete(destination);
        if (_readFile(volume.VolumeGuid, relative).Availability != ObservationAvailability.Absent)
            throw new InvalidOperationException("The completed seed file remains after cleanup.");
    }

    private static string HashFile(string path, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int count;
        while ((count = file.Read(buffer)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string[] SafeSegments(string relative)
    {
        if (!ReviewedSeedPathRules.TrySegments(relative, out var segments))
            throw new InvalidDataException("The reviewed seed destination path is unsafe.");
        return segments;
    }

    private static void RequirePlainDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The reviewed seed volume path is unavailable or redirected.");
    }
}
