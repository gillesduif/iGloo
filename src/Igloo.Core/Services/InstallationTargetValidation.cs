using Igloo.Core.Models;

namespace Igloo.Core.Services;

/// <summary>Pure identity and creation postconditions; never accesses or modifies a device.</summary>
public static class InstallationTargetValidation
{
    public static readonly Guid LinuxFileSystemType = new("0fc63daf-8483-4772-8e79-3d69d8477de4");
    public static readonly Guid EfiSystemType = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    private const long MiB = 1024 * 1024;

    public static void ValidateLayout(GptDiskLayout layout)
    {
        if (layout is null || layout.DiskGuid == Guid.Empty)
            throw Invalid("GPT disk identity is missing.");
        if (layout.LogicalSectorSize is not (512 or 4096)
            || layout.DiskSizeBytes < 2 * MiB
            || layout.DiskSizeBytes % layout.LogicalSectorSize != 0)
            throw Invalid("Unsupported or impossible disk geometry.");
        if (layout.Partitions is null || layout.Partitions.Count > 4096)
            throw Invalid("The complete GPT partition list is required (maximum 4096 entries).");

        var seen = new HashSet<Guid>();
        long previousEnd = 2L * layout.LogicalSectorSize;
        if (layout.Partitions.Any(p => p is null))
            throw Invalid("A GPT partition entry is missing.");
        foreach (var part in layout.Partitions.OrderBy(p => p.OffsetBytes))
        {
            if (part.PartitionGuid == Guid.Empty || part.GptType == Guid.Empty || !seen.Add(part.PartitionGuid))
                throw Invalid("Missing or duplicate GPT partition identity/type.");
            // Subtraction avoids overflow from hostile serialized lengths.
            if (part.OffsetBytes < previousEnd || part.LengthBytes <= 0
                || part.OffsetBytes % layout.LogicalSectorSize != 0
                || part.LengthBytes % layout.LogicalSectorSize != 0
                || part.OffsetBytes > layout.DiskSizeBytes - layout.LogicalSectorSize
                || part.LengthBytes > layout.DiskSizeBytes - layout.LogicalSectorSize - part.OffsetBytes)
                throw Invalid("Invalid, overlapping, unaligned or out-of-bounds GPT partition geometry.");
            previousEnd = part.OffsetBytes + part.LengthBytes;
        }
    }

    public static void ValidateUnchangedLayout(GptDiskLayout expected, GptDiskLayout actual)
    {
        ValidateLayout(expected);
        ValidateLayout(actual);
        if (expected.DiskGuid != actual.DiskGuid || expected.DiskSizeBytes != actual.DiskSizeBytes
            || expected.LogicalSectorSize != actual.LogicalSectorSize
            || expected.Partitions.Count != actual.Partitions.Count
            || !expected.Partitions.ToHashSet().SetEquals(actual.Partitions))
            throw Invalid("GPT disk identity, geometry or partition layout changed; the authorization is stale.");
    }

    public static void ValidateRequest(InstallationTargetRequest request)
    {
        if (request is null || request.InstallationId == Guid.Empty)
            throw Invalid("An explicit installation ID and creation request are required.");
        ValidateLayout(request.ExpectedLayout);
        var disk = request.ExpectedLayout;
        if (request.RootOffsetBytes < MiB || request.RootLengthBytes <= 0
            || request.RootOffsetBytes % disk.LogicalSectorSize != 0
            || request.RootLengthBytes % disk.LogicalSectorSize != 0
            || request.RootOffsetBytes > disk.DiskSizeBytes - MiB
            || request.RootLengthBytes > disk.DiskSizeBytes - MiB - request.RootOffsetBytes)
            throw Invalid("The authorized root extent is invalid or outside the conservative GPT bounds.");
        var end = request.RootOffsetBytes + request.RootLengthBytes;
        if (disk.Partitions.Any(p => request.RootOffsetBytes < p.OffsetBytes + p.LengthBytes && end > p.OffsetBytes))
            throw Invalid("The authorized extent is occupied; existing partitions cannot be adopted or reused.");
        ValidateEsp(disk, request.EspPartitionGuid, requireEsp: false);
    }

    public static InstallationTargetClaim CaptureCreated(
        InstallationTargetRequest request, GptDiskLayout actualLayout, Guid returnedPartitionGuid)
    {
        ValidateRequest(request);
        ValidateLayout(actualLayout);
        if (returnedPartitionGuid == Guid.Empty
            || request.ExpectedLayout.Partitions.Any(p => p.PartitionGuid == returnedPartitionGuid))
            throw Invalid("Creation did not return a new partition identity; no ownership claim can be issued.");
        var created = actualLayout.Partitions.SingleOrDefault(p => p.PartitionGuid == returnedPartitionGuid);
        if (created is null || created.GptType != LinuxFileSystemType
            || created.OffsetBytes != request.RootOffsetBytes || created.LengthBytes != request.RootLengthBytes)
            throw Invalid("The re-read partition does not match the exact authorized creation operation.");
        ValidateUnchangedLayout(request.ExpectedLayout, actualLayout with
        {
            Partitions = actualLayout.Partitions.Where(p => p.PartitionGuid != returnedPartitionGuid).ToArray(),
        });
        return new InstallationTargetClaim
        {
            Version = 1,
            InstallationId = request.InstallationId,
            Ownership = "created-by-igloo",
            // Freeze the provider's collection before publishing the verified receipt.
            Disk = actualLayout with { Partitions = actualLayout.Partitions.OrderBy(p => p.OffsetBytes).ToArray() },
            RootPartitionGuid = returnedPartitionGuid,
            EspPartitionGuid = request.EspPartitionGuid,
        };
    }

    public static InstallationTargetClaim RequireClaim(
        MigrationManifest manifest, Guid expectedInstallationId, bool requireEsp = true)
    {
        if (manifest is null || manifest.SchemaVersion != 1 || expectedInstallationId == Guid.Empty
            || manifest.InstallationId != expectedInstallationId || manifest.InstallationTarget is null)
            throw Invalid("The required installation identity is unavailable, unsupported or belongs to another run.");
        var claim = manifest.InstallationTarget;
        ValidateClaim(claim, requireEsp);
        if (claim.InstallationId != expectedInstallationId)
            throw Invalid("The target claim belongs to another installation run.");
        return claim;
    }

    public static void ValidateClaim(InstallationTargetClaim claim, bool requireEsp = true)
    {
        if (claim is null || claim.Version != 1 || claim.InstallationId == Guid.Empty
            || !string.Equals(claim.Ownership, "created-by-igloo", StringComparison.Ordinal))
            throw Invalid("A supported, verified iGloo creation receipt is required.");
        ValidateLayout(claim.Disk);
        if (claim.RootPartitionGuid == Guid.Empty)
            throw Invalid("Root partition identity is missing.");
        var root = claim.Disk.Partitions.SingleOrDefault(p => p.PartitionGuid == claim.RootPartitionGuid);
        if (root is null || root.GptType != LinuxFileSystemType)
            throw Invalid("The claimed Linux root partition is missing or has the wrong GPT type.");
        if (root.OffsetBytes < MiB || root.LengthBytes > claim.Disk.DiskSizeBytes - MiB - root.OffsetBytes)
            throw Invalid("The claimed root was not created within the authorized GPT bounds.");
        ValidateEsp(claim.Disk, claim.EspPartitionGuid, requireEsp);
    }

    public static GptDiskLayout ValidateObserved(InstallationTargetClaim claim,
        IReadOnlyList<GptDiskLayout> observed, bool requireEsp = true)
    {
        ValidateClaim(claim, requireEsp);
        if (observed is null)
            throw Invalid("No inspected GPT inventory was supplied.");
        var diskGuids = new HashSet<Guid>();
        var partitionGuids = new HashSet<Guid>();
        foreach (var disk in observed)
        {
            ValidateLayout(disk);
            if (!diskGuids.Add(disk.DiskGuid)
                || disk.Partitions.Any(p => !partitionGuids.Add(p.PartitionGuid)))
                throw Invalid("Duplicate disk or partition GUIDs in the observed inventory; identity is ambiguous.");
        }
        var matches = observed.Where(d => d.DiskGuid == claim.Disk.DiskGuid).ToArray();
        if (matches.Length != 1)
            throw Invalid("Exactly one disk must match the authorized GPT disk GUID.");
        ValidateUnchangedLayout(claim.Disk, matches[0]);
        return matches[0];
    }

    private static void ValidateEsp(GptDiskLayout disk, Guid? espGuid, bool requireEsp)
    {
        if (espGuid is null && !requireEsp)
            return;
        if (espGuid is null || espGuid == Guid.Empty
            || disk.Partitions.SingleOrDefault(p => p.PartitionGuid == espGuid)?.GptType != EfiSystemType)
            throw Invalid("The explicitly authorized EFI System Partition is missing or has the wrong GPT type.");
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
