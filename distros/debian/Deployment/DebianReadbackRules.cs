using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;

namespace Igloo.Distro.Debian.Deployment;

public sealed record DebianInstalledPackageV1(string Name, string Version, string Architecture, string DpkgStatus);
public sealed record DebianFirmwareSlotV1(Guid Vendor, string Name);
public sealed record DebianFirmwareChangeV1(DebianFirmwareSlotV1 Slot, FirmwareVariableV1 Before, FirmwareVariableV1 After);

public static class DebianReadbackRules
{
    // The acquisition implementation must authenticate InRelease with a trusted keyring and
    // verify its index/archive chain AND the dependency solver result. Hash equality below
    // compares that independently authenticated evidence; it does not perform signature checks.
    public static Observation<bool> Source(DebianDeploymentPlanV1 plan,
        Observation<DebianSourceV1> independentlyAuthenticated, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(independentlyAuthenticated);
        if (!DebianDeploymentPlanning.ValidStructure(plan)) return Fail(ObservationAvailability.Ambiguous, "DebianPlanInvalid");
        if (independentlyAuthenticated.Availability != ObservationAvailability.Available)
            return Fail(independentlyAuthenticated.Availability, "DebianSourceAuthenticationUnavailable");
        var actual = independentlyAuthenticated.Value;
        if (actual is null || actual.Archives.IsDefault || actual.Packages.IsDefault ||
            actual.Archives.Any(a => a.ValidUntilUtc <= observedAtUtc) ||
            JsonSerializer.Serialize(actual) != JsonSerializer.Serialize(plan.Source))
            return Fail(ObservationAvailability.Ambiguous, "DebianAuthenticatedSourceChangedOrExpired");
        return Observations.Available(true);
    }

    public static Observation<bool> Packages(DebianDeploymentPlanV1 plan,
        Observation<ImmutableArray<DebianInstalledPackageV1>> independentlyQueried)
    {
        ArgumentNullException.ThrowIfNull(independentlyQueried);
        if (!DebianDeploymentPlanning.ValidStructure(plan)) return Fail(ObservationAvailability.Ambiguous, "DebianPlanInvalid");
        if (independentlyQueried.Availability != ObservationAvailability.Available) return Fail(independentlyQueried.Availability, "DebianDpkgUnavailable");
        var actual = independentlyQueried.Value;
        if (actual.IsDefault || actual.Length != plan.Source.Packages.Length || actual.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            actual.Any(p => p.DpkgStatus != "install ok installed") || plan.Source.Packages.Any(p =>
                !actual.Any(a => a.Name == p.Name && a.Version == p.Version && a.Architecture == p.Architecture)))
            return Fail(ObservationAvailability.Ambiguous, "DebianPackageClosureNotConfigured");
        return Observations.Available(true);
    }

    public static Observation<bool> Agent(DebianDeploymentPlanV1 plan, Observation<InstallerRuntimeInventoryV1> inventory,
        Observation<ImmutableArray<InstallerPayloadReadbackV1>> independentPayloadRead)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return InstallerPayloadVerification.VerifyForFormattedRoot(plan.Ownership, plan.Root.GenerationId, plan.Root,
            plan.Agent.Payload, inventory, independentPayloadRead);
    }

    // These are COMMAND CONTRACTS, not an executor. No shell interpolation, guessed disk,
    // removable fallback, NVRAM write, --force, or unsigned fallback is offered here.
    public static DebianCommandV1 LoaderFilesCommand { get; } = new("/usr/sbin/grub-install",
        ["--target=x86_64-efi", "--efi-directory=/boot/efi", "--boot-directory=/boot",
         "--bootloader-id=debian", "--uefi-secure-boot", "--no-nvram"]);
    public static string SignedPackageDebconf =>
        "grub-efi-amd64 grub2/update_nvram boolean false\n" +
        "grub-efi-amd64 grub2/force_efi_extra_removable boolean false\n";

    // Full visible variable sets supplied by an independent native inventory, including unknown
    // names/vendors. A known-key-only scan is NOT such an inventory. This checks a later explicit
    // journal's exact declared delta. It neither chooses an index nor generates/writes variables.
    public static Observation<bool> FirmwareDelta(ImmutableArray<DebianFirmwareChangeV1> declared,
        Observation<ImmutableDictionary<DebianFirmwareSlotV1, FirmwareVariableV1>> before,
        Observation<ImmutableDictionary<DebianFirmwareSlotV1, FirmwareVariableV1>> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Availability != ObservationAvailability.Available) return Fail(before.Availability, "DebianFirmwareBeforeUnavailable");
        if (after.Availability != ObservationAvailability.Available) return Fail(after.Availability, "DebianFirmwareAfterUnavailable");
        if (declared.IsDefault || declared.Select(d => d.Slot).Distinct().Count() != declared.Length)
            return Fail(ObservationAvailability.Ambiguous, "DebianFirmwareDeclarationInvalid");
        var slots = before.Value.Keys.Union(after.Value.Keys).Union(declared.Select(d => d.Slot));
        foreach (var slot in slots)
        {
            // Require explicit Absent observations for new/deleted slots. A missing dictionary
            // item is not evidence of absence, even from a purported full enumeration.
            if (!before.Value.TryGetValue(slot, out var b) || !after.Value.TryGetValue(slot, out var a))
                return Fail(ObservationAvailability.Unavailable, "DebianFirmwareSlotUnobserved");
            var readable = Readable(b);
            if (readable != ObservationAvailability.Available) return Fail(readable, "DebianFirmwareBeforeUnreadable");
            readable = Readable(a);
            if (readable != ObservationAvailability.Available) return Fail(readable, "DebianFirmwareAfterUnreadable");
            var change = declared.SingleOrDefault(d => d.Slot == slot);
            if (change is null ? !Same(b, a) : !Same(b, change.Before) || !Same(a, change.After))
                return Fail(ObservationAvailability.Ambiguous, "DebianUnexpectedFirmwareEffect");
        }
        return Observations.Available(true);
    }

    private static ObservationAvailability Readable(FirmwareVariableV1 value) => value.Bytes.Availability switch
    {
        ObservationAvailability.Absent => ObservationAvailability.Available,
        ObservationAvailability.Available when value.Bytes.Value.IsDefault => ObservationAvailability.Ambiguous,
        ObservationAvailability.Available => value.VariableAttributes.Availability,
        _ => value.Bytes.Availability,
    };
    private static bool Same(FirmwareVariableV1 left, FirmwareVariableV1 right) => Readable(right) == ObservationAvailability.Available &&
        left.Bytes.Availability == right.Bytes.Availability && (left.Bytes.Availability == ObservationAvailability.Absent ||
        (left.VariableAttributes.Value == right.VariableAttributes.Value && left.Bytes.Value.SequenceEqual(right.Bytes.Value)));
    private static Observation<bool> Fail(ObservationAvailability state, string code) => Observations.Failure<bool>(state, code);
}
