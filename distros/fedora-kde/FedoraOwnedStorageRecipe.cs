using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Distro.FedoraKde;

// Fedora 44-1.7: pykickstart 3.69 / Anaconda 44.30 / Blivet 3.13.2.
// Complete STORAGE section only. Fresh runtime verification must precede installer consumption;
// this helper is not wired to the legacy IDistroPlugin flow or permission to prepare a machine.
public static class FedoraOwnedStorageRecipe
{
    // Actual 44-1.7 initrd anaconda-lib.sh disk_to_devpath supports UUID=. Transport only:
    // staged content hashes and early-boot uniqueness still require independent verification.
    public static Observation<string> PayloadTransportArguments(InstallationOwnershipV1 ownership, Guid expectedGeneration,
        Observation<InstallerRuntimeInventoryV1> inventory)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        var resolved = InstallationOwnership.Resolve(ownership, expectedGeneration, inventory);
        return resolved.Availability == ObservationAvailability.Available
            ? Observations.Available($"inst.stage2=hd:UUID={ownership.Esp.Payload.FileSystemUuid}: inst.ks=hd:UUID={ownership.Esp.Payload.FileSystemUuid}:/ks.cfg inst.geoloc=0")
            : Observations.Failure<string>(resolved.Availability, resolved.Code!);
    }

    public static Observation<string> Generate(InstallationOwnershipV1 ownership, Guid expectedGeneration,
        Observation<InstallerRuntimeInventoryV1> inventory)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        var resolved = InstallationOwnership.Resolve(ownership, expectedGeneration, inventory);
        if (resolved.Availability != ObservationAvailability.Available)
            return Observations.Failure<string>(resolved.Availability, resolved.Code!);
        var target = resolved.Value;
        var disk = target.DiskDevice["/dev/".Length..];
        // Disk is a fresh transient locator, not persisted authority. Restrict to plain native
        // disks; layered devices need a separate ownership contract, not shell/path escaping.
        if (disk.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            return Observations.Failure<string>(ObservationAvailability.Unsupported, "FedoraLayeredDiskUnsupported");
        return Observations.Available(
            $"ignoredisk --only-use={disk}\n" +
            "clearpart --none\n" +
            $"bootloader --boot-drive={disk}\n" +
            $"part /boot/efi --onpart=UUID={ownership.Esp.LinuxEsp.FileSystemUuid} --noformat\n" +
            $"part / --onpart=PARTUUID={target.RootPartitionGuid:D} --fstype=ext4\n");
    }

    public static Observation<string> ReplaceLegacyStorageSection(string template, InstallationOwnershipV1 ownership,
        Guid expectedGeneration, Observation<InstallerRuntimeInventoryV1> inventory)
    {
        ArgumentNullException.ThrowIfNull(template);
        var recipe = Generate(ownership, expectedGeneration, inventory);
        if (recipe.Availability != ObservationAvailability.Available) return recipe;
        const string startMarker = "# ── Disk detection";
        const string networkMarker = "# ── Wi-Fi connectivity for the netinstall";
        const string endMarker = "%include /tmp/ks-storage.cfg";
        template = template.Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = template.IndexOf(startMarker, StringComparison.Ordinal);
        var network = template.IndexOf(networkMarker, StringComparison.Ordinal);
        var end = template.IndexOf(endMarker, StringComparison.Ordinal);
        if (start < 0 || network <= start || end <= network || template.LastIndexOf(startMarker, StringComparison.Ordinal) != start ||
            template.LastIndexOf(networkMarker, StringComparison.Ordinal) != network ||
            template.LastIndexOf(endMarker, StringComparison.Ordinal) != end)
            return Observations.Failure<string>(ObservationAvailability.Ambiguous, "FedoraStorageTemplateChanged");
        var rendered = template[..start] + "# Storage resolved from the independently verified preparation generation.\n" + recipe.Value +
            "\n%pre --interpreter=/bin/bash --erroronfail\nset -euo pipefail\nlog() { echo \"[igloo-pre] $*\" >&2; }\n" +
            template[network..end] + template[(end + endMarker.Length)..];
        // No appended command can reintroduce another storage allocator from the legacy template.
        string[] storageCommands = ["part", "partition", "autopart", "reqpart", "clearpart", "zerombr", "ignoredisk", "bootloader", "raid", "logvol", "volgroup", "btrfs", "mount"];
        var actual = new List<string>();
        var inSection = false;
        foreach (var line in rendered.Split('\n').Select(l => l.Trim()))
        {
            var command = line.Split(' ', '\t')[0];
            if (command == "%end") inSection = false;
            else if (command is "%pre" or "%post" or "%packages") inSection = true;
            else if (!inSection && storageCommands.Contains(command, StringComparer.Ordinal)) actual.Add(line);
        }
        if (!actual.SequenceEqual(recipe.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal))
            return Observations.Failure<string>(ObservationAvailability.Ambiguous, "FedoraUnexpectedStorageInstruction");
        return Observations.Available(rendered);
    }
}
