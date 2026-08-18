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
    //   GRUB config patching                          

    private Task PatchGrubConfigAsync(
        UsbDriveInfo drive,
        IProgress<UsbWriteProgress>? progress)
    {
        progress?.Report(new UsbWriteProgress(UsbWritePhase.PatchingGrub, 0, 0, null));

        const uint GENERIC_READ = 0x80000000u;
        const uint GENERIC_WRITE = 0x40000000u;
        const uint FILE_SHARE_READ = 0x00000001u;
        const uint FILE_SHARE_WRITE = 0x00000002u;
        const uint OPEN_EXISTING = 3u;

        var handle = NativeMethods.CreateFileW(
            drive.DeviceId, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nint.Zero, OPEN_EXISTING, 0u, nint.Zero);

        if (handle.IsInvalid)
        {
            using var invalid = handle;
            var err = Marshal.GetLastWin32Error();
            _logger.LogWarning(
                "GRUB patch - cannot open {Dev} (Win32 {Err})", drive.DeviceId, err);
            progress?.Report(new UsbWriteProgress(
                UsbWritePhase.PatchingGrub, 0, 0,
                $"⚠ GRUB patch skipped - drive not accessible (Win32 error {err})."));
            return Task.CompletedTask;
        }

        var patchedPaths = new List<string>();
        var skippedPaths = new List<string>();

        using (handle)
        {
            //   Path 1: EFI FAT32 (UEFI boot)                 
            long efiLba = FindEfiPartitionStartLba(handle);
            if (efiLba > 0)
            {
                _logger.LogInformation(
                    "GRUB patch - EFI partition at LBA {L}, patching via raw FAT32", efiLba);
                PatchGrubCfgsOnFatVolume(handle, efiLba, patchedPaths, skippedPaths);
            }
            else
            {
                _logger.LogWarning("GRUB patch - EFI System Partition not found in GPT");
                skippedPaths.Add("EFI/*/grub.cfg (ESP not found)");
            }

            //   Path 2: ISO9660 (BIOS/legacy boot)              ─
            PatchIso9660GrubCfg(handle, patchedPaths, skippedPaths);
        }

        // Build a human-readable summary for the UI.
        string note = patchedPaths.Count > 0
            ? $"✓ GRUB boot parameters applied ({string.Join(", ", patchedPaths)}).\n" +
              "nomodeset added; rd.live.check removed - media check will be skipped."
            : $"⚠ No grub.cfg files were modified ({string.Join("; ", skippedPaths)}).\n" +
              "You may need to remove 'rd.live.check' manually at the GRUB prompt.";

        _logger.LogInformation("GRUB patch result: {Note}", note);
        progress?.Report(new UsbWriteProgress(UsbWritePhase.PatchingGrub, 0, 0, note));
        return Task.CompletedTask;
    }

    private long FindEfiPartitionStartLba(SafeFileHandle handle)
    {
        byte[] efiGuid =
        [
            0x28, 0x73, 0x2A, 0xC1,
            0x1F, 0xF8, 0xD2, 0x11,
            0xBA, 0x4B, 0x00, 0xA0, 0xC9, 0x3E, 0xC9, 0x3B,
        ];

        var hdr = new byte[512];
        if (!ReadSector(handle, 1L, hdr))
            return -1;
        if (Encoding.ASCII.GetString(hdr, 0, 8) != "EFI PART")
            return -1;

        long entryLBA = BitConverter.ToInt64(hdr, 72);
        uint entryCount = BitConverter.ToUInt32(hdr, 80);
        uint entrySize = BitConverter.ToUInt32(hdr, 84);
        if (entrySize < 128 || entryCount == 0 || entryCount > 512)
            return -1;

        int totalSectors = (int)((entryCount * entrySize + 511) / 512);
        var entries = new byte[totalSectors * 512];
        for (int s = 0; s < totalSectors; s++)
        {
            var sec = new byte[512];
            if (!ReadSector(handle, entryLBA + s, sec))
                return -1;
            Buffer.BlockCopy(sec, 0, entries, s * 512, 512);
        }

        for (uint i = 0; i < entryCount; i++)
        {
            int off = (int)(i * entrySize);
            bool empty = true;
            for (int b = 0; b < 16; b++)
                if (entries[off + b] != 0)
                { empty = false; break; }
            if (empty)
                continue;

            bool isEfi = true;
            for (int b = 0; b < 16; b++)
                if (entries[off + b] != efiGuid[b])
                { isEfi = false; break; }

            if (isEfi)
            {
                long lba = BitConverter.ToInt64(entries, off + 32); // StartingLBA
                _logger.LogDebug("FindEfiStartLba: entry {I} → LBA {L}", i, lba);
                return lba;
            }
        }
        return -1;
    }

    private void PatchGrubCfgsOnFatVolume(
        SafeFileHandle disk,
        long partLba,
        List<string> patchedPaths,
        List<string> skippedPaths)
    {
        //   Parse BPB                               
        var bpb = new byte[512];
        if (!ReadSector(disk, partLba, bpb))
        {
            _logger.LogWarning("FAT: failed to read BPB at LBA {L}", partLba);
            skippedPaths.Add("EFI/*/grub.cfg (BPB read failed)");
            return;
        }
        if (bpb[510] != 0x55 || bpb[511] != 0xAA)
        {
            _logger.LogWarning("FAT: no 0x55AA at LBA {L}", partLba);
            skippedPaths.Add("EFI/*/grub.cfg (not a FAT volume)");
            return;
        }

        ushort bytesPerSec = BitConverter.ToUInt16(bpb, 11);
        byte secsPerClust = bpb[13];
        ushort reservedSecs = BitConverter.ToUInt16(bpb, 14);
        byte numFats = bpb[16];
        // rootEntCnt: FAT12/16 = fixed root entry count; FAT32 = 0
        ushort rootEntCnt = BitConverter.ToUInt16(bpb, 17);
        // fatSz16: FAT12/16 = sectors per FAT; FAT32 = 0 (FAT32 uses fatSz32 at offset 36)
        ushort fatSz16 = BitConverter.ToUInt16(bpb, 22);
        uint fatSz32 = BitConverter.ToUInt32(bpb, 36);
        uint rootCluster = BitConverter.ToUInt32(bpb, 44);  // FAT32 only

        bool isFat32 = fatSz16 == 0;
        uint fatSize = isFat32 ? fatSz32 : fatSz16;

        if (bytesPerSec != 512 || secsPerClust == 0 ||
            reservedSecs == 0 || numFats == 0 || fatSize == 0)
        {
            _logger.LogWarning(
                "FAT: unexpected BPB (bps={B} spc={S} res={R} fats={F} fsz={Z})",
                bytesPerSec, secsPerClust, reservedSecs, numFats, fatSize);
            skippedPaths.Add("EFI/*/grub.cfg (invalid BPB)");
            return;
        }

        long fatLba = partLba + reservedSecs;

        // FAT16/12: root directory occupies fixed sectors between the FATs and data area.
        // FAT32:    root directory is cluster-based; data starts right after the FATs.
        long fat16RootLba = 0;
        long fat16RootSectors = 0;
        long dataLba;

        if (isFat32)
        {
            dataLba = fatLba + (long)numFats * fatSz32;
        }
        else
        {
            fat16RootLba = fatLba + (long)numFats * fatSz16;
            fat16RootSectors = (rootEntCnt * 32L + 511) / 512;
            dataLba = fat16RootLba + fat16RootSectors;
        }

        _logger.LogDebug(
            "FAT{T} @ LBA {P}: spc={S} fat@{F} data@{D} {R}",
            isFat32 ? "32" : "16",
            partLba, secsPerClust, fatLba, dataLba,
            isFat32 ? $"root-cluster={rootCluster}" : $"root@LBA {fat16RootLba}+{fat16RootSectors}");

        //   Patch every known grub.cfg location                ─
        (string Label, string[] Parts)[] candidates =
        [
            ("efi/boot/grub.cfg",   ["EFI", "BOOT",   "GRUB.CFG"]),
            ("efi/fedora/grub.cfg", ["EFI", "FEDORA", "GRUB.CFG"]),
        ];

        foreach (var (label, parts) in candidates)
        {

            // Start at the root.  For FAT16/12 the root is a fixed linear region;
            // for FAT32 and all subdirectories it is a cluster chain.
            bool inFat16Root = !isFat32;
            uint dirCluster = isFat32 ? rootCluster : 0;
            bool ok = true;

            for (int d = 0; d < parts.Length - 1 && ok; d++)
            {
                uint sub;
                bool found = inFat16Root
                    ? FatFindInFixedRoot(disk, fat16RootLba, fat16RootSectors,
                          parts[d], isDirectory: true,
                          out sub, out _, out _, out _)
                    : FatFindInClusters(disk, dirCluster, parts[d], isDirectory: true,
                          secsPerClust, fatLba, dataLba, isFat32,
                          out sub, out _, out _, out _);

                inFat16Root = false;   // subdirs are always cluster-based
                if (found)
                    dirCluster = sub;
                else
                {
                    _logger.LogDebug("FAT: '{S}' not found at depth {D}", parts[d], d);
                    ok = false;
                }
            }
            if (!ok)
            { skippedPaths.Add($"{label} (dir not found)"); continue; }

            uint fileCluster;
            uint fileSize;
            long dirEntLba;
            int dirEntOff;
            bool fileFound = inFat16Root
                ? FatFindInFixedRoot(disk, fat16RootLba, fat16RootSectors,
                      parts[^1], isDirectory: false,
                      out fileCluster, out fileSize, out dirEntLba, out dirEntOff)
                : FatFindInClusters(disk, dirCluster, parts[^1], isDirectory: false,
                      secsPerClust, fatLba, dataLba, isFat32,
                      out fileCluster, out fileSize, out dirEntLba, out dirEntOff);

            if (!fileFound)
            {
                _logger.LogDebug("FAT: '{F}' not found", parts[^1]);
                skippedPaths.Add($"{label} (file not found)");
                continue;
            }

            _logger.LogInformation(
                "FAT: found {Path} - cluster {C}, {S} bytes",
                label, fileCluster, fileSize);

            var data = FatReadFile(disk, fileCluster, (int)fileSize,
                                   secsPerClust, fatLba, dataLba, isFat32);
            if (data is null)
            {
                _logger.LogWarning("FAT: read failed for {Path}", label);
                skippedPaths.Add($"{label} (read error)");
                continue;
            }

            var text = Encoding.UTF8.GetString(data);
            var patched = PatchGrubCfgContent(text);

            if (patched == text)
            {
                var preview = text.Length > 200
                    ? text[..200].Replace("\n", "↵", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal) + "…"
                    : text.Replace("\n", "↵", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);
                _logger.LogInformation(
                    "FAT: {Path} - no linux/linuxefi lines found. Preview: {P}",
                    label, preview);
                skippedPaths.Add($"{label} (no linux lines)");
                continue;
            }

            var patchedBytes = Encoding.UTF8.GetBytes(patched);

            if (!FatWriteFile(disk, fileCluster, patchedBytes, (int)fileSize,
                              secsPerClust, fatLba, dataLba, isFat32))
            {
                _logger.LogWarning("FAT: write failed for {Path}", label);
                skippedPaths.Add($"{label} (write error)");
                continue;
            }

            // Update the file-size field in the directory entry if the length changed.
            if ((uint)patchedBytes.Length != fileSize)
            {
                var dirSec = new byte[512];
                if (ReadSector(disk, dirEntLba, dirSec))
                {
                    BitConverter.TryWriteBytes(
                        dirSec.AsSpan(dirEntOff + 28, 4), (uint)patchedBytes.Length);
                    WriteSector(disk, dirEntLba, dirSec);
                }
            }

            _logger.LogInformation(
                "FAT: {Path} patched ({Old} → {New} bytes)",
                label, fileSize, patchedBytes.Length);
            patchedPaths.Add(label);
        }
    }

    //   ISO9660 GRUB config patching (BIOS/legacy boot path)         ─

    private void PatchIso9660GrubCfg(
        SafeFileHandle disk,
        List<string> patchedPaths,
        List<string> skippedPaths)
    {
        //   Validate Primary Volume Descriptor at logical block 16       
        var pvd = ReadIso9660Block(disk, 16);
        if (pvd is null)
        {
            _logger.LogDebug("ISO9660: could not read block 16");
            return;  // silent: may not be an ISO9660 disk at all
        }

        if (pvd[0] != 0x01 ||
            Encoding.ASCII.GetString(pvd, 1, 5) != "CD001")
        {
            _logger.LogDebug(
                "ISO9660: no PVD at block 16 (type={T}, id={Id})",
                pvd[0], Encoding.ASCII.GetString(pvd, 1, 5));
            return;  // not an ISO9660 volume - silent skip
        }

        // Root Directory Record is embedded in the PVD at offset 156 (34 bytes fixed).
        //   +2  Extent Location, LE uint32
        //   +10 Data Length,     LE uint32
        uint rootLba = BitConverter.ToUInt32(pvd, 156 + 2);
        uint rootSize = BitConverter.ToUInt32(pvd, 156 + 10);
        _logger.LogDebug("ISO9660: PVD OK, root dir at block {B}, {S} bytes",
            rootLba, rootSize);

        //   Navigate /boot/grub2/                       ─
        uint dirLba = rootLba;
        uint dirSize = rootSize;

        foreach (var segment in new[] { "boot", "grub2" })
        {
            if (!Iso9660FindEntry(disk, dirLba, dirSize, segment, isDir: true,
                    out uint nextLba, out uint nextSize, out _, out _))
            {
                _logger.LogDebug("ISO9660: directory '{S}' not found", segment);
                skippedPaths.Add($"/boot/grub2/grub.cfg ('{segment}' dir not found)");
                return;
            }
            dirLba = nextLba;
            dirSize = nextSize;
        }

        //   Find grub.cfg                           ─
        if (!Iso9660FindEntry(disk, dirLba, dirSize, "grub.cfg", isDir: false,
                out uint fileLba, out uint fileSize,
                out uint fileEntryBlock, out int fileEntryOff))
        {
            _logger.LogDebug("ISO9660: grub.cfg not found in /boot/grub2/");
            skippedPaths.Add("/boot/grub2/grub.cfg (file not found)");
            return;
        }

        _logger.LogInformation(
            "ISO9660: grub.cfg at block {B}, {S} bytes", fileLba, fileSize);

        //   Read, patch, validate fit                     ─
        var data = Iso9660ReadFile(disk, fileLba, (int)fileSize);
        if (data is null)
        {
            _logger.LogWarning("ISO9660: read failed for /boot/grub2/grub.cfg");
            skippedPaths.Add("/boot/grub2/grub.cfg (read error)");
            return;
        }

        var text = Encoding.UTF8.GetString(data);
        var patched = PatchGrubCfgContent(text);

        if (patched == text)
        {
            var preview = text.Length > 200
                ? text[..200].Replace("\n", "↵", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal) + "…"
                : text.Replace("\n", "↵", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);
            _logger.LogInformation(
                "ISO9660: /boot/grub2/grub.cfg - no linux/linuxefi lines found. Preview: {P}",
                preview);
            skippedPaths.Add("/boot/grub2/grub.cfg (no linux lines)");
            return;
        }

        var patchedBytes = Encoding.UTF8.GetBytes(patched);

        // ISO9660 file extents are allocated in full 2048-byte blocks.
        // The patched file (only ~25 bytes larger per line) always fits within
        // the same blocks as the original.
        uint blocksAllocated = (fileSize + 2047u) / 2048u;
        if ((uint)patchedBytes.Length > blocksAllocated * 2048u)
        {
            _logger.LogWarning(
                "ISO9660: patched grub.cfg ({N} B) exceeds {K}×2048 B - skipping",
                patchedBytes.Length, blocksAllocated);
            skippedPaths.Add("/boot/grub2/grub.cfg (patched content too large)");
            return;
        }

        //   Write back                             
        if (!Iso9660WriteFile(disk, fileLba, patchedBytes, blocksAllocated))
        {
            _logger.LogWarning("ISO9660: write failed for /boot/grub2/grub.cfg");
            skippedPaths.Add("/boot/grub2/grub.cfg (write error)");
            return;
        }

        // Update Data Length in the directory entry (LE at +10, BE at +14).
        if ((uint)patchedBytes.Length != fileSize)
            Iso9660UpdateEntrySize(disk, fileEntryBlock, fileEntryOff, (uint)patchedBytes.Length);

        _logger.LogInformation(
            "ISO9660: /boot/grub2/grub.cfg patched ({Old} → {New} bytes)",
            fileSize, patchedBytes.Length);
        patchedPaths.Add("/boot/grub2/grub.cfg");
    }

    //   ISO9660 sector helpers                         

    
    private static byte[]? ReadIso9660Block(SafeFileHandle disk, uint blockNum)
    {
        var buf = new byte[2048];
        long baseLba = (long)blockNum * 4;
        for (int i = 0; i < 4; i++)
        {
            var sec = new byte[512];
            if (!ReadSector(disk, baseLba + i, sec))
                return null;
            Buffer.BlockCopy(sec, 0, buf, i * 512, 512);
        }
        return buf;
    }

    
    private static bool WriteIso9660Block(SafeFileHandle disk, uint blockNum, byte[] data)
    {
        long baseLba = (long)blockNum * 4;
        for (int i = 0; i < 4; i++)
        {
            var sec = new byte[512];
            Buffer.BlockCopy(data, i * 512, sec, 0, 512);
            if (!WriteSector(disk, baseLba + i, sec))
                return false;
        }
        return true;
    }

    private static bool Iso9660FindEntry(
        SafeFileHandle disk,
        uint dirLba,
        uint dirSize,
        string name,
        bool isDir,
        out uint extLba,
        out uint extSize,
        out uint entryBlock,
        out int entryOff)
    {
        extLba = extSize = entryBlock = 0;
        entryOff = 0;
        uint blocksToRead = (dirSize + 2047u) / 2048u;

        for (uint b = 0; b < blocksToRead; b++)
        {
            var block = ReadIso9660Block(disk, dirLba + b);
            if (block is null)
                return false;

            int off = 0;
            while (off + 33 <= 2048)
            {
                byte recLen = block[off];
                if (recLen == 0)
                    break;          // padding to end of logical block
                if (recLen < 33)
                { off++; continue; }

                byte fileFlags = block[off + 25];
                bool entIsDir = (fileFlags & 0x02) != 0;
                if (entIsDir != isDir)
                { off += recLen; continue; }

                byte fileIdLen = block[off + 32];
                if (fileIdLen == 0 || off + 33 + fileIdLen > 2048)
                { off += recLen; continue; }

                // Skip "." (0x00) and ".." (0x01) self/parent entries.
                if (fileIdLen == 1 &&
                    (block[off + 33] == 0x00 || block[off + 33] == 0x01))
                { off += recLen; continue; }

                var id = Encoding.ASCII.GetString(block, off + 33, fileIdLen);
                // ISO9660 file identifiers carry a version suffix (";1", ";2", …) - strip it.
                int semi = id.IndexOf(';', StringComparison.Ordinal);
                if (semi >= 0)
                    id = id[..semi];

                if (!id.Equals(name, StringComparison.OrdinalIgnoreCase))
                { off += recLen; continue; }

                extLba = BitConverter.ToUInt32(block, off + 2);   // Extent Location, LE
                extSize = BitConverter.ToUInt32(block, off + 10);  // Data Length, LE
                entryBlock = dirLba + b;
                entryOff = off;
                return true;
            }
        }
        return false;
    }

    
    private static byte[]? Iso9660ReadFile(SafeFileHandle disk, uint fileLba, int fileSize)
    {
        var result = new byte[fileSize];
        uint blocks = ((uint)fileSize + 2047u) / 2048u;
        int written = 0;

        for (uint b = 0; b < blocks && written < fileSize; b++)
        {
            var block = ReadIso9660Block(disk, fileLba + b);
            if (block is null)
                return null;
            int copy = Math.Min(2048, fileSize - written);
            Buffer.BlockCopy(block, 0, result, written, copy);
            written += copy;
        }
        return result;
    }

    private static bool Iso9660WriteFile(
        SafeFileHandle disk, uint fileLba, byte[] content, uint blocksAllocated)
    {
        for (uint b = 0; b < blocksAllocated; b++)
        {
            var block = new byte[2048];          // zero-initialised → auto-pad
            int srcOff = (int)(b * 2048);
            int copy = Math.Max(0, Math.Min(2048, content.Length - srcOff));
            if (copy > 0)
                Buffer.BlockCopy(content, srcOff, block, 0, copy);
            if (!WriteIso9660Block(disk, fileLba + b, block))
                return false;
        }
        return true;
    }

    private static bool Iso9660UpdateEntrySize(
        SafeFileHandle disk, uint blockNum, int off, uint newSize)
    {
        var block = ReadIso9660Block(disk, blockNum);
        if (block is null)
            return false;

        // LE copy
        BitConverter.TryWriteBytes(block.AsSpan(off + 10, 4), newSize);
        // BE copy
        block[off + 14] = (byte)(newSize >> 24);
        block[off + 15] = (byte)(newSize >> 16);
        block[off + 16] = (byte)(newSize >> 8);
        block[off + 17] = (byte)newSize;

        return WriteIso9660Block(disk, blockNum, block);
    }

    //   FAT helpers (FAT12 / FAT16 / FAT32)                  

    private static bool FatFindInFixedRoot(
        SafeFileHandle disk,
        long rootLba,
        long rootSectors,
        string name,
        bool isDirectory,
        out uint entCluster,
        out uint entSize,
        out long entLba,
        out int entOff)
    {
        entCluster = entSize = 0;
        entLba = 0;
        entOff = 0;
        var target = Fat32Make83(name);

        for (long s = 0; s < rootSectors; s++)
        {
            var sec = new byte[512];
            if (!ReadSector(disk, rootLba + s, sec))
                return false;

            for (int i = 0; i <= 512 - 32; i += 32)
            {
                if (sec[i] == 0x00)
                    return false;  // end of directory
                if (sec[i] == 0xE5)
                    continue;       // deleted
                byte attr = sec[i + 11];
                if (attr == 0x0F)
                    continue;   // LFN
                if ((attr & 0x08) != 0)
                    continue;   // volume label
                if ((attr & 0x10) != 0 != isDirectory)
                    continue;

                bool match = true;
                for (int b = 0; b < 11; b++)
                    if (sec[i + b] != target[b])
                    { match = false; break; }
                if (!match)
                    continue;

                uint hi = BitConverter.ToUInt16(sec, i + 20);
                uint lo = BitConverter.ToUInt16(sec, i + 26);
                entCluster = (hi << 16) | lo;
                entSize = BitConverter.ToUInt32(sec, i + 28);
                entLba = rootLba + s;
                entOff = i;
                return true;
            }
        }
        return false;
    }

    private static bool FatFindInClusters(
        SafeFileHandle disk,
        uint dirCluster,
        string name,
        bool isDirectory,
        byte secsPerClust,
        long fatLba,
        long dataLba,
        bool isFat32,
        out uint entCluster,
        out uint entSize,
        out long entLba,
        out int entOff)
    {
        entCluster = entSize = 0;
        entLba = 0;
        entOff = 0;
        var target = Fat32Make83(name);
        uint cluster = dirCluster;

        while (FatIsValidCluster(cluster, isFat32))
        {
            long clBase = dataLba + (long)(cluster - 2) * secsPerClust;
            for (int s = 0; s < secsPerClust; s++)
            {
                var sec = new byte[512];
                if (!ReadSector(disk, clBase + s, sec))
                    return false;

                for (int i = 0; i <= 512 - 32; i += 32)
                {
                    if (sec[i] == 0x00)
                        return false;  // end of directory
                    if (sec[i] == 0xE5)
                        continue;       // deleted
                    byte attr = sec[i + 11];
                    if (attr == 0x0F)
                        continue;   // LFN
                    if ((attr & 0x08) != 0)
                        continue;   // volume label
                    if ((attr & 0x10) != 0 != isDirectory)
                        continue;

                    bool match = true;
                    for (int b = 0; b < 11; b++)
                        if (sec[i + b] != target[b])
                        { match = false; break; }
                    if (!match)
                        continue;

                    uint hi = BitConverter.ToUInt16(sec, i + 20);
                    uint lo = BitConverter.ToUInt16(sec, i + 26);
                    entCluster = (hi << 16) | lo;
                    entSize = BitConverter.ToUInt32(sec, i + 28);
                    entLba = clBase + s;
                    entOff = i;
                    return true;
                }
            }

            if (!FatNextCluster(disk, fatLba, cluster, isFat32, out cluster))
                return false;
        }
        return false;
    }

    
    private static byte[]? FatReadFile(
        SafeFileHandle disk, uint startCluster, int fileSize,
        byte secsPerClust, long fatLba, long dataLba, bool isFat32)
    {
        var buf = new List<byte>(fileSize + 512);
        uint cluster = startCluster;

        while (FatIsValidCluster(cluster, isFat32)
               && buf.Count <= fileSize + secsPerClust * 512)
        {
            long clBase = dataLba + (long)(cluster - 2) * secsPerClust;
            for (int s = 0; s < secsPerClust; s++)
            {
                var sec = new byte[512];
                if (!ReadSector(disk, clBase + s, sec))
                    return null;
                buf.AddRange(sec);
            }
            if (!FatNextCluster(disk, fatLba, cluster, isFat32, out cluster))
                break;
        }

        return buf.Count >= fileSize ? buf.Take(fileSize).ToArray() : null;
    }

    private bool FatWriteFile(
        SafeFileHandle disk, uint startCluster, byte[] content, int originalSize,
        byte secsPerClust, long fatLba, long dataLba, bool isFat32)
    {
        int clSize = secsPerClust * 512;

        var clusters = new List<uint>();
        uint c = startCluster;
        while (FatIsValidCluster(c, isFat32))
        {
            clusters.Add(c);
            if (!FatNextCluster(disk, fatLba, c, isFat32, out c))
                break;
        }

        if (content.Length > clusters.Count * clSize)
        {
            _logger.LogWarning(
                "FAT: patched content ({N} B) exceeds {K}×{CS} B - skipping write",
                content.Length, clusters.Count, clSize);
            return false;
        }

        int written = 0;
        foreach (var clBase in clusters.Select(cl => dataLba + (long)(cl - 2) * secsPerClust))
        {
            for (int s = 0; s < secsPerClust; s++)
            {
                var sec = new byte[512];
                int copy = Math.Min(512, content.Length - written);
                if (copy > 0)
                    Buffer.BlockCopy(content, written, sec, 0, copy);
                if (!WriteSector(disk, clBase + s, sec))
                    return false;
                written += copy;
            }
        }
        return true;
    }

    private static bool FatNextCluster(
        SafeFileHandle disk, long fatLba, uint cluster, bool isFat32, out uint next)
    {
        if (isFat32)
        {
            next = 0x0FFF_FFF7u;
            long off = (long)cluster * 4;
            var sec = new byte[512];
            if (!ReadSector(disk, fatLba + off / 512, sec))
                return false;
            next = BitConverter.ToUInt32(sec, (int)(off % 512)) & 0x0FFF_FFFFu;
        }
        else   // FAT16 / FAT12
        {
            next = 0xFFF7u;
            long off = (long)cluster * 2;
            var sec = new byte[512];
            if (!ReadSector(disk, fatLba + off / 512, sec))
                return false;
            next = BitConverter.ToUInt16(sec, (int)(off % 512));
        }
        return true;
    }

    private static bool FatIsValidCluster(uint cluster, bool isFat32)
        => cluster >= 2 && (isFat32 ? cluster < 0x0FFF_FFF7u : cluster < 0xFFF7u);

    internal static byte[] Fat32Make83(string name)
    {
        var r = new byte[11];
        Array.Fill(r, (byte)' ');
        var up = name.ToUpperInvariant();
        int dot = up.LastIndexOf('.');
        var b = dot >= 0 ? up[..dot] : up;
        var e = dot >= 0 ? up[(dot + 1)..] : "";
        for (int i = 0; i < Math.Min(8, b.Length); i++)
            r[i] = (byte)b[i];
        for (int i = 0; i < Math.Min(3, e.Length); i++)
            r[8 + i] = (byte)e[i];
        return r;
    }

    /// <remarks>
    /// <c>internal</c> for unit-testing without a real USB drive.
    /// </remarks>
    internal static string PatchGrubCfgContent(string content)
    {
        // Match: [indent] linuxefi|linux [path] [params...] [line-end]
        // Group 1 = whole line body (no trailing whitespace or line-end).
        // Group 2 = line terminator (\r\n or \n or end-of-string).
        return Regex.Replace(
            content,
            @"^([ \t]*linux(?:efi)?[ \t]+\S[^\r\n]*?)[ \t]*(\r?\n|$)",
            m =>
            {
                var line = m.Groups[1].Value;
                var newline = m.Groups[2].Value;

                //   rd.live.check                       ─
                // Fedora's dracut live module uses `getarg rd.live.check` which
                // is a PRESENCE check - even `rd.live.check=0` triggers the media
                // integrity check.  Because we modified LBA 0/1 (MBR/GPT) and the
                // grub.cfg itself, the check always fails on our USB.
                // The only reliable fix is to REMOVE the parameter entirely so
                // dracut never starts checkisomd5@.service.
                line = Regex.Replace(line, @"[ \t]+rd\.live\.check(?:=\S*)?", string.Empty);

                //   nomodeset                         ─
                // Strip then re-add so it appears exactly once, de-duplicated on
                // re-runs.  Prevents the black screen on VMs with a virtual GPU.
                line = Regex.Replace(line, @"[ \t]+nomodeset(?=[ \t]|$)", string.Empty);

                return line.TrimEnd() + " nomodeset" + newline;
            },
            RegexOptions.Multiline);
    }

}
