using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityPreparation;
using Igloo.Preflight.CommunityRecovery;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Igloo.Preflight;

[SupportedOSPlatform("windows")]
public sealed partial class DirectInstallService : IDirectInstallService, IReviewedDirectInstallService
{
    private const long MiB = 1024L * 1024;
    // Overhead on top of the measured squashfs + kernel + initrd sizes.
    // Covers: EFI binaries (~3 MB), grub.cfg, kickstart, agent payload, FAT32
    // filesystem metadata, and 512 MiB headroom so FAT32 is never full-to-the-brim.
    private const long PartitionOverheadBytes = 512L * MiB;
    // FAT32 cannot store a single file ≥ 4 GiB. An ISO at/above this goes on its
    // own NTFS partition instead of the FAT32 seed partition (Ubuntu desktop is
    // ~6 GB; Mint/Debian/Fedora artefacts stay under the limit).
    private const long Fat32MaxFileBytes = 4L * 1024 * 1024 * 1024;
    private const long IsoPartitionOverheadBytes = 256L * MiB;
    private const string IsoPartitionLabel = "IGLOOISO";

    // Boot chain:
    //
    //   UEFI firmware
    //     → \igloo-boot\shimx64.efi    (selected ISO's UEFI shim)
    //     → \igloo-boot\grubx64.efi    (selected ISO's GRUB loader)
    //     → \EFI\fedora\grub.cfg       (our config; grubx64.efi compiled prefix = /EFI/fedora)
    //     → \igloo-boot\linux          (kernel extracted from ISO onto OEMDRV FAT32)
    //     → \igloo-boot\initrd         (initrd extracted from ISO onto OEMDRV FAT32)
    //     → Anaconda (netinstall initrd) reads ks.cfg from OEMDRV
    //     → installs packages from the internet, partitions disk, sets up GRUB
    //     → GRUB dual-boot menu: Fedora + Windows (os-prober detects Windows)
    //
    // Why netinstall ISO (not live ISO)?
    //   The Fedora KDE Desktop Live ISO does NOT support full kickstart:
    //   Anaconda shows "Configuration not supported" and falls back to interactive.
    //   The netinstall ISO runs Anaconda directly with complete kickstart support -
    //   package selection, storage layout, bootloader config, %post scripts.
    //   End users get a proper GRUB dual-boot menu on every startup.
    //
    // The shim + grub are staged to TWO locations on the OEMDRV FAT32 volume:
    //
    //   \igloo-boot\shimx64.efi  is the target of the one-shot NVRAM Boot#### entry.
    //     Primary path, used by firmware that honours OS-registered boot entries
    //     (VMware, the Fedora reference hardware).
    //   \EFI\BOOT\BOOTX64.EFI  is the UEFI removable/fallback path. Consumer
    //     AMI/Gigabyte firmware silently discards the runtime-registered NVRAM entry
    //     across a reboot (validated on a B650 AORUS board: after reboot the entry
    //     was gone and BootOrder had reset to Windows-only). Such firmware still
    //     boots \EFI\BOOT\BOOTX64.EFI from any FAT volume, via its fallback scan or
    //     one-time boot menu, so the installer stays reachable there.
    //
    // Both copies are always written. The fallback loader is dormant whenever the
    // NVRAM entry survives, so the already-validated configurations boot exactly as
    // before. (A previous comment here claimed Windows write-protects \EFI\BOOT\ on
    // FAT32 even for Administrator; that is not true for a normal data partition we
    // create and assign a letter, confirmed by writing BOOTX64.EFI there directly.)
    //
    // grub.cfg is written to every compiled-in prefix (\EFI\fedora, \EFI\debian,
    // \EFI\ubuntu, \boot\grub, \EFI\BOOT) because grubx64.efi's prefix differs per
    // distro; whichever shim/grub the ISO shipped then finds its config.
    private const string BootDir = "igloo-boot";           // shim, grub, kernel, initrd (NVRAM path)
    private const string ShimFile = "shimx64.efi";         // UEFI entry point from the selected ISO
    private const string GrubFile = "grubx64.efi";         // GRUB loader from the selected ISO
    private const string FallbackBootDir = @"EFI\BOOT";    // UEFI removable/fallback directory
    private const string FallbackBootFile = "BOOTX64.EFI"; // default x64 loader name firmware boots there
    private const string KernelFile = "linux";   // kernel on OEMDRV (under BootDir)
    private const string InitrdFile = "initrd";  // initrd on OEMDRV (under BootDir)

    // EFI/<vendor> covers the Fedora/Debian/Ubuntu grubx64 prefixes; boot/grub covers
    // Ubuntu/Mint casper grub, whose compiled prefix is /boot/grub.
    private static readonly string[] GrubCfgDirs =
        [@"EFI\BOOT", @"EFI\fedora", @"EFI\debian", @"EFI\ubuntu", @"boot\grub"];

    internal static IReadOnlyList<string> ReviewedGrubCfgDirs => Array.AsReadOnly(GrubCfgDirs);

    // Stored by PrepareAsync, consumed by RegisterBootEntryAsync.
    private char? _oemDrvLetter;
    private char? _isoPartitionLetter;   // separate NTFS partition for a >4 GiB ISO

    // Read from the staged manifest; substituted into the kernel command line so
    // debian-installer's localechooser never gets a chance to ask.
    private string _locale = "en_US.UTF-8";
    private string _keymap = "us";
    private int? _diskNumber;
    private uint? _partitionNumber;
    private Observation<CanonicalVolumeIdentityV1>? _preparedBootTarget;
    private readonly DirectInstallPreparationCompletion _preparationCompletion = new();

    // The selected distro's boot recipe (kernel/initrd paths, cmdline, volume
    // label, config-delivery). Set at the start of Prepare; read by the boot
    // helpers so the same pipeline drives Anaconda, debian-installer, subiquity, …
    private InstallerBootSpec _bootSpec = null!;

    private readonly IWindowsStorageReader _storage;
    private readonly IWindowsBcdReader _bcd;
    private readonly IWindowsFirmwareReader _firmware;
    private readonly IPartitionResizeService _resizer;
    private readonly ILogger<DirectInstallService> _logger;
    private readonly CommunityRecoveryBoundary _bootRecovery = new(new WindowsRecoverySnapshotCapture(),
        CommunityRecoveryArtifactStore.ForCurrentUser());

    //   Constructor                              ─

    public DirectInstallService(
        IPartitionResizeService resizer,
        ILogger<DirectInstallService> logger)
        : this(resizer, logger, new WindowsStorageReader()) { }

    public DirectInstallService(IPartitionResizeService resizer, ILogger<DirectInstallService> logger, IWindowsStorageReader storage)
        : this(resizer, logger, storage, new WindowsBcdReader()) { }

    public DirectInstallService(IPartitionResizeService resizer, ILogger<DirectInstallService> logger,
        IWindowsStorageReader storage, IWindowsBcdReader bcd)
        : this(resizer, logger, storage, bcd, WindowsFirmwareReader.Shared) { }

    internal DirectInstallService(IPartitionResizeService resizer, ILogger<DirectInstallService> logger,
        IWindowsStorageReader storage, IWindowsBcdReader bcd, IWindowsFirmwareReader firmware)
    { _resizer = resizer; _logger = logger; _storage = storage; _bcd = bcd; _firmware = firmware; }

    //   IDirectInstallService                         ─

    // The legacy Community OEMDRV workflow remains available. The dedicated Linux ESP
    // writer is still gated inside the reviewed preparation entry point.
    public Observation<bool> PreparationAvailability => Observations.Available(true);

    public async Task<DirectInstallSpaceEstimate> MeasureSpaceAsync(int diskNumber,
        string isoPath, InstallerBootSpec bootSpec, long stagedBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bootSpec);
        ArgumentOutOfRangeException.ThrowIfNegative(stagedBytes);
        ValidateExternalInstallerMedia(bootSpec);
        using var externalMedia = await AcquireExternalInstallerMediaAsync(bootSpec,
            ExternalInstallerMediaCache, null, ct).ConfigureAwait(false);
        // MeasureIsoContent mounts the ISO as a virtual DVD drive. Outside Prepare's
        // suppressor that makes Windows pop an AutoPlay toast for "DVD Drive (E:)" on
        // the Target Disk page, which looks like something went wrong. Same quieting
        // as Prepare, restored on the way out.
        var measured = await Task.Run(() =>
        {
            using var shellQuiet = new ShellNoiseSuppressor(_logger);
            return MeasureIsoContent(isoPath, bootSpec, externalMedia);
        }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var shrinkable = await _resizer.GetShrinkableSpaceAsync(diskNumber, ct).ConfigureAwait(false);
        // Free space that already exists on the disk (a removed Linux install, an
        // earlier run) is usable without shrinking Windows at all. Offer whichever
        // route gives Linux more room; Prepare picks the same route.
        var freeExtent = LargestFreeExtentBytes(diskNumber);
        if (freeExtent > shrinkable)
        {
            _logger.LogInformation("Disk {Disk} has {GiB:N1} GiB of unallocated space; using it instead of shrinking Windows ({ShrinkGiB:N1} GiB shrinkable)",
                diskNumber, freeExtent / (1024.0 * 1024 * 1024), shrinkable / (1024.0 * 1024 * 1024));
            shrinkable = freeExtent;
        }
        if (shrinkable <= 0)
            throw new InvalidOperationException("Windows could not report available shrink space for the selected boot volume.");
        return PlanSpaceEstimate(bootSpec, shrinkable, new FileInfo(isoPath).Length,
            measured.kernelBytes, measured.initrdBytes, measured.installImgBytes, stagedBytes);
    }

    internal static DirectInstallSpaceEstimate PlanSpaceEstimate(InstallerBootSpec bootSpec,
        long shrinkableBytes, long fullIsoBytes, long kernelBytes, long initrdBytes,
        long installImgBytes, long stagedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(shrinkableBytes);
        var size = PlanStorageSizes(bootSpec, 0, fullIsoBytes, kernelBytes, initrdBytes,
            installImgBytes, stagedBytes);
        // The package is copied to LocalApplicationData before Windows is shrunk.
        // Keep its Windows copy as well as its copy on the installer partition.
        return new(shrinkableBytes, size.TotalShrinkBytes, stagedBytes);
    }

    public Task PrepareAsync(
        int diskNumber, long linuxSizeBytes, string isoPath, string stagingDirectory,
        InstallerBootSpec bootSpec,
        Uri? stage2Url = null,
        IProgress<DirectInstallProgress>? progress = null, CancellationToken ct = default)
        => _preparationCompletion.PrepareAsync(ResetPreparedBootState,
            () => Prepare(diskNumber, linuxSizeBytes, isoPath, stagingDirectory, bootSpec, stage2Url, progress, ct),
            CapturePreparedBootState, ct);

    public Task RegisterBootEntryAsync(
        IProgress<DirectInstallProgress>? progress = null, CancellationToken ct = default)
        => _preparationCompletion.RegisterAsync(CapturePreparedBootState,
            completed => RegisterBootEntry(completed, progress, ct), ct);

    private void ResetPreparedBootState()
    {
        _diskNumber = null;
        _partitionNumber = null;
        _oemDrvLetter = null;
        _isoPartitionLetter = null;
        _preparedBootTarget = null;
        _bootSpec = null!;
    }

    private CompletedDirectInstallPreparation? CapturePreparedBootState() =>
        _diskNumber is { } disk && _partitionNumber is { } partition && _oemDrvLetter is { } letter &&
        _preparedBootTarget is { Availability: ObservationAvailability.Available } target
            ? new(disk, partition, letter, target.Value) : null;

    // Explicit reviewed entry. Reopen the exact checkpoint and remeasure every staged
    // file before the execution gate. A plan alone cannot enter this path, and no
    // ordinal, drive letter or label is accepted as source authority.
    internal static Task PrepareReviewedAsync(ReviewedPartitionCreationService reviewed,
        ReviewedContentHandoffV1 expectedContent,
        PreparationCheckpointReference stagedReference, InstallerBootSpec bootSpec,
        long requestedLinuxBytes, string pinnedIsoPath, string? pinnedRawInitrdPath,
        Func<Guid, string, Observation<CanonicalFileIdentityV1>>? readFile = null,
        ReviewedLinuxEspBootBindingV1? expectedBootBinding = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reviewed);
        ArgumentNullException.ThrowIfNull(expectedContent);
        ArgumentNullException.ThrowIfNull(expectedContent.Storage);
        ArgumentNullException.ThrowIfNull(stagedReference);
        ArgumentNullException.ThrowIfNull(bootSpec);
        if (string.IsNullOrWhiteSpace(expectedContent.StagingDirectory) ||
            expectedContent.StagedFiles.IsDefaultOrEmpty)
            throw new InvalidDataException("The reviewed content handoff is incomplete.");
        var actual = reviewed.ObserveContentHandoff(expectedContent.Storage.Checkpoint,
            expectedContent.Storage, bootSpec, requestedLinuxBytes,
            expectedContent.StagingDirectory, ct);
        if (actual.Storage != expectedContent.Storage ||
            !string.Equals(actual.StagingDirectory, expectedContent.StagingDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            actual.StagedBytes != expectedContent.StagedBytes ||
            actual.Sizing != expectedContent.Sizing ||
            !string.Equals(actual.BootSpecSha256, expectedContent.BootSpecSha256,
                StringComparison.Ordinal) ||
            !actual.StagedFiles.SequenceEqual(expectedContent.StagedFiles))
            throw new InvalidDataException("The reviewed content handoff changed before preparation.");
        // Materialize the exact initrd inputs and Linux ESP boot argument from
        // the pinned handoff before reaching the still-closed Windows write gate.
        // The future reviewed writer must reobserve before copying these bytes.
        _ = BuildReviewedBootPayload(actual, bootSpec, ct);
        _ = reviewed.VerifyPersistedReviewedSeed(stagedReference, actual.Storage,
            actual, bootSpec, pinnedIsoPath, pinnedRawInitrdPath, readFile, ct);
        if (expectedBootBinding is not null)
        {
            // The existing ESP copier emits exact file receipts. Rebind them to
            // the current seed and independently read every volume-GUID path
            // before considering a boot step. This remains read-only and does
            // not turn the receipt into recovery or execution authority.
            if (expectedBootBinding.Storage != actual.Storage ||
                expectedBootBinding.SeedCheckpoint != stagedReference)
                throw new InvalidDataException("The reviewed Linux ESP boot binding changed.");
            var writer = readFile is null
                ? new ReviewedSeedFileWriter(reviewed)
                : new ReviewedSeedFileWriter(reviewed,
                    volume => @"\\?\Volume{" + volume.ToString("D") + @"}\", readFile);
            ReviewedLinuxEspFileCopyReceiptV1 Receipt(CanonicalFileIdentityV1 file) =>
                new(actual.Storage.GenerationId, actual.Storage.TargetDisk,
                    actual.Storage.LinuxEsp, file);
            var observed = writer.BindReviewedLinuxEspBootFiles(actual, stagedReference,
                bootSpec, pinnedIsoPath, pinnedRawInitrdPath,
                Receipt(expectedBootBinding.Shim), Receipt(expectedBootBinding.Grub),
                Receipt(expectedBootBinding.Config),
                expectedBootBinding.CompiledPrefixConfig is { } prefix
                    ? Receipt(prefix) : null, ct);
            if (observed != expectedBootBinding)
                throw new InvalidDataException("The reviewed Linux ESP boot binding changed during readback.");
        }
        DedicatedEspPreparationSupport.RequireSupported();
        throw new NotSupportedException("ReviewedWindowsPartitionWriterNotImplemented");
    }

    internal static Observation<bool> ReviewPreparedIntent(PreparationPlanV1 plan,
        InstallerBootSpec bootSpec, long requestedLinuxBytes)
    {
        ArgumentNullException.ThrowIfNull(bootSpec);
        var verified = PreparedLayoutRules.VerifyReviewedIntent(plan);
        if (verified.Availability != ObservationAvailability.Available) return verified;
        var parts = plan.Space.Allocations;
        var intent = plan.ReviewedIntent!;
        if (!string.Equals(bootSpec.IsoSha256, intent.ArtifactSha256,
                StringComparison.OrdinalIgnoreCase))
            return Observations.Failure<bool>(ObservationAvailability.Ambiguous,
                "ReviewedArtifactChanged");
        DirectInstallSizePlan measuredMedia;
        try
        {
            measuredMedia = PlanStorageSizes(bootSpec, requestedLinuxBytes,
                checked((long)intent.Media.FullIsoBytes), checked((long)intent.Media.KernelBytes),
                checked((long)intent.Media.InitrdBytes), checked((long)intent.Media.Stage2Bytes));
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        {
            return Observations.Failure<bool>(ObservationAvailability.Ambiguous, "ReviewedMediaSizingInvalid");
        }
        var payload = parts.Single(p => p.Role == PreparationRole.Payload);
        var root = parts.Single(p => p.Role == PreparationRole.LinuxRoot);
        var iso = parts.SingleOrDefault(p => p.Role == PreparationRole.Iso);
        if (requestedLinuxBytes <= 0 || measuredMedia.OemDrvBytes <= 0 ||
            measuredMedia.IsoPartitionBytes < 0 || measuredMedia.TotalShrinkBytes <= 0 ||
            payload.SizeBytes < (ulong)measuredMedia.OemDrvBytes ||
            root.SizeBytes < (ulong)requestedLinuxBytes ||
            root.CreateInWindows != bootSpec.PreCreateRootPartition ||
            (measuredMedia.IsoOnOwnPartition ?
                iso is null || iso.SizeBytes < (ulong)measuredMedia.IsoPartitionBytes : iso is not null) ||
            intent.ShrinkBytes < plan.Space.RequiredBytes)
            return Observations.Failure<bool>(ObservationAvailability.Ambiguous, "ReviewedMediaSizingMismatch");
        return Observations.Available(true);
    }

    //   Private - Prepare                           ─

    private void Prepare(
        int diskNumber, long linuxSizeBytes, string isoPath, string stagingDirectory,
        InstallerBootSpec bootSpec, Uri? stage2Url,
        IProgress<DirectInstallProgress>? prog, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Complete network acquisition and pin both files before changing any
        // storage. The same read-locked bytes supply sizing and the final copy.
        using var externalMedia = AcquireExternalInstallerMediaAsync(bootSpec,
            ExternalInstallerMediaCache, prog, ct).GetAwaiter().GetResult();
        // Creating lettered volumes makes Windows announce them: AutoPlay fires and
        // Explorer windows open over the wizard, leaving an OEMDRV window sitting there
        // afterwards. Harmless, but on this screen it looks like something went wrong.
        // Suppressed for the duration and restored on the way out - including when the
        // install throws, hence the using.
        using var shellQuiet = new ShellNoiseSuppressor(_logger);

        _diskNumber = diskNumber;
        _preparedBootTarget = null;
        _bootSpec = bootSpec;
        ValidateExternalInstallerMedia(bootSpec);
        VerifySelectedIso(isoPath, bootSpec.IsoSha256, ct);
        ReadLocaleFromStaging(stagingDirectory);
        // Everything in the staging generation, then the subset that goes onto the
        // seed. User folders and browser profiles stay on NTFS for providers whose
        // first boot reads them there (alpha flow); they never had to fit a FAT32
        // partition, be hashed twice, or be copied a second time on Windows.
        var allStagedFiles = InspectStagingFiles(stagingDirectory, requireFat32Fit: false);
        var stagedFiles = _bootSpec.MigrationDataOnSeed
            ? allStagedFiles
            : allStagedFiles.Where(file => !IsUserDataPath(file.RelativePath)).ToArray();
        foreach (var file in stagedFiles)
            if (file.SizeBytes >= Fat32MaxFileBytes)
                throw new InvalidDataException("A selected migration file exceeds the FAT32 file-size limit.");
        if (!_bootSpec.MigrationDataOnSeed)
            _logger.LogInformation("Seed excludes user data: {Seed} of {All} staged file(s) go onto the installer partition; " +
                "{UserMiB} MiB of selected folders/profiles stay on the Windows volume for first boot",
                stagedFiles.Count, allStagedFiles.Count,
                (allStagedFiles.Sum(file => file.SizeBytes) - stagedFiles.Sum(file => file.SizeBytes)) / MiB);
        var initrdExtras = ReadRequiredInitrdExtras(_bootSpec, stagingDirectory);
        List<(string Name, byte[] Contents)>? initrdFiles = null;
        if (_bootSpec.ConfigDelivery == ConfigDelivery.InjectIntoInitrd)
        {
            initrdFiles = [ReadRequiredInitrdConfig(_bootSpec, stagingDirectory)];
            initrdFiles.AddRange(initrdExtras);
        }

        // Every check that can still refuse runs here, read-only, before the ISO
        // mount and the NTFS shrink (B-02): seed inventory bounds, NVRAM access, a
        // free Boot#### slot, bcdedit, and the storage inventory that pins the seed.
        Report(prog, DirectInstallPhase.ShrinkingPartition, message: "Checking seed limits and boot registration…");
        var stagedAgentPaths = StagedAgentPaths(stagedFiles);
        RequirePlannedSeedWithinBounds(stagingDirectory, stagedFiles, stagedAgentPaths);
        RequireBootRegistrationReadiness(_firmware, _bcd, _logger);
        RequireStorageIdentityReadable(diskNumber);
        ct.ThrowIfCancellationRequested();

        //   Step 1: Measure ISO content to size the OEMDRV partition     ─
        // Mount the ISO just long enough to stat kernel + initrd + install.img sizes.
        // install.img is Anaconda's stage2 squashfs (~870 MiB on Fedora 44).
        // We copy it to OEMDRV so that inst.stage2=hd:LABEL=OEMDRV: avoids
        // downloading 862 MB from a mirror during installation.
        Report(prog, DirectInstallPhase.ShrinkingPartition, message: "Measuring ISO content…");
        var (kernelBytes, initrdBytes, installImgBytes) = MeasureIsoContent(isoPath, bootSpec, externalMedia);
        _logger.LogInformation(
            "ISO content: kernel={K} MiB  initrd={I} MiB  install.img={S} MiB",
            kernelBytes / MiB, initrdBytes / MiB, installImgBytes / MiB);
        ct.ThrowIfCancellationRequested();

        //   Step 2: Shrink Windows partition (skip if OEMDRV already exists)  
        // Distros whose installer loop-mounts the whole ISO (Debian iso-scan,
        // Ubuntu/Mint casper) need room for the ISO too. But the seed/boot
        // partition MUST be FAT32 (UEFI firmware only boots FAT), and FAT32 cannot
        // hold a file ≥ 4 GiB. So an oversized ISO (Ubuntu desktop ~6 GB) goes on
        // a SEPARATE NTFS partition; casper's iso-scan finds the .iso on any
        // partition it can mount. Small ISOs (Mint, Debian hd-media) stay on the
        // single FAT32 partition exactly as before  no behaviour change for them.
        long fullIsoBytes = _bootSpec.CopyFullIsoToVolume ? new FileInfo(isoPath).Length : 0;
        var sizing = PlanStorageSizes(_bootSpec, linuxSizeBytes, fullIsoBytes,
            kernelBytes, initrdBytes, installImgBytes,
            stagedFiles.Sum(file => file.SizeBytes));
        bool isoOnOwnPartition = sizing.IsoOnOwnPartition;
        long oemDrvBytes = sizing.OemDrvBytes;
        long isoPartBytes = sizing.IsoPartitionBytes;
        char driveLetter;

        var seedOutputPaths = SeedOutputPaths(bootSpec, stagedFiles.Select(file => file.RelativePath), isoOnOwnPartition);
        var staleSeeds = new List<char>();
        var existing = FindExistingOemDrv(diskNumber, stagingDirectory, stagedFiles, seedOutputPaths, staleSeeds, ct);

        // The label only discovers candidates. FindExistingOemDrv admits reuse
        // only after exact seed identity, content and protected-partition checks.
        var canReuse = existing is not null && !isoOnOwnPartition
            && InstallerPartitionFits(new DriveInfo($"{existing.Value.letter}:").TotalSize, oemDrvBytes);

        // A seed iGloo itself wrote on an earlier run (another distribution, a
        // smaller layout, an interrupted copy) is disposable: remove it and carve a
        // fresh one, as v0.2-alpha did. Only volumes carrying iGloo's own marker
        // files are ever touched; a foreign OEMDRV volume stops the run instead.
        if (existing is not null && !canReuse)
        {
            staleSeeds.Add(existing.Value.letter);
            existing = null;
        }
        foreach (var stale in staleSeeds.Distinct())
        {
            Report(prog, DirectInstallPhase.ShrinkingPartition, message: "Removing an installer partition from an earlier run…");
            DeleteInstallerVolume(stale);
            ct.ThrowIfCancellationRequested();
        }

        if (canReuse)
        {
            // A previous run already created a large-enough OEMDRV partition on this disk.
            // Skip shrink + partition creation and reuse it - just re-copy the files.
            driveLetter = existing!.Value.letter;
            _oemDrvLetter = driveLetter;
            _partitionNumber = existing.Value.partitionNumber;
            var current = RequireReusableSeedTarget(_storage.ReadIdentitySnapshot(),
                checked((uint)diskNumber), existing.Value.partitionNumber, driveLetter,
                _bootSpec.VolumeLabel);
            if (current != existing.Value.identity)
                throw new InvalidOperationException("The existing installer partition changed before reuse. Its contents have been preserved.");
            _logger.LogInformation(
                "Reusing existing OEMDRV partition {N} at {L}: - skipping shrink and partition creation",
                _partitionNumber, driveLetter);
            Report(prog, DirectInstallPhase.ConfiguringGrub, message: "Reusing existing installer partition…");

            // Ensure the NTFS ISO partition exists too: reuse it, or carve it from
            // the free space the original shrink already set aside for it.
            if (isoOnOwnPartition)
                _isoPartitionLetter = FindExistingIsoPartition(diskNumber)
                                      ?? CreateIsoPartition(diskNumber, isoPartBytes, ct);

            if (_bootSpec.PreCreateRootPartition)
                EnsureRootPartition(diskNumber, ct);
        }
        else
        {
            Report(prog, DirectInstallPhase.ShrinkingPartition, message: "Querying Windows partition…");
            long totalShrink = sizing.TotalShrinkBytes;
            // Last read-only refusal: the new partitions need letters, and diskpart's
            // assign would otherwise fail after the shrink.
            RequireAvailableDriveLetters(isoOnOwnPartition ? 2 : 1);

            // Existing unallocated space that already fits the installer partition
            // plus the Linux allocation is used as is: no shrink. Otherwise Windows is
            // shrunk by the full requirement, as before.
            var freeExtent = LargestFreeExtentBytes(diskNumber);
            if (freeExtent >= totalShrink)
            {
                _logger.LogInformation("Direct install: disk {Disk} already has {GiB:N1} GiB unallocated (need {NeedGiB:N1} GiB) - Windows is not shrunk",
                    diskNumber, freeExtent / (1024.0 * 1024 * 1024), totalShrink / (1024.0 * 1024 * 1024));
                Report(prog, DirectInstallPhase.ShrinkingPartition, message: "Using existing free space; Windows is not resized…");
            }
            else
            {
                _logger.LogInformation("Direct install: shrinking disk {Disk} by {GiB} GiB",
                    diskNumber, linuxSizeBytes / MiB / 1024);
                var shrinkProg = new Progress<string>(msg =>
                    Report(prog, DirectInstallPhase.ShrinkingPartition, message: msg));
                _resizer.ShrinkAsync(diskNumber, totalShrink, shrinkProg, ct).GetAwaiter().GetResult();
            }
            ct.ThrowIfCancellationRequested();

            //   Step 3: Create FAT32 OEMDRV partition (boot + seed)      
            Report(prog, DirectInstallPhase.CreatingPartition, message: "Creating installer partition…");
            driveLetter = CreateOemDrvPartition(diskNumber, oemDrvBytes, ct);
            _oemDrvLetter = driveLetter;
            ct.ThrowIfCancellationRequested();

            //   Step 3b: Create NTFS ISO partition for a >4 GiB ISO      
            if (isoOnOwnPartition)
            {
                Report(prog, DirectInstallPhase.CreatingPartition, message: "Creating ISO partition…");
                _isoPartitionLetter = CreateIsoPartition(diskNumber, isoPartBytes, ct);
                ct.ThrowIfCancellationRequested();
            }

            //   Step 3c: Pre-create the Linux root partition (subiquity path)  
            if (_bootSpec.PreCreateRootPartition)
            {
                Report(prog, DirectInstallPhase.CreatingPartition, message: "Creating Linux partition…");
                EnsureRootPartition(diskNumber, ct);
                ct.ThrowIfCancellationRequested();
            }
        }

        // Pin the newly created or reused seed partition before generating its
        // boot command line. Installer and first boot receive this exact identity;
        // a label lookup alone could select a different OEMDRV volume.
        _preparedBootTarget = WindowsBootRegistrationPlanning.ObservePreparedTarget(_storage,
            checked((uint)diskNumber), _partitionNumber!.Value);
        if (_preparedBootTarget.Availability != ObservationAvailability.Available)
            throw new InvalidOperationException("The prepared installer seed has no exact GPT identity.");

        //   Step 4: Extract boot content from ISO onto OEMDRV         ─
        // Extracts: igloo-boot/linux, igloo-boot/initrd,
        //           igloo-boot/shimx64.efi, igloo-boot/grubx64.efi
        //           images/install.img   (Anaconda stage2, ~870 MiB)
        // Writes:   EFI/fedora/grub.cfg  (points Anaconda at inst.ks=hd:LABEL=OEMDRV:/ks.cfg)
        Report(prog, DirectInstallPhase.ConfiguringGrub, message: "Extracting boot files from ISO…");
        ConfigureBootFiles(isoPath, driveLetter, initrdFiles, initrdBytes, installImgBytes, externalMedia, prog, ct);
        ct.ThrowIfCancellationRequested();

        // Digests of every large output, computed while the bytes were written. The
        // seed identity and the reuse receipt below use them instead of reading the
        // whole partition back (previously: copy, read back for the identity, read
        // back again for the receipt - three passes over the user's data on Windows).
        var writtenHashes = new Dictionary<string, KnownSeedFileHash>(StringComparer.OrdinalIgnoreCase);

        //   Step 4b: Copy the whole ISO (iso-scan / casper need it)      
        // Small ISOs land on the FAT32 seed partition; an oversized ISO lands on
        // its dedicated NTFS partition (see Step 3b). iso-scan finds it either way.
        if (_bootSpec.CopyFullIsoToVolume && _bootSpec.IsoVolumeFileName is { } isoName)
        {
            char isoVolLetter = isoOnOwnPartition ? _isoPartitionLetter!.Value : driveLetter;
            var isoDst = Path.Join($"{isoVolLetter}:\\", isoName);
            Report(prog, DirectInstallPhase.CopyingIso, message: "Copying installer ISO…");
            var isoLength = new FileInfo(isoPath).Length;
            var isoCopied = CopyWithProgress(isoPath, isoDst, isoLength, prog, ct);
            ct.ThrowIfCancellationRequested();
            if (isoCopied.Length != isoLength || new FileInfo(isoDst).Length != isoLength)
                throw new IOException("The installer ISO did not copy completely.");
            // VerifySelectedIso proved the source matches the plugin pin before any disk
            // write; the copy's own digest must agree, or the seed does not carry the ISO
            // that was verified.
            if (!string.Equals(isoCopied.Sha256, bootSpec.IsoSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The installer ISO changed while it was copied to the installer partition.");
            if (!isoOnOwnPartition)
                writtenHashes[isoName.Replace('\\', '/')] = isoCopied;
            _logger.LogInformation("Full ISO copied to {Dst}", isoDst);
        }

        //   Step 5: Copy staging artefacts (ks.cfg, manifest, agent)     ─
        Report(prog, DirectInstallPhase.CopyingFiles, message: "Copying migration files…");
        foreach (var (path, hash) in CopyStagingArtefacts(stagingDirectory, stagedFiles, $"{driveLetter}:\\", prog, ct))
            writtenHashes[path] = hash;
        ct.ThrowIfCancellationRequested();

        // Pin the exact GPT owner after the final seed copy. First boot uses this
        // binding and the hashes of the copied credential inputs to retire only
        // this installer's files, never another OEMDRV found by label.
        var finalSeedTarget = WindowsBootRegistrationPlanning.ObservePreparedTarget(_storage,
            checked((uint)diskNumber), _partitionNumber!.Value);
        if (finalSeedTarget.Availability != ObservationAvailability.Available ||
            finalSeedTarget.Value != _preparedBootTarget.Value)
            throw new InvalidOperationException("The prepared installer seed identity changed during staging.");
        SeedIdentityWriter.WriteAsync($"{driveLetter}:\\",
            _preparedBootTarget.Value.Disk.GptDiskGuid,
            _preparedBootTarget.Value.PartitionGuid, stagedAgentPaths, writtenHashes, ct)
            .GetAwaiter().GetResult();
        var optionalOutputs = bootSpec.ExtraIsoFiles.Where(file => !file.Required)
            .Select(file => file.OemDrvRelativePath.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        WriteSeedReuseReceipt($"{driveLetter}:\\", _preparedBootTarget.Value,
            seedOutputPaths.Where(path => !optionalOutputs.Contains(path) ||
                File.Exists(Path.Join($"{driveLetter}:\\", path.Replace('/', Path.DirectorySeparatorChar)))).ToArray(),
            writtenHashes, ct);

        // Anything the shell managed to open before suppression took hold - or that a
        // user double-click opened - is tidied away here, so the wizard is not left
        // sitting behind a stray OEMDRV window.
        shellQuiet.CloseExplorerWindowsFor(driveLetter);
        if (_isoPartitionLetter is { } isoLetter && isoLetter != driveLetter)
            shellQuiet.CloseExplorerWindowsFor(isoLetter);

        Report(prog, DirectInstallPhase.Complete, message: "Installer partition ready.");
        _logger.LogInformation("Direct install partition prepared on {Letter}:", driveLetter);
    }

    private (long kernelBytes, long initrdBytes, long installImgBytes) MeasureIsoContent(
        string isoPath, InstallerBootSpec bootSpec, PinnedExternalInstallerMedia? externalMedia)
    {
        string? mountedLetter = null;
        try
        {
            mountedLetter = MountIso(isoPath);
            var isoRoot = $"{mountedLetter}:\\";
            long kernelBytes;
            long initrdBytes;
            if (externalMedia is not null)
            {
                kernelBytes = externalMedia.Kernel.Length;
                initrdBytes = externalMedia.Initrd.Length;
            }
            else
            {
                var (kernelSrc, initrdSrc) = FindKernelFiles(isoRoot, bootSpec);
                kernelBytes = new FileInfo(kernelSrc).Length;
                initrdBytes = new FileInfo(initrdSrc).Length;
            }

            // Missing boot or required stage-2 files must stop before resize.
            _ = FindEfiFiles(isoRoot);

            // Sum the distro's declared extra ISO files (e.g. Anaconda's
            // images/install.img stage-2 squashfs). Empty for d-i / subiquity.
            var extraBytes = MeasureRequiredIsoContent(isoRoot, bootSpec);

            return (kernelBytes, initrdBytes, extraBytes);
        }
        finally
        {
            if (mountedLetter is not null)
                DismountIso(isoPath);
        }
    }

    //   Step 2 helpers                             

    private char CreateOemDrvPartition(int diskNumber, long sizeBytes, CancellationToken ct)
    {
        long sizeMiB = RoundUpMiB(sizeBytes) / MiB;
        var letter = FindAvailableDriveLetter();

        _logger.LogInformation("Creating {MiB} MiB FAT32 OEMDRV partition (letter {L}:) on disk {D}",
            sizeMiB, letter, diskNumber);

        // rescan: tells diskpart to re-read the partition table so it sees the space
        // freed by the WMI resize that just completed.
        // align=1024: 1 MiB alignment - required for GPT/UEFI disks; align=1 (1 KB)
        // triggers VDS_E_OPERATION_NOT_SUPPORTED_ON_DISK (0x80042554).
        var script = $"""
            rescan
            select disk {diskNumber}
            create partition primary size={sizeMiB} align=1024
            format fs=fat32 label={_bootSpec.VolumeLabel} quick
            assign letter={letter}
            exit
            """;
        var output = RunDiskpart(script);
        _logger.LogInformation("diskpart output:\n{Output}", output);

        // Wait up to 15 s for the drive to appear.
        var root = $"{letter}:\\";
        for (var i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(root))
                break;
            Thread.Sleep(500);
        }
        if (!Directory.Exists(root))
            throw new InvalidOperationException($"Drive {letter}: did not appear after partition creation.");

        // Record partition number for RegisterBootEntry.
        _partitionNumber = FindPartitionByLetter(diskNumber, letter);
        _logger.LogInformation("OEMDRV partition {N} ready at {L}:", _partitionNumber, letter);
        return letter;
    }

    private static string RunDiskpart(string script)
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, script, Encoding.ASCII);
        try
        {
            var psi = new ProcessStartInfo("diskpart.exe", $"/s \"{tmp}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            return RunInstallerProcessAsync(psi, TimeSpan.FromSeconds(60))
                .GetAwaiter().GetResult();
        }
        finally
        {
            TryDeleteFile(tmp);
        }
    }

    
    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private uint FindPartitionByLetter(int diskNumber, char letter)
    {
        try
        {
            foreach (var p in _storage.ReadPartitions(diskNumber).RowsOrThrow())
            {
                char dl = WmiValues.ToDriveLetter(p["DriveLetter"]);
                if (char.ToUpperInvariant(dl) == char.ToUpperInvariant(letter))
                    return Convert.ToUInt32(p["PartitionNumber"], CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "FindPartitionByLetter failed");
        }
        return 0;
    }

    private (char letter, uint partitionNumber, CanonicalVolumeIdentityV1 identity)? FindExistingOemDrv(
        int diskNumber, string stagingDirectory, IReadOnlyList<StagedFile> stagedFiles,
        IReadOnlyCollection<string> seedOutputPaths, List<char> staleSeeds, CancellationToken ct)
    {
        var incomingPaths = stagedFiles.Select(file => file.RelativePath.Replace('\\', '/'))
            .Concat(Directory.EnumerateDirectories(stagingDirectory, "*", SearchOption.AllDirectories)
                .Select(directory => Path.GetRelativePath(stagingDirectory, directory).Replace('\\', '/') + "/"))
            .ToArray();
        (char letter, uint partitionNumber, CanonicalVolumeIdentityV1 identity)? found = null;
        foreach (var di in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(di.VolumeLabel, _bootSpec.VolumeLabel, StringComparison.OrdinalIgnoreCase))
                continue;

            var letter = char.ToUpperInvariant(di.Name[0]);
            var observed = _storage.ReadIdentitySnapshot();
            if (observed.Availability != ObservationAvailability.Available)
                throw ExistingSeedOwnershipUnavailable();
            var matches = observed.Value.Partitions.Where(p =>
                p.DriveLetter.Availability == ObservationAvailability.Available &&
                char.ToUpperInvariant(p.DriveLetter.Value) == letter).ToArray();
            if (matches.Length != 1 || matches[0].DiskNumber.Availability != ObservationAvailability.Available ||
                matches[0].Number.Availability != ObservationAvailability.Available)
                throw ExistingSeedOwnershipUnavailable();
            if (matches[0].DiskNumber.Value != diskNumber)
                continue;
            if (found is not null)
                throw ExistingSeedOwnershipUnavailable();

            var partition = matches[0].Number.Value;
            // A volume that fails target admission (protected partition, wrong type,
            // not FAT32) stops the run; only content failures below make it disposable.
            _ = RequireReusableSeedTarget(observed, checked((uint)diskNumber), partition, letter,
                _bootSpec.VolumeLabel);
            CanonicalVolumeIdentityV1 identity;
            try
            {
                identity = RequireReusableInstallerSeed(observed, checked((uint)diskNumber),
                    partition, letter, _bootSpec.VolumeLabel, di.RootDirectory.FullName,
                    incomingPaths, seedOutputPaths, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException &&
                                       IsIglooSeedVolume(di.RootDirectory.FullName))
            {
                // Written by iGloo, but not reusable for this run (another distribution,
                // a different layout, an interrupted copy). Disposable - see Prepare.
                _logger.LogInformation(ex, "Existing installer seed at {L}: is from an earlier run and will be replaced", letter);
                staleSeeds.Add(letter);
                continue;
            }
            _logger.LogInformation(
                "Validated existing installer seed at {L}: (partition {N}, disk {D})",
                letter, partition, diskNumber);
            found = (letter, partition, identity);
        }
        return found;
    }

    internal static bool InstallerPartitionFits(long capacityBytes, long requiredBytes)
        => capacityBytes >= requiredBytes;

    // iGloo's own seed volumes carry the seed identity file and the boot directory;
    // a vendor's OEMDRV driver disk carries neither.
    internal static bool IsIglooSeedVolume(string root) =>
        File.Exists(Path.Join(root, SeedIdentityWriter.FileName)) ||
        File.Exists(Path.Join(root, SeedReuseReceiptFileName)) ||
        Directory.Exists(Path.Join(root, BootDir));

    /// <summary>Deletes an installer seed volume iGloo wrote on an earlier run (v0.2-alpha behaviour).</summary>
    private void DeleteInstallerVolume(char letter)
    {
        string label, root;
        try
        {
            var di = new DriveInfo($"{letter}:");
            if (!di.IsReady)
                throw new InvalidOperationException($"Installer volume {letter}: is not ready; refusing to delete it.");
            label = di.VolumeLabel;
            root = di.RootDirectory.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new InvalidOperationException(
                $"Could not confirm volume {letter}: before deletion; aborting rather than risk the wrong partition.", ex);
        }
        if (!string.Equals(label, _bootSpec.VolumeLabel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to delete volume {letter}: - its label '{label}' is not the '{_bootSpec.VolumeLabel}' installer partition that was detected.");
        if (!IsIglooSeedVolume(root))
            throw new InvalidOperationException(
                $"Refusing to delete volume {letter}: - it carries no iGloo installer files, so it may not be ours.");
        var output = RunDiskpart($"""
            select volume {letter}
            delete volume
            exit
            """);
        _logger.LogInformation("Removed installer seed volume {L}: from an earlier run (diskpart output:\n{Output})", letter, output);
    }

    /// <summary>Largest unallocated extent on the disk, in bytes (0 when none or unknown).</summary>
    private long LargestFreeExtentBytes(int diskNumber)
    {
        try
        {
            long diskSize = 0;
            foreach (var disk in _storage.ReadDisks().RowsOrThrow())
                if (Convert.ToInt32(disk["Number"], CultureInfo.InvariantCulture) == diskNumber)
                    diskSize = Convert.ToInt64(disk["Size"], CultureInfo.InvariantCulture);
            var partitions = new List<(long Offset, long Size)>();
            foreach (var p in _storage.ReadPartitions(diskNumber).RowsOrThrow())
                partitions.Add((Convert.ToInt64(p["Offset"], CultureInfo.InvariantCulture),
                    Convert.ToInt64(p["Size"], CultureInfo.InvariantCulture)));
            return LargestFreeExtentBytes(partitions, diskSize);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException
                                   or InvalidCastException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not measure unallocated space on disk {Disk}; assuming none", diskNumber);
            return 0;
        }
    }

    internal static long LargestFreeExtentBytes(IEnumerable<(long Offset, long Size)> partitions, long diskSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        if (diskSizeBytes <= 0) return 0;
        // GPT keeps its backup header and table in the last ~17 KiB; keep a 1 MiB
        // alignment margin on every gap so what is reported is what diskpart can
        // actually allocate.
        const long margin = MiB;
        long cursor = 0, largest = 0;
        foreach (var (offset, size) in partitions.Where(p => p.Size > 0).OrderBy(p => p.Offset))
        {
            if (offset > cursor)
                largest = Math.Max(largest, offset - cursor - margin);
            cursor = Math.Max(cursor, offset + size);
        }
        if (diskSizeBytes > cursor)
            largest = Math.Max(largest, diskSizeBytes - cursor - margin);
        return Math.Max(0, largest);
    }

    private static char FindAvailableDriveLetter()
    {
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();
        var free = CandidateDriveLetters
            .Where(c => !used.Contains(c))
            .Select(c => (char?)c)
            .FirstOrDefault();
        return free ?? throw new InvalidOperationException(
            "No available drive letter for the installer partition.");
    }

    private char CreateIsoPartition(int diskNumber, long sizeBytes, CancellationToken ct)
    {
        long sizeMiB = RoundUpMiB(sizeBytes) / MiB;
        var letter = FindAvailableDriveLetter();

        _logger.LogInformation("Creating {MiB} MiB NTFS ISO partition (letter {L}:) on disk {D}",
            sizeMiB, letter, diskNumber);

        var script = $"""
            rescan
            select disk {diskNumber}
            create partition primary size={sizeMiB} align=1024
            format fs=ntfs label={IsoPartitionLabel} quick
            assign letter={letter}
            exit
            """;
        var output = RunDiskpart(script);
        _logger.LogInformation("diskpart output:\n{Output}", output);

        // NTFS quick-format can take a little longer than FAT32 to surface.
        var root = $"{letter}:\\";
        for (var i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(root))
                break;
            Thread.Sleep(500);
        }
        if (!Directory.Exists(root))
            throw new InvalidOperationException($"ISO partition drive {letter}: did not appear after creation.");

        _logger.LogInformation("NTFS ISO partition ready at {L}:", letter);
        return letter;
    }

    private char? FindExistingIsoPartition(int diskNumber)
    {
        try
        {
            foreach (var di in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                try
                {
                    if (!string.Equals(di.VolumeLabel, IsoPartitionLabel, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                { continue; }

                var letter = char.ToUpperInvariant(di.Name[0]);
                if (FindPartitionByLetter(diskNumber, letter) == 0)
                    continue; // not on this disk

                _logger.LogInformation("Reusing existing NTFS ISO partition at {L}:", letter);
                return letter;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "FindExistingIsoPartition scan failed (non-fatal)");
        }
        return null;
    }

    //   Step 3 - copy ISO with progress                    

    private static KnownSeedFileHash CopyWithProgress(
        string src, string dst, long totalBytes,
        IProgress<DirectInstallProgress>? prog, CancellationToken ct)
    {
        const int BufSize = 4 * 1024 * 1024; // 4 MiB
        using var fsIn = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, BufSize, FileOptions.SequentialScan);
        using var fsOut = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, BufSize);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buf = new byte[BufSize];
        long copied = 0;
        int read;
        while ((read = fsIn.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            fsOut.Write(buf, 0, read);
            digest.AppendData(buf, 0, read);
            copied += read;
            prog?.Report(new DirectInstallProgress(
                DirectInstallPhase.CopyingIso, copied, totalBytes));
        }
        return new KnownSeedFileHash(Convert.ToHexString(digest.GetHashAndReset()), copied);
    }

    // One pass: read the source, write the seed copy, hash the bytes written. Last-write
    // time and attributes follow the source as File.Copy would carry them; the first-boot
    // agents preserve those timestamps into the new home directory.
    private KnownSeedFileHash CopyAndHash(string src, string dst, CancellationToken ct)
    {
        const int BufSize = 1024 * 1024;
        using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, BufSize, FileOptions.SequentialScan))
        using (var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, BufSize))
        using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[BufSize];
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                digest.AppendData(buffer, 0, read);
                written += read;
            }
            var result = new KnownSeedFileHash(Convert.ToHexString(digest.GetHashAndReset()), written);
            try
            {
                File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
                var attributes = File.GetAttributes(src) & (FileAttributes.ReadOnly | FileAttributes.Hidden |
                    FileAttributes.System | FileAttributes.Archive);
                File.SetAttributes(dst, attributes == 0 ? FileAttributes.Normal : attributes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
            {
                // FAT32 cannot store every NTFS timestamp (pre-1980, for instance). The
                // bytes are what the identity binds; metadata is best-effort.
                _logger.LogDebug(ex, "Could not carry timestamp/attributes to {Dst} (non-fatal)", dst);
            }
            return result;
        }
    }

    internal static void ValidateExternalInstallerMedia(InstallerBootSpec bootSpec)
    {
        ArgumentNullException.ThrowIfNull(bootSpec);
        var hasExternalMedia = bootSpec.KernelUrl is not null || bootSpec.InitrdUrl is not null ||
            bootSpec.KernelSha256 is not null || bootSpec.InitrdSha256 is not null;
        if (!hasExternalMedia) return;
        if (!SecureHttps(bootSpec.KernelUrl) || !SecureHttps(bootSpec.InitrdUrl) ||
            !ValidSha256(bootSpec.KernelSha256) || !ValidSha256(bootSpec.InitrdSha256))
            throw new InvalidDataException("External installer kernel and initrd need HTTPS URLs and SHA-256 pins.");
    }

    private static bool SecureHttps(Uri? value) => value is { IsAbsoluteUri: true } &&
        string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool ValidSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static void VerifySelectedIso(string isoPath, string? expectedSha256, CancellationToken ct)
    {
        if (!ValidSha256(expectedSha256))
            throw new InvalidDataException("Selected installer image has no SHA-256 pin.");
        if ((File.GetAttributes(isoPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Selected installer image is a redirected path.");
        using var source = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = source.Read(buffer)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            digest.AppendData(buffer, 0, read);
        }
        var actualSha256 = Convert.ToHexString(digest.GetHashAndReset());
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Selected installer image failed its SHA-256 pin.");
    }

    //   Step 4 - copy artefacts                        ─

    internal sealed record StagedFile(string SourcePath, string RelativePath, long SizeBytes);

    // The two staging namespaces that hold the user's own data (FileStagingService
    // layout): selected folders and portable browser profiles.
    internal static bool IsUserDataPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return normalized.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("browser-profiles/", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<StagedFile> InspectStagingFiles(string stagingDirectory, bool requireFat32Fit = true)
    {
        if (!Directory.Exists(stagingDirectory))
            throw new DirectoryNotFoundException("Migration staging directory is unavailable.");
        var root = Path.GetFullPath(stagingDirectory);
        var pending = new Stack<string>();
        var files = new List<StagedFile>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Migration staging contains a redirected directory.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Migration staging contains a redirected file.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }
                var length = new FileInfo(entry).Length;
                if (requireFat32Fit && length >= Fat32MaxFileBytes)
                    throw new InvalidDataException("A selected migration file exceeds the FAT32 file-size limit.");
                files.Add(new(entry, Path.GetRelativePath(root, entry), length));
            }
        }
        if (!files.Any(file => file.RelativePath.Equals("migration-manifest.json", StringComparison.OrdinalIgnoreCase)) ||
            !files.Any(file => file.RelativePath.Equals(Path.Join("igloo-agent", "first-boot.sh"),
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Migration staging lacks its manifest or required first-boot launcher.");
        return files;
    }

    private Dictionary<string, KnownSeedFileHash> CopyStagingArtefacts(string stagingDirectory,
        IReadOnlyList<StagedFile> files, string oemDrvRoot, IProgress<DirectInstallProgress>? prog,
        CancellationToken ct)
    {
        var hashes = new Dictionary<string, KnownSeedFileHash>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = files.Sum(file => file.SizeBytes);
        long writtenBytes = 0;
        // Copy the complete staged tree, including selected user files. Both the
        // USB and direct paths must give first boot the same migration source.
        foreach (var directory in Directory.EnumerateDirectories(stagingDirectory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Migration staging changed to a redirected directory while copying.");
            Directory.CreateDirectory(Path.Join(oemDrvRoot, Path.GetRelativePath(stagingDirectory, directory)));
        }
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var dst = Path.Join(oemDrvRoot, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if ((File.GetAttributes(file.SourcePath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A migration staging file changed to a redirected path while copying.");
            if (new FileInfo(file.SourcePath).Length != file.SizeBytes)
                throw new IOException("A migration staging file changed before copying.");
            // A rendered installer config may carry install-geometry tokens that can
            // only be resolved now the disk is partitioned (Ubuntu's subiquity needs
            // the free-gap offset/size explicitly). Substitute for that file; copy
            // everything else byte-for-byte.
            var geometryResolved = file.RelativePath.IndexOf(Path.DirectorySeparatorChar,
                StringComparison.Ordinal) < 0 &&
                TryCopyWithGeometry(file.SourcePath, dst);
            if (!geometryResolved)
            {
                var copied = CopyAndHash(file.SourcePath, dst, ct);
                if (copied.Length != file.SizeBytes || new FileInfo(dst).Length != file.SizeBytes)
                    throw new IOException("A migration staging file did not copy completely.");
                hashes[file.RelativePath.Replace('\\', '/')] = copied;
            }
            writtenBytes += file.SizeBytes;
            Report(prog, DirectInstallPhase.CopyingFiles, writtenBytes, totalBytes,
                $"Copying migration files… {writtenBytes / MiB:N0} / {totalBytes / MiB:N0} MiB");
        }
        _logger.LogInformation("Staging artefacts copied to {Root} ({Files} files, {MiB} MiB)",
            oemDrvRoot, files.Count, totalBytes / MiB);
        return hashes;
    }

    private bool TryCopyWithGeometry(string src, string dst)
    {
        var info = new FileInfo(src);
        if (info.Length is 0 or > 512 * 1024)
            return false;   // configs are tiny
        string text;
        try
        { text = File.ReadAllText(src); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        { return false; }
        // Any igloo geometry token ({{IGLOO_STORAGE_PARTITIONS}}, historic
        // {{IGLOO_ROOT_*}}, …)  prefix match so the guard can never silently
        // diverge from the template again: an unsubstituted token means user-data
        // ships as broken YAML and cloud-init quietly ignores the whole autoinstall.
        if (!text.Contains("{{IGLOO_", StringComparison.Ordinal))
            return false;

        var resolved = SubstituteGeometryTokens(text);
        File.WriteAllText(dst, resolved, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _logger.LogInformation("Wrote {Dst} with resolved install geometry", dst);
        return true;
    }

    private string SubstituteGeometryTokens(string content)
    {
        if (_diskNumber is not int disk)
            throw new InvalidOperationException("Disk number unknown; cannot resolve install geometry.");

        var block = BuildStoragePartitionList(disk);
        var resolved = content.Replace("{{IGLOO_STORAGE_PARTITIONS}}", block, StringComparison.Ordinal);
        if (resolved.Contains("{{IGLOO_", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Installer config still contains an unresolved {{IGLOO_*}} token after " +
                "substitution  template and DirectInstallService are out of sync.");
        return resolved;
    }

    private const string EspGptType = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
    private const string LinuxFsGptType = "{0fc63daf-8483-4772-8e79-3d69d8477de4}";

    private void EnsureRootPartition(int diskNumber, CancellationToken ct)
    {
        foreach (var p in _storage.ReadPartitions(diskNumber).RowsOrThrow())
            if (string.Equals(p["GptType"]?.ToString(), LinuxFsGptType, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Linux root partition already present on disk {D} - reusing", diskNumber);
                return;
            }

        _logger.LogInformation("Creating Linux root partition on disk {D} (fills remaining free space)", diskNumber);
        // No size argument: diskpart fills the largest free region, which is the
        // space the shrink reserved for Linux (seed + ISO partitions are already
        // carved out of their share). No format, no drive letter  Linux-side.
        var script = $"""
            rescan
            select disk {diskNumber}
            create partition primary align=1024
            set id={LinuxFsGptType.Trim('{', '}')}
            exit
            """;
        var output = RunDiskpart(script);
        _logger.LogInformation("diskpart output:\n{Output}", output);
        ct.ThrowIfCancellationRequested();
    }

    private string BuildStoragePartitionList(int diskNumber)
    {
        var parts = new List<(uint number, long off, long size, string gptType, string guid)>();
        foreach (var p in _storage.ReadPartitions(diskNumber).RowsOrThrow())
            parts.Add((Convert.ToUInt32(p["PartitionNumber"], CultureInfo.InvariantCulture),
                       Convert.ToInt64(p["Offset"], CultureInfo.InvariantCulture),
                       Convert.ToInt64(p["Size"], CultureInfo.InvariantCulture),
                       p["GptType"]?.ToString() ?? string.Empty,
                       p["Guid"]?.ToString() ?? string.Empty));

        if (parts.Count == 0)
            throw new InvalidOperationException($"No partitions found on disk {diskNumber}.");
        parts.Sort((a, b) => a.off.CompareTo(b.off));

        // partition_type + uuid are NOT optional fidelity: curtin REWRITES the
        // whole GPT when it adds a partition, from exactly what the config
        // declares. Declaring only number/offset/size made it stamp every
        // preserved partition as "Linux filesystem" with fresh PARTUUIDs 
        // Windows/MSR/recovery types clobbered and UEFI boot entries (which
        // reference PARTUUIDs) dangling. Feed back the real GUIDs so the
        // rewritten table is byte-faithful for everything we preserve.
        // EVERY partition is preserved  including root, which Igloo pre-created
        // from Windows (see EnsureRootPartition). curtin therefore has NOTHING to
        // add and performs no partition-table write at all: no disklabel rewrite,
        // no renumbering, no partprobe  the failure modes that killed every
        // "let curtin create root" attempt while live media occupied this disk.
        // Root is recognised by its GPT type and only gets wipe+format actions.
        bool sawEsp = false, sawRoot = false;
        var lines = new List<string>();
        foreach (var (number, off, size, gptType, guid) in parts)
        {
            bool isEsp = string.Equals(gptType, EspGptType, StringComparison.OrdinalIgnoreCase);
            bool isRoot = string.Equals(gptType, LinuxFsGptType, StringComparison.OrdinalIgnoreCase);
            var id = isEsp ? "esp" : isRoot ? "root" : $"part{number}";
            var extra = isEsp ? ", grub_device: true"
                        : isRoot ? ", wipe: superblock" : "";
            var ptype = gptType.Trim('{', '}');
            var puuid = guid.Trim('{', '}');
            if (ptype.Length > 0)
                extra += $", partition_type: {ptype}";
            if (puuid.Length > 0)
                extra += $", uuid: {puuid}";
            sawEsp |= isEsp;
            sawRoot |= isRoot;
            lines.Add(FormattableString.Invariant(
                $"      - {{type: partition, id: {id}, device: disk0, number: {number}, offset: {off}, size: {size}, preserve: true{extra}}}"));
        }
        if (!sawEsp)
            throw new InvalidOperationException(
                $"No EFI System Partition (GPT type {EspGptType}) found on disk {diskNumber}.");
        if (!sawRoot)
            throw new InvalidOperationException(
                $"No pre-created Linux root partition (GPT type {LinuxFsGptType}) found on disk {diskNumber}  " +
                "EnsureRootPartition should have created it before config substitution.");

        _logger.LogInformation(
            "Storage config: {Count} partition(s), all preserved (root pre-created; no table changes for curtin)",
            parts.Count);
        return string.Join("\n", lines);
    }

    //   Step 5 - extract kernel + initrd from ISO               

    private void ConfigureBootFiles(
        string isoPath, char oemDrvLetter,
        List<(string Name, byte[] Contents)>? initrdFiles,
        long initrdBytes, long installImgBytes,
        PinnedExternalInstallerMedia? externalMedia,
        IProgress<DirectInstallProgress>? prog, CancellationToken ct)
    {
        var oemDrvRoot = $"{oemDrvLetter}:\\";
        var bootDst = Path.Join(oemDrvRoot, BootDir);
        Directory.CreateDirectory(bootDst);

        string? mountedLetter = null;
        try
        {
            mountedLetter = MountIso(isoPath);
            ct.ThrowIfCancellationRequested();

            var isoRoot = $"{mountedLetter}:\\";

            //   1. Extract kernel + initrd                   ─
            var kernelDst = Path.Join(bootDst, KernelFile);
            var initrdDst = Path.Join(bootDst, InitrdFile);
            if (externalMedia is not null)
            {
                externalMedia.Kernel.CopyTo(kernelDst, prog, ct);
                externalMedia.Initrd.CopyTo(initrdDst, prog, ct);
            }
            else
            {
                var (kernelSrc, initrdSrc) = FindKernelFiles(isoRoot, _bootSpec);
                _logger.LogInformation("ISO kernel: {K}  initrd: {I}", kernelSrc, initrdSrc);
                CopyFileRobust(kernelSrc, kernelDst);
                ct.ThrowIfCancellationRequested();
                CopyWithProgress(initrdSrc, initrdDst, initrdBytes, prog, ct);
            }
            ct.ThrowIfCancellationRequested();

            // Inject the rendered installer config into the initrd (preseed
            // delivery) when the distro uses that method - the standard
            // fully-unattended path for debian-installer / Ubiquity.
            if (_bootSpec.ConfigDelivery == ConfigDelivery.InjectIntoInitrd)
            {
                if (initrdFiles is null || initrdFiles.Count == 0)
                    throw new InvalidDataException("Installer initrd payload is unavailable.");
                AppendFilesToInitrd(Path.Join(bootDst, InitrdFile), initrdFiles);
                _logger.LogInformation("Injected {Cfg} and {Count} helpers into initrd for unattended install",
                    initrdFiles[0].Name, initrdFiles.Count - 1);
            }

            //   2. Copy shim + GRUB EFI binaries                
            var (shimSrc, grubSrc) = FindEfiFiles(isoRoot);
            _logger.LogInformation("ISO shim: {S}", shimSrc);
            _logger.LogInformation("ISO grub: {G}", grubSrc);
            CopyFileRobust(shimSrc, Path.Join(bootDst, ShimFile));
            CopyFileRobust(grubSrc, Path.Join(bootDst, GrubFile));
            _logger.LogInformation("shim + grubx64.efi copied to {Dir}", bootDst);

            // Also stage shim + grub at the UEFI fallback path so firmware that
            // discards our NVRAM boot entry (see the BootDir/FallbackBootDir notes)
            // can still boot the installer. The shim loads grubx64.efi from its own
            // directory, so both must sit together under \EFI\BOOT; grub then locates
            // grub.cfg via its compiled prefix (written to every candidate below).
            var fallbackDst = Path.Join(oemDrvRoot, FallbackBootDir);
            Directory.CreateDirectory(fallbackDst);
            CopyFileRobust(shimSrc, Path.Join(fallbackDst, FallbackBootFile));
            CopyFileRobust(grubSrc, Path.Join(fallbackDst, GrubFile));
            _logger.LogInformation("shim + grubx64.efi also staged at fallback path {Dir}", fallbackDst);

            //   3. Copy images/install.img (Anaconda stage2 squashfs)     ─
            // The Fedora netinstall ISO contains images/install.img - the squashfs
            // that holds Anaconda and all installer tools (~870 MiB on Fedora 44).
            // Without inst.stage2= the initrd hangs; with inst.stage2=<network-url>
            // Anaconda downloads 862 MiB at boot time which proved unreliable
            // (connection drops, VM power-off at 99%). Copying to OEMDRV and using
            // inst.stage2=hd:LABEL=OEMDRV: is always fast and works offline.
            foreach (var f in _bootSpec.ExtraIsoFiles)
            {
                var src = Path.Join(isoRoot, f.IsoRelativePath.Replace('/', '\\'));
                if (!File.Exists(src))
                {
                    if (f.Required)
                        throw new FileNotFoundException("A required installer image file is missing.", src);
                    continue;
                }
                var dst = Path.Join(oemDrvRoot, f.OemDrvRelativePath.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                var len = new FileInfo(src).Length;
                Report(prog, DirectInstallPhase.ConfiguringGrub,
                    message: $"Copying installer payload ({len / MiB} MiB)...");
                CopyWithProgress(src, dst, len, prog, ct);
                ct.ThrowIfCancellationRequested();
                _logger.LogInformation("Copied {Src} to OEMDRV {Dst}", f.IsoRelativePath, f.OemDrvRelativePath);
            }
        }
        finally
        {
            if (mountedLetter is not null)
                DismountIso(isoPath);
        }

        //   4. Write grub.cfg                          
        // grubx64.efi has a compiled-in prefix that determines where it looks for
        // grub.cfg. Fedora ISOs ship two variants:
        //   • EFI/fedora/grubx64.efi  → prefix /EFI/fedora  → looks for /EFI/fedora/grub.cfg
        //   • EFI/BOOT/grubx64.efi   → prefix /EFI/BOOT    → looks for /EFI/BOOT/grub.cfg
        // Because we don't know at runtime which binary we got (and they can differ
        // even across minor Fedora releases), we write grub.cfg to BOTH locations.
        // grubx64.efi's compiled-in prefix differs per distro (/EFI/fedora,
        // /EFI/debian, /EFI/ubuntu, or /EFI/BOOT). We don't know which binary the
        // ISO shipped, so write grub.cfg to all of them.
        VerifyPreparedSeedBeforeBootConfiguration();
        var grubCfgContent = BuildGrubConfig(_bootSpec, _locale, _keymap,
            _preparedBootTarget!.Value.Disk.GptDiskGuid, _preparedBootTarget.Value.PartitionGuid);
        foreach (var dir in GrubCfgDirs.Select(cfgDir => Path.Join(oemDrvRoot, cfgDir)))
        {
            var path = Path.Join(dir, "grub.cfg");
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, grubCfgContent, Encoding.ASCII);
            _logger.LogInformation("grub.cfg written to {Path}", path);
        }
    }

    internal static (string Name, byte[] Contents) ReadRequiredInitrdConfig(
        InstallerBootSpec bootSpec, string stagingDirectory)
    {
        ArgumentNullException.ThrowIfNull(bootSpec);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        var name = bootSpec.InitrdConfigPath;
        if (bootSpec.ConfigDelivery != ConfigDelivery.InjectIntoInitrd ||
            string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) ||
            !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) ||
            name is "." or "..")
            throw new InvalidDataException("Installer initrd config name is unavailable or unsafe.");
        var source = Path.Join(stagingDirectory, name);
        var contents = File.ReadAllBytes(source);
        if (contents.Length == 0)
            throw new InvalidDataException("Installer initrd config is empty.");
        return (name, contents);
    }

    internal static IReadOnlyList<(string Name, byte[] Contents)> ReadRequiredInitrdExtras(
        InstallerBootSpec bootSpec, string stagingDirectory)
    {
        ArgumentNullException.ThrowIfNull(bootSpec);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        var declared = bootSpec.InitrdExtraPaths ?? throw new InvalidDataException(
            "Installer initrd extra file list is unavailable.");
        if (declared.Count > 0 && bootSpec.ConfigDelivery != ConfigDelivery.InjectIntoInitrd)
            throw new InvalidDataException("Installer initrd extras require initrd config delivery.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (bootSpec.InitrdConfigPath is { } configName)
            names.Add(configName);
        var result = new List<(string Name, byte[] Contents)>();
        foreach (var relative in declared)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                relative.Contains(':', StringComparison.Ordinal))
                throw new InvalidDataException("Installer initrd extra path is unavailable or unsafe.");
            var segments = relative.Replace('\\', '/').Split('/');
            if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) ||
                    segment is "." or ".." ||
                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new InvalidDataException("Installer initrd extra path is unavailable or unsafe.");
            var name = segments[^1];
            if (!names.Add(name))
                throw new InvalidDataException("Installer initrd extra names must be unique.");
            var source = Path.Join(stagingDirectory, Path.Join(segments));
            var contents = File.ReadAllBytes(source);
            if (contents.Length == 0)
                throw new InvalidDataException("Installer initrd extra is empty.");
            result.Add((name, contents));
        }
        return result;
    }

    private (string kernel, string initrd) FindKernelFiles(string isoRoot, InstallerBootSpec bootSpec)
    {
        // Try the distro's declared kernel/initrd locations (from the boot spec),
        // pairing any existing kernel with any existing initrd.
        foreach (var kFull in bootSpec.KernelIsoPaths
                     .Select(k => Path.Join(isoRoot, k.Replace('/', '\\')))
                     .Where(File.Exists))
        {
            var iFull = bootSpec.InitrdIsoPaths
                .Select(i => Path.Join(isoRoot, i.Replace('/', '\\')))
                .FirstOrDefault(File.Exists);
            if (iFull is not null)
                return (kFull, iFull);
        }

        // Full scan: find any file named "linux" or "vmlinuz" whose sibling is
        // "initrd" or "initrd.img".
        _logger.LogDebug("Kernel not found in standard locations; scanning ISO recursively…");
        var kernelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "vmlinuz", "linux" };
        var initrdNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "initrd.img", "initrd" };

        foreach (var f in Directory.EnumerateFiles(isoRoot, "*", SearchOption.AllDirectories)
                     .Where(x => kernelNames.Contains(Path.GetFileName(x))))
        {
            var dir = Path.GetDirectoryName(f)!;
            var initrdHit = Directory.EnumerateFiles(dir)
                .FirstOrDefault(x => initrdNames.Contains(Path.GetFileName(x)));
            if (initrdHit is null)
                continue;
            _logger.LogInformation("Found kernel via scan: {K}", f);
            return (f, initrdHit);
        }

        // Last resort: dump the full file listing so the next iteration can add
        // the correct fast-path candidate.
        var allFiles = Directory
            .EnumerateFiles(isoRoot, "*", SearchOption.AllDirectories)
            .Select(f => f[isoRoot.Length..])
            .OrderBy(f => f);
        throw new InvalidOperationException(
            $"Cannot locate kernel + initrd on the mounted ISO.\n" +
            $"ISO file listing:\n  {string.Join("\n  ", allFiles)}");
    }

    internal static (string shimPath, string grubPath) FindEfiFiles(string isoRoot)
    {
        //   Shim candidates (in priority order)               ─
        string[] shimCandidates =
        [
            Path.Join(isoRoot, "EFI", "fedora", "shimx64.efi"),   // Fedora live/full ISO
            Path.Join(isoRoot, "EFI", "debian", "shimx64.efi"),   // Debian
            Path.Join(isoRoot, "EFI", "ubuntu", "shimx64.efi"),   // Ubuntu / Mint
            Path.Join(isoRoot, "EFI", "BOOT",   "shimx64.efi"),   // some ISOs
            Path.Join(isoRoot, "EFI", "BOOT",   "BOOTX64.EFI"),   // UEFI fallback name (most netinst/live)
        ];

        var shimPath = shimCandidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "shimx64.efi not found on ISO. Checked: " +
                string.Join(", ", shimCandidates.Select(p => p[(isoRoot.Length)..])));

        //   GRUB candidates                          
        string[] grubCandidates =
        [
            Path.Join(isoRoot, "EFI", "fedora", "grubx64.efi"),   // Fedora
            Path.Join(isoRoot, "EFI", "debian", "grubx64.efi"),   // Debian
            Path.Join(isoRoot, "EFI", "ubuntu", "grubx64.efi"),   // Ubuntu / Mint
            Path.Join(isoRoot, "EFI", "BOOT",   "grubx64.efi"),   // fallback
        ];

        var grubPath = grubCandidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "grubx64.efi not found on ISO. Checked: " +
                string.Join(", ", grubCandidates.Select(p => p[(isoRoot.Length)..])));

        return (shimPath, grubPath);
    }

    private static void CopyFileRobust(string src, string dst)
    {
        const int bufSize = 65536;
        using var fsIn = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufSize);
        using var fsOut = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, bufSize);
        fsIn.CopyTo(fsOut);
    }

    //   initrd config injection                        

    internal static void AppendFilesToInitrd(string initrdPath,
        IReadOnlyList<(string Name, byte[] Contents)> files)
    {
        var cpio = BuildNewcCpio(files);
        using var gzBuf = new MemoryStream();
        using (var gz = new GZipStream(gzBuf, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(cpio, 0, cpio.Length);
        var gzBytes = gzBuf.ToArray();
        using var fs = new FileStream(initrdPath, FileMode.Append, FileAccess.Write);
        fs.Write(gzBytes, 0, gzBytes.Length);
    }

    
    internal static byte[] BuildNewcCpio(string name, byte[] data) =>
        BuildNewcCpio([(name, data)]);

    internal static byte[] BuildNewcCpio(IReadOnlyList<(string Name, byte[] Contents)> files)
    {
        using var ms = new MemoryStream();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, contents) in files)
        {
            if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) ||
                name is "." or ".." or "TRAILER!!!" ||
                name.Contains('/', StringComparison.Ordinal) ||
                name.Contains('\\', StringComparison.Ordinal) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !names.Add(name))
                throw new InvalidDataException("Installer initrd entry name is unsafe.");
            WriteCpioEntry(ms, name, contents, mode: 0x81A4, nlink: 1); // 0100644
        }
        WriteCpioEntry(ms, "TRAILER!!!", Array.Empty<byte>(), mode: 0, nlink: 1);
        return ms.ToArray();
    }

    private static void WriteCpioEntry(MemoryStream ms, string name, byte[] data, uint mode, uint nlink)
    {
        static string H(uint v) => v.ToString("X8", CultureInfo.InvariantCulture);
        var nameBytes = Encoding.ASCII.GetBytes(name);
        uint namesize = (uint)nameBytes.Length + 1; // include trailing NUL

        // newc header: 6-byte magic + 13 × 8-hex fields = 110 bytes.
        var header =
            "070701"               // magic
            + H(0)                 // ino
            + H(mode)              // mode
            + H(0) + H(0)          // uid, gid
            + H(nlink)             // nlink
            + H(0)                 // mtime
            + H((uint)data.Length) // filesize
            + H(0) + H(0)          // devmajor, devminor
            + H(0) + H(0)          // rdevmajor, rdevminor
            + H(namesize)          // namesize
            + H(0);                // check

        ms.Write(Encoding.ASCII.GetBytes(header));
        ms.Write(nameBytes);
        ms.WriteByte(0);
        Pad4(ms);          // pad header+name to a 4-byte boundary
        ms.Write(data);
        Pad4(ms);          // pad file data to a 4-byte boundary
    }

    private static void Pad4(MemoryStream ms)
    {
        int pad = (int)((4 - (ms.Length & 3)) & 3);
        for (int i = 0; i < pad; i++)
            ms.WriteByte(0);
    }

    /// <summary>
    /// Picks the user's locale and keymap out of the staged migration manifest.
    /// </summary>
    /// <remarks>
    /// Taken from the manifest that is already being staged rather than threaded
    /// through the service interface: it is the same file the installer itself reads,
    /// so the two cannot drift apart.
    ///
    /// It matters for the kernel command line because debian-installer asks about
    /// language and country in localechooser, which runs BEFORE most preseed
    /// processing. A preseeded debian-installer/locale therefore arrives too late to
    /// stop the question; the value has to be on the command line, where d-i reads it
    /// early enough. That is the difference between an unattended install and one that
    /// stops on the first screen asking the user to pick a country.
    /// </remarks>
    private void ReadLocaleFromStaging(string stagingDirectory)
    {
        try
        {
            var path = Path.Join(stagingDirectory, "migration-manifest.json");
            if (!File.Exists(path))
                return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("user", out var user))
                return;

            if (user.TryGetProperty("locale", out var locale))
            {
                var value = locale.GetString();
                if (!string.IsNullOrEmpty(value))
                    _locale = value;
            }
            if (user.TryGetProperty("keymap", out var keymap))
            {
                var value = keymap.GetString();
                if (!string.IsNullOrEmpty(value))
                    _keymap = value;
            }

            _logger.LogInformation("Installer locale={Locale} keymap={Keymap}", _locale, _keymap);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read locale/keymap from the staged manifest - using defaults");
        }
    }

    private void VerifyPreparedSeedBeforeBootConfiguration()
    {
        if (_preparedBootTarget is not { Availability: ObservationAvailability.Available } prepared ||
            _diskNumber is not { } diskNumber || _partitionNumber is not { } partitionNumber ||
            prepared.Value.FileSystem != "FAT32")
            throw new InvalidOperationException("The prepared installer seed has no exact FAT volume.");
        var observed = WindowsBootRegistrationPlanning.ObservePreparedTarget(_storage,
            checked((uint)diskNumber), partitionNumber);
        if (observed.Availability != ObservationAvailability.Available ||
            observed.Value != prepared.Value)
            throw new InvalidOperationException("The prepared installer seed changed before boot configuration.");
    }

    internal static string BuildGrubConfig(InstallerBootSpec bootSpec, string locale,
        string keymap, Guid seedDiskGuid, Guid seedPartitionGuid,
        Guid? reviewedLinuxEspPartitionGuid = null)
    {
        ArgumentNullException.ThrowIfNull(bootSpec);
        if (seedDiskGuid == Guid.Empty || seedPartitionGuid == Guid.Empty)
            throw new InvalidDataException("The installer boot entry has no exact seed GPT identity.");
        if (reviewedLinuxEspPartitionGuid is { } linuxEspGuid &&
            (linuxEspGuid == Guid.Empty || linuxEspGuid == seedPartitionGuid))
            throw new InvalidDataException("The reviewed Linux ESP has no distinct GPT partition identity.");
        if (bootSpec.KernelCmdline.Contains("igloo.linuxesppartuuid=", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installer boot recipe contains an unreviewed Linux ESP binding.");
        var label = bootSpec.VolumeLabel;
        var cmdline = bootSpec.KernelCmdline
            .Replace("{LABEL}", label, StringComparison.Ordinal)
            .Replace("{SEED_PARTUUID}", seedPartitionGuid.ToString("D"), StringComparison.Ordinal)
            // Bare language code: localechooser wants "en_US", not "en_US.UTF-8".
            .Replace("{LOCALE}", locale.Split('.')[0], StringComparison.Ordinal)
            .Replace("{KEYMAP}", keymap, StringComparison.Ordinal);
        if (cmdline.Contains('{', StringComparison.Ordinal) ||
            cmdline.Contains('}', StringComparison.Ordinal))
            throw new InvalidDataException("The installer boot arguments contain an unbound value.");
        var seedArgs = "igloo.seedpartuuid=" + seedPartitionGuid.ToString("D") +
            " igloo.seeddiskguid=" + seedDiskGuid.ToString("D");
        if (reviewedLinuxEspPartitionGuid is { } reviewedGuid)
            seedArgs += " igloo.linuxesppartuuid=" + reviewedGuid.ToString("D");
        cmdline = cmdline.EndsWith(" ---", StringComparison.Ordinal)
            ? cmdline[..^4] + " " + seedArgs + " ---"
            : cmdline + " " + seedArgs;
        return $$"""
            insmod part_gpt
            insmod fat
            insmod linux

            # Keep the partition that loaded this configuration. A FAT serial is
            # not unique, so a filesystem UUID search can switch to a decoy seed.
            set igloo_origin="$root"
            if [ -z "$igloo_origin" ]; then
                echo "The installer seed origin is unavailable."
                halt
            fi
            set igloo_candidate=
            search --no-floppy --set=igloo_candidate --file /{{SeedIdentityWriter.FileName}}
            if [ -z "$igloo_candidate" ]; then
                echo "The installer seed identity is unavailable."
                halt
            fi
            if [ "$igloo_candidate" != "$igloo_origin" ]; then
                echo "The installer seed identity resolved to another partition."
                halt
            fi
            set root="$igloo_origin"

            set default=0
            set timeout=5

            menuentry "{{bootSpec.MenuTitle}}" {
                linux  ($root)/{{BootDir}}/{{KernelFile}} {{cmdline}}
                initrd ($root)/{{BootDir}}/{{InitrdFile}}
            }
            """;
    }

    // ISO mounting via PowerShell
    private string MountIso(string isoPath)
    {
        // Dismount first in case a previous run left the ISO mounted.
        // Errors are silently ignored - the image may not be mounted at all.
        DismountIso(isoPath);
        Thread.Sleep(500);

        RunPowerShell($"Mount-DiskImage -ImagePath {PowerShellLiteral(isoPath)} -Access ReadOnly");

        // Mount-DiskImage is asynchronous on real hardware; the volume letter is
        // assigned by Windows after the virtual disk is attached.
        Thread.Sleep(2_000);

        // First attempt: poll up to 30 seconds.
        var letter = PollForDriveLetter(isoPath, retries: 60);
        if (letter is not null)
            return letter;

        _logger.LogWarning("ISO did not appear after 30 s - dismounting and retrying once");

        // Recovery: dismount, remount, and give it 10 more seconds.
        DismountIso(isoPath);
        Thread.Sleep(1_000);
        RunPowerShell($"Mount-DiskImage -ImagePath {PowerShellLiteral(isoPath)} -Access ReadOnly");
        Thread.Sleep(2_000);

        letter = PollForDriveLetter(isoPath, retries: 20);
        if (letter is not null)
            return letter;

        throw new InvalidOperationException("ISO did not mount within 40 seconds.");
    }

    private string? PollForDriveLetter(string isoPath, int retries)
    {
        for (var i = 0; i < retries; i++)
        {
            var letter = RunPowerShell(
                $"(Get-DiskImage -ImagePath {PowerShellLiteral(isoPath)} | Get-Volume).DriveLetter");
            letter = letter.Trim();
            if (!string.IsNullOrEmpty(letter))
            {
                _logger.LogDebug("ISO mounted at {L}:", letter);
                return letter;
            }
            Thread.Sleep(500);
        }
        return null;
    }

    private void DismountIso(string isoPath)
    {
        try
        { RunPowerShell($"Dismount-DiskImage -ImagePath {PowerShellLiteral(isoPath)}"); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        { _logger.LogWarning(ex, "Dismount-DiskImage failed (non-fatal)"); }
    }

    private static string RunPowerShell(string command)
    {
        // EncodedCommand protects the command-line boundary, while each path
        // must still be quoted as literal PowerShell data inside the script.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "$ErrorActionPreference = 'Stop'; " + command));
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NonInteractive -NoProfile -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        return RunInstallerProcessAsync(psi, TimeSpan.FromSeconds(30))
            .GetAwaiter().GetResult().Trim();
    }

    internal static string PowerShellLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    //   RegisterBootEntry                           ─

    // Firmware boot-menu name of the one-shot entry. Distro-neutral: the first-boot
    // agents and LinuxRemovalService find and delete this entry by a case-insensitive
    // "igloo" substring match on the description, so the word must stay in it.
    private const string BootEntryDescription = CommunityBootRegistrationPlan.Description;
    private const string EfiGlobGuid = "{8be4df61-93ca-11d2-aa0d-00e098032b8c}";

    // Field-proven registration path (v0.1/v0.2-alpha). It writes the one-shot entry
    // through TWO independent mechanisms and treats everything after the first NVRAM
    // write as best-effort:
    //
    //   1. Raw UEFI NVRAM: Boot#### + BootNext (+ BootOrder prepend).
    //   2. Windows BCD:    bcdedit /copy {bootmgr} → firmware application + bootsequence.
    //
    // Consumer AMI/Gigabyte firmware prunes OS-registered Boot#### entries at POST and
    // substitutes its own "UEFI OS" entry for the same \EFI\BOOT\BOOTX64.EFI target; the
    // BootNext choice and the sibling promotion below exist for exactly that case.
    //
    // The CommunityRecovery planner (CommunityBootRegistrationPlan / Executor) is NOT on
    // this path any more: it required a fully parsed BCD store, 256 proven-absent
    // firmware slots and no pre-existing entry for the target partition before it would
    // write anything, so it refused on every retry (stale iGloo BCD objects) and on every
    // board whose firmware creates its own entry. It remains available to tests and to the
    // reviewed/recovery workflow; the product path uses this method.
    private void RegisterBootEntry(
        CompletedDirectInstallPreparation completed,
        IProgress<DirectInstallProgress>? prog, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ct.ThrowIfCancellationRequested();

        Report(prog, DirectInstallPhase.RegisteringBootEntry, message: "Registering UEFI boot entry…");

        FirmwareNative.EnablePrivilege(_logger, reportAssignmentFailures: true);
        ct.ThrowIfCancellationRequested();

        // Partition geometry for the EFI_LOAD_OPTION HARDDRIVE node comes from the
        // canonical identity pinned at the end of Prepare - no second WMI round trip,
        // and no chance of binding a different partition by ordinal.
        var target = completed.Target;
        var sectorSize = target.Disk.LogicalSectorSize == 0 ? 512UL : target.Disk.LogicalSectorSize;
        var lbaStart = target.OffsetBytes / sectorSize;
        var lbaSize = target.SizeBytes / sectorSize;
        var partGuid = target.PartitionGuid;
        var partitionNumber = completed.PartitionNumber;

        // Pick an unused Boot#### index.
        var idx = FindFreeBootIndex();
        ct.ThrowIfCancellationRequested();

        // efiPath points at the STANDARD fallback loader \EFI\BOOT\BOOTX64.EFI (staged by
        // ConfigureBootFiles), NOT the private \igloo-boot\shimx64.efi. Field finding on a
        // Gigabyte/AMI board: firmware validates boot options at POST and prunes any
        // Boot#### whose target it deems non-standard; the \EFI\BOOT\BOOTX64.EFI target
        // survived. Same shim binary, second path - no regression elsewhere.
        var loadOption = BuildEfiLoadOption(
            partitionNumber, lbaStart, lbaSize, partGuid,
            $@"\{FallbackBootDir}\{FallbackBootFile}", BootEntryDescription);

        var bootVar = $"Boot{idx:X4}";
        _logger.LogInformation("Writing UEFI {Var} ({Bytes} bytes) for partition {Guid} (LBA {Start}+{Size})",
            bootVar, loadOption.Length, partGuid, lbaStart, lbaSize);

        if (!FirmwareNative.SetFirmwareEnvironmentVariableW(bootVar, EfiGlobGuid, loadOption, (uint)loadOption.Length))
            throw new InvalidOperationException(
                $"SetFirmwareEnvironmentVariable({bootVar}) failed: Win32 error {Marshal.GetLastWin32Error()}. " +
                "Run iGloo as Administrator to allow UEFI NVRAM writes.");

        // Choose which entry BootNext should name. When the firmware already has its own
        // entry for this partition, that one is the entry proven to survive this
        // firmware's validation pass; ours stays written and stays in BootOrder.
        var bootNextIdx = idx;
        var firmwareOwn = EfiBootEntries.FindEntriesTargetingPartition(partGuid, _logger)
            .Where(i => i != idx)
            .ToList();
        if (firmwareOwn.Count > 0)
        {
            bootNextIdx = firmwareOwn[0];
            _logger.LogInformation(
                "Firmware already lists Boot{Own:X4} for the installer partition - pointing BootNext at " +
                "that instead of our own Boot{Ours:X4}, which this firmware may prune at POST",
                bootNextIdx, idx);
        }

        var bootNext = BitConverter.GetBytes(bootNextIdx);
        if (!FirmwareNative.SetFirmwareEnvironmentVariableW("BootNext", EfiGlobGuid, bootNext, 2))
            throw new InvalidOperationException(
                $"SetFirmwareEnvironmentVariable(BootNext) failed: Win32 error {Marshal.GetLastWin32Error()}.");
        _logger.LogInformation("BootNext set to {Idx:X4}", bootNextIdx);

        // Second mechanism: a Windows-managed BCD firmware application + bootsequence.
        // bcdedit re-synchronises BCD into NVRAM the same way Windows registers its own
        // Boot Manager, which pruning firmware demonstrably keeps. Purely additive.
        RegisterBootEntryViaBcdedit(completed.DriveLetter);

        // Belt-and-suspenders for firmware that ignores BootNext but honours BootOrder.
        PrependBootOrder(idx);

        // ...and raise every OTHER entry targeting the same partition so the handoff
        // survives when our own entry is culled (otherwise an already-installed Linux
        // tends to be first in BootOrder and boots instead of the installer).
        PrioritiseEntriesTargetingInstallerPartition(partGuid, idx);

        // Dual-boot clock fix: Linux keeps the RTC in UTC, Windows assumes local time.
        SetRtcUniversalTime();

        _logger.LogInformation("BootNext + BootOrder updated - reboot to install");
        Report(prog, DirectInstallPhase.Complete, message: "UEFI boot entry registered. Ready to reboot.");
    }

    private void SetRtcUniversalTime()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation");
            key.SetValue("RealTimeIsUniversal", 1L, RegistryValueKind.QWord);
            _logger.LogInformation("RealTimeIsUniversal=1 - Windows now reads the RTC as UTC");
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex,
                "Could not set RealTimeIsUniversal (non-fatal) - the Windows clock " +
                "will drift by the timezone offset after switching from Linux");
        }
    }

    /// <summary>
    /// Registers the installer as a Windows-managed firmware application and makes it the
    /// next boot via <c>bcdedit</c>. Entirely best-effort: the NVRAM path has already run.
    /// </summary>
    private void RegisterBootEntryViaBcdedit(char letter)
    {
        try
        {
            // Every run copies {bootmgr}; bootsequence consumes the copy but never
            // deletes it, so without this they pile up in the firmware store.
            RemoveStaleBcdEntries();

            var created = RunBcdedit($"/copy {{bootmgr}} /d \"{BootEntryDescription}\"");
            // Output: 'The entry was successfully copied to {xxxxxxxx-....}.'
            var guid = Regex.Match(created ?? string.Empty, @"\{[0-9a-fA-F-]{36}\}").Value;
            if (guid.Length == 0)
            {
                _logger.LogWarning("bcdedit /copy did not return a GUID - skipping the BCD boot entry");
                return;
            }

            RunBcdedit($"/set {guid} device partition={letter}:");
            RunBcdedit($"/set {guid} path \\{FallbackBootDir}\\{FallbackBootFile}");
            // Inherited Windows-loader settings make no sense for a third-party EFI application.
            RunBcdedit($"/deletevalue {guid} locale");
            RunBcdedit($"/deletevalue {guid} inherit");
            RunBcdedit($"/set {{fwbootmgr}} bootsequence {guid}");

            _logger.LogInformation(
                "BCD firmware entry {Guid} registered for {Letter}: and set as the next boot", guid, letter);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                      or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not register the BCD boot entry (non-fatal) - "
                                   + "the NVRAM BootNext above still applies");
        }
    }

    /// <summary>Deletes firmware BCD entries this service created on earlier runs.</summary>
    /// <remarks>
    /// Matched on the description rather than a stored GUID: a run that ended in a crash or
    /// a reinstall leaves entries behind that no state file knows about. Windows Boot
    /// Manager carries its own description, so it can never match.
    /// </remarks>
    private void RemoveStaleBcdEntries()
    {
        var listing = RunBcdedit("/enum firmware");
        if (string.IsNullOrEmpty(listing))
            return;

        foreach (var id in ParseStaleBcdIds(listing, BootEntryDescription))
        {
            RunBcdedit($"/delete {id} /f");
            _logger.LogInformation("Removed a leftover BCD boot entry {Guid}", id);
        }
    }

    private string? RunBcdedit(string arguments)
    {
        using var proc = Process.Start(new ProcessStartInfo(WindowsBcdReader.FindNativeExecutable("bcdedit.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (proc is null)
            return null;

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);

        if (proc.ExitCode != 0)
            _logger.LogWarning("bcdedit {Args} exited {Code}: {Err}",
                arguments, proc.ExitCode, (stderr + stdout).Trim());
        return stdout;
    }

    private static List<ushort> ReadBootOrderList()
    {
        var buf = new byte[1024];
        var size = FirmwareNative.GetFirmwareEnvironmentVariableW("BootOrder", EfiGlobGuid, buf, (uint)buf.Length);
        var order = new List<ushort>();
        for (var i = 0; i + 1 < size; i += 2)
            order.Add(BitConverter.ToUInt16(buf, i));
        return order;
    }

    private static byte[] EncodeBootOrder(List<ushort> order)
    {
        var bytes = new byte[order.Count * 2];
        for (var i = 0; i < order.Count; i++)
            BitConverter.GetBytes(order[i]).CopyTo(bytes, i * 2);
        return bytes;
    }

    private void PrependBootOrder(ushort idx)
    {
        try
        {
            var order = ReadBootOrderList();
            order.RemoveAll(e => e == idx);
            order.Insert(0, idx);
            var bytes = EncodeBootOrder(order);
            if (!FirmwareNative.SetFirmwareEnvironmentVariableW("BootOrder", EfiGlobGuid, bytes, (uint)bytes.Length))
                _logger.LogWarning("SetFirmwareEnvironmentVariable(BootOrder) failed: {Err} (non-fatal)",
                    Marshal.GetLastWin32Error());
            else
                _logger.LogInformation("BootOrder prepended with {Idx:X4}", idx);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or OverflowException)
        {
            _logger.LogWarning(ex, "PrependBootOrder failed (non-fatal)");
        }
    }

    /// <summary>
    /// Raises every boot entry that targets the installer partition to the front of
    /// BootOrder, just behind <paramref name="ownIndex"/>. Non-fatal.
    /// </summary>
    private void PrioritiseEntriesTargetingInstallerPartition(Guid partGuid, ushort ownIndex)
    {
        try
        {
            var siblings = EfiBootEntries.FindEntriesTargetingPartition(partGuid, _logger)
                .Where(i => i != ownIndex)
                .ToList();
            if (siblings.Count == 0)
                return;

            _logger.LogInformation(
                "Firmware also lists {Count} entry(s) for the installer partition ({Indices}) - " +
                "promoting them so the handoff survives if our own entry is pruned",
                siblings.Count, string.Join(", ", siblings.Select(i => i.ToString("X4", CultureInfo.InvariantCulture))));

            var order = ReadBootOrderList();
            order.RemoveAll(siblings.Contains);
            var insertAt = order.IndexOf(ownIndex) + 1;
            order.InsertRange(Math.Clamp(insertAt, 0, order.Count), siblings);

            var bytes = EncodeBootOrder(order);
            if (!FirmwareNative.SetFirmwareEnvironmentVariableW("BootOrder", EfiGlobGuid, bytes, (uint)bytes.Length))
                _logger.LogWarning("Could not reorder BootOrder for the firmware's own entries: {Err} (non-fatal)",
                    Marshal.GetLastWin32Error());
            else
                _logger.LogInformation("BootOrder is now {Order}",
                    string.Join(",", order.Select(i => i.ToString("X4", CultureInfo.InvariantCulture))));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or OverflowException)
        {
            _logger.LogWarning(ex, "Could not prioritise the firmware's installer entries (non-fatal)");
        }
    }

    private static ushort FindFreeBootIndex()
    {
        var buf = new byte[4096];
        for (ushort i = 0x0080; i < 0x00FF; i++)
        {
            var read = FirmwareNative.GetFirmwareEnvironmentVariableW($"Boot{i:X4}", EfiGlobGuid, buf, (uint)buf.Length);
            if (read == 0)
                return i; // variable doesn't exist → free slot
        }
        return 0x0090; // fallback - overwrite 0x0090
    }

    // Diagnostic only. Runs the CommunityRecovery planner against the prepared target and
    // returns its verdict without writing anything. Not on the product path: the planner
    // is informational evidence for the recovery workflow, never a gate on registration.
    internal Observation<CommunityBootRegistrationPlan> PlanBootRegistrationForDiagnostics(
        CompletedDirectInstallPreparation completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var evidence = WindowsBootRegistrationPlanning.Read(_storage, _bcd, completed.Target, completed.PartitionNumber);
        return CommunityBootRegistrationPlan.Create(evidence, Guid.NewGuid());
    }

    // Only the boot-registration stage is gated. Preparation and its storage behavior are unchanged.
    internal void RegisterPreparedBootEntry(Action mutation, CancellationToken ct = default) =>
        _bootRecovery.Execute(CommunityRecoveryBoundary.DirectInstallDeclaration, mutation, ct);

    // Compatibility parser retained for its existing callers/tests; registration never uses text.
    internal static IReadOnlyList<string> ParseStaleBcdIds(string? listing, string description) =>
        BcdListingParser.ParseStaleBcdIds(listing, description);

    internal static byte[] BuildEfiLoadOption(
        uint partitionNumber, ulong lbaStart, ulong lbaSize,
        Guid partGuid, string efiPath, string description,
        string? cmdLine = null)
    {
        using var ms = new MemoryStream();

        // Attributes: LOAD_OPTION_ACTIVE (0x00000001)
        ms.Write(BitConverter.GetBytes((uint)1));

        // FilePathListLength placeholder (2 bytes) - patched below.
        int fplLenOffset = (int)ms.Position;
        ms.Write(BitConverter.GetBytes((ushort)0));

        // Description (UCS-2 null-terminated).
        ms.Write(Encoding.Unicode.GetBytes(description + '\0'));

        int devicePathStart = (int)ms.Position;

        // HARDDRIVE media device path node (42 bytes).
        ms.WriteByte(0x04);           // Type
        ms.WriteByte(0x01);           // SubType
        ms.Write(BitConverter.GetBytes((ushort)42));
        ms.Write(BitConverter.GetBytes(partitionNumber));
        ms.Write(BitConverter.GetBytes(lbaStart));
        ms.Write(BitConverter.GetBytes(lbaSize));
        ms.Write(partGuid.ToByteArray()); // 16 bytes, correct endianness for EFI
        ms.WriteByte(0x02);           // MBRType: GPT
        ms.WriteByte(0x02);           // SignatureType: GUID

        // FILE_PATH media device path node.
        var pathBytes = Encoding.Unicode.GetBytes(efiPath + '\0');
        ms.WriteByte(0x04);           // Type
        ms.WriteByte(0x04);           // SubType
        ms.Write(BitConverter.GetBytes((ushort)(4 + pathBytes.Length)));
        ms.Write(pathBytes);

        // End of Hardware Device Path (4 bytes).
        ms.WriteByte(0x7F);
        ms.WriteByte(0xFF);
        ms.Write(BitConverter.GetBytes((ushort)4));

        // FilePathListLength covers only the device path (up to here), NOT optional data.
        int devicePathEnd = (int)ms.Position;

        // OptionalData: kernel command line encoded as UTF-16LE (no NUL terminator).
        // The Linux EFI stub detects UTF-16LE by checking for wide-char encoding and
        // uses this as the kernel command line.  initrd= paths use UEFI backslash
        // convention; the remaining parameters use the standard Linux cmdline format.
        if (cmdLine is not null)
            ms.Write(Encoding.Unicode.GetBytes(cmdLine));

        var result = ms.ToArray();
        ushort fplLength = (ushort)(devicePathEnd - devicePathStart);
        var fplBytes = BitConverter.GetBytes(fplLength);
        result[fplLenOffset] = fplBytes[0];
        result[fplLenOffset + 1] = fplBytes[1];
        return result;
    }

    //   Utility                                ─

    // This mirrors the size arithmetic used by the current private installer. It deliberately
    // does not choose a partition, free extent, EFI relationship or installer-side layout.
    internal static DirectInstallSizePlan PlanStorageSizes(InstallerBootSpec bootSpec,
        long linuxSizeBytes, long fullIsoBytes, long kernelBytes, long initrdBytes,
        long installImgBytes, long stagedBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(bootSpec);
        if (linuxSizeBytes < 0 || fullIsoBytes < 0 || kernelBytes < 0 ||
            initrdBytes < 0 || installImgBytes < 0 || stagedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(linuxSizeBytes));
        checked
        {
            bool separateIso = bootSpec.CopyFullIsoToVolume && fullIsoBytes >= Fat32MaxFileBytes;
            long fat32IsoBytes = bootSpec.CopyFullIsoToVolume && !separateIso ? fullIsoBytes : 0;
            long oemDrvBytes = RoundUpMiB(kernelBytes + initrdBytes + installImgBytes + stagedBytes +
                fat32IsoBytes + PartitionOverheadBytes);
            long isoPartitionBytes = separateIso
                ? RoundUpMiB(fullIsoBytes + IsoPartitionOverheadBytes) : 0;
            return new(separateIso, oemDrvBytes, isoPartitionBytes,
                linuxSizeBytes + oemDrvBytes + isoPartitionBytes);
        }
    }

    internal sealed record DirectInstallSizePlan(bool IsoOnOwnPartition,
        long OemDrvBytes, long IsoPartitionBytes, long TotalShrinkBytes);

    internal static long RoundUpMiB(long bytes) =>
        ((bytes + MiB - 1) / MiB) * MiB;

    private static void Report(
        IProgress<DirectInstallProgress>? prog,
        DirectInstallPhase phase,
        long written = 0, long total = 0, string? message = null)
        => prog?.Report(new DirectInstallProgress(phase, written, total, message));
}
