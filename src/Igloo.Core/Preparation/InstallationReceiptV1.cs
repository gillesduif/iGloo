using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;

namespace Igloo.Core.Preparation;

// An installation evidence record, NOT RecoverySnapshotV1, an execution journal, a supported
// distro profile, rollback, or permission to reboot. No production receipt producer exists yet.
public enum DeploymentStage
{
    LayoutResolved, RootFormatted, MountsVerified, SourceVerified, FilesystemDeployed,
    AptConfigured, KernelInstalled, InitramfsGenerated, SystemConfigured, AccountsConfigured,
    DesktopFirmwareConfigured, SystemIdentityFinalized, SignedBootPackagesConfigured,
    BootloaderFilesFinalized, BootConfigurationGenerated, FirmwareFinalized, AgentInstalled,
    WindowsPreservationVerified, TargetUnmounted,
}
public enum DeploymentStageOutcome { NotStarted, ReadbackVerified, Failed, OutcomeUnknown }
public enum InstallationEvidenceState { Incomplete, Failed, OutcomeUnknown, EvidenceComplete }
public enum InstalledFileRole { Kernel, Initramfs, Fstab, GrubConfiguration, Shim, Grub, Agent, AgentConfiguration, AgentService }
public sealed record DeploymentStageEvidenceV1(DeploymentStage Stage, DeploymentStageOutcome Outcome,
    int? ToolExitCode, string? ReadbackSha256);
public sealed record InstalledFileEvidenceV1(InstalledFileRole Role, Guid PartitionGuid, string Path, long Length, string Sha256);
public sealed record InstallationSourceV1(string ArtifactName, long Length, string Sha256, string PayloadManifestSha256);
// Raw readback of the declared permanent boot entry and BootOrder. Parsing remains in the shared
// EFI parser. This witness does not claim these were the only firmware variables changed.
public sealed record InstalledFirmwareEvidenceV1(ushort BootIndex, uint EntryNativeAttributes,
    ImmutableArray<byte> EntryBytes, uint OrderNativeAttributes, ImmutableArray<byte> OrderBytes);
public sealed record InstallationReceiptV1(int SchemaVersion, InstallationOwnershipV1 Ownership,
    RootFileSystemReceiptV1 Root, string DistroId, string DistroVersion, string StrategyVersion,
    InstallationSourceV1 Source, ImmutableArray<DeploymentStageEvidenceV1> Stages,
    ImmutableArray<InstalledFileEvidenceV1> Files, InstalledFirmwareEvidenceV1? Firmware);
public sealed record InstallationReceiptAssessment(InstallationEvidenceState State,
    ObservationAvailability Availability, string Code);

public static class InstallationReceiptRules
{
    public static bool IsStructurallyValid(InstallationReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.SchemaVersion != 1 || receipt.Ownership is null || receipt.Root is null || receipt.Source is null ||
            string.IsNullOrWhiteSpace(receipt.DistroId) || string.IsNullOrWhiteSpace(receipt.DistroVersion) ||
            string.IsNullOrWhiteSpace(receipt.StrategyVersion) || string.IsNullOrWhiteSpace(receipt.Source.ArtifactName) ||
            receipt.Source.Length <= 0 || !Hash(receipt.Source.Sha256) || !Hash(receipt.Source.PayloadManifestSha256) ||
            receipt.Stages.IsDefault || receipt.Files.IsDefault || receipt.Root.FileSystem != "EXT4" || receipt.Root.FileSystemUuid == Guid.Empty ||
            receipt.Root.GenerationId != receipt.Ownership.Layout.Plan.GenerationId ||
            PreparedStorageOwnership.VerifyStructure(receipt.Ownership.Layout).Availability != ObservationAvailability.Available ||
            !receipt.Ownership.Layout.StorageOwnership!.CreatedPartitions.Any(p => p.Role == PreparationRole.LinuxRoot && p.Identity == receipt.Root.Partition))
            return false;
        if (receipt.Stages.Select(s => s.Stage).Distinct().Count() != receipt.Stages.Length ||
            receipt.Stages.Any(s => !Enum.IsDefined(s.Stage) || !Enum.IsDefined(s.Outcome) ||
                (s.Outcome == DeploymentStageOutcome.ReadbackVerified && (s.ToolExitCode is not (null or 0) || !Hash(s.ReadbackSha256))) ||
                (s.Outcome == DeploymentStageOutcome.NotStarted && (s.ToolExitCode is not null || s.ReadbackSha256 is not null))))
            return false;
        var root = receipt.Root.Partition.PartitionGuid;
        var esp = receipt.Ownership.Esp.LinuxEsp.Volume.PartitionGuid;
        return receipt.Files.Select(f => f.Role).Distinct().Count() == receipt.Files.Length &&
            receipt.Files.Select(f => (f.PartitionGuid, f.Path)).Distinct().Count() == receipt.Files.Length &&
            receipt.Files.All(f => Enum.IsDefined(f.Role) && f.Length > 0 && Hash(f.Sha256) && SafePath(f.Path) &&
                f.PartitionGuid == (f.Role is InstalledFileRole.Shim or InstalledFileRole.Grub ? esp : root) && CorrectPath(f));
    }

    // Stage digests reference independently retained audit evidence. This checks completeness,
    // not the truth of arbitrary producer assertions or Mint/Debian package semantics.
    public static InstallationReceiptAssessment AssessEvidence(InstallationReceiptV1 receipt,
        Observation<InstallerRuntimeInventoryV1> inventory,
        Observation<ImmutableArray<InstalledFileEvidenceV1>> freshFiles,
        Observation<InstalledFirmwareEvidenceV1> freshFirmware)
    {
        ArgumentNullException.ThrowIfNull(freshFiles);
        ArgumentNullException.ThrowIfNull(freshFirmware);
        if (!IsStructurallyValid(receipt)) return Result(InstallationEvidenceState.Incomplete, ObservationAvailability.Ambiguous, "InstallationReceiptInvalid");
        var stageById = receipt.Stages.ToDictionary(s => s.Stage);
        foreach (var stage in Enum.GetValues<DeploymentStage>())
        {
            if (!stageById.TryGetValue(stage, out var evidence) || evidence.Outcome == DeploymentStageOutcome.NotStarted)
                return Result(InstallationEvidenceState.Incomplete, ObservationAvailability.Unavailable, "InstallationStageMissing:" + stage);
            if (evidence.Outcome != DeploymentStageOutcome.ReadbackVerified)
                return evidence.Outcome == DeploymentStageOutcome.Failed
                    ? Result(InstallationEvidenceState.Failed, ObservationAvailability.Unavailable, "InstallationStageFailed:" + stage)
                    : Result(InstallationEvidenceState.OutcomeUnknown, ObservationAvailability.Unavailable, "InstallationStageOutcomeUnknown:" + stage);
        }
        var resolved = InstallationOwnership.ResolveFormattedRoot(receipt.Ownership, receipt.Root.GenerationId, receipt.Root, inventory);
        if (resolved.Availability != ObservationAvailability.Available)
            return Result(InstallationEvidenceState.Incomplete, resolved.Availability, resolved.Code!);
        if (receipt.Files.Length != Enum.GetValues<InstalledFileRole>().Length || receipt.Firmware is null)
            return Result(InstallationEvidenceState.Incomplete, ObservationAvailability.Unavailable, "InstallationFinalEvidenceMissing");
        if (freshFiles.Availability != ObservationAvailability.Available)
            return Result(InstallationEvidenceState.Incomplete, freshFiles.Availability, "InstallationFilesUnreadable");
        if (freshFiles.Value.IsDefault || !receipt.Files.OrderBy(f => f.Role).SequenceEqual(freshFiles.Value.OrderBy(f => f.Role)))
            return Result(InstallationEvidenceState.Incomplete, ObservationAvailability.Ambiguous, "InstalledContentChanged");
        if (freshFirmware.Availability != ObservationAvailability.Available)
            return Result(InstallationEvidenceState.Incomplete, freshFirmware.Availability, "InstalledFirmwareUnreadable");
        var expected = receipt.Firmware; var actual = freshFirmware.Value;
        if (actual.EntryBytes.IsDefault || actual.OrderBytes.IsDefault || expected.EntryBytes.IsDefault || expected.OrderBytes.IsDefault ||
            expected.BootIndex != actual.BootIndex || expected.EntryNativeAttributes != actual.EntryNativeAttributes ||
            expected.OrderNativeAttributes != actual.OrderNativeAttributes || !expected.EntryBytes.SequenceEqual(actual.EntryBytes) ||
            !expected.OrderBytes.SequenceEqual(actual.OrderBytes))
            return Result(InstallationEvidenceState.Incomplete, ObservationAvailability.Ambiguous, "InstalledFirmwareChanged");
        if (actual.EntryNativeAttributes != 7 || actual.OrderNativeAttributes != 7)
            return Result(InstallationEvidenceState.Incomplete, ObservationAvailability.Unsupported, "InstalledFirmwareAttributesUnsupported");
        var parsed = EfiRecoveryParser.ParseLoadOption(actual.EntryBytes);
        if (parsed.Availability != ObservationAvailability.Available)
            return Result(InstallationEvidenceState.Incomplete, parsed.Availability, "InstalledBootEntryUninterpretable");
        var path = parsed.Value.GptFilePath;
        var order = EfiRecoveryParser.ParseBootOrder(new(Observations.Available(actual.OrderBytes), null, Observations.Available(actual.OrderNativeAttributes)));
        if (path.Availability != ObservationAvailability.Available || order.Availability != ObservationAvailability.Available)
            return Result(InstallationEvidenceState.Incomplete, path.Availability != ObservationAvailability.Available ? path.Availability : order.Availability, "InstalledBootPathUnavailable");
        if (!parsed.Value.OptionalData.IsEmpty)
            return Result(InstallationEvidenceState.Incomplete, ObservationAvailability.Unsupported, "InstalledBootOptionalDataOpaque");
        var esp = receipt.Ownership.Esp.LinuxEsp.Volume;
        var shim = receipt.Files.Single(f => f.Role == InstalledFileRole.Shim);
        var grub = receipt.Files.Single(f => f.Role == InstalledFileRole.Grub);
        var kernel = receipt.Files.Single(f => f.Role == InstalledFileRole.Kernel);
        var initramfs = receipt.Files.Single(f => f.Role == InstalledFileRole.Initramfs);
        if ((parsed.Value.Attributes & 1) == 0 || !order.Value.Contains(actual.BootIndex) ||
            shim.Path[..^"shimx64.efi".Length] != grub.Path[..^"grubx64.efi".Length] ||
            kernel.Path["/boot/vmlinuz-".Length..] != initramfs.Path["/boot/initrd.img-".Length..] ||
            path.Value.PartitionGuid != esp.PartitionGuid || path.Value.StartLba != esp.OffsetBytes / esp.Disk.LogicalSectorSize ||
            path.Value.SizeLba != esp.SizeBytes / esp.Disk.LogicalSectorSize ||
            !string.Equals(path.Value.FilePath, shim.Path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
            return Result(InstallationEvidenceState.Incomplete, ObservationAvailability.Ambiguous, "InstalledBootDestinationChanged");
        return Result(InstallationEvidenceState.EvidenceComplete, ObservationAvailability.Available, "InstallationEvidenceCompleteNotBootabilityOrAuthorization");
    }

    private static bool CorrectPath(InstalledFileEvidenceV1 file) => file.Role switch
    {
        InstalledFileRole.Kernel => file.Path.StartsWith("/boot/vmlinuz-", StringComparison.Ordinal),
        InstalledFileRole.Initramfs => file.Path.StartsWith("/boot/initrd.img-", StringComparison.Ordinal),
        InstalledFileRole.Fstab => file.Path == "/etc/fstab",
        InstalledFileRole.GrubConfiguration => file.Path == "/boot/grub/grub.cfg",
        InstalledFileRole.Shim => file.Path.StartsWith("/EFI/", StringComparison.Ordinal) && file.Path.EndsWith("/shimx64.efi", StringComparison.Ordinal),
        InstalledFileRole.Grub => file.Path.StartsWith("/EFI/", StringComparison.Ordinal) && file.Path.EndsWith("/grubx64.efi", StringComparison.Ordinal),
        InstalledFileRole.Agent => file.Path == "/opt/igloo/agent.py",
        InstalledFileRole.AgentConfiguration => file.Path == "/var/lib/igloo/manifest.json",
        InstalledFileRole.AgentService => file.Path.StartsWith("/etc/systemd/system/", StringComparison.Ordinal) && file.Path.EndsWith(".service", StringComparison.Ordinal),
        _ => false,
    };
    private static bool SafePath(string path) => !string.IsNullOrEmpty(path) && path.StartsWith('/') &&
        path.Split('/').Skip(1).All(p => p.Length > 0 && p is not ("." or "..") && !p.Any(char.IsControl) && !p.Contains('\\', StringComparison.Ordinal));
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static InstallationReceiptAssessment Result(InstallationEvidenceState state, ObservationAvailability availability, string code) => new(state, availability, code);
}
