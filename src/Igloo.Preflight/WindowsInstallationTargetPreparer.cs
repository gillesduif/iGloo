using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Services;

namespace Igloo.Preflight;

/// <summary>
/// Explicitly creates and captures one unformatted GPT root partition in a caller-approved
/// free extent. This service is opt-in and is not connected to legacy installer staging.
/// </summary>
/// <remarks>
/// The caller must approve the complete expected layout and exact already-free byte extent.
/// A Linux GPT type is never evidence of ownership. No existing partition is reused, resized,
/// formatted or removed. Failure after creation leaves the new partition in place without
/// a claim; this service never retries or compensates by deleting a partition.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsInstallationTargetPreparer : IInstallationTargetPreparer
{
    private readonly IInstallationTargetStorage _storage;

    public WindowsInstallationTargetPreparer() : this(new WindowsInstallationTargetStorage())
    {
    }

    internal WindowsInstallationTargetPreparer(IInstallationTargetStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    /// <summary>Reads a fresh layout by unique GPT disk GUID without changing the partition table.</summary>
    public Task<GptDiskLayout> ReadLayoutAsync(Guid diskGuid, CancellationToken ct = default)
    {
        if (diskGuid == Guid.Empty)
            throw new ArgumentException("A nonempty GPT disk GUID is required.", nameof(diskGuid));
        return Task.Run(() =>
        {
            using var disk = _storage.OpenDisk(diskGuid);
            ct.ThrowIfCancellationRequested();
            var layout = disk.ReadLayout();
            InstallationTargetValidation.ValidateLayout(layout);
            if (layout.DiskGuid != diskGuid)
                throw new InvalidDataException("The storage provider returned a different GPT disk.");
            ct.ThrowIfCancellationRequested();
            return layout;
        }, ct);
    }

    /// <summary>
    /// Creates exactly one partition and writes its validated ownership claim to a pending
    /// migration manifest. A changed manifest or disk layout aborts instead of selecting a fallback.
    /// </summary>
    public async Task<InstallationTargetClaim> PrepareAsync(
        InstallationTargetRequest request, string manifestPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        // Keep the approved partition list independent from a caller-owned mutable collection.
        var expected = request.ExpectedLayout
            ?? throw new InvalidDataException("The caller-approved GPT layout is missing.");
        var partitions = expected.Partitions
            ?? throw new InvalidDataException("The caller-approved partition list is missing.");
        var approved = request with
        {
            ExpectedLayout = expected with { Partitions = partitions.ToArray() },
        };
        InstallationTargetValidation.ValidateRequest(approved);
        ct.ThrowIfCancellationRequested();
        var manifestHash = await InstallationTargetManifest.ValidatePendingAsync(
            manifestPath, approved.InstallationId, ct).ConfigureAwait(false);

        var claim = await Task.Run(() =>
        {
            using var disk = _storage.OpenDisk(approved.ExpectedLayout.DiskGuid);
            ct.ThrowIfCancellationRequested();
            InstallationTargetValidation.ValidateUnchangedLayout(approved.ExpectedLayout, disk.ReadLayout());
            ct.ThrowIfCancellationRequested();
            var createdGuid = disk.CreateRootPartition(approved.RootOffsetBytes, approved.RootLengthBytes, ct);
            ct.ThrowIfCancellationRequested();
            var actual = disk.ReadLayout();
            return InstallationTargetValidation.CaptureCreated(approved, actual, createdGuid);
        }, ct).ConfigureAwait(false);

        await InstallationTargetManifest.WriteClaimAsync(manifestPath, claim, manifestHash, ct).ConfigureAwait(false);
        return claim;
    }
}

// A session binds mutation to the same freshly resolved disk object whose layout was checked.
internal interface IInstallationTargetStorage
{
    IInstallationTargetDisk OpenDisk(Guid diskGuid);
}

internal interface IInstallationTargetDisk : IDisposable
{
    GptDiskLayout ReadLayout();
    Guid CreateRootPartition(long offsetBytes, long lengthBytes, CancellationToken ct);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsInstallationTargetStorage : IInstallationTargetStorage
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    public IInstallationTargetDisk OpenDisk(Guid diskGuid) => new DiskSession(diskGuid, ResolveUnique(diskGuid));

    private static ManagementObject ResolveUnique(Guid diskGuid)
    {
        using var searcher = new ManagementObjectSearcher(StorageNamespace, "SELECT * FROM MSFT_Disk");
        using var disks = searcher.Get();
        ManagementObject? match = null;
        try
        {
            foreach (ManagementObject disk in disks)
            {
                using (disk)
                {
                    if (!Guid.TryParse(disk["Guid"] as string, out var candidate) || candidate != diskGuid)
                        continue;
                    if (match is not null)
                        throw new InvalidDataException("Multiple disks have the requested GPT GUID; refusing ambiguous storage.");
                    match = (ManagementObject)disk.Clone();
                }
            }
            return match ?? throw new InvalidDataException("The requested GPT disk is absent; no fallback is permitted.");
        }
        catch
        {
            match?.Dispose();
            throw;
        }
    }

    private sealed class DiskSession : IInstallationTargetDisk
    {
        private static readonly Guid LinuxFilesystemType = new("0fc63daf-8483-4772-8e79-3d69d8477de4");
        private readonly Guid _diskGuid;
        private ManagementObject _disk;
        private GptDiskLayout? _lastLayout;

        internal DiskSession(Guid diskGuid, ManagementObject disk)
        {
            _diskGuid = diskGuid;
            _disk = disk;
        }

        public GptDiskLayout ReadLayout()
        {
            _disk.Get();
            RequireUsableDisk();
            using (var refresh = _disk.InvokeMethod("Refresh", null, null))
                RequireSuccess(refresh, "MSFT_Disk.Refresh");

            // Refresh updates the provider cache. Resolve again, including the uniqueness check,
            // before walking associations; a disk number is never used as persistent identity.
            var fresh = ResolveUnique(_diskGuid);
            _disk.Dispose();
            _disk = fresh;
            RequireUsableDisk();
            var diskNumber = ReadUInt32(_disk, "Number");
            var partitions = new List<GptPartitionIdentity>();
            using var related = _disk.GetRelated("MSFT_Partition", "MSFT_DiskToPartition",
                null, null, null, null, false, null);
            foreach (ManagementObject partition in related)
            {
                using (partition)
                {
                    if (ReadUInt32(partition, "DiskNumber") != diskNumber)
                        throw new InvalidDataException("The storage association returned a partition on another disk.");
                    partitions.Add(ReadPartition(partition));
                }
            }
            var layout = new GptDiskLayout
            {
                DiskGuid = ReadGuid(_disk, "Guid"),
                LogicalSectorSize = checked((int)ReadUInt32(_disk, "LogicalSectorSize")),
                DiskSizeBytes = ReadInt64(_disk, "Size"),
                Partitions = partitions.ToArray(),
            };
            InstallationTargetValidation.ValidateLayout(layout);
            _lastLayout = layout;
            return layout;
        }

        public Guid CreateRootPartition(long offsetBytes, long lengthBytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var approved = _lastLayout
                ?? throw new InvalidOperationException("Read and validate the disk layout before requesting creation.");
            InstallationTargetValidation.ValidateUnchangedLayout(approved, ReadLayout());
            ct.ThrowIfCancellationRequested();

            // CreatePartition's Size, Offset and Alignment are bytes. Do not use the default
            // extent, maximum-size mode, drive-letter assignment, formatting or a second command.
            using var parameters = _disk.GetMethodParameters("CreatePartition");
            parameters["Size"] = checked((ulong)lengthBytes);
            parameters["Offset"] = checked((ulong)offsetBytes);
            parameters["Alignment"] = checked((uint)approved.LogicalSectorSize);
            parameters["GptType"] = LinuxFilesystemType.ToString("D");
            parameters["AssignDriveLetter"] = false;
            ct.ThrowIfCancellationRequested();
            using var result = _disk.InvokeMethod("CreatePartition", parameters, null);
            RequireSuccess(result, "MSFT_Disk.CreatePartition");

            // The WMI EmbeddedInstance qualifier exposes this as ManagementBaseObject. Never
            // reinterpret a serialized string as an object path or infer the returned PARTUUID.
            using var created = result!["CreatedPartition"] as ManagementBaseObject
                ?? throw new InvalidDataException("CreatePartition did not return an embedded partition identity; no claim was written.");
            if (!string.Equals(created.ClassPath.ClassName, "MSFT_Partition", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("CreatePartition returned an unexpected embedded object type.");
            var partition = ReadPartition(created);
            if (ReadUInt32(created, "DiskNumber") != ReadUInt32(_disk, "Number")
                || partition.GptType != LinuxFilesystemType
                || partition.OffsetBytes != offsetBytes
                || partition.LengthBytes != lengthBytes)
                throw new InvalidDataException("CreatePartition returned a partition outside the approved target geometry; no claim was written.");
            return partition.PartitionGuid;
        }

        private void RequireUsableDisk()
        {
            if (ReadGuid(_disk, "Guid") != _diskGuid || ReadUInt32(_disk, "PartitionStyle") != 2)
                throw new InvalidDataException("The requested GPT disk identity changed.");
            if (_disk["IsOffline"] is not false || _disk["IsReadOnly"] is not false)
                throw new InvalidDataException("The requested disk must be online and writable.");
        }

        public void Dispose() => _disk.Dispose();
    }

    private static GptPartitionIdentity ReadPartition(ManagementBaseObject partition) => new()
    {
        PartitionGuid = ReadGuid(partition, "Guid"),
        GptType = ReadGuid(partition, "GptType"),
        OffsetBytes = ReadInt64(partition, "Offset"),
        LengthBytes = ReadInt64(partition, "Size"),
    };

    private static Guid ReadGuid(ManagementBaseObject value, string name) =>
        Guid.TryParse(value[name] as string, out var parsed) && parsed != Guid.Empty
            ? parsed : throw new InvalidDataException($"Storage property {name} is missing or not a nonempty GUID.");

    private static uint ReadUInt32(ManagementBaseObject value, string name) =>
        Convert.ToUInt32(value[name] ?? throw new InvalidDataException($"Storage property {name} is missing."),
            CultureInfo.InvariantCulture);

    private static long ReadInt64(ManagementBaseObject value, string name) =>
        Convert.ToInt64(value[name] ?? throw new InvalidDataException($"Storage property {name} is missing."),
            CultureInfo.InvariantCulture);

    private static void RequireSuccess(ManagementBaseObject? result, string operation)
    {
        if (result is null)
            throw new InvalidOperationException($"{operation} returned no status; automatic retry is prohibited.");
        var status = ReadUInt32(result, "ReturnValue");
        if (status != 0)
            throw new InvalidOperationException($"{operation} failed with status {status}; automatic retry is prohibited.");
    }
}
