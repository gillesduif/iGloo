using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;

namespace Igloo.Preflight;

// Shared read-only acquisition, intentionally not wired into either product's mutation workflow.
public sealed class WindowsRecoverySnapshotCapture : IRecoverySnapshotCapture
{
    public RecoverySnapshotV1 Capture(RecoveryScopeV1 scope, Guid canonicalTargetVolume)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var first = CaptureOnce(scope, canonicalTargetVolume);
        var second = CaptureOnce(scope, canonicalTargetVolume);
        var complete = RecoverySnapshotRules.Assess(first).Issues.All(i => i.Code is RecoverySnapshotIssueCode.ReadbackUnavailable or RecoverySnapshotIssueCode.ScopeUnresolved) &&
            RecoverySnapshotRules.Assess(second).Issues.All(i => i.Code is RecoverySnapshotIssueCode.ReadbackUnavailable or RecoverySnapshotIssueCode.ScopeUnresolved);
        return first with { IndependentReadback = complete ? Observations.Available(first.CanonicalHash == second.CanonicalHash) :
            Observations.Failure<bool>(ObservationAvailability.Unavailable, "RequiredEvidenceIncomplete") };
    }

    private static RecoverySnapshotV1 CaptureOnce(RecoveryScopeV1 scope, Guid targetId)
    {
        var storage = new WindowsStorageReader().ReadIdentitySnapshot();
        var windowsId = WindowsRecoveryPathReader.ReadWindowsVolume();
        var target = CanonicalRecoveryIdentity.Bind(storage, targetId);
        var windows = windowsId.Availability == ObservationAvailability.Available ? CanonicalRecoveryIdentity.Bind(storage, windowsId.Value) :
            Observations.Failure<CanonicalVolumeIdentityV1>(windowsId.Availability, "WindowsIdentityUnavailable");
        var binding = windows.Availability != ObservationAvailability.Available ? Observations.Failure<RecoveryTargetBindingV1>(windows.Availability, "WindowsBindingUnavailable") :
            target.Availability != ObservationAvailability.Available ? Observations.Failure<RecoveryTargetBindingV1>(target.Availability, "TargetBindingUnavailable") :
            Observations.Available(new RecoveryTargetBindingV1(windows.Value, target.Value));
        var bcd = new WindowsBcdReader().ReadRecoveryGraph();
        var firmware = new WindowsFirmwareSnapshotCapture(new WindowsFirmwareReader()).Capture(scope.FirmwareMutationEntries.Append(scope.WindowsBootEntry));
        var esp = CaptureEsp(storage, windows, scope.WindowsBootEntry, firmware);
        // B3 proved location/handle correlation, but not a locale-independent typed WinRE configuration reader.
        // No BCD recovery-enabled flag or mere recovery partition substitutes for configured WinRE state.
        const string winreCode = "TypedWinReConfigurationNotImplemented";
        var winre = new WinReConfigurationV1(1, Observations.Failure<bool>(ObservationAvailability.Unavailable, winreCode),
            Observations.Failure<Guid>(ObservationAvailability.Unavailable, winreCode),
            Observations.Failure<CanonicalVolumeIdentityV1>(ObservationAvailability.Unavailable, winreCode),
            Observations.Failure<CanonicalFileIdentityV1>(ObservationAvailability.Unavailable, winreCode));
        var snapshot = new RecoverySnapshotV1(1, DateTimeOffset.UtcNow, scope, binding, bcd, firmware, esp, winre,
            scope.IncludeRtc ? new WindowsRtcRecoveryReader().Capture() : null,
            Observations.Failure<bool>(ObservationAvailability.Unavailable, "IndependentRecapturePending"), new([], []));
        return RecoverySnapshotSerialization.Seal(snapshot);
    }

    private static Observation<EspAssociationV1> CaptureEsp(Observation<WindowsStorageSnapshot> storage,
        Observation<CanonicalVolumeIdentityV1> windows, ushort index, FirmwareSnapshotV1 firmware)
    {
        if (windows.Availability != ObservationAvailability.Available) return Observations.Failure<EspAssociationV1>(windows.Availability, "WindowsVolumeUnavailable");
        if (storage.Availability != ObservationAvailability.Available) return Observations.Failure<EspAssociationV1>(storage.Availability, "StorageUnavailable");
        var entry = firmware.BootEntries.Single(e => e.Index == index);
        if (entry.LoadOption.Availability != ObservationAvailability.Available) return Observations.Failure<EspAssociationV1>(entry.LoadOption.Availability, "WindowsBootEntryUnavailable");
        var path = entry.LoadOption.Value.GptFilePath;
        if (path.Availability != ObservationAvailability.Available) return Observations.Failure<EspAssociationV1>(path.Availability, "WindowsEfiPathUnavailable");
        var volumes = storage.Value.Volumes.Where(v => v.PartitionGuid.Availability == ObservationAvailability.Available && v.PartitionGuid.Value == path.Value.PartitionGuid).ToArray();
        if (volumes.Length != 1) return Observations.Failure<EspAssociationV1>(volumes.Length > 1 ? ObservationAvailability.Ambiguous : ObservationAvailability.Unavailable, "EspVolumeNotUnique");
        if (volumes[0].VolumeGuid.Availability != ObservationAvailability.Available) return Observations.Failure<EspAssociationV1>(volumes[0].VolumeGuid.Availability, "EspVolumeGuidUnavailable");
        var esp = CanonicalRecoveryIdentity.Bind(storage, volumes[0].VolumeGuid.Value);
        if (esp.Availability != ObservationAvailability.Available) return Observations.Failure<EspAssociationV1>(esp.Availability, "EspIdentityUnavailable");
        var file = WindowsRecoveryPathReader.ReadBootManagerFile(esp.Value.VolumeGuid);
        return file.Availability == ObservationAvailability.Available ? Observations.Available(new EspAssociationV1(1, esp.Value, windows.Value, index,
            BcdRecoveryGraphV1.WindowsBootManagerId, file.Value)) : Observations.Failure<EspAssociationV1>(file.Availability, "BootManagerFileUnavailable");
    }
}
