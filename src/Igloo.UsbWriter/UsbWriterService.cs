using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Igloo.UsbWriter;

[SupportedOSPlatform("windows")]
public sealed partial class UsbWriterService : IUsbWriterService
{
    // 128 KB - a power-of-two multiple of the 512-byte sector size; good USB throughput.
    private const int BufferSize = 128 * 1024;

    private readonly ILogger<UsbWriterService> _logger;

    public UsbWriterService(ILogger<UsbWriterService> logger) => _logger = logger;

    //   Enumerate                               ─

    public Task<IReadOnlyList<UsbDriveInfo>> EnumerateDrivesAsync(
        CancellationToken ct = default)
    {
        // TODO v1.1: Win32_DiskDrive first-call latency on a cold WMI host is typically
        // 1–3 seconds.  Lower-latency alternative: SetupDiGetClassDevs with
        // GUID_DEVINTERFACE_DISK (SetupAPI) goes through the kernel device manager instead.
        var drives = new List<UsbDriveInfo>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, PNPDeviceID, Index, Model, Size, BytesPerSector FROM Win32_DiskDrive " +
            "WHERE InterfaceType='USB'");

        foreach (ManagementObject obj in searcher.Get())
        {
            ct.ThrowIfCancellationRequested();

            var deviceId = obj["DeviceID"]?.ToString() ?? string.Empty;
            var index = Convert.ToInt32(obj["Index"], CultureInfo.InvariantCulture);
            var model = obj["Model"]?.ToString() ?? "Unknown USB Drive";
            var sizeStr = obj["Size"]?.ToString();
            var sizeBytes = sizeStr is not null ? long.Parse(sizeStr, CultureInfo.InvariantCulture) : 0L;
            var pnpDeviceId = obj["PNPDeviceID"]?.ToString();
            var logicalSectorBytes = obj["BytesPerSector"] is { } sector
                ? Convert.ToInt32(sector, CultureInfo.InvariantCulture) : (int?)null;

            drives.Add(new UsbDriveInfo(index, model, sizeBytes, deviceId,
                pnpDeviceId, logicalSectorBytes));
            _logger.LogDebug("USB drive found: [{Index}] {Model} ({Size:N0} bytes)", index, model, sizeBytes);
        }

        return Task.FromResult<IReadOnlyList<UsbDriveInfo>>(drives);
    }

    //   Write (orchestration)                         ─

    public async Task WriteAsync(
        UsbDriveInfo drive,
        string isoPath,
        string stagingDirectory,
        string expectedIsoSha256,
        IProgress<UsbWriteProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(drive);

        if (drive.LogicalSectorBytes != 512)
            throw new InvalidOperationException(
                "The selected USB drive must report 512-byte logical sectors for this installer image.");

        if (!IsCurrentProcessElevated())
            throw new UnauthorizedAccessException(
                "Writing to a physical drive requires administrator rights. " +
                "Please restart iGloo as Administrator and repeat the process from this step.");

        RequireSourceOutsideSelectedDrive(drive, isoPath);
        RequireSourceOutsideSelectedDrive(drive, stagingDirectory);
        RequireNonSystemUsbDisk(drive);
        await EnsureCurrentDriveAsync(drive, ct).ConfigureAwait(false);

        // Hold a read-only handle through the raw copy so the authenticated image
        // cannot be replaced or modified between verification and use.
        if ((File.GetAttributes(isoPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The verified installer image identity is unavailable.");
        using var isoStream = new FileStream(isoPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferSize, useAsync: true);
        await VerifyIsoAsync(isoStream, expectedIsoSha256, ct).ConfigureAwait(false);
        await RequireGptInstallerImageAsync(isoStream, ct).ConfigureAwait(false);
        // Compute sizes upfront so we can fail before touching the drive.
        var isoSize = isoStream.Length;
        var stagingSize = InspectStagingDirectory(stagingDirectory);
        var unattendedKickstart = File.Exists(Path.Join(stagingDirectory, "ks.cfg"));
        if (unattendedKickstart)
            throw new NotSupportedException(
                "This raw Fedora installer image cannot load its kickstart from an appended " +
                "partition. USB preparation is unavailable until a bootable layout is verified.");
        var pinnedAgent = await OpenObservedAgentArtifactsAsync(stagingDirectory, ct)
            .ConfigureAwait(false);
        await using var pinnedAgentLease = pinnedAgent.ConfigureAwait(false);

        // Partition must hold all staging files plus a 256 MB buffer; minimum 512 MB.
        var partSizeMbLong = Math.Max(512L, stagingSize / (1024 * 1024) + 256);
        if (partSizeMbLong > int.MaxValue)
            throw new InvalidOperationException("The migration staging directory is too large for USB preparation.");
        var partSizeMb = (int)partSizeMbLong;

        //   Pre-flight: does the drive have enough room?            
        ValidateFit(drive, isoSize, partSizeMb);

        //   Dismount volumes on target disk                  ─
        // Windows refuses raw WriteFile to \\.\PhysicalDriveN while any volume on
        // that disk is mounted, even from an elevated process (Win32 error 5 /
        // ERROR_ACCESS_DENIED).  We lock and force-dismount every volume on the
        // target disk and keep the handles open so Windows cannot re-mount them
        // during the write.  Disposing the set releases them so Windows can re-mount.
        using var heldVolumeHandles = LockAndDismountVolumesOnDisk(drive.DriveIndex);
        {
            //   Phase 1: raw ISO write - cancelable              ─

            _logger.LogInformation(
                "Phase 1 - writing ISO {Path} ({Size:N0} bytes) to {Device}",
                isoPath, isoSize, drive.DeviceId);

            progress?.Report(new UsbWriteProgress(
                UsbWritePhase.WritingIso, 0, isoSize, "Writing installer image…"));

            await WriteIsoRawAsync(drive.DeviceId, isoStream, isoSize, progress, ct).ConfigureAwait(false);
            await EnsureCurrentDriveAsync(drive, ct).ConfigureAwait(false);
            await VerifyRawImageReadbackAsync(drive.DeviceId, isoSize, expectedIsoSha256,
                ct).ConfigureAwait(false);

            // Cancellation point: checked between Phase 1 and Phase 2.
            ct.ThrowIfCancellationRequested();

            //   GRUB config patch - must run BEFORE EnsureProtectiveMbrAsync  
            // EnsureProtectiveMbrAsync ends with IOCTL_DISK_UPDATE_PROPERTIES, which
            // tells Windows to re-scan the partition table.  Once Windows sees the
            // new partitions it may auto-mount them, after which raw WriteFile calls
            // to \\.\PhysicalDriveN are blocked by the volume manager.
            // Patching here, right after the raw ISO write, means Windows still
            // has no idea the partition layout has changed - raw writes always succeed.
            await EnsureCurrentDriveAsync(drive, ct).ConfigureAwait(false);
            try
            {
                await PatchGrubConfigAsync(drive, unattendedKickstart, progress).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or Win32Exception or InvalidOperationException)
            {
                if (unattendedKickstart)
                    throw new InvalidOperationException(
                        "The installer boot menu could not be bound to its kickstart. The USB preparation is incomplete.", ex);
                _logger.LogWarning(ex,
                    "GRUB config patch failed (non-fatal) - boot may require " +
                    "manual kernel parameters: nomodeset rd.live.check=0");
                progress?.Report(new UsbWriteProgress(
                    UsbWritePhase.PatchingGrub, 0, 0,
                    "⚠ GRUB patch failed - you may see a media check error on first boot."));
            }

            //   MBR → GPT fix (runs before diskpart, no separate UI phase)  
            // Fedora Live ISOs embed a hybrid MBR that fills all 4 primary
            // partition slots, so diskpart cannot create a 5th.  The disk has
            // a valid GPT at LBA 1.  Replace the hybrid MBR partition entries
            // with a single protective-MBR entry (type 0xEE) so Windows exposes
            // the GPT view and diskpart gains room to add the OEMDRV partition.
            // The raw write may trigger a device rescan. Recheck before each
            // following operation reopens the target by its Windows ordinal.
            await EnsureCurrentDriveAsync(drive, ct).ConfigureAwait(false);
            await EnsureProtectiveMbrAsync(drive.DeviceId, drive.SizeBytes).ConfigureAwait(false);
            await EnsureCurrentDriveAsync(drive, ct).ConfigureAwait(false);

            //   Phase 2: create OEMDRV partition - atomic (not cancelable)   

            _logger.LogInformation("Phase 2 - creating OEMDRV partition on disk {Index}", drive.DriveIndex);

            progress?.Report(new UsbWriteProgress(
                UsbWritePhase.CreatingOemdrv, 0, 0, "Creating OEMDRV partition…"));

            var driveLetter = FindFreeDriveLetter();

            // CancellationToken.None is intentional: interrupting diskpart mid-run can
            // leave the partition table in an inconsistent state.
            await RunDiskpartAsync(drive.DriveIndex, partSizeMb, driveLetter, CancellationToken.None).ConfigureAwait(false);

            // Give Windows a moment to mount the new FAT32 volume before we validate.
            await Task.Delay(2500, CancellationToken.None).ConfigureAwait(false);

            // Verify that diskpart actually created and mounted the partition.
            // diskpart returns exit code 0 even when individual commands fail silently.
            var seedIdentity = ValidateOemDrvMounted(drive, driveLetter, partSizeMb);

            // Cancellation point: checked between Phase 2 and Phase 3.
            ct.ThrowIfCancellationRequested();

            //   Phase 3: copy staging files - cancelable            

            _logger.LogInformation(
                "Phase 3 - copying staging directory {Dir} to {Letter}:\\",
                stagingDirectory, driveLetter);

            progress?.Report(new UsbWriteProgress(
                UsbWritePhase.CopyingFiles, 0, stagingSize, "Copying migration files…"));

            await CopyStagingFilesAsync($"{driveLetter}:\\", stagingDirectory, stagingSize,
                progress, ct).ConfigureAwait(false);
            await VerifyProviderAgentCopyAsync(stagingDirectory, $"{driveLetter}:\\",
                pinnedAgent, ct).ConfigureAwait(false);
            await EnsureCurrentDriveAsync(drive, ct).ConfigureAwait(false);
            var currentSeed = ValidateOemDrvMounted(drive, driveLetter, partSizeMb);
            if (currentSeed != seedIdentity)
                throw new InvalidOperationException("The installer seed partition changed while files were copied.");
            await SeedIdentityWriter.WriteAsync($"{driveLetter}:\\", seedIdentity.DiskGuid,
                seedIdentity.PartitionGuid, pinnedAgent.RelativePaths, ct)
                .ConfigureAwait(false);
            _logger.LogInformation("Staging copy complete: {Written:N0} bytes copied to {Root}",
                stagingSize, $"{driveLetter}:\\");

            progress?.Report(new UsbWriteProgress(UsbWritePhase.Complete, 0, 0, "USB drive is ready."));
            _logger.LogInformation("USB write complete - drive {Index} is ready", drive.DriveIndex);
        }
    }

    //   Pre-flight validation                         ─

    internal static bool SameObservedDrive(UsbDriveInfo selected, UsbDriveInfo current) =>
        selected.DriveIndex >= 0 && selected.DriveIndex == current.DriveIndex &&
        selected.LogicalSectorBytes == 512 && current.LogicalSectorBytes == 512 &&
        selected.SizeBytes > 0 && selected.SizeBytes == current.SizeBytes &&
        !string.IsNullOrWhiteSpace(selected.PnpDeviceId) &&
        string.Equals(selected.PnpDeviceId, current.PnpDeviceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(selected.DeviceId, @"\\.\PHYSICALDRIVE" + selected.DriveIndex.ToString(CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(selected.DeviceId, current.DeviceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(selected.Model, current.Model, StringComparison.Ordinal);

    internal static bool UniqueObservedDrive(UsbDriveInfo selected,
        IReadOnlyList<UsbDriveInfo> current) =>
        current.Count(d => d.DriveIndex == selected.DriveIndex) == 1 &&
        current.Count(d => !string.IsNullOrWhiteSpace(selected.PnpDeviceId) &&
            string.Equals(d.PnpDeviceId, selected.PnpDeviceId,
                StringComparison.OrdinalIgnoreCase)) == 1 &&
        current.Any(d => SameObservedDrive(selected, d));

    internal static async Task VerifyIsoAsync(Stream source, string expectedSha256,
        CancellationToken ct = default)
    {
        if (expectedSha256 is null || expectedSha256.Length != 64 ||
            !expectedSha256.All(Uri.IsHexDigit) || !source.CanRead || !source.CanSeek ||
            source.Length <= 0)
            throw new InvalidDataException("The verified installer image identity is unavailable.");
        source.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, ct).ConfigureAwait(false));
        if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installer image changed after acquisition; acquire it again.");
        source.Position = 0;
    }

    private static async Task VerifyRawImageReadbackAsync(string deviceId, long isoSize,
        string expectedSha256, CancellationToken ct)
    {
        const uint GENERIC_READ = 0x80000000u;
        const uint FILE_SHARE_READ = 0x00000001u;
        const uint FILE_SHARE_WRITE = 0x00000002u;
        const uint OPEN_EXISTING = 3u;
        using var handle = NativeMethods.CreateFileW(deviceId, GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE, nint.Zero, OPEN_EXISTING, 0u, nint.Zero);
        if (handle.IsInvalid)
            throw new IOException($"The written installer image could not be reopened for verification (Win32 {Marshal.GetLastWin32Error()}).");
        using var stream = new FileStream(handle, FileAccess.Read, BufferSize, isAsync: false);
        await VerifyWrittenImageAsync(stream, isoSize, expectedSha256, ct).ConfigureAwait(false);
    }

    internal static async Task VerifyWrittenImageAsync(Stream stream, long length,
        string expectedSha256, CancellationToken ct = default)
    {
        if (length <= 0 || expectedSha256 is null || expectedSha256.Length != 64 ||
            !expectedSha256.All(Uri.IsHexDigit) || !stream.CanRead)
            throw new InvalidDataException("The written installer image cannot be verified.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("The written installer image ended before its verified length.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expectedSha256,
            StringComparison.OrdinalIgnoreCase))
            throw new IOException("The USB installer image differs from the acquired image after writing.");
    }

    internal static async Task RequireGptInstallerImageAsync(Stream source,
        CancellationToken ct = default)
    {
        if (!source.CanRead || !source.CanSeek || source.Length < 1024)
            throw new InvalidDataException("The installer image has no valid GPT header for the USB workflow.");
        var header = new byte[512];
        source.Position = 512;
        try
        {
            await source.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            var headerSize = BitConverter.ToUInt32(header, 12);
            var expectedCrc = BitConverter.ToUInt32(header, 16);
            if (!header.AsSpan(0, 8).SequenceEqual("EFI PART"u8) ||
                headerSize < 92 || headerSize > 512 || expectedCrc == 0)
                throw new InvalidDataException("The installer image has no valid GPT header for the USB workflow.");
            Array.Clear(header, 16, 4);
            if (GptCrc32(header, (int)headerSize) != expectedCrc)
                throw new InvalidDataException("The installer image GPT header checksum is invalid.");

            // The physical writer currently copies 512-byte sectors from LBA 2.
            // Validate the image's complete table and backup before any USB write;
            // ISO hybrids can carry a GPT signature while still having a broken or
            // differently placed entry table.
            var imageSectors = (ulong)(source.Length / 512);
            var alternateLba = BitConverter.ToUInt64(header, 32);
            var firstUsableLba = BitConverter.ToUInt64(header, 40);
            var lastUsableLba = BitConverter.ToUInt64(header, 48);
            var entryLba = BitConverter.ToUInt64(header, 72);
            var entryCount = BitConverter.ToUInt32(header, 80);
            var entrySize = BitConverter.ToUInt32(header, 84);
            if (BitConverter.ToUInt64(header, 24) != 1 || entryLba != 2 ||
                entryCount is < 1 or > 4096 || entrySize != 128 ||
                alternateLba >= imageSectors)
                throw new InvalidDataException("The installer image GPT layout is unsupported for the USB workflow.");
            var entryBytes = checked((int)(entryCount * entrySize));
            var entrySectors = (ulong)((entryBytes + 511) / 512);
            if (firstUsableLba < entryLba + entrySectors ||
                lastUsableLba < firstUsableLba ||
                alternateLba <= entrySectors ||
                lastUsableLba >= alternateLba - entrySectors)
                throw new InvalidDataException("The installer image GPT layout is invalid.");

            var entries = new byte[entryBytes];
            source.Position = checked((long)entryLba * 512);
            await source.ReadExactlyAsync(entries, ct).ConfigureAwait(false);
            if (GptCrc32(entries, entries.Length) != BitConverter.ToUInt32(header, 88))
                throw new InvalidDataException("The installer image GPT partition table checksum is invalid.");

            ValidateGptPartitionEntries(entries, entryCount, (int)entrySize,
                firstUsableLba, lastUsableLba);

            var backup = new byte[512];
            source.Position = checked((long)alternateLba * 512);
            await source.ReadExactlyAsync(backup, ct).ConfigureAwait(false);
            var backupSize = BitConverter.ToUInt32(backup, 12);
            var backupCrc = BitConverter.ToUInt32(backup, 16);
            if (!backup.AsSpan(0, 8).SequenceEqual("EFI PART"u8) ||
                backupSize != headerSize || backupCrc == 0 ||
                BitConverter.ToUInt64(backup, 24) != alternateLba ||
                BitConverter.ToUInt64(backup, 32) != 1 ||
                BitConverter.ToUInt64(backup, 72) != alternateLba - entrySectors ||
                BitConverter.ToUInt64(backup, 40) != firstUsableLba ||
                BitConverter.ToUInt64(backup, 48) != lastUsableLba ||
                !backup.AsSpan(56, 16).SequenceEqual(header.AsSpan(56, 16)) ||
                BitConverter.ToUInt32(backup, 80) != entryCount ||
                BitConverter.ToUInt32(backup, 84) != entrySize ||
                BitConverter.ToUInt32(backup, 88) != BitConverter.ToUInt32(header, 88))
                throw new InvalidDataException("The installer image backup GPT is invalid.");
            Array.Clear(backup, 16, 4);
            if (GptCrc32(backup, (int)backupSize) != backupCrc)
                throw new InvalidDataException("The installer image backup GPT checksum is invalid.");
            var backupEntries = new byte[entryBytes];
            source.Position = checked((long)(alternateLba - entrySectors) * 512);
            await source.ReadExactlyAsync(backupEntries, ct).ConfigureAwait(false);
            if (GptCrc32(backupEntries, backupEntries.Length) !=
                BitConverter.ToUInt32(header, 88) ||
                !backupEntries.AsSpan().SequenceEqual(entries))
                throw new InvalidDataException("The installer image backup GPT partition table is invalid.");
        }
        finally
        {
            source.Position = 0;
        }
    }

    private static void ValidateGptPartitionEntries(byte[] entries, uint entryCount,
        int entrySize, ulong firstUsableLba, ulong lastUsableLba)
    {
        var occupied = new List<(ulong First, ulong Last)>();
        for (var i = 0; i < entryCount; i++)
        {
            var entry = entries.AsSpan(i * entrySize, entrySize);
            if (entry[..16].IndexOfAnyExcept((byte)0) < 0)
                continue;
            var first = BitConverter.ToUInt64(entry[32..40]);
            var last = BitConverter.ToUInt64(entry[40..48]);
            if (first < firstUsableLba || last < first || last > lastUsableLba)
                throw new InvalidDataException("The installer image GPT partition range is invalid.");
            occupied.Add((first, last));
        }
        occupied.Sort((left, right) => left.First.CompareTo(right.First));
        for (var i = 1; i < occupied.Count; i++)
            if (occupied[i].First <= occupied[i - 1].Last)
                throw new InvalidDataException("The installer image GPT partitions overlap.");
    }

    internal static void ValidateFit(UsbDriveInfo drive, long isoSize, int partSizeMb)
    {
        if (drive.SizeBytes <= 0)
            throw new InvalidOperationException("The selected USB drive size is unavailable; writing cannot start.");
        if (isoSize <= 0 || partSizeMb <= 0 || isoSize > long.MaxValue - (long)partSizeMb * 1024 * 1024)
            throw new InvalidOperationException("The installer image or staging size is invalid.");

        var requiredBytes = isoSize + (long)partSizeMb * 1024 * 1024;
        if (drive.SizeBytes >= requiredBytes)
            return;

        throw new InvalidOperationException(
            $"The selected USB drive ({drive.SizeBytes / (1024.0 * 1024 * 1024):F1} GB) is too small. " +
            $"It needs at least {requiredBytes / (1024.0 * 1024 * 1024):F1} GB: " +
            $"{isoSize / (1024.0 * 1024 * 1024):F1} GB for the installer image " +
            $"and {partSizeMb} MB for the OEMDRV migration partition. " +
            "Use a larger drive (8 GB or more is recommended).");
    }

    private async Task EnsureCurrentDriveAsync(UsbDriveInfo drive, CancellationToken ct)
    {
        var current = await EnumerateDrivesAsync(ct).ConfigureAwait(false);
        if (!UniqueObservedDrive(drive, current))
            throw new InvalidOperationException(
                "The selected USB drive identity changed; refresh the drive list before writing.");
    }

    private (Guid DiskGuid, Guid PartitionGuid) ValidateOemDrvMounted(UsbDriveInfo drive,
        char driveLetter, int partSizeMb)
    {
        var root = $"{driveLetter}:\\";
        using var query = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
            FormattableString.Invariant($"SELECT DiskNumber, PartitionNumber, DriveLetter, Size, Guid FROM MSFT_Partition WHERE DiskNumber = {drive.DriveIndex}"));
        using var results = query.Get();
        var matches = results.Cast<ManagementObject>().Where(partition =>
            string.Equals(partition["DriveLetter"]?.ToString(),
                driveLetter.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1 || !Directory.Exists(root))
            throw new InvalidOperationException("The OEMDRV letter is not uniquely bound to the selected USB disk.");
        var size = Convert.ToInt64(matches[0]["Size"], CultureInfo.InvariantCulture);
        var volume = new DriveInfo(root);
        if (size < (long)partSizeMb * 1024 * 1024 - 1024 * 1024 ||
            !volume.IsReady ||
            !string.Equals(volume.DriveFormat, "FAT32", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(volume.VolumeLabel, "OEMDRV", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected USB disk has no verified FAT32 OEMDRV volume.");
        if (!Guid.TryParse(matches[0]["Guid"]?.ToString(), out var partitionGuid) ||
            partitionGuid == Guid.Empty)
            throw new InvalidOperationException("The installer seed partition has no GPT identity.");
        using var diskQuery = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
            FormattableString.Invariant($"SELECT Number, Guid, PartitionStyle FROM MSFT_Disk WHERE Number = {drive.DriveIndex}"));
        using var diskResults = diskQuery.Get();
        var disks = diskResults.Cast<ManagementObject>().ToArray();
        if (disks.Length != 1 ||
            Convert.ToInt32(disks[0]["PartitionStyle"], CultureInfo.InvariantCulture) != 2 ||
            !Guid.TryParse(disks[0]["Guid"]?.ToString(), out var diskGuid) ||
            diskGuid == Guid.Empty)
            throw new InvalidOperationException("The installer seed disk has no unique GPT identity.");
        _logger.LogInformation("OEMDRV partition confirmed at {Root} on USB disk {Disk}",
            root, drive.DriveIndex);
        return (diskGuid, partitionGuid);
    }

    //   Phase 1 implementation                         

    private async Task WriteIsoRawAsync(
        string deviceId,
        Stream src,
        long isoSize,
        IProgress<UsbWriteProgress>? progress,
        CancellationToken ct)
    {
        const uint GENERIC_WRITE = 0x40000000u;
        const uint FILE_SHARE_READ = 0x00000001u;
        const uint FILE_SHARE_WRITE = 0x00000002u;
        const uint OPEN_EXISTING = 3u;
        const uint FILE_FLAG_OVERLAPPED = 0x40000000u; // required for FileStream isAsync: true
        const uint FILE_FLAG_WRITE_THROUGH = 0x80000000u;

        // FILE_FLAG_OVERLAPPED is in dwFlagsAndAttributes (not dwDesiredAccess), so the
        // 0x40000000 value does not conflict with GENERIC_WRITE which is in a different param.
        // The handle is scoped with 'using': FileStream adopts and closes it, and this
        // outer using is an idempotent second close that also covers a FileStream ctor throw.
        using var handle = NativeMethods.CreateFileW(
            deviceId,
            GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nint.Zero,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | FILE_FLAG_WRITE_THROUGH,
            nint.Zero);

        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, err == 5 /* ERROR_ACCESS_DENIED */
                ? $"Access denied opening {deviceId}. " +
                  "Ensure iGloo is running as Administrator and close any " +
                  "File Explorer windows showing the USB drive."
                : $"Failed to open physical drive '{deviceId}' (Win32 error {err}).");
        }

        using var dest = new FileStream(handle, FileAccess.Write, bufferSize: BufferSize, isAsync: true);
        var buffer = new byte[BufferSize];
        var written = 0L;
        int bytesRead;

        while ((bytesRead = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            // Physical-drive writes must be sector-aligned (512 bytes); pad last chunk.
            var toWrite = RoundUpToSector(bytesRead);
            if (toWrite != bytesRead)
                Array.Clear(buffer, bytesRead, toWrite - bytesRead);

            await dest.WriteAsync(buffer.AsMemory(0, toWrite), ct).ConfigureAwait(false);
            written += bytesRead;

            progress?.Report(new UsbWriteProgress(
                UsbWritePhase.WritingIso, written, isoSize,
                $"Writing… {written / (1024 * 1024):N0} MB / {isoSize / (1024 * 1024):N0} MB"));
        }

        if (written != isoSize)
            throw new IOException("The installer image ended before all verified bytes were written.");

        await dest.FlushAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("ISO write complete: {Written:N0} bytes written to {Device}", written, deviceId);
    }

    internal static int RoundUpToSector(int bytes, int sectorSize = 512) =>
        ((bytes + sectorSize - 1) / sectorSize) * sectorSize;

    // GetRelativePath returns its input unchanged across volumes, so a rooted or
    // traversing segment must be rejected before the copy targets a user's device.
    internal static string ResolveDestinationPath(string destRoot, string relative)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidOperationException(
                $"Refusing to write outside {destRoot}: '{relative}' is an absolute path.");

        var full = Path.GetFullPath(Path.Join(destRoot, relative));
        var boundary = destRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destRoot
            : destRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to write outside {destRoot}: '{relative}' resolved to '{full}'.");

        return full;
    }

    //   Phase 2 implementation                         

    private async Task RunDiskpartAsync(
        int diskIndex,
        int partSizeMb,
        char driveLetter,
        CancellationToken ct)
    {
        //   TODO v1.1                              
        // Consider replacing this diskpart shell-out with the Windows Storage
        // Management API.  Two concrete options:
        //
        //   a) VDS via COM (IVdsDisk / IVdsAdvancedDisk) - binary interface,
        //      proper HRESULTs, no locale dependency.
        //
        //   b) Storage WMI provider (MSFT_Disk.CreatePartition + MSFT_Partition.
        //      Format + MSFT_Volume.Mount) - same WMI stack we already use for
        //      drive enumeration, stable across Windows locales.
        //
        // Why it matters today:
        //   • diskpart returns exit code 0 even when individual commands fail;
        //     the "success" check below only guards against diskpart crashing, not
        //     against a silently skipped 'create partition' or 'format' step.
        //     We mitigate this by calling ValidateOemDrvMounted afterwards, but a
        //     binary API would surface the error directly.
        //   • stdout is locale-dependent; we do NOT parse it (no English string
        //     matching), but any future diagnostic improvement would be blocked on it.
        //                                    

        var script = $"""
            select disk {diskIndex}
            create partition primary size={partSizeMb}
            format fs=fat32 label=OEMDRV quick
            assign letter={driveLetter}
            """;

        var scriptPath = Path.Join(
            Path.GetTempPath(), $"igloo-diskpart-{Guid.NewGuid():N}.txt");

        _logger.LogDebug("diskpart script ({Path}):\n{Script}", scriptPath, script);

        try
        {
            await File.WriteAllTextAsync(scriptPath, script, ct).ConfigureAwait(false);

            var psi = new ProcessStartInfo("diskpart.exe", $"/s \"{scriptPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start diskpart.exe.");

            // Read stdout/stderr concurrently to avoid blocking on large output.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            _logger.LogDebug("diskpart stdout: {Out}", stdout);
            if (!string.IsNullOrWhiteSpace(stderr))
                _logger.LogWarning("diskpart stderr: {Err}", stderr);

            // Note: exit code 0 means diskpart ran, NOT that all commands succeeded.
            // ValidateOemDrvMounted() in the caller provides the real success check.
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"diskpart exited with code {proc.ExitCode}. Output: {stdout}");
        }
        finally
        {
            TryDeleteFile(scriptPath);
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

    //   Phase 3 implementation

    internal static async Task CopyStagingFilesAsync(
        string destRoot,
        string stagingDirectory,
        long totalBytes,
        IProgress<UsbWriteProgress>? progress,
        CancellationToken ct)
    {
        var written = 0L;

        foreach (var srcDirectory in Directory.EnumerateDirectories(
            stagingDirectory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(srcDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The migration staging directory changed while copying.");
            var relative = Path.GetRelativePath(stagingDirectory, srcDirectory);
            Directory.CreateDirectory(ResolveDestinationPath(destRoot, relative));
        }

        foreach (var srcPath in
            Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            if ((File.GetAttributes(srcPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A staged source file changed to a redirected path during the USB copy.");

            var fileSize = new FileInfo(srcPath).Length;
            var relative = Path.GetRelativePath(stagingDirectory, srcPath);
            var destPath = ResolveDestinationPath(destRoot, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            using var srcStream = new FileStream(
                srcPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: BufferSize, useAsync: true);
            using var destStream = new FileStream(
                destPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: BufferSize, useAsync: true);

            await srcStream.CopyToAsync(destStream, ct).ConfigureAwait(false);
            await destStream.FlushAsync(ct).ConfigureAwait(false);

            if (srcStream.Position != fileSize || destStream.Position != fileSize)
                throw new IOException("A staged source file changed during the USB copy.");
            written = checked(written + destStream.Position);
            progress?.Report(new UsbWriteProgress(
                UsbWritePhase.CopyingFiles, written, totalBytes,
                Path.GetFileName(srcPath)));
        }

        if (written != totalBytes)
            throw new IOException("The staging directory changed during the USB copy.");

    }

    //   Helpers                                ─

    private static bool IsCurrentProcessElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

    private static char FindFreeDriveLetter()
    {
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();

        var free = "FGHIJKLMNOPQRSTUVWXYZ"
            .Where(letter => !used.Contains(letter))
            .Select(letter => (char?)letter)
            .FirstOrDefault();
        if (free is not null)
            return free.Value;

        throw new InvalidOperationException(
            "No free drive letter is available to assign to the OEMDRV partition.");
    }

    internal static void ValidateFat32FileSize(long size)
    {
        if (size < 0 || size > uint.MaxValue)
            throw new InvalidOperationException(
                "A selected migration file exceeds FAT32's 4 GiB single-file limit.");
    }

    private static long InspectStagingDirectory(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException("The migration staging directory is unavailable.");

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The migration staging directory contains a redirected directory.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The migration staging directory contains a redirected entry.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                var size = new FileInfo(entry).Length;
                ValidateFat32FileSize(size);
                total = checked(total + size);
            }
        }

        return total;
    }

}
