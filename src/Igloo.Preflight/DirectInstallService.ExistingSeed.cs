using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;

namespace Igloo.Preflight;

public sealed partial class DirectInstallService
{
    private static readonly string[] SeedUserDataDirectories = ["files", "browser-profiles"];
    private static readonly JsonSerializerOptions ExistingSeedJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    private static InvalidOperationException ExistingSeedOwnershipUnavailable(Exception? inner = null) =>
        new("Ownership of the existing installer partition could not be verified. " +
            "Its contents have been preserved; a volume label alone does not authorize reuse or deletion.", inner);

    internal static CanonicalVolumeIdentityV1 RequireReusableSeedTarget(
        Observation<WindowsStorageSnapshot> observed, uint diskNumber, uint partitionNumber,
        char letter, string expectedLabel)
    {
        if (observed.Availability != ObservationAvailability.Available ||
            observed.Value.Partitions.IsDefault || observed.Value.Volumes.IsDefault)
            throw ExistingSeedOwnershipUnavailable();
        if (observed.Value.Partitions.Count(p => p.DriveLetter.Availability == ObservationAvailability.Available &&
                char.ToUpperInvariant(p.DriveLetter.Value) == char.ToUpperInvariant(letter)) != 1 ||
            observed.Value.Volumes.Count(v => v.DriveLetter.Availability == ObservationAvailability.Available &&
                char.ToUpperInvariant(v.DriveLetter.Value) == char.ToUpperInvariant(letter)) != 1)
            throw ExistingSeedOwnershipUnavailable();
        var partitions = observed.Value.Partitions.Where(p =>
            p.DiskNumber.Availability == ObservationAvailability.Available && p.DiskNumber.Value == diskNumber &&
            p.Number.Availability == ObservationAvailability.Available && p.Number.Value == partitionNumber).ToArray();
        if (partitions.Length != 1) throw ExistingSeedOwnershipUnavailable();
        var partition = partitions[0];
        // FAT32 does not distinguish a seed from the Windows ESP. Require the
        // ordinary data type and explicit non-protected state before reading it.
        if (partition.PartitionGuid.Availability != ObservationAvailability.Available ||
            partition.GptType.Availability != ObservationAvailability.Available ||
            partition.GptType.Value != new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7") ||
            partition.DriveLetter.Availability != ObservationAvailability.Available ||
            char.ToUpperInvariant(partition.DriveLetter.Value) != char.ToUpperInvariant(letter) ||
            !ObservedFalse(partition.IsBoot) || !ObservedFalse(partition.IsSystem) || !ObservedFalse(partition.IsActive))
            throw ExistingSeedOwnershipUnavailable();
        var volumes = observed.Value.Volumes.Where(v =>
            v.PartitionGuid.Availability == ObservationAvailability.Available &&
            v.PartitionGuid.Value == partition.PartitionGuid.Value).ToArray();
        if (volumes.Length != 1) throw ExistingSeedOwnershipUnavailable();
        var volume = volumes[0];
        if (volume.VolumeGuid.Availability != ObservationAvailability.Available ||
            volume.DriveLetter.Availability != ObservationAvailability.Available ||
            char.ToUpperInvariant(volume.DriveLetter.Value) != char.ToUpperInvariant(letter) ||
            volume.Label.Availability != ObservationAvailability.Available ||
            !string.Equals(volume.Label.Value, expectedLabel, StringComparison.OrdinalIgnoreCase) ||
            volume.FileSystem.Availability != ObservationAvailability.Available ||
            !string.Equals(volume.FileSystem.Value, "FAT32", StringComparison.OrdinalIgnoreCase))
            throw ExistingSeedOwnershipUnavailable();
        var bound = CanonicalRecoveryIdentity.BindForInstallation(observed, volume.VolumeGuid.Value);
        if (bound.Availability != ObservationAvailability.Available)
            throw ExistingSeedOwnershipUnavailable();
        return bound.Value;

        static bool ObservedFalse(Observation<bool> value) =>
            value.Availability == ObservationAvailability.Available && !value.Value;
    }

    internal static CanonicalVolumeIdentityV1 RequireReusableInstallerSeed(
        Observation<WindowsStorageSnapshot> observed, uint diskNumber, uint partitionNumber,
        char letter, string expectedLabel, string seedRoot,
        IReadOnlyCollection<string> incomingStagedPaths, IReadOnlyCollection<string> seedOutputPaths,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(incomingStagedPaths);
        var target = RequireReusableSeedTarget(observed, diskNumber, partitionNumber, letter, expectedLabel);
        try
        {
            var root = Path.GetFullPath(seedRoot);
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The existing seed root is unavailable or redirected.");
            var marker = Path.Join(root, SeedIdentityWriter.FileName);
            if ((File.GetAttributes(marker) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("The existing seed identity is redirected.");
            using var stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Follows the SeedIdentityWriter inventory bounds (1,000,000 bound files).
            if (stream.Length is <= 0 or > 256L * 1024 * 1024)
                throw new InvalidDataException("The existing seed identity has an invalid size.");
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            RequireUniqueProperties(document.RootElement);
            var saved = document.RootElement.Deserialize<SeedIdentityV1>(ExistingSeedJson);
            if (saved is null || saved.Version != 1 || saved.DiskGuid != target.Disk.GptDiskGuid ||
                saved.PartitionGuid != target.PartitionGuid || saved.Files is null ||
                saved.Files.Count is < 4 or > 1_000_256 ||
                saved.Files.Any(file => file is null || string.IsNullOrEmpty(file.Path) ||
                    file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit)) ||
                saved.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != saved.Files.Count ||
                !saved.Files.Any(file => file.Path == "igloo-boot/initrd") ||
                !saved.Files.Any(file => file.Path.StartsWith("igloo-agent/", StringComparison.Ordinal)))
                throw new InvalidDataException("The existing seed identity does not match this installer partition.");

            // Reuse the seed writer's path rules and selected-data enumeration.
            // Missing, altered, unselected or redirected content invalidates reuse.
            var agentPaths = saved.Files.Where(file => file.Path.StartsWith("igloo-agent/", StringComparison.Ordinal))
                .Select(file => file.Path).ToArray();
            var currentBytes = SeedIdentityWriter.BuildBytesAsync(root, saved.DiskGuid,
                saved.PartitionGuid, agentPaths, ct).GetAwaiter().GetResult();
            var current = JsonSerializer.Deserialize<SeedIdentityV1>(currentBytes, ExistingSeedJson)!;
            if (!saved.Files.OrderBy(file => file.Path, StringComparer.Ordinal)
                    .SequenceEqual(current.Files.OrderBy(file => file.Path, StringComparer.Ordinal)))
                throw new InvalidDataException("The existing seed files no longer match their recorded identity.");
            var incoming = incomingStagedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var previousUserDirectories = SeedUserDataDirectories
                .Select(name => Path.Join(root, name)).Where(Directory.Exists)
                .SelectMany(directory => Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                    .Prepend(directory))
                .Select(directory => Path.GetRelativePath(root, directory).Replace('\\', '/') + "/")
                .Where(path => path.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("browser-profiles/", StringComparison.OrdinalIgnoreCase));
            if (saved.Files.Any(file => file.Path != "igloo-boot/initrd" && !incoming.Contains(file.Path)) ||
                previousUserDirectories.Any(path => !incoming.Contains(path)))
                throw new InvalidOperationException("The existing installer partition contains previous migration files " +
                    "that are absent from the new selection. Its contents have been preserved; reuse would retain stale data.");
            RequireSeedReuseReceipt(root, target, seedOutputPaths, ct);
            return target;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            SecurityException or JsonException or ArgumentException)
        {
            throw ExistingSeedOwnershipUnavailable(error);
        }
    }

    private static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("The existing seed identity contains duplicate properties.");
                RequireUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RequireUniqueProperties(item);
    }
}
