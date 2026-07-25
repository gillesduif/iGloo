using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Igloo.UsbWriter;

public sealed partial class UsbWriterService
{
    //   Hybrid-MBR → protective-MBR conversion                ─

    private async Task EnsureProtectiveMbrAsync(string deviceId, long diskSizeBytes)
    {
        const uint GENERIC_READ = 0x80000000u;
        const uint GENERIC_WRITE = 0x40000000u;
        const uint FILE_SHARE_READ = 0x00000001u;
        const uint FILE_SHARE_WRITE = 0x00000002u;
        const uint OPEN_EXISTING = 3u;
        // CTL_CODE(FILE_DEVICE_DISK=7, 0x0050, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
        const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140u;

        var handle = NativeMethods.CreateFileW(
            deviceId, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nint.Zero, OPEN_EXISTING, 0u, nint.Zero);

        if (handle.IsInvalid)
        {
            _logger.LogWarning(
                "Cannot open {Dev} for MBR/GPT fix (Win32 error {Err}) - skipping",
                deviceId, Marshal.GetLastWin32Error());
            handle.Dispose();
            return;
        }

        try
        {
            //   Step 1: read LBA 0 (MBR) + LBA 1 (GPT header)         
            var buf = new byte[1024];
            if (!NativeMethods.ReadFile(handle, buf, 1024, out int bytesRead, nint.Zero)
                || bytesRead < 1024)
            {
                _logger.LogWarning("Could not read MBR+GPT from {Dev} ({N} bytes read) - skipping", deviceId, bytesRead);
                return;
            }

            // Verify GPT signature at LBA 1.
            // ReadOnlySpan<byte> cannot be a local in an async method (C# 12), use Encoding.
            if (Encoding.ASCII.GetString(buf, 512, 8) != "EFI PART")
            {
                throw new InvalidOperationException(
                    "The ISO does not contain a GPT partition table (LBA 1 has no 'EFI PART' signature) " +
                    "and all four MBR primary partition slots are occupied. " +
                    "iGloo cannot create the OEMDRV partition on this ISO format. " +
                    "Please use a Fedora Live ISO.");
            }

            //   Step 2: write protective MBR if needed             
            bool alreadyProtective =
                buf[446 + 4] == 0xEE &&   // entry 1 type = 0xEE
                buf[462 + 4] == 0x00 &&   // entry 2 type = empty
                buf[478 + 4] == 0x00 &&   // entry 3 type = empty
                buf[494 + 4] == 0x00;     // entry 4 type = empty

            if (alreadyProtective)
            {
                _logger.LogInformation("Disk {Dev} MBR is already protective - skipping MBR rewrite", deviceId);
            }
            else
            {
                _logger.LogInformation(
                    "Hybrid MBR on {Dev} (types {T0:X2}/{T1:X2}/{T2:X2}/{T3:X2}) - rewriting as protective",
                    deviceId, buf[450], buf[466], buf[482], buf[498]);

                long diskSectors = diskSizeBytes > 0 ? diskSizeBytes / 512 : 0L;
                uint sizeInSectors = diskSectors > 1
                    ? (diskSectors - 1 > 0xFFFF_FFFFu ? 0xFFFF_FFFFu : (uint)(diskSectors - 1))
                    : 0xFFFF_FFFFu;

                Array.Clear(buf, 446, 64);          // zero all four entries

                // Entry 1: type 0xEE, LBA 1 → end-of-disk
                buf[446] = 0x00;
                buf[447] = 0x00;
                buf[448] = 0x02;
                buf[449] = 0x00;  // CHS first (legacy)
                buf[450] = 0xEE;                                      // GPT protective type
                buf[451] = 0xFF;
                buf[452] = 0xFF;
                buf[453] = 0xFF;  // CHS last (legacy)
                BitConverter.TryWriteBytes(buf.AsSpan(454, 4), 1u);
                BitConverter.TryWriteBytes(buf.AsSpan(458, 4), sizeInSectors);

                buf[510] = 0x55;
                buf[511] = 0xAA;

                if (!NativeMethods.SetFilePointerEx(handle, 0L, nint.Zero, 0u))
                {
                    _logger.LogWarning("SetFilePointerEx(0) failed on {Dev} (Win32 {Err})", deviceId, Marshal.GetLastWin32Error());
                    return;
                }
                if (!NativeMethods.WriteFile(handle, buf, 512, out int written, nint.Zero) || written < 512)
                {
                    _logger.LogWarning("WriteFile LBA 0 failed on {Dev} (Win32 {Err})", deviceId, Marshal.GetLastWin32Error());
                    return;
                }

                _logger.LogInformation("Protective MBR written to {Dev}", deviceId);
            }

            //   Step 3: extend GPT to the full physical disk size       ─
            // The ISO's GPT header records AlternateLBA / LastUsableLBA at the
            // end of the ISO file (~4.5 M sectors for a 2.3 GB ISO), not at the
            // end of the 115 GB USB drive.  diskpart respects those boundaries
            // and reports "no free space" even though 112+ GB are unallocated.
            TryExtendGptToFullDisk(handle, diskSizeBytes);

            //   Step 4: tell the driver to re-read the updated partition table ─
            NativeMethods.DeviceIoControl(
                handle, IOCTL_DISK_UPDATE_PROPERTIES,
                nint.Zero, 0, nint.Zero, 0, out _, nint.Zero);

            _logger.LogInformation("MBR/GPT preparation complete on {Dev}", deviceId);
        }
        finally
        {
            handle.Dispose();
        }

        // Give the disk driver time to finish the re-read before diskpart starts.
        await Task.Delay(1500, CancellationToken.None).ConfigureAwait(false);
    }

    //   GPT size extension                           

    private bool TryExtendGptToFullDisk(SafeFileHandle diskHandle, long diskSizeBytes)
    {
        if (diskSizeBytes <= 0)
        {
            _logger.LogWarning("TryExtendGpt: disk size unknown - cannot extend GPT");
            return false;
        }

        const int BackupEntrySectors = 32; // 128 entries × 128 B = 32 × 512-B sectors

        long diskSectors = diskSizeBytes / 512;
        long newAlternateLBA = diskSectors - 1;
        long newBackupEntryStart = diskSectors - 1 - BackupEntrySectors;
        long newLastUsableLBA = diskSectors - 1 - BackupEntrySectors - 1;

        //   Read primary GPT header (LBA 1)                  ─
        var hdr = new byte[512];
        if (!ReadSector(diskHandle, 1L, hdr))
        {
            _logger.LogWarning("TryExtendGpt: failed to read LBA 1");
            return false;
        }

        if (Encoding.ASCII.GetString(hdr, 0, 8) != "EFI PART")
        {
            _logger.LogWarning("TryExtendGpt: no GPT signature at LBA 1 after MBR fix");
            return false;
        }

        uint headerSize = BitConverter.ToUInt32(hdr, 12);
        if (headerSize < 92 || headerSize > 512)
        {
            _logger.LogWarning("TryExtendGpt: unexpected GPT header size {S}", headerSize);
            return false;
        }

        // Validate primary header CRC32 before touching anything.
        uint storedCrc = BitConverter.ToUInt32(hdr, 16);
        Array.Clear(hdr, 16, 4);
        uint computedCrc = GptCrc32(hdr, (int)headerSize);
        if (computedCrc != storedCrc)
        {
            _logger.LogWarning("TryExtendGpt: primary GPT CRC mismatch (stored {S:X8}, computed {C:X8}) - aborting", storedCrc, computedCrc);
            return false;
        }
        // Restore the CRC field.
        BitConverter.TryWriteBytes(hdr.AsSpan(16, 4), storedCrc);

        long currentAlternate = BitConverter.ToInt64(hdr, 32);
        long currentLastUsable = BitConverter.ToInt64(hdr, 48);

        if (currentAlternate == newAlternateLBA && currentLastUsable == newLastUsableLBA)
        {
            _logger.LogInformation("TryExtendGpt: GPT already covers the full disk - nothing to do");
            return true;
        }

        _logger.LogInformation(
            "TryExtendGpt: extending GPT from AlternateLBA={Old} to {New} ({GB:F1} GB)",
            currentAlternate, newAlternateLBA, diskSizeBytes / 1073741824.0);

        //   Read primary partition entries (LBA 2–33)             ─
        var entries = new byte[BackupEntrySectors * 512];
        for (int i = 0; i < BackupEntrySectors; i++)
        {
            var sec = new byte[512];
            if (!ReadSector(diskHandle, 2L + i, sec))
            {
                _logger.LogWarning("TryExtendGpt: failed to read partition entry sector {I}", i);
                return false;
            }
            Buffer.BlockCopy(sec, 0, entries, i * 512, 512);
        }

        //   Update primary GPT header                     ─
        BitConverter.TryWriteBytes(hdr.AsSpan(32, 8), newAlternateLBA);   // AlternateLBA
        BitConverter.TryWriteBytes(hdr.AsSpan(48, 8), newLastUsableLBA);  // LastUsableLBA
        // PartitionEntryLBA (offset 72) stays at 2.
        // PartitionEntryArrayCRC32 (offset 88) is unchanged (entries themselves didn't change).

        Array.Clear(hdr, 16, 4);
        uint newPrimaryCrc = GptCrc32(hdr, (int)headerSize);
        BitConverter.TryWriteBytes(hdr.AsSpan(16, 4), newPrimaryCrc);

        //   Write backup partition entries at new location           
        for (int i = 0; i < BackupEntrySectors; i++)
        {
            var sec = new byte[512];
            Buffer.BlockCopy(entries, i * 512, sec, 0, 512);
            if (!WriteSector(diskHandle, newBackupEntryStart + i, sec))
            {
                _logger.LogWarning("TryExtendGpt: failed to write backup entry sector {I}", i);
                return false;
            }
        }

        //   Build and write backup GPT header                 ─
        // Backup header is a mirror of the primary with MyLBA/AlternateLBA swapped
        // and PartitionEntryLBA pointing to the backup entries' new location.
        var backupHdr = (byte[])hdr.Clone();
        BitConverter.TryWriteBytes(backupHdr.AsSpan(24, 8), newAlternateLBA);    // MyLBA
        BitConverter.TryWriteBytes(backupHdr.AsSpan(32, 8), 1L);                 // AlternateLBA → primary
        BitConverter.TryWriteBytes(backupHdr.AsSpan(72, 8), newBackupEntryStart);// PartitionEntryLBA

        Array.Clear(backupHdr, 16, 4);
        uint backupCrc = GptCrc32(backupHdr, (int)headerSize);
        BitConverter.TryWriteBytes(backupHdr.AsSpan(16, 4), backupCrc);

        if (!WriteSector(diskHandle, newAlternateLBA, backupHdr))
        {
            _logger.LogWarning("TryExtendGpt: failed to write backup GPT header at LBA {L}", newAlternateLBA);
            return false;
        }

        //   Write updated primary GPT header                  
        if (!WriteSector(diskHandle, 1L, hdr))
        {
            _logger.LogWarning("TryExtendGpt: failed to write primary GPT header");
            return false;
        }

        _logger.LogInformation(
            "TryExtendGpt: GPT successfully extended - {GB:F1} GB now usable",
            (newLastUsableLBA - 34) * 512 / 1073741824.0);
        return true;
    }

    private static bool ReadSector(SafeFileHandle h, long lba, byte[] buf)
    {
        if (!NativeMethods.SetFilePointerEx(h, lba * 512, nint.Zero, 0u))
            return false;
        return NativeMethods.ReadFile(h, buf, 512, out int n, nint.Zero) && n == 512;
    }

    private static bool WriteSector(SafeFileHandle h, long lba, byte[] buf)
    {
        if (!NativeMethods.SetFilePointerEx(h, lba * 512, nint.Zero, 0u))
            return false;
        return NativeMethods.WriteFile(h, buf, 512, out int n, nint.Zero) && n == 512;
    }

    internal static uint GptCrc32(byte[] data, int length)
    {
        uint crc = 0xFFFF_FFFFu;
        for (int i = 0; i < length; i++)
        {
            crc ^= data[i];
            for (int b = 0; b < 8; b++)
                crc = (crc & 1u) != 0u ? (crc >> 1) ^ 0xEDB8_8320u : crc >> 1;
        }
        return ~crc;
    }

    //   Volume locking / dismounting                      

    // Returns one open handle per dismounted volume; ownership passes to the caller, who
    // keeps each handle open (releasing it re-allows mounting) and disposes it when done.
    private List<SafeFileHandle> LockAndDismountVolumesOnDisk(int diskIndex)
    {
        var held = new List<SafeFileHandle>();

        foreach (var driveInfo in DriveInfo.GetDrives())
        {
            if (driveInfo.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Unknown))
                continue;

            var letter = char.ToUpperInvariant(driveInfo.Name[0]);

            // Standard CA2000 hand-off: the handle is disposed in 'finally' unless it was
            // added to 'held', at which point nulling the local passes ownership to the list.
            SafeFileHandle? handle = null;
            try
            {
                handle = TryLockAndDismountVolume($@"\\.\{letter}:", diskIndex);
                if (handle is not null)
                {
                    held.Add(handle);
                    handle = null;
                }
            }
            finally
            {
                handle?.Dispose();
            }
        }

        return held;
    }

    private SafeFileHandle? TryLockAndDismountVolume(string volumePath, int diskIndex)
    {
        const uint GENERIC_READ = 0x80000000u;
        const uint GENERIC_WRITE = 0x40000000u;
        const uint FILE_SHARE_READ = 0x00000001u;
        const uint FILE_SHARE_WRITE = 0x00000002u;
        const uint OPEN_EXISTING = 3u;
        const uint IOCTL_VOLUME_GET_EXTENTS = 0x00560000u;
        const uint FSCTL_LOCK_VOLUME = 0x00090018u;
        const uint FSCTL_DISMOUNT_VOLUME = 0x00090020u;

        var handle = NativeMethods.CreateFileW(
            volumePath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nint.Zero,
            OPEN_EXISTING,
            0u,
            nint.Zero);

        if (handle.IsInvalid)
        {
            _logger.LogDebug("Cannot open volume {Vol} - skipping dismount", volumePath);
            handle.Dispose();
            return null;
        }

        if (!VolumeIsOnDisk(handle, diskIndex, IOCTL_VOLUME_GET_EXTENTS))
        {
            handle.Dispose();
            return null;
        }

        // Lock: advises Windows no new opens are allowed on this volume.
        // Non-fatal if files are already open - FSCTL_DISMOUNT_VOLUME forces
        // it offline regardless.
        bool locked = NativeMethods.DeviceIoControl(
            handle, FSCTL_LOCK_VOLUME,
            nint.Zero, 0, nint.Zero, 0, out _, nint.Zero);
        if (!locked)
            _logger.LogDebug("Lock advisory failed on {Vol} (will force-dismount)", volumePath);

        // Dismount: flushes dirty buffers and takes the volume offline.
        bool dismounted = NativeMethods.DeviceIoControl(
            handle, FSCTL_DISMOUNT_VOLUME,
            nint.Zero, 0, nint.Zero, 0, out _, nint.Zero);

        if (!dismounted)
        {
            _logger.LogWarning("Failed to dismount volume {Vol} (Win32 error {Err})",
                volumePath, Marshal.GetLastWin32Error());
            handle.Dispose();
            return null;
        }

        _logger.LogInformation("Dismounted volume {Vol} on disk {Index}", volumePath, diskIndex);
        return handle;
    }

    private static bool VolumeIsOnDisk(SafeFileHandle volHandle, int diskIndex, uint ioctlGetExtents)
    {
        // Allocate output buffer for up to 8 extents (covers all practical cases).
        const int MaxExtents = 8;
        const int HeaderSize = 8;              // 4 bytes count + 4 bytes alignment padding
        const int ExtentSize = 24;             // sizeof(DISK_EXTENT) including its internal padding
        int bufSize = HeaderSize + MaxExtents * ExtentSize;
        var buf = new byte[bufSize];

        // Pin the buffer so the kernel can write into it via DeviceIoControl.
        var gcHandle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            bool ok = NativeMethods.DeviceIoControl(
                volHandle, ioctlGetExtents,
                nint.Zero, 0,
                gcHandle.AddrOfPinnedObject(), bufSize,
                out int bytesReturned,
                nint.Zero);

            if (!ok || bytesReturned < HeaderSize)
                return false;

            int count = BitConverter.ToInt32(buf, 0);
            for (int i = 0; i < count && i < MaxExtents; i++)
            {
                // Extents[i].DiskNumber is at HeaderSize + i * ExtentSize (NOT 4 + i*24).
                int diskNum = BitConverter.ToInt32(buf, HeaderSize + i * ExtentSize);
                if (diskNum == diskIndex)
                    return true;
            }
            return false;
        }
        finally
        {
            gcHandle.Free();
        }
    }
}
