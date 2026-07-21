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
public sealed partial class UsbWriterService : IUsbWriterService
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

}
