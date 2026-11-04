using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Preflight.CommunityPreparation;

namespace Igloo.Preflight;

internal sealed record ReviewedInitrdFileV1(string Name, ImmutableArray<byte> Contents);

// Owned bytes and the exact boot arguments for the reviewed storage handoff.
// Constructing this record does not copy anything to a Windows volume or EFI.
internal sealed record ReviewedBootPayloadV1(ReviewedInstallerHandoffV1 Storage,
    string BootSpecSha256, string GrubConfig, string LinuxEspGrubConfig,
    string? LinuxEspCompiledPrefixConfigPath,
    ImmutableArray<ReviewedInitrdFileV1> InitrdFiles);

public sealed partial class DirectInstallService
{
    internal static ReviewedBootPayloadV1 BuildReviewedBootPayload(
        ReviewedContentHandoffV1 content, InstallerBootSpec bootSpec,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(bootSpec);
        if (content.Storage is null || content.StagedFiles.IsDefaultOrEmpty ||
            !Path.IsPathFullyQualified(content.StagingDirectory) ||
            !string.Equals(content.BootSpecSha256,
                ReviewedPartitionCreationService.HashBootSpec(bootSpec), StringComparison.Ordinal))
            throw new InvalidDataException("The reviewed boot payload has no exact content or recipe binding.");

        var stage = Path.GetFullPath(content.StagingDirectory);
        if (!string.Equals(stage, content.StagingDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reviewed boot staging path changed.");
        RequirePlainDirectoryTree(stage);

        var manifestBytes = ReadPinnedReviewedBootFile(content, "migration-manifest.json", ct);
        MigrationManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<MigrationManifest>(manifestBytes) ??
                throw new JsonException("The reviewed migration manifest is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The reviewed boot manifest is invalid.", error);
        }
        if (manifest.SchemaVersion != 1 || manifest.User is null || manifest.Hardware is null ||
            manifest.Hardware.TargetDiskGuid != content.Storage.TargetDisk.GptDiskGuid ||
            content.Storage.TargetDisk.SizeBytes > long.MaxValue ||
            manifest.Hardware.TargetDiskBytes != (long)content.Storage.TargetDisk.SizeBytes ||
            !SafeBootToken(manifest.User.Locale) || !SafeBootToken(manifest.User.Keymap))
            throw new InvalidDataException("The reviewed boot locale, keymap or target is invalid.");

        var files = ImmutableArray.CreateBuilder<ReviewedInitrdFileV1>();
        if (bootSpec.ConfigDelivery == ConfigDelivery.InjectIntoInitrd)
        {
            var configName = bootSpec.InitrdConfigPath;
            if (string.IsNullOrWhiteSpace(configName) ||
                !string.Equals(Path.GetFileName(configName), configName, StringComparison.Ordinal))
                throw new InvalidDataException("The reviewed initrd config name is unsafe.");
            files.Add(new(configName,
                ImmutableArray.CreateRange(ReadPinnedReviewedBootFile(content, configName, ct))));
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { configName };
            foreach (var relative in bootSpec.InitrdExtraPaths ??
                throw new InvalidDataException("The reviewed initrd extra list is unavailable."))
            {
                ct.ThrowIfCancellationRequested();
                var name = relative.Replace('\\', '/').Split('/').Last();
                if (!usedNames.Add(name))
                    throw new InvalidDataException("The reviewed initrd filenames are ambiguous.");
                files.Add(new(name,
                    ImmutableArray.CreateRange(ReadPinnedReviewedBootFile(content, relative, ct))));
            }
        }
        else if (bootSpec.InitrdExtraPaths is { Count: > 0 })
        {
            throw new InvalidDataException("A volume-delivered installer has unexpected initrd extras.");
        }

        var storage = content.Storage;
        if (storage.Payload.PartitionGuid == Guid.Empty ||
            storage.LinuxEsp.PartitionGuid == Guid.Empty ||
            storage.LinuxEsp.PartitionGuid == storage.Payload.PartitionGuid ||
            storage.Payload.Disk != storage.TargetDisk || storage.LinuxEsp.Disk != storage.TargetDisk)
            throw new InvalidDataException("The reviewed boot volumes have no distinct exact GPT binding.");
        var grub = BuildGrubConfig(bootSpec, manifest.User.Locale, manifest.User.Keymap,
            storage.TargetDisk.GptDiskGuid, storage.Payload.PartitionGuid,
            storage.LinuxEsp.PartitionGuid);
        var (espGrub, compiledPrefixPath) = manifest.DistroId switch
        {
            "debian" or "linuxmint-cinnamon" when
                bootSpec.ConfigDelivery == ConfigDelivery.InjectIntoInitrd =>
                (BuildReviewedLinuxEspTrampoline(storage.LinuxEsp.PartitionGuid,
                    storage.Payload.PartitionGuid),
                    ReviewedSeedFileWriter.LinuxEspCompiledPrefixConfigPath),
            "fedora-kde" when bootSpec.ConfigDelivery == ConfigDelivery.OemDrvLabel =>
                (BuildReviewedLinuxEspTrampoline(storage.LinuxEsp.PartitionGuid,
                    storage.Payload.PartitionGuid),
                    ReviewedSeedFileWriter.LinuxEspFedoraPrefixConfigPath),
            _ => throw new InvalidDataException(
                "The reviewed Linux ESP has no verified provider boot configuration."),
        };
        return new(storage, content.BootSpecSha256, grub, espGrub,
            compiledPrefixPath, files.ToImmutable());
    }

    private static string BuildReviewedLinuxEspTrampoline(Guid linuxEspPartuuid,
        Guid seedPartuuid)
    {
        if (linuxEspPartuuid == Guid.Empty || seedPartuuid == Guid.Empty ||
            linuxEspPartuuid == seedPartuuid)
            throw new InvalidDataException("The reviewed GRUB handoff has no distinct ESP and seed partitions.");
        // The selected ISO GRUB reads a config from its compiled ESP prefix.
        // Select the receipted seed before the existing OEMDRV config runs.
        // A decoy identity file on another partition fails the PARTUUID check.
        return $$"""
            serial --unit=0 --speed=115200
            terminal_output serial
            insmod part_gpt
            insmod fat
            insmod probe

            set igloo_esp="$root"
            if [ -z "$igloo_esp" ]; then
                echo "The reviewed Linux ESP is unavailable."
                halt
            fi
            set igloo_esp_partuuid=
            probe --set=igloo_esp_partuuid --part-uuid "($igloo_esp)"
            if [ "$igloo_esp_partuuid" != "{{linuxEspPartuuid:D}}" ]; then
                echo "The reviewed Linux ESP identity changed."
                halt
            fi

            set igloo_seed=
            search --no-floppy --set=igloo_seed --file /igloo-seed-identity.json
            if [ -z "$igloo_seed" ]; then
                echo "The installer seed identity is unavailable."
                halt
            fi
            set igloo_seed_partuuid=
            probe --set=igloo_seed_partuuid --part-uuid "($igloo_seed)"
            if [ "$igloo_seed_partuuid" != "{{seedPartuuid:D}}" ]; then
                echo "The installer seed partition identity changed."
                halt
            fi
            set root="$igloo_seed"
            configfile ($root)/EFI/BOOT/grub.cfg
            echo "The installer seed configuration could not be loaded."
            halt
            """;
    }

    private static byte[] ReadPinnedReviewedBootFile(ReviewedContentHandoffV1 content,
        string relative, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(relative) ||
            !ReviewedSeedPathRules.TrySegments("\\" + relative.Replace('/', '\\'),
                out var segments))
            throw new InvalidDataException("A reviewed boot file path is unsafe.");
        var portable = string.Join('/', segments);
        var matches = content.StagedFiles.Where(file =>
            string.Equals(file.RelativePath.Replace('\\', '/'), portable,
                StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || matches[0].SizeBytes is <= 0 or > 8 * 1024 * 1024)
            throw new InvalidDataException("A reviewed boot file is unavailable or oversized.");
        var path = Path.GetFullPath(Path.Join(content.StagingDirectory, Path.Join(segments)));
        if (!path.StartsWith(content.StagingDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A reviewed boot file escaped staging.");
        RequirePlainDirectoryTree(Path.GetDirectoryName(path)!);
        RequirePlainFile(path);
        var bytes = File.ReadAllBytes(path);
        ct.ThrowIfCancellationRequested();
        if (bytes.LongLength != matches[0].SizeBytes ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)),
                matches[0].Sha256, StringComparison.Ordinal) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A reviewed boot file changed after its content handoff.");
        return bytes;
    }

    private static bool SafeBootToken(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(ch =>
            char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.' or '-' or '+' or '@');
}
