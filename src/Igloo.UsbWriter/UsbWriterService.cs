using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Igloo.UsbWriter;

/// <summary>
/// Implements the USB write pipeline:
///   1. Raw-write the ISO to <c>\\.\PhysicalDriveN</c>.
///   2. Create a FAT32 <c>OEMDRV</c> partition in the remaining unallocated space
///      so Anaconda finds the kickstart automatically.
///   3. Copy the staging directory (ks.cfg, igloo-agent, migration-manifest.json,
///      and any staged Windows files) onto the OEMDRV partition.
///
/// <b>Elevation required:</b> direct physical-drive writes need administrator rights.
/// <see cref="WriteAsync"/> throws <see cref="UnauthorizedAccessException"/> before
/// touching any drive when the calling process is not elevated.
///
/// <b>Cancellation contract:</b>
///   Phase 1 (raw write)  - cancelable at any 128 KB boundary.
///   Phase 2 (diskpart)   - <em>atomic</em>; the CancellationToken is checked
///                          <em>before</em> and <em>after</em> but never during the
///                          diskpart run.  Cancelling mid-partition creation would leave
///                          the partition table in an undefined state.
///   Phase 3 (file copy)  - cancelable at every file boundary.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UsbWriterService : IUsbWriterService
{
    // 128 KB - a power-of-two multiple of the 512-byte sector size; good USB throughput.
    private const int BufferSize = 128 * 1024;

    private readonly ILogger<UsbWriterService> _logger;

    public UsbWriterService(ILogger<UsbWriterService> logger) => _logger = logger;

    // ── Enumerate ─────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<UsbDriveInfo>> EnumerateDrivesAsync(
        CancellationToken ct = default)
    {
        // TODO v1.1: Win32_DiskDrive first-call latency on a cold WMI host is typically
        // 1–3 seconds.  Lower-latency alternative: SetupDiGetClassDevs with
        // GUID_DEVINTERFACE_DISK (SetupAPI) goes through the kernel device manager instead.
        var drives = new List<UsbDriveInfo>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Index, Model, Size FROM Win32_DiskDrive " +
            "WHERE InterfaceType='USB'");

        foreach (ManagementObject obj in searcher.Get())
        {
            ct.ThrowIfCancellationRequested();

            var deviceId = obj["DeviceID"]?.ToString() ?? string.Empty;
            var index = Convert.ToInt32(obj["Index"]);
            var model = obj["Model"]?.ToString() ?? "Unknown USB Drive";
            var sizeStr = obj["Size"]?.ToString();
            var sizeBytes = sizeStr is not null ? long.Parse(sizeStr) : 0L;

            drives.Add(new UsbDriveInfo(index, model, sizeBytes, deviceId));
            _logger.LogDebug("USB drive found: [{Index}] {Model} ({Size:N0} bytes)", index, model, sizeBytes);
        }

        return Task.FromResult<IReadOnlyList<UsbDriveInfo>>(drives);
    }

    // ── Write (orchestration) ─────────────────────────────────────────────────

    public async Task WriteAsync(
        UsbDriveInfo drive,
        string isoPath,
        string stagingDirectory,
        IProgress<UsbWriteProgress>? progress,
        CancellationToken ct = default)
    {
        if (!IsCurrentProcessElevated())
            throw new UnauthorizedAccessException(
                "Writing to a physical drive requires administrator rights. " +
                "Please restart iGloo as Administrator and repeat the process from this step.");

        // Compute sizes upfront so we can fail before touching the drive.
        var isoSize = new FileInfo(isoPath).Length;
        var stagingSize = GetDirectorySize(stagingDirectory);

        // Partition must hold all staging files plus a 256 MB buffer; minimum 512 MB.
        var partSizeMb = (int)Math.Max(512L, stagingSize / (1024 * 1024) + 256);

        // ── Pre-flight: does the drive have enough room? ──────────────────────
        ValidateFit(drive, isoSize, partSizeMb);

        // ── Dismount volumes on target disk ───────────────────────────────────
        // Windows refuses raw WriteFile to \\.\PhysicalDriveN while any volume on
        // that disk is mounted, even from an elevated process (Win32 error 5 /
        // ERROR_ACCESS_DENIED).  We lock and force-dismount every volume on the
        // target disk and keep the handles open so Windows cannot re-mount them
        // during the write.  The finally block disposes the handles when we are done.
        var heldVolumeHandles = LockAndDismountVolumesOnDisk(drive.DriveIndex);
        try
        {
            // ── Phase 1: raw ISO write - cancelable ───────────────────────────

            _logger.LogInformation(
                "Phase 1 - writing ISO {Path} ({Size:N0} bytes) to {Device}",
                isoPath, isoSize, drive.DeviceId);

            progress?.Report(new UsbWriteProgress(
                UsbWritePhase.WritingIso, 0, isoSize, "Writing installer image…"));

            await WriteIsoRawAsync(drive.DeviceId, isoPath, isoSize, progress, ct);

            // Cancellation point: checked between Phase 1 and Phase 2.
            ct.ThrowIfCancellationRequested();

            // ── GRUB config patch - must run BEFORE EnsureProtectiveMbrAsync ──
            // EnsureProtectiveMbrAsync ends with IOCTL_DISK_UPDATE_PROPERTIES, which
            // tells Windows to re-scan the partition table.  Once Windows sees the
            // new partitions it may auto-mount them, after which raw WriteFile calls
            // to \\.\PhysicalDriveN are blocked by the volume manager.
            // Patching here, right after the raw ISO write, means Windows still
            // has no idea the partition layout has changed - raw writes always succeed.
            try
            {
                await PatchGrubConfigAsync(drive, progress);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "GRUB config patch failed (non-fatal) - boot may require " +
                    "manual kernel parameters: nomodeset rd.live.check=0");
                progress?.Report(new UsbWriteProgress(
                    UsbWritePhase.PatchingGrub, 0, 0,
                    "⚠ GRUB patch failed - you may see a media check error on first boot."));
            }

            // ── MBR → GPT fix (runs before diskpart, no separate UI phase) ──
            // Fedora Live ISOs embed a hybrid MBR that fills all 4 primary
            // partition slots, so diskpart cannot create a 5th.  The disk has
            // a valid GPT at LBA 1.  Replace the hybrid MBR partition entries
            // with a single protective-MBR entry (type 0xEE) so Windows exposes
            // the GPT view and diskpart gains room to add the OEMDRV partition.
            await EnsureProtectiveMbrAsync(drive.DeviceId, drive.SizeBytes);

            // ── Phase 2: create OEMDRV partition - atomic (not cancelable) ────

            _logger.LogInformation("Phase 2 - creating OEMDRV partition on disk {Index}", drive.DriveIndex);

            progress?.Report(new UsbWriteProgress(
                UsbWritePhase.CreatingOemdrv, 0, 0, "Creating OEMDRV partition…"));

            var driveLetter = FindFreeDriveLetter();

            // CancellationToken.None is intentional: interrupting diskpart mid-run can
            // leave the partition table in an inconsistent state.
            await RunDiskpartAsync(drive.DriveIndex, partSizeMb, driveLetter, CancellationToken.None);

            // Give Windows a moment to mount the new FAT32 volume before we validate.
            await Task.Delay(2500, CancellationToken.None);

            // Verify that diskpart actually created and mounted the partition.
            // diskpart returns exit code 0 even when individual commands fail silently.
            ValidateOemDrvMounted(driveLetter);

            // Cancellation point: checked between Phase 2 and Phase 3.
            ct.ThrowIfCancellationRequested();

            // ── Phase 3: copy staging files - cancelable ──────────────────────

            _logger.LogInformation(
                "Phase 3 - copying staging directory {Dir} to {Letter}:\\",
                stagingDirectory, driveLetter);

            progress?.Report(new UsbWriteProgress(
                UsbWritePhase.CopyingFiles, 0, stagingSize, "Copying migration files…"));

            await CopyStagingFilesAsync(driveLetter, stagingDirectory, stagingSize, progress, ct);

            progress?.Report(new UsbWriteProgress(UsbWritePhase.Complete, 0, 0, "USB drive is ready."));
            _logger.LogInformation("USB write complete - drive {Index} is ready", drive.DriveIndex);
        }
        finally
        {
            // Releasing the volume handles allows Windows to attempt re-mounting.
            foreach (var h in heldVolumeHandles)
                h.Dispose();
        }
    }

    // ── Pre-flight validation ─────────────────────────────────────────────────

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> with a human-readable message
    /// when the drive is too small to hold both the ISO and the OEMDRV partition.
    /// Called <em>before</em> any write, so the user gets a clear explanation
    /// rather than a mid-write failure or a silent "Anaconda drops to manual install".
    /// </summary>
    internal static void ValidateFit(UsbDriveInfo drive, long isoSize, int partSizeMb)
    {
        if (drive.SizeBytes <= 0)
            return; // WMI returned unknown size - skip check, let the write attempt proceed.

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

    /// <summary>
    /// Verifies that the OEMDRV partition was actually created and mounted.
    /// diskpart exits with code 0 even when individual commands fail silently,
    /// so trusting the exit code alone would leave the user with a USB stick
    /// that boots into manual-install mode with no kickstart.
    /// </summary>
    private void ValidateOemDrvMounted(char driveLetter)
    {
        var root = $"{driveLetter}:\\";
        if (Directory.Exists(root))
        {
            _logger.LogInformation("OEMDRV partition confirmed at {Root}", root);
            return;
        }

        throw new InvalidOperationException(
            $"diskpart reported success but the OEMDRV partition did not mount at {driveLetter}:\\. " +
            "The partition table may be in an inconsistent state. " +
            "To recover: open diskpart manually, run 'select disk N' then 'clean', " +
            "and re-run the writer from the beginning.");
    }

    // ── Phase 1 implementation ────────────────────────────────────────────────

    private async Task WriteIsoRawAsync(
        string deviceId,
        string isoPath,
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
        var handle = NativeMethods.CreateFileW(
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

        // FileStream takes ownership of the SafeFileHandle and closes it on Dispose.
        await using var dest = new FileStream(handle, FileAccess.Write, bufferSize: BufferSize, isAsync: true);
        using var src = new FileStream(isoPath, FileMode.Open, FileAccess.Read,
                                         FileShare.Read, bufferSize: BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        var written = 0L;
        int bytesRead;

        while ((bytesRead = await src.ReadAsync(buffer, ct)) > 0)
        {
            // Physical-drive writes must be sector-aligned (512 bytes); pad last chunk.
            var toWrite = RoundUpToSector(bytesRead);
            if (toWrite != bytesRead)
                Array.Clear(buffer, bytesRead, toWrite - bytesRead);

            await dest.WriteAsync(buffer.AsMemory(0, toWrite), ct);
            written += bytesRead;

            progress?.Report(new UsbWriteProgress(
                UsbWritePhase.WritingIso, written, isoSize,
                $"Writing… {written / (1024 * 1024):N0} MB / {isoSize / (1024 * 1024):N0} MB"));
        }

        await dest.FlushAsync(ct);
        _logger.LogInformation("ISO write complete: {Written:N0} bytes written to {Device}", written, deviceId);
    }

    internal static int RoundUpToSector(int bytes, int sectorSize = 512) =>
        ((bytes + sectorSize - 1) / sectorSize) * sectorSize;

    // ── Phase 2 implementation ────────────────────────────────────────────────

    private async Task RunDiskpartAsync(
        int diskIndex,
        int partSizeMb,
        char driveLetter,
        CancellationToken ct)
    {
        // ── TODO v1.1 ──────────────────────────────────────────────────────────
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
        // ──────────────────────────────────────────────────────────────────────

        var script = $"""
            select disk {diskIndex}
            create partition primary size={partSizeMb}
            format fs=fat32 label=OEMDRV quick
            assign letter={driveLetter}
            """;

        var scriptPath = Path.Combine(
            Path.GetTempPath(), $"igloo-diskpart-{Guid.NewGuid():N}.txt");

        _logger.LogDebug("diskpart script ({Path}):\n{Script}", scriptPath, script);

        try
        {
            await File.WriteAllTextAsync(scriptPath, script, ct);

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
            await proc.WaitForExitAsync(ct);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

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
            try
            { File.Delete(scriptPath); }
            catch { /* best-effort cleanup */ }
        }
    }

    // ── Phase 3 implementation ────────────────────────────────────────────────

    private async Task CopyStagingFilesAsync(
        char driveLetter,
        string stagingDirectory,
        long totalBytes,
        IProgress<UsbWriteProgress>? progress,
        CancellationToken ct)
    {
        var destRoot = $"{driveLetter}:\\";
        var written = 0L;

        foreach (var srcPath in
            Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var fileSize = new FileInfo(srcPath).Length;
            var relative = Path.GetRelativePath(stagingDirectory, srcPath);
            var destPath = Path.Combine(destRoot, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            await using var srcStream = new FileStream(
                srcPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: BufferSize, useAsync: true);
            await using var destStream = new FileStream(
                destPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: BufferSize, useAsync: true);

            await srcStream.CopyToAsync(destStream, ct);

            written += fileSize;
            progress?.Report(new UsbWriteProgress(
                UsbWritePhase.CopyingFiles, written, totalBytes,
                Path.GetFileName(srcPath)));
        }

        _logger.LogInformation("Staging copy complete: {Written:N0} bytes copied to {Root}", written, destRoot);
    }

    // ── GRUB config patching ──────────────────────────────────────────────────

    /// <summary>
    /// Best-effort: opens the physical disk once and patches every grub.cfg it can
    /// find via raw sector access - no drive-letter assignment, no diskpart.
    ///
    /// <para>Two boot paths are covered:</para>
    /// <list type="bullet">
    ///   <item><b>UEFI</b> - FAT32 EFI partition:
    ///     <c>EFI/BOOT/grub.cfg</c> and <c>EFI/fedora/grub.cfg</c>.</item>
    ///   <item><b>BIOS/legacy</b> - ISO9660 data area:
    ///     <c>/boot/grub2/grub.cfg</c>.</item>
    /// </list>
    ///
    /// <para>Required because:</para>
    /// <list type="bullet">
    ///   <item><b>rd.live.check=0</b> - the ISO integrity check fails after our
    ///   MBR/GPT modifications (LBA 0 and LBA 1 were rewritten).</item>
    ///   <item><b>nomodeset</b> - prevents a black screen on VMs where Wayland
    ///   cannot enumerate the virtual GPU on first boot.</item>
    /// </list>
    /// </summary>
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

        try
        {
            // ── Path 1: EFI FAT32 (UEFI boot) ────────────────────────────────
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

            // ── Path 2: ISO9660 (BIOS/legacy boot) ───────────────────────────
            PatchIso9660GrubCfg(handle, patchedPaths, skippedPaths);
        }
        finally { handle.Dispose(); }

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

    /// <summary>
    /// Reads the GPT on the already-open <paramref name="handle"/> and returns the
    /// <c>StartingLBA</c> of the EFI System Partition, or −1 if not found.
    /// </summary>
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

    /// <summary>
    /// Walks the FAT filesystem on the EFI partition and patches every
    /// <c>grub.cfg</c> found at the known Fedora Live paths.
    /// Supports FAT12, FAT16, and FAT32 - Fedora's EFI partition is often FAT16
    /// (~20 MB image), not FAT32.
    /// Results are appended to <paramref name="patchedPaths"/> / <paramref name="skippedPaths"/>.
    /// </summary>
    private void PatchGrubCfgsOnFatVolume(
        SafeFileHandle disk,
        long partLba,
        List<string> patchedPaths,
        List<string> skippedPaths)
    {
        // ── Parse BPB ────────────────────────────────────────────────────────────
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

        // ── Patch every known grub.cfg location ───────────────────────────────
        string[][] candidates =
        [
            ["EFI", "BOOT",   "GRUB.CFG"],
            ["EFI", "FEDORA", "GRUB.CFG"],
        ];

        foreach (var parts in candidates)
        {
            var label = string.Join("/", parts).ToLowerInvariant();

            // Start at the root.  For FAT16/12 the root is a fixed linear region;
            // for FAT32 and all subdirectories it is a cluster chain.
            bool inFat16Root = !isFat32;
            uint dirCluster = isFat32 ? rootCluster : 0;
            bool ok = true;

            for (int d = 0; d < parts.Length - 1 && ok; d++)
            {
                bool found;
                uint sub;
                if (inFat16Root)
                    found = FatFindInFixedRoot(disk, fat16RootLba, fat16RootSectors,
                                parts[d], isDirectory: true,
                                out sub, out _, out _, out _);
                else
                    found = FatFindInClusters(disk, dirCluster, parts[d], isDirectory: true,
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
                    ? text[..200].Replace("\n", "↵").Replace("\r", "") + "…"
                    : text.Replace("\n", "↵").Replace("\r", "");
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

    // ── ISO9660 GRUB config patching (BIOS/legacy boot path) ─────────────────

    /// <summary>
    /// Locates and patches <c>/boot/grub2/grub.cfg</c> in the ISO9660 filesystem
    /// that was raw-written to the disk in Phase 1.  This covers BIOS/legacy boot,
    /// where GRUB reads its config from the ISO data area rather than the EFI partition.
    ///
    /// <para>
    /// ISO9660 uses 2048-byte logical blocks.  Block N maps to disk LBA N×4
    /// (since we use 512-byte sectors).  The PVD lives at block 16 (LBA 64).
    /// Files are stored in contiguous block extents so the patch writes back
    /// into the same extent without any reallocation.
    /// </para>
    /// </summary>
    private void PatchIso9660GrubCfg(
        SafeFileHandle disk,
        List<string> patchedPaths,
        List<string> skippedPaths)
    {
        // ── Validate Primary Volume Descriptor at logical block 16 ────────────
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

        // ── Navigate /boot/grub2/ ─────────────────────────────────────────────
        uint dirLba = rootLba;
        uint dirSize = rootSize;

        foreach (var segment in (string[])["boot", "grub2"])
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

        // ── Find grub.cfg ─────────────────────────────────────────────────────
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

        // ── Read, patch, validate fit ─────────────────────────────────────────
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
                ? text[..200].Replace("\n", "↵").Replace("\r", "") + "…"
                : text.Replace("\n", "↵").Replace("\r", "");
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

        // ── Write back ────────────────────────────────────────────────────────
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

    // ── ISO9660 sector helpers ────────────────────────────────────────────────

    /// <summary>Reads one 2048-byte ISO9660 logical block (4 consecutive 512-byte sectors).</summary>
    private byte[]? ReadIso9660Block(SafeFileHandle disk, uint blockNum)
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

    /// <summary>Writes one 2048-byte ISO9660 logical block as 4 consecutive 512-byte sectors.</summary>
    private bool WriteIso9660Block(SafeFileHandle disk, uint blockNum, byte[] data)
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

    /// <summary>
    /// Scans the ISO9660 directory spanning (<paramref name="dirLba"/>,
    /// <paramref name="dirSize"/>) for an entry whose identifier matches
    /// <paramref name="name"/> (case-insensitive, ISO9660 version suffix stripped).
    ///
    /// Returns the entry's extent location, data size, and its position within
    /// the on-disk directory block so the caller can update the file size in-place.
    /// </summary>
    private bool Iso9660FindEntry(
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
                int semi = id.IndexOf(';');
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

    /// <summary>Reads <paramref name="fileSize"/> bytes of file data from the ISO9660 extent.</summary>
    private byte[]? Iso9660ReadFile(SafeFileHandle disk, uint fileLba, int fileSize)
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

    /// <summary>
    /// Writes <paramref name="content"/> into the ISO9660 extent at <paramref name="fileLba"/>,
    /// zero-padding any remaining space in the last 2048-byte block.
    /// </summary>
    private bool Iso9660WriteFile(
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

    /// <summary>
    /// Patches the Data Length field of an ISO9660 directory entry in-place.
    /// Writes both the little-endian copy (record offset +10) and the
    /// big-endian copy (record offset +14) as required by the ISO9660 spec.
    /// </summary>
    private bool Iso9660UpdateEntrySize(
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

    // ── FAT helpers (FAT12 / FAT16 / FAT32) ──────────────────────────────────

    /// <summary>
    /// Scans the FAT16/12 <em>fixed</em> root directory (at a known LBA range,
    /// not managed by the cluster chain) for an entry matching <paramref name="name"/>.
    /// </summary>
    private bool FatFindInFixedRoot(
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

    /// <summary>
    /// Scans a cluster-chain directory (FAT16/32 subdirectory or FAT32 root) for
    /// an entry matching <paramref name="name"/>.
    /// Pass <paramref name="isFat32"/>=<see langword="false"/> for FAT16/12 cluster chains.
    /// </summary>
    private bool FatFindInClusters(
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

    /// <summary>Reads all bytes of a FAT file by following its cluster chain.</summary>
    private byte[]? FatReadFile(
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

    /// <summary>
    /// Writes <paramref name="content"/> into the file's existing cluster chain.
    /// Returns <see langword="false"/> if the content exceeds the allocated clusters
    /// (no new clusters are allocated - the patch only adds ~25 bytes/line so this
    /// never triggers in practice).
    /// </summary>
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
        foreach (var cl in clusters)
        {
            long clBase = dataLba + (long)(cl - 2) * secsPerClust;
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

    /// <summary>
    /// Returns the next cluster in the FAT chain.
    /// Handles both FAT16 (2-byte entries) and FAT32 (4-byte entries, upper 4 bits reserved).
    /// </summary>
    private bool FatNextCluster(
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

    /// <summary>Returns <see langword="true"/> when <paramref name="cluster"/> is a
    /// valid data cluster (≥ 2 and below the bad/EOC markers).</summary>
    private static bool FatIsValidCluster(uint cluster, bool isFat32)
        => cluster >= 2 && (isFat32 ? cluster < 0x0FFF_FFF7u : cluster < 0xFFF7u);

    /// <summary>
    /// Converts <paramref name="name"/> to an 11-byte FAT 8.3 short name:
    /// up to 8 uppercase base-name bytes followed by up to 3 uppercase extension
    /// bytes, space-padded to exactly 11 bytes.
    /// </summary>
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

    /// <summary>
    /// Returns a new copy of <paramref name="content"/> with every
    /// <c>linuxefi</c> / <c>linux</c> kernel line modified to include
    /// <c>nomodeset</c> and <c>rd.live.check=0</c>.
    ///
    /// <para>
    /// Existing <c>nomodeset</c> and <c>rd.live.check[=…]</c> tokens are stripped
    /// first so re-running the writer does not accumulate duplicate parameters.
    /// </para>
    /// </summary>
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

                // ── rd.live.check ─────────────────────────────────────────────
                // Fedora's dracut live module uses `getarg rd.live.check` which
                // is a PRESENCE check - even `rd.live.check=0` triggers the media
                // integrity check.  Because we modified LBA 0/1 (MBR/GPT) and the
                // grub.cfg itself, the check always fails on our USB.
                // The only reliable fix is to REMOVE the parameter entirely so
                // dracut never starts checkisomd5@.service.
                line = Regex.Replace(line, @"[ \t]+rd\.live\.check(?:=\S*)?", string.Empty);

                // ── nomodeset ─────────────────────────────────────────────────
                // Strip then re-add so it appears exactly once, de-duplicated on
                // re-runs.  Prevents the black screen on VMs with a virtual GPU.
                line = Regex.Replace(line, @"[ \t]+nomodeset(?=[ \t]|$)", string.Empty);

                return line.TrimEnd() + " nomodeset" + newline;
            },
            RegexOptions.Multiline);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsCurrentProcessElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

    private static char FindFreeDriveLetter()
    {
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();

        foreach (var letter in "FGHIJKLMNOPQRSTUVWXYZ")
        {
            if (!used.Contains(letter))
                return letter;
        }

        throw new InvalidOperationException(
            "No free drive letter is available to assign to the OEMDRV partition.");
    }

    private static long GetDirectorySize(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);

    // ── Hybrid-MBR → protective-MBR conversion ───────────────────────────────

    /// <summary>
    /// Prepares the partition table on <paramref name="deviceId"/> so that
    /// diskpart can create the OEMDRV partition. Does two things in order:
    ///
    /// <list type="number">
    ///   <item><b>Protective MBR</b> - Fedora Live ISOs embed a hybrid MBR that
    ///   fills all four primary MBR slots for BIOS-boot compatibility while also
    ///   carrying a full GPT at LBA 1.  Windows/diskpart prefers the hybrid MBR
    ///   and reports the disk as "MBR, 4 partitions, no room".  Replacing the
    ///   four MBR entries with a single type-0xEE protective entry makes Windows
    ///   switch to the GPT view.</item>
    ///
    ///   <item><b>GPT resize</b> - The ISO's GPT header records the disk size as
    ///   the ISO file size (~2.3 GB).  <c>LastUsableLBA</c> and
    ///   <c>AlternateLBA</c> point to the end of the ISO, so diskpart sees zero
    ///   free space even on a 115 GB drive.  This step updates both headers (and
    ///   moves the backup GPT to the actual end of the drive) so diskpart sees
    ///   the full unallocated space.</item>
    /// </list>
    ///
    /// Throws <see cref="InvalidOperationException"/> when there is no GPT and
    /// the MBR is full (pure-MBR ISOs are not supported in v1).
    /// </summary>
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
            return;
        }

        try
        {
            // ── Step 1: read LBA 0 (MBR) + LBA 1 (GPT header) ────────────────
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

            // ── Step 2: write protective MBR if needed ────────────────────────
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

            // ── Step 3: extend GPT to the full physical disk size ─────────────
            // The ISO's GPT header records AlternateLBA / LastUsableLBA at the
            // end of the ISO file (~4.5 M sectors for a 2.3 GB ISO), not at the
            // end of the 115 GB USB drive.  diskpart respects those boundaries
            // and reports "no free space" even though 112+ GB are unallocated.
            TryExtendGptToFullDisk(handle, diskSizeBytes);

            // ── Step 4: tell the driver to re-read the updated partition table ─
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
        await Task.Delay(1500, CancellationToken.None);
    }

    // ── GPT size extension ────────────────────────────────────────────────────

    /// <summary>
    /// Updates the primary and backup GPT headers so their
    /// <c>LastUsableLBA</c> / <c>AlternateLBA</c> reflect the full physical
    /// disk size, not just the ISO file size.
    ///
    /// <para>
    /// Standard GPT layout (sectors from start/end of disk):
    /// <code>
    ///   LBA 0        Protective MBR
    ///   LBA 1        Primary GPT header
    ///   LBA 2–33     Primary partition entries (128 × 128 B = 16 384 B)
    ///   LBA 34 …     Usable space (FirstUsableLBA … LastUsableLBA)
    ///   …–33 from end  Backup partition entries
    ///   last LBA     Backup GPT header  (AlternateLBA)
    /// </code>
    /// </para>
    /// </summary>
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

        // ── Read primary GPT header (LBA 1) ───────────────────────────────────
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

        // ── Read primary partition entries (LBA 2–33) ─────────────────────────
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

        // ── Update primary GPT header ─────────────────────────────────────────
        BitConverter.TryWriteBytes(hdr.AsSpan(32, 8), newAlternateLBA);   // AlternateLBA
        BitConverter.TryWriteBytes(hdr.AsSpan(48, 8), newLastUsableLBA);  // LastUsableLBA
        // PartitionEntryLBA (offset 72) stays at 2.
        // PartitionEntryArrayCRC32 (offset 88) is unchanged (entries themselves didn't change).

        Array.Clear(hdr, 16, 4);
        uint newPrimaryCrc = GptCrc32(hdr, (int)headerSize);
        BitConverter.TryWriteBytes(hdr.AsSpan(16, 4), newPrimaryCrc);

        // ── Write backup partition entries at new location ────────────────────
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

        // ── Build and write backup GPT header ─────────────────────────────────
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

        // ── Write updated primary GPT header ──────────────────────────────────
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

    /// <summary>
    /// Standard CRC32 (IEEE 802.3 / Ethernet) used by the GPT specification.
    /// Polynomial 0xEDB88320 (bit-reversed), seed 0xFFFFFFFF, final XOR 0xFFFFFFFF.
    /// The CRC is computed over the first <paramref name="length"/> bytes of
    /// <paramref name="data"/>, with the header's own CRC field zeroed before calling.
    /// </summary>
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

    // ── Volume locking / dismounting ──────────────────────────────────────────

    /// <summary>
    /// Locks and force-dismounts every volume that resides on <paramref name="diskIndex"/>
    /// so that Windows permits raw <c>WriteFile</c> calls to <c>\\.\PhysicalDriveN</c>.
    ///
    /// <para>
    /// Windows returns <c>ERROR_ACCESS_DENIED</c> (Win32 error 5) for any raw write to a
    /// physical disk that has at least one mounted volume, even from an elevated process.
    /// The fix is to open each volume device, issue <c>FSCTL_LOCK_VOLUME</c> (advisory -
    /// may fail if files are open) and then <c>FSCTL_DISMOUNT_VOLUME</c> (forceful - flushes
    /// and takes the volume offline).  Keeping the returned handles open prevents Windows
    /// from silently re-mounting the volumes during the write; dispose them when done.
    /// </para>
    /// </summary>
    private List<SafeFileHandle> LockAndDismountVolumesOnDisk(int diskIndex)
    {
        const uint GENERIC_READ = 0x80000000u;
        const uint GENERIC_WRITE = 0x40000000u;
        const uint FILE_SHARE_READ = 0x00000001u;
        const uint FILE_SHARE_WRITE = 0x00000002u;
        const uint OPEN_EXISTING = 3u;
        const uint IOCTL_VOLUME_GET_EXTENTS = 0x00560000u;
        const uint FSCTL_LOCK_VOLUME = 0x00090018u;
        const uint FSCTL_DISMOUNT_VOLUME = 0x00090020u;

        var held = new List<SafeFileHandle>();

        foreach (var driveInfo in DriveInfo.GetDrives())
        {
            if (driveInfo.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Unknown))
                continue;

            var letter = char.ToUpperInvariant(driveInfo.Name[0]);
            var volumePath = $@"\\.\{letter}:";

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
                continue;
            }

            if (!VolumeIsOnDisk(handle, diskIndex, IOCTL_VOLUME_GET_EXTENTS))
            {
                handle.Dispose();
                continue;
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

            if (dismounted)
            {
                _logger.LogInformation("Dismounted volume {Vol} on disk {Index}", volumePath, diskIndex);
                held.Add(handle);   // keep open - releasing re-allows mounting
            }
            else
            {
                _logger.LogWarning("Failed to dismount volume {Vol} (Win32 error {Err})",
                    volumePath, Marshal.GetLastWin32Error());
                handle.Dispose();
            }
        }

        return held;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="volHandle"/> refers to a
    /// volume that has at least one extent on the physical disk identified by
    /// <paramref name="diskIndex"/>.
    ///
    /// Uses <c>IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS</c> (0x00560000) to query the
    /// kernel for the disk-extent list.
    ///
    /// Struct layout (natural C alignment, no pragma pack override):
    /// <code>
    ///   VOLUME_DISK_EXTENTS:
    ///     offset  0  DWORD  NumberOfDiskExtents  (4 bytes)
    ///     offset  4  BYTE   _pad[4]              (4 bytes - aligns DISK_EXTENT to 8)
    ///     offset  8  DISK_EXTENT Extents[N]:     (24 bytes each)
    ///                  +0  DWORD  DiskNumber     (4 bytes)
    ///                  +4  BYTE   _pad[4]        (4 bytes - aligns LARGE_INTEGER to 8)
    ///                  +8  INT64  StartingOffset (8 bytes)
    ///                 +16  INT64  ExtentLength   (8 bytes)
    /// </code>
    /// <para>
    /// The 4-byte gap between <c>NumberOfDiskExtents</c> and <c>Extents[0]</c> is the
    /// critical detail: reading from offset 4 yields padding bytes (0x00…), so the
    /// disk-number comparison silently fails for every drive except disk 0.  The first
    /// extent's <c>DiskNumber</c> is always at offset <b>8</b>.
    /// </para>
    /// </summary>
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

// ── Native helpers ────────────────────────────────────────────────────────────

internal static partial class NativeMethods
{
    /// <summary>
    /// Opens a file or device (including raw physical drives such as
    /// <c>\\.\PHYSICALDRIVE1</c> and volume devices such as <c>\\.\C:</c>).
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    /// <summary>
    /// Sends a control code directly to a device driver.
    /// Used here to issue volume FSCTL codes
    /// (<c>FSCTL_LOCK_VOLUME</c>, <c>FSCTL_DISMOUNT_VOLUME</c>),
    /// <c>IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS</c>, and
    /// <c>IOCTL_DISK_UPDATE_PROPERTIES</c>.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        nint lpInBuffer,
        int nInBufferSize,
        nint lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        nint lpOverlapped);

    /// <summary>Moves the file pointer of the specified file.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        nint lpNewFilePointer,   // may be null/Zero
        uint dwMoveMethod);      // 0 = FILE_BEGIN

    /// <summary>
    /// Reads data from a file using a synchronous (non-overlapped) handle.
    /// Pass <see cref="nint.Zero"/> for <c>lpOverlapped</c>.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead,
        nint lpOverlapped);

    /// <summary>
    /// Writes data to a file using a synchronous (non-overlapped) handle.
    /// Pass <see cref="nint.Zero"/> for <c>lpOverlapped</c>.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten,
        nint lpOverlapped);
}
