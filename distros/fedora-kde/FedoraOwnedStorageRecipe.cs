using System.Globalization;
using System.Text;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;

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
            $"part /boot/efi --onpart=/dev/disk/by-partuuid/{target.LinuxEspPartitionGuid:D} --noformat\n" +
            $"part / --onpart=/dev/disk/by-partuuid/{target.RootPartitionGuid:D} --fstype=ext4\n");
    }

    public static Observation<string> ReplaceLegacyStorageSection(string template, InstallationOwnershipV1 ownership,
        Guid expectedGeneration, Observation<InstallerRuntimeInventoryV1> inventory)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(ownership);
        var reviewed = PreparedLayoutRules.VerifyReviewedIntent(ownership.Layout.Plan);
        if (reviewed.Availability != ObservationAvailability.Available)
            return Observations.Failure<string>(reviewed.Availability, reviewed.Code!);
        var recipe = Generate(ownership, expectedGeneration, inventory);
        if (recipe.Availability != ObservationAvailability.Available) return recipe;
        return ReplaceLegacyStorageSectionCore(template, ownership, recipe.Value, deferred: false);
    }

    // Windows cannot supply an Anaconda device name. The receipted layout is a
    // declaration here; %pre must independently match the seed, complete GPT
    // partition set, and owned ESP/root before it writes the only storage include.
    public static Observation<string> ReplaceLegacyStorageSectionDeferred(string template,
        InstallationOwnershipV1 ownership, Guid expectedGeneration)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(ownership);
        var validated = ValidateDeferredOwnership(ownership, expectedGeneration);
        return validated.Availability == ObservationAvailability.Available
            ? ReplaceLegacyStorageSectionCore(template, ownership, string.Empty, deferred: true)
            : Observations.Failure<string>(validated.Availability, validated.Code!);
    }

    private static Observation<bool> ValidateDeferredOwnership(InstallationOwnershipV1 ownership,
        Guid expectedGeneration)
    {
        var layout = ownership.Layout;
        var plan = layout.Plan;
        var esp = ownership.Esp;
        var volumes = new[] { esp.WindowsEsp, esp.LinuxEsp, esp.Payload };
        if (PreparedLayoutRules.VerifyReviewedIntent(plan).Availability != ObservationAvailability.Available ||
            expectedGeneration == Guid.Empty || plan.GenerationId != expectedGeneration ||
            esp.GenerationId != expectedGeneration ||
            esp.TargetDisk != plan.TargetDisk ||
            esp.WindowsEsp.Volume != plan.WindowsEsp ||
            volumes.Any(volume => !CanonicalRecoveryIdentity.IsValid(volume.Volume) ||
                volume.Volume.FileSystem != "FAT32" || !SafeFatUuid(volume.FileSystemUuid)) ||
            volumes.Select(volume => volume.FileSystemUuid).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3 ||
            esp.WindowsEsp.Volume.PartitionType != PreparationSpacePlanning.EspType ||
            esp.LinuxEsp.Volume.PartitionType != PreparationSpacePlanning.EspType ||
            esp.Payload.Volume.PartitionType != PreparationSpacePlanning.BasicDataType ||
            PreparedStorageOwnership.VerifyStructure(layout).Availability != ObservationAvailability.Available ||
            !layout.Partitions.Any(part => part.Role == PreparationRole.LinuxEsp &&
                part.Identity == esp.LinuxEsp.Volume) ||
            !layout.Partitions.Any(part => part.Role == PreparationRole.Payload &&
                part.Identity == esp.Payload.Volume) ||
            !string.Equals(layout.PayloadFileSystemUuid, esp.Payload.FileSystemUuid,
                StringComparison.OrdinalIgnoreCase))
            return Observations.Failure<bool>(ObservationAvailability.Ambiguous,
                "FedoraDeferredOwnershipInvalid");
        var root = layout.StorageOwnership!.CreatedPartitions.SingleOrDefault(part =>
            part.Role == PreparationRole.LinuxRoot);
        var allocation = plan.Space.Allocations.SingleOrDefault(part =>
            part.Role == PreparationRole.LinuxRoot);
        if (root is null || allocation is null || !allocation.CreateInWindows ||
            allocation.FileSystem.Length != 0 ||
            layout.Partitions.Any(part => part.Role == PreparationRole.LinuxRoot) ||
            layout.StorageOwnership.CreatedPartitions.SingleOrDefault(part =>
                part.Role == PreparationRole.LinuxEsp)?.Identity !=
                PreparedStorageOwnership.PartitionOf(esp.LinuxEsp.Volume))
            return Observations.Failure<bool>(ObservationAvailability.Unsupported,
                "FedoraOwnedRootReceiptUnavailable");
        var iso = layout.StorageOwnership.CreatedPartitions.SingleOrDefault(part =>
            part.Role == PreparationRole.Iso);
        if (iso is null ? ownership.IsoFileSystemUuid is not null :
            ownership.IsoFileSystemUuid is not { Length: 16 } uuid ||
            uuid.Any(value => !Uri.IsHexDigit(value)))
            return Observations.Failure<bool>(ObservationAvailability.Ambiguous,
                "FedoraIsoFilesystemBindingInvalid");
        return Observations.Available(true);
    }

    private static bool SafeFatUuid(string value) => value is { Length: 9 } && value[4] == '-' &&
        value.Where((character, index) => index != 4).All(Uri.IsHexDigit);

    private static Observation<string> ReplaceLegacyStorageSectionCore(string template,
        InstallationOwnershipV1 ownership, string recipe, bool deferred)
    {
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
        // The legacy %pre combined exact seed/target checks with autopart
        // generation and Wi-Fi. Keep its existing validator and target
        // selection for %post, then verify owned partition geometry before
        // Anaconda consumes the typed --onpart directives.
        const string targetStart = "TARGET_BYTES=\"{{TARGET_DISK_BYTES}}\"";
        const string allocatorStart = "    # NVIDIA GPUs: do NOT";
        var targetAt = template.IndexOf(targetStart, StringComparison.Ordinal);
        var allocatorAt = template.IndexOf(allocatorStart, StringComparison.Ordinal);
        if (targetAt < start || allocatorAt <= targetAt || allocatorAt >= network ||
            template.LastIndexOf(targetStart, StringComparison.Ordinal) != targetAt ||
            template.LastIndexOf(allocatorStart, StringComparison.Ordinal) != allocatorAt)
            return Observations.Failure<string>(ObservationAvailability.Ambiguous,
                "FedoraReviewedPreflightTemplateChanged");
        var created = ownership.Layout.StorageOwnership!.CreatedPartitions;
        var linuxEsp = created.Single(partition => partition.Role == PreparationRole.LinuxEsp).Identity;
        var root = created.Single(partition => partition.Role == PreparationRole.LinuxRoot).Identity;
        var iso = created.SingleOrDefault(partition => partition.Role == PreparationRole.Iso)?.Identity;
        var isoCheck = deferred && iso is not null
            ? $"verify_owned_partition {iso.PartitionGuid:D} {iso.OffsetBytes.ToString(CultureInfo.InvariantCulture)} " +
              $"{iso.SizeBytes.ToString(CultureInfo.InvariantCulture)} {iso.PartitionType:D} ntfs " +
              $"{ownership.IsoFileSystemUuid}\n"
            : string.Empty;
        var ownedCheck = $$"""
                verify_owned_partition() {
                    local expected_guid="$1" expected_offset="$2" expected_size="$3"
                    local expected_type="$4" expected_fs="$5" expected_uuid="$6"
                    local device parent name start sectors observed_type observed_fs observed_uuid linked
                    local -a matches
                    mapfile -t matches < <(blkid -t "PARTUUID=$expected_guid" -o device 2>/dev/null)
                    if [ "${#matches[@]}" -ne 1 ]; then
                        log "ERROR: owned partition $expected_guid is absent or ambiguous"
                        exit 1
                    fi
                    device=$(readlink -f "${matches[0]}")
                    linked=$(readlink -f "/dev/disk/by-partuuid/$expected_guid" 2>/dev/null || true)
                    if [ "$linked" != "$device" ]; then
                        log "ERROR: owned partition $expected_guid has no matching by-partuuid device path"
                        exit 1
                    fi
                    parent=$(lsblk -n -o PKNAME "$device" | tr -d '[:space:]')
                    name=$(basename "$device")
                    if [ "$parent" != "$TARGET_DISK" ] ||
                       [ ! -r "/sys/class/block/$name/start" ] ||
                       [ ! -r "/sys/class/block/$name/size" ]; then
                        log "ERROR: owned partition $expected_guid has the wrong parent disk"
                        exit 1
                    fi
                    start=$(cat "/sys/class/block/$name/start")
                    sectors=$(cat "/sys/class/block/$name/size")
                    if [ "$((start * 512))" -ne "$expected_offset" ] ||
                       [ "$((sectors * 512))" -ne "$expected_size" ]; then
                        log "ERROR: owned partition $expected_guid geometry changed"
                        exit 1
                    fi
                    observed_type=$(lsblk -n -o PARTTYPE "$device" | tr -d '[:space:]')
                    observed_fs=$(blkid -p -s TYPE -o value "$device" 2>/dev/null || true)
                    observed_uuid=$(blkid -p -s UUID -o value "$device" 2>/dev/null || true)
                    if [ "${observed_type,,}" != "$expected_type" ]; then
                        log "ERROR: owned partition $expected_guid type changed"
                        exit 1
                    fi
                    if [ "$expected_fs" = NONE ]; then
                        if [ -n "$observed_fs" ]; then
                            log "ERROR: owned root $expected_guid already has a filesystem"
                            exit 1
                        fi
                    elif [ "$observed_fs" != "$expected_fs" ] ||
                         [ "${observed_uuid,,}" != "${expected_uuid,,}" ]; then
                        log "ERROR: owned partition $expected_guid filesystem changed"
                        exit 1
                    fi
                }
                verify_owned_partition {{linuxEsp.PartitionGuid:D}} {{linuxEsp.OffsetBytes.ToString(CultureInfo.InvariantCulture)}} {{linuxEsp.SizeBytes.ToString(CultureInfo.InvariantCulture)}} {{linuxEsp.PartitionType:D}} vfat {{ownership.Esp.LinuxEsp.FileSystemUuid}}
                verify_owned_partition {{root.PartitionGuid:D}} {{root.OffsetBytes.ToString(CultureInfo.InvariantCulture)}} {{root.SizeBytes.ToString(CultureInfo.InvariantCulture)}} {{root.PartitionType:D}} NONE NONE
                {{isoCheck}}
                log "Reviewed owned root and Linux ESP identities verified"
                """;
        var deferredCheck = deferred ? DeferredPartitionClosure(ownership) : string.Empty;
        var deferredRecipe = deferred ? $$"""
                if [[ ! "$TARGET_DISK" =~ ^[A-Za-z0-9_-]+$ ]]; then
                    log "ERROR: selected target device name is unsafe"
                    exit 1
                fi
                {
                    printf 'ignoredisk --only-use=%s\n' "$TARGET_DISK"
                    printf 'clearpart --none\n'
                    printf 'bootloader --boot-drive=%s\n' "$TARGET_DISK"
                    printf 'part /boot/efi --onpart=/dev/disk/by-partuuid/{{linuxEsp.PartitionGuid:D}} --noformat\n'
                    printf 'part / --onpart=/dev/disk/by-partuuid/{{root.PartitionGuid:D}} --fstype=ext4\n'
                } > /tmp/ks-storage.cfg
                log "Reviewed storage directives written for the uniquely matched target"
                """ : string.Empty;
        var preScript = "\n%pre --interpreter=/bin/bash --erroronfail\nset -euo pipefail\n" +
            template[targetAt..allocatorAt] + ownedCheck + deferredCheck + deferredRecipe +
            "\n%end\n";
        var rendered = template[..start] +
            "# Storage bound to the reviewed preparation generation.\n" +
            (deferred ? string.Empty : recipe) + preScript +
            (deferred ? "\n" + endMarker + "\n" : string.Empty) +
            template[(end + endMarker.Length)..];
        // The reviewed route carries no unattended Wi-Fi credential, so it
        // has no %pre script to create this optional include file.
        const string networkInclude = "%include /tmp/ks-network.cfg";
        if (rendered.Split(networkInclude, StringSplitOptions.None).Length != 2)
            return Observations.Failure<string>(ObservationAvailability.Ambiguous,
                "FedoraNetworkTemplateChanged");
        rendered = rendered.Replace(networkInclude, string.Empty,
            StringComparison.Ordinal);
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
        if (!actual.SequenceEqual(recipe.Split('\n', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal) ||
            rendered.Split(endMarker, StringSplitOptions.None).Length != (deferred ? 2 : 1))
            return Observations.Failure<string>(ObservationAvailability.Ambiguous, "FedoraUnexpectedStorageInstruction");
        if (rendered.Contains("nmcli device wifi connect", StringComparison.Ordinal) ||
            rendered.Contains("{{WIFI_LIST}}", StringComparison.Ordinal) ||
            rendered.Contains("%include /tmp/ks-network.cfg", StringComparison.Ordinal))
            return Observations.Failure<string>(ObservationAvailability.Ambiguous, "FedoraUnsafeNetworkHandoff");
        return Observations.Available(rendered);
    }

    private static string DeferredPartitionClosure(InstallationOwnershipV1 ownership)
    {
        var owned = ownership.Layout.StorageOwnership!;
        var partitions = owned.PreservedPartitions.Concat(owned.CreatedPartitions.Select(part => part.Identity))
            .OrderBy(part => part.PartitionGuid).ToArray();
        var expected = new StringBuilder();
        foreach (var part in partitions)
            expected.Append(part.PartitionGuid.ToString("D")).Append('|')
                .Append(part.PartitionType.ToString("D")).Append('|')
                .Append(part.OffsetBytes.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(part.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return $$"""

                # Check the complete selected GPT partition set, including preserved Windows
                # partitions, before Anaconda can consume a storage directive.
                EXPECTED_PARTITIONS=$(LC_ALL=C sort <<'IGLOO_EXPECTED_PARTITIONS'
                {{expected.ToString().TrimEnd('\n')}}
                IGLOO_EXPECTED_PARTITIONS
                )
                OBSERVED_PARTITIONS=()
                PARTITION_ROWS=$(lsblk -nr -o PATH,TYPE "/dev/$TARGET_DISK") || exit 1
                while read -r partition_path partition_kind; do
                    [ "$partition_kind" = part ] || continue
                    partition_name=$(basename "$(readlink -f "$partition_path")")
                    observed_guid=$(blkid -s PARTUUID -o value "$partition_path") || exit 1
                    observed_type=$(lsblk -n -o PARTTYPE "$partition_path" | tr -d '[:space:]')
                    [ -r "/sys/class/block/$partition_name/start" ] || exit 1
                    [ -r "/sys/class/block/$partition_name/size" ] || exit 1
                    observed_start=$(cat "/sys/class/block/$partition_name/start")
                    observed_sectors=$(cat "/sys/class/block/$partition_name/size")
                    OBSERVED_PARTITIONS+=("${observed_guid,,}|${observed_type,,}|$((observed_start * 512))|$((observed_sectors * 512))")
                done <<< "$PARTITION_ROWS"
                if [ "${#OBSERVED_PARTITIONS[@]}" -ne {{partitions.Length.ToString(CultureInfo.InvariantCulture)}} ] ||
                   [ "$(printf '%s\n' "${OBSERVED_PARTITIONS[@]}" | LC_ALL=C sort)" != "$EXPECTED_PARTITIONS" ]; then
                    log "ERROR: reviewed target GPT partition set changed"
                    exit 1
                fi
                log "Reviewed complete target GPT partition set verified"

                """;
    }
}
