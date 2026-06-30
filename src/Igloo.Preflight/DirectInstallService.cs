using System.Diagnostics;
using System.IO.Compression;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.Preflight;

/// <summary>
/// Implements direct-install (no USB) for dual-boot scenarios using the
/// Fedora netinstall ISO (Anaconda + network package fetch).
///
/// Flow:
///   1. Mount the ISO briefly to measure kernel + initrd sizes; unmount.
///   2. Shrink the Windows NTFS partition.
///   3. Create a FAT32 partition labeled <c>OEMDRV</c> in the freed space.
///   4. Mount the ISO; extract kernel, initrd, shim and GRUB onto OEMDRV;
///      write a custom <c>grub.cfg</c>; unmount.
///   5. Copy migration artefacts (kickstart, agent, manifest) to OEMDRV.
///   6. Register a one-time UEFI boot entry (BootNext) so the firmware boots
///      the GRUB installer on the next restart.
///
/// Using the netinstall ISO (not the live ISO) gives full kickstart support:
/// package selection, dual-boot bootloader config, and unattended installation.
/// The kickstart's <c>%post</c> enables os-prober so GRUB detects Windows and
/// adds it to the boot menu - giving end users the OS choice at every startup.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DirectInstallService : IDirectInstallService
{
    private const string StorageNs   = @"root\Microsoft\Windows\Storage";
    private const string EfiGlobGuid = "{8be4df61-93ca-11d2-aa0d-00e098032b8c}";
    private const long   MiB         = 1024L * 1024;
    // Overhead on top of the measured squashfs + kernel + initrd sizes.
    // Covers: EFI binaries (~3 MB), grub.cfg, kickstart, agent payload, FAT32
    // filesystem metadata, and 512 MiB headroom so FAT32 is never full-to-the-brim.
    private const long   PartitionOverheadBytes = 512L * MiB;

    // Boot chain:
    //
    //   UEFI firmware
    //     → \igloo-boot\shimx64.efi    (Microsoft-signed; firmware trusts it)
    //     → \igloo-boot\grubx64.efi    (Red Hat-signed; shim verifies it)
    //     → \EFI\fedora\grub.cfg       (our config; grubx64.efi compiled prefix = /EFI/fedora)
    //     → \igloo-boot\linux          (kernel extracted from ISO onto OEMDRV FAT32)
    //     → \igloo-boot\initrd         (initrd extracted from ISO onto OEMDRV FAT32)
    //     → Anaconda (netinstall initrd) reads ks.cfg from OEMDRV
    //     → installs packages from the internet, partitions disk, sets up GRUB
    //     → GRUB dual-boot menu: Fedora + Windows (os-prober detects Windows)
    //
    // Why netinstall ISO (not live ISO)?
    //   The Fedora KDE Desktop Live ISO does NOT support full kickstart:
    //   Anaconda shows "Configuration not supported" and falls back to interactive.
    //   The netinstall ISO runs Anaconda directly with complete kickstart support -
    //   package selection, storage layout, bootloader config, %post scripts.
    //   End users get a proper GRUB dual-boot menu on every startup.
    //
    // Why \igloo-boot\ for the EFI binaries?
    //   Windows write-protects \EFI\BOOT\ on FAT32 (UEFI fallback path), even for
    //   Administrator.  \igloo-boot\ is unprotected - we create it.
    //
    // Why \EFI\fedora\ for grub.cfg?
    //   grubx64.efi has its compiled prefix hard-coded to /EFI/fedora; it always
    //   looks for grub.cfg there.  Windows does NOT protect \EFI\fedora\.
    private const string BootDir    = "igloo-boot";   // shim, grub, kernel, initrd
    private const string ShimFile   = "shimx64.efi";  // UEFI entry point; Microsoft-signed
    private const string GrubFile   = "grubx64.efi";  // loaded by shim; Red Hat-signed
    private const string GrubCfgDir = @"EFI\fedora";  // grubx64.efi's compiled prefix
    private const string KernelFile = "linux";   // kernel on OEMDRV (under BootDir)
    private const string InitrdFile = "initrd";  // initrd on OEMDRV (under BootDir)

    // Stored by PrepareAsync, consumed by RegisterBootEntryAsync.
    private char?  _oemDrvLetter;
    private int?   _diskNumber;
    private uint?  _partitionNumber;

    // The selected distro's boot recipe (kernel/initrd paths, cmdline, volume
    // label, config-delivery). Set at the start of Prepare; read by the boot
    // helpers so the same pipeline drives Anaconda, debian-installer, subiquity, …
    private InstallerBootSpec _bootSpec = null!;

    private readonly IPartitionResizeService          _resizer;
    private readonly ILogger<DirectInstallService>    _logger;

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFirmwareEnvironmentVariableW(
        string lpName, string lpGuid, byte[] pBuffer, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetFirmwareEnvironmentVariableW(
        string lpName, string lpGuid, byte[]? pValue, uint nSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(
        string? lpSystemName, string lpName, out long lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle, bool DisableAllPrivileges,
        ref TokenPrivileges NewState, uint Length,
        IntPtr PreviousState, IntPtr ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // Pack=4 is critical: on x64 .NET, LayoutKind.Sequential without Pack inserts
    // 4 bytes of padding after uint PrivilegeCount to align the long Luid field to
    // 8 bytes, producing a 24-byte struct.  Win32 TOKEN_PRIVILEGES is 16 bytes with
    // no padding.  Without Pack=4 the LUID is at the wrong offset and
    // AdjustTokenPrivileges silently receives a garbage LUID, leaving
    // SeSystemEnvironmentPrivilege disabled → ERROR_PRIVILEGE_NOT_HELD (1314).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public long Luid;            // LUID = LowPart (4 b) + HighPart (4 b) at offset 4
        public uint Attributes;      // SE_PRIVILEGE_ENABLED = 2, at offset 12
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public DirectInstallService(
        IPartitionResizeService         resizer,
        ILogger<DirectInstallService>   logger)
    {
        _resizer = resizer;
        _logger  = logger;
    }

    // ── IDirectInstallService ─────────────────────────────────────────────────

    public Task PrepareAsync(
        int diskNumber, long linuxSizeBytes, string isoPath, string stagingDirectory,
        InstallerBootSpec bootSpec,
        string? stage2Url = null,
        IProgress<DirectInstallProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Prepare(diskNumber, linuxSizeBytes, isoPath, stagingDirectory, bootSpec, stage2Url, progress, ct), ct);

    public Task RegisterBootEntryAsync(
        IProgress<DirectInstallProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => RegisterBootEntry(progress, ct), ct);

    // ── Private - Prepare ─────────────────────────────────────────────────────

    private void Prepare(
        int diskNumber, long linuxSizeBytes, string isoPath, string stagingDirectory,
        InstallerBootSpec bootSpec, string? stage2Url,
        IProgress<DirectInstallProgress>? prog, CancellationToken ct)
    {
        _diskNumber = diskNumber;
        _bootSpec   = bootSpec;

        // ── Step 1: Measure ISO content to size the OEMDRV partition ─────────
        // Mount the ISO just long enough to stat kernel + initrd + install.img sizes.
        // install.img is Anaconda's stage2 squashfs (~870 MiB on Fedora 44).
        // We copy it to OEMDRV so that inst.stage2=hd:LABEL=OEMDRV: avoids
        // downloading 862 MB from a mirror during installation.
        Report(prog, DirectInstallPhase.ShrinkingPartition, message: "Measuring ISO content…");
        var (kernelBytes, initrdBytes, installImgBytes) = MeasureIsoContent(isoPath);
        long extractedBytes = kernelBytes + initrdBytes + installImgBytes;
        _logger.LogInformation(
            "ISO content: kernel={K} MiB  initrd={I} MiB  install.img={S} MiB",
            kernelBytes / MiB, initrdBytes / MiB, installImgBytes / MiB);
        ct.ThrowIfCancellationRequested();

        // ── Step 2: Shrink Windows partition (skip if OEMDRV already exists) ──
        // Distros whose installer loop-mounts the whole ISO (Debian iso-scan,
        // Ubuntu/Mint casper) need room for the ISO on the partition too.
        long fullIsoBytes = _bootSpec.CopyFullIsoToVolume ? new FileInfo(isoPath).Length : 0;
        long oemDrvBytes  = RoundUpMiB(extractedBytes + fullIsoBytes + PartitionOverheadBytes);
        char driveLetter;

        var existing = FindExistingOemDrv(diskNumber);
        if (existing is not null)
        {
            // A previous run already created the OEMDRV partition on this disk.
            // Skip shrink + partition creation and reuse it - just re-copy the files.
            driveLetter   = existing.Value.letter;
            _oemDrvLetter = driveLetter;
            _partitionNumber = existing.Value.partitionNumber;
            _logger.LogInformation(
                "Reusing existing OEMDRV partition {N} at {L}: - skipping shrink and partition creation",
                _partitionNumber, driveLetter);
            Report(prog, DirectInstallPhase.ConfiguringGrub, message: "Reusing existing installer partition…");
        }
        else
        {
            Report(prog, DirectInstallPhase.ShrinkingPartition, message: "Querying Windows partition…");
            _logger.LogInformation("Direct install: shrinking disk {Disk} by {GiB} GiB",
                diskNumber, linuxSizeBytes / MiB / 1024);

            long totalShrink = linuxSizeBytes + oemDrvBytes;

            var shrinkProg = new Progress<string>(msg =>
                Report(prog, DirectInstallPhase.ShrinkingPartition, message: msg));
            _resizer.ShrinkAsync(diskNumber, totalShrink, shrinkProg, ct).GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested();

            // ── Step 3: Create FAT32 OEMDRV partition ────────────────────────
            Report(prog, DirectInstallPhase.CreatingPartition, message: "Creating installer partition…");
            driveLetter   = CreateOemDrvPartition(diskNumber, oemDrvBytes, ct);
            _oemDrvLetter = driveLetter;
            ct.ThrowIfCancellationRequested();
        }

        // ── Step 4: Extract boot content from ISO onto OEMDRV ─────────────────
        // Extracts: igloo-boot/linux, igloo-boot/initrd,
        //           igloo-boot/shimx64.efi, igloo-boot/grubx64.efi
        //           images/install.img   (Anaconda stage2, ~870 MiB)
        // Writes:   EFI/fedora/grub.cfg  (points Anaconda at inst.ks=hd:LABEL=OEMDRV:/ks.cfg)
        Report(prog, DirectInstallPhase.ConfiguringGrub, message: "Extracting boot files from ISO…");
        ConfigureBootFiles(isoPath, driveLetter, stagingDirectory, initrdBytes, installImgBytes, prog, ct);
        ct.ThrowIfCancellationRequested();

        // ── Step 4b: Copy the whole ISO onto OEMDRV (iso-scan / casper need it) ──
        if (_bootSpec.CopyFullIsoToVolume && _bootSpec.IsoVolumeFileName is { } isoName)
        {
            var isoDst = Path.Combine($"{driveLetter}:\\", isoName);
            Report(prog, DirectInstallPhase.CopyingIso, message: "Copying installer ISO…");
            CopyWithProgress(isoPath, isoDst, new FileInfo(isoPath).Length, prog, ct);
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("Full ISO copied to {Dst}", isoDst);
        }

        // ── Step 5: Copy staging artefacts (ks.cfg, manifest, agent) ─────────
        Report(prog, DirectInstallPhase.CopyingFiles, message: "Copying migration files…");
        CopyStagingArtefacts(stagingDirectory, $"{driveLetter}:\\", ct);
        ct.ThrowIfCancellationRequested();

        Report(prog, DirectInstallPhase.Complete, message: "Installer partition ready.");
        _logger.LogInformation("Direct install partition prepared on {Letter}:", driveLetter);
    }

    /// <summary>
    /// Mounts the ISO, reads the kernel, initrd, and <c>images/install.img</c> sizes,
    /// then dismounts. Used to right-size the OEMDRV partition before it is created.
    /// </summary>
    private (long kernelBytes, long initrdBytes, long installImgBytes) MeasureIsoContent(string isoPath)
    {
        string? mountedLetter = null;
        try
        {
            mountedLetter = MountIso(isoPath);
            var isoRoot = $"{mountedLetter}:\\";
            var (kernelSrc, initrdSrc) = FindKernelFiles(isoRoot);

            // Sum the distro's declared extra ISO files (e.g. Anaconda's
            // images/install.img stage-2 squashfs). Empty for d-i / subiquity.
            long extraBytes = 0;
            foreach (var f in _bootSpec.ExtraIsoFiles)
            {
                var src = Path.Combine(isoRoot, f.IsoRelativePath.Replace('/', '\\'));
                if (File.Exists(src))
                {
                    var len = new FileInfo(src).Length;
                    extraBytes += len;
                    _logger.LogInformation("ISO extra {Path}: {MiB} MiB", f.IsoRelativePath, len / MiB);
                }
                else if (f.Required)
                {
                    _logger.LogWarning("Required ISO file {Path} not found on ISO", f.IsoRelativePath);
                }
            }

            return (new FileInfo(kernelSrc).Length, new FileInfo(initrdSrc).Length, extraBytes);
        }
        finally
        {
            if (mountedLetter is not null) DismountIso(isoPath);
        }
    }

    // ── Step 2 helpers ────────────────────────────────────────────────────────

    private char CreateOemDrvPartition(int diskNumber, long sizeBytes, CancellationToken ct)
    {
        long sizeMiB = RoundUpMiB(sizeBytes) / MiB;
        var letter = FindAvailableDriveLetter();

        _logger.LogInformation("Creating {MiB} MiB FAT32 OEMDRV partition (letter {L}:) on disk {D}",
            sizeMiB, letter, diskNumber);

        // rescan: tells diskpart to re-read the partition table so it sees the space
        // freed by the WMI resize that just completed.
        // align=1024: 1 MiB alignment - required for GPT/UEFI disks; align=1 (1 KB)
        // triggers VDS_E_OPERATION_NOT_SUPPORTED_ON_DISK (0x80042554).
        var script = $"""
            rescan
            select disk {diskNumber}
            create partition primary size={sizeMiB} align=1024
            format fs=fat32 label={_bootSpec.VolumeLabel} quick
            assign letter={letter}
            exit
            """;
        var output = RunDiskpart(script);
        _logger.LogInformation("diskpart output:\n{Output}", output);

        // Wait up to 15 s for the drive to appear.
        var root = $"{letter}:\\";
        for (var i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(root)) break;
            Thread.Sleep(500);
        }
        if (!Directory.Exists(root))
            throw new InvalidOperationException($"Drive {letter}: did not appear after partition creation.");

        // Record partition number for RegisterBootEntry.
        _partitionNumber = FindPartitionByLetter(diskNumber, letter);
        _logger.LogInformation("OEMDRV partition {N} ready at {L}:", _partitionNumber, letter);
        return letter;
    }

    /// <summary>
    /// Runs a diskpart script file and returns the combined stdout output.
    /// Stdout and stderr are drained asynchronously to prevent pipe-buffer deadlock.
    /// Throws <see cref="InvalidOperationException"/> with the captured output appended
    /// when diskpart exits with a non-zero code, so the error message is actionable.
    /// </summary>
    private static string RunDiskpart(string script)
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, script, Encoding.ASCII);
        try
        {
            var psi = new ProcessStartInfo("diskpart.exe", $"/s \"{tmp}\"")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start diskpart.");

            // Drain both streams on background threads to avoid pipe-buffer deadlock.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            var exited = p.WaitForExit(60_000); // 60 s - format can be slow
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            var combined = string.IsNullOrWhiteSpace(stderr)
                ? stdout
                : stdout + "\n[stderr]\n" + stderr;

            if (!exited)
            {
                try { p.Kill(); } catch { /* best effort */ }
                throw new InvalidOperationException(
                    $"diskpart timed out after 60 s.\n\nOutput:\n{combined}");
            }

            if (p.ExitCode != 0)
                throw new InvalidOperationException(
                    $"diskpart exited with code {p.ExitCode} (0x{(uint)p.ExitCode:X8})." +
                    $"\n\nOutput:\n{combined}");

            return combined;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private uint FindPartitionByLetter(int diskNumber, char letter)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(StorageNs,
                $"SELECT PartitionNumber, DriveLetter FROM MSFT_Partition WHERE DiskNumber = {diskNumber}");
            using var results  = searcher.Get();
            foreach (ManagementBaseObject p in results)
            {
                char dl = p["DriveLetter"] switch
                {
                    char c              => c,
                    ushort u when u > 0 => (char)u,
                    string s when s.Length > 0 => s[0],
                    _                   => '\0',
                };
                if (char.ToUpper(dl) == char.ToUpper(letter))
                    return Convert.ToUInt32(p["PartitionNumber"]);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "FindPartitionByLetter failed"); }
        return 0;
    }

    /// <summary>
    /// Looks for a mounted volume labelled <c>OEMDRV</c> that belongs to
    /// <paramref name="diskNumber"/>. Returns the drive letter and WMI partition
    /// number if found, <see langword="null"/> otherwise.
    /// </summary>
    private (char letter, uint partitionNumber)? FindExistingOemDrv(int diskNumber)
    {
        try
        {
            foreach (var di in DriveInfo.GetDrives())
            {
                if (!di.IsReady) continue;
                try
                {
                    if (!string.Equals(di.VolumeLabel, _bootSpec.VolumeLabel, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                catch { continue; }

                var letter = char.ToUpper(di.Name[0]);
                var pn     = FindPartitionByLetter(diskNumber, letter);
                if (pn == 0) continue; // not on this disk

                _logger.LogInformation(
                    "Found existing OEMDRV volume at {L}: (partition {N}, disk {D})",
                    letter, pn, diskNumber);
                return (letter, pn);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FindExistingOemDrv scan failed (non-fatal)");
        }
        return null;
    }

    private static char FindAvailableDriveLetter()
    {
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpper(d.Name[0]))
            .ToHashSet();
        foreach (var c in "IJKLMNOPQRSTUVWXYZ")
            if (!used.Contains(c)) return c;
        throw new InvalidOperationException("No available drive letter for the installer partition.");
    }

    // ── Step 3 - copy ISO with progress ──────────────────────────────────────

    private static void CopyWithProgress(
        string src, string dst, long totalBytes,
        IProgress<DirectInstallProgress>? prog, CancellationToken ct)
    {
        const int BufSize = 4 * 1024 * 1024; // 4 MiB
        using var fsIn  = new FileStream(src, FileMode.Open,  FileAccess.Read,  FileShare.Read,  BufSize);
        using var fsOut = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, BufSize);

        var buf     = new byte[BufSize];
        long copied = 0;
        int  read;
        while ((read = fsIn.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            fsOut.Write(buf, 0, read);
            copied += read;
            prog?.Report(new DirectInstallProgress(
                DirectInstallPhase.CopyingIso, copied, totalBytes));
        }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destPath"/> with progress.
    /// Used for installer kernel/initrd that don't live on the ISO (Debian hd-media,
    /// which runs iso-scan). A short-lived HttpClient is fine for these one-off fetches.
    /// </summary>
    private static void DownloadTo(Uri url, string destPath,
        IProgress<DirectInstallProgress>? prog, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                             .GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 0;

        using var src = resp.Content.ReadAsStream();
        using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var  buf  = new byte[1 << 20];
        long done = 0;
        int  read;
        while ((read = src.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            dst.Write(buf, 0, read);
            done += read;
            prog?.Report(new DirectInstallProgress(DirectInstallPhase.CopyingIso, done, total));
        }
    }

    // ── Step 4 - copy artefacts ───────────────────────────────────────────────

    private void CopyStagingArtefacts(string stagingDir, string oemDrvRoot, CancellationToken ct)
    {
        // Copy every top-level staging file to the volume root: the rendered
        // installer config (ks.cfg / preseed.cfg / user-data + meta-data — name
        // varies per distro) and migration-manifest.json. Ubuntu's subiquity reads
        // user-data/meta-data straight from this (CIDATA-labelled) volume, so a
        // distro-agnostic copy is required, not a hardcoded "ks.cfg".
        foreach (var f in Directory.EnumerateFiles(stagingDir))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(f, Path.Combine(oemDrvRoot, Path.GetFileName(f)), overwrite: true);
        }
        // Copy igloo-agent directory
        var agentSrc = Path.Combine(stagingDir, "igloo-agent");
        var agentDst = Path.Combine(oemDrvRoot, "igloo-agent");
        if (Directory.Exists(agentSrc))
        {
            Directory.CreateDirectory(agentDst);
            foreach (var f in Directory.EnumerateFiles(agentSrc))
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(f, Path.Combine(agentDst, Path.GetFileName(f)), overwrite: true);
            }
            _logger.LogInformation("Agent payload copied to {Dst}", agentDst);
        }
        // Note: user files (staging/files/) are intentionally NOT copied here.
        // The kickstart %post will mount the Windows NTFS partition and copy directly.
        _logger.LogInformation("Staging artefacts copied to {Root}", oemDrvRoot);
    }

    // ── Step 5 - extract kernel + initrd from ISO ────────────────────────────

    /// <summary>
    /// Mounts the netinstall ISO and extracts the boot files onto OEMDRV:
    /// <list type="bullet">
    ///   <item>kernel → <c>\igloo-boot\linux</c></item>
    ///   <item>initrd → <c>\igloo-boot\initrd</c> (Anaconda installer, ~200–400 MB)</item>
    ///   <item><c>shimx64.efi</c> + <c>grubx64.efi</c> → <c>\igloo-boot\</c></item>
    ///   <item><c>images/install.img</c> → OEMDRV <c>\images\install.img</c> (~870 MiB)
    ///         - the Anaconda stage2 squashfs, copied locally so the installer
    ///         does not need to download 862 MB from the network during installation.</item>
    /// </list>
    /// Then writes <c>\EFI\fedora\grub.cfg</c> and <c>\EFI\BOOT\grub.cfg</c> that
    /// boot Anaconda using <c>inst.stage2=hd:LABEL=OEMDRV:</c> (local copy) and
    /// <c>inst.ks=hd:LABEL=OEMDRV:/ks.cfg</c> for unattended dual-boot install.
    /// </summary>
    private void ConfigureBootFiles(
        string isoPath, char oemDrvLetter, string stagingDirectory, long initrdBytes, long installImgBytes,
        IProgress<DirectInstallProgress>? prog, CancellationToken ct)
    {
        var oemDrvRoot = $"{oemDrvLetter}:\\";
        var bootDst    = Path.Combine(oemDrvRoot, BootDir);
        Directory.CreateDirectory(bootDst);

        string? mountedLetter = null;
        try
        {
            mountedLetter = MountIso(isoPath);
            ct.ThrowIfCancellationRequested();

            var isoRoot = $"{mountedLetter}:\\";

            // ── 1. Extract kernel + initrd ─────────────────────────────────────
            var kernelDst = Path.Combine(bootDst, KernelFile);
            var initrdDst = Path.Combine(bootDst, InitrdFile);
            if (_bootSpec.KernelUrl is { } kernelUrl && _bootSpec.InitrdUrl is { } initrdUrl)
            {
                // Download the installer kernel+initrd (e.g. Debian hd-media, which
                // runs iso-scan) rather than extracting the ISO's cdrom-detect initrd.
                _logger.LogInformation("Downloading installer kernel from {Url}", kernelUrl);
                DownloadTo(kernelUrl, kernelDst, prog, ct);
                _logger.LogInformation("Downloading installer initrd from {Url}", initrdUrl);
                DownloadTo(initrdUrl, initrdDst, prog, ct);
            }
            else
            {
                var (kernelSrc, initrdSrc) = FindKernelFiles(isoRoot);
                _logger.LogInformation("ISO kernel: {K}  initrd: {I}", kernelSrc, initrdSrc);
                CopyFileRobust(kernelSrc, kernelDst);
                ct.ThrowIfCancellationRequested();
                CopyWithProgress(initrdSrc, initrdDst, initrdBytes, prog, ct);
            }
            ct.ThrowIfCancellationRequested();

            // Inject the rendered installer config into the initrd (preseed
            // delivery) when the distro uses that method - the standard
            // fully-unattended path for debian-installer / Ubiquity.
            if (_bootSpec.ConfigDelivery == ConfigDelivery.InjectIntoInitrd
                && _bootSpec.InitrdConfigPath is { } injPath)
            {
                var cfgSrc = Path.Combine(stagingDirectory, injPath);
                if (File.Exists(cfgSrc))
                {
                    AppendFileToInitrd(Path.Combine(bootDst, InitrdFile), injPath, File.ReadAllBytes(cfgSrc));
                    _logger.LogInformation("Injected {Cfg} into initrd for unattended install", injPath);
                }
                else
                {
                    _logger.LogWarning("Config {Cfg} not found in staging for initrd injection", cfgSrc);
                }
            }

            // ── 2. Copy shim + GRUB EFI binaries ──────────────────────────────
            var (shimSrc, grubSrc) = FindEfiFiles(isoRoot);
            _logger.LogInformation("ISO shim: {S}", shimSrc);
            _logger.LogInformation("ISO grub: {G}", grubSrc);
            CopyFileRobust(shimSrc, Path.Combine(bootDst, ShimFile));
            CopyFileRobust(grubSrc, Path.Combine(bootDst, GrubFile));
            _logger.LogInformation("shim + grubx64.efi copied to {Dir}", bootDst);

            // ── 3. Copy images/install.img (Anaconda stage2 squashfs) ─────────
            // The Fedora netinstall ISO contains images/install.img - the squashfs
            // that holds Anaconda and all installer tools (~870 MiB on Fedora 44).
            // Without inst.stage2= the initrd hangs; with inst.stage2=<network-url>
            // Anaconda downloads 862 MiB at boot time which proved unreliable
            // (connection drops, VM power-off at 99%). Copying to OEMDRV and using
            // inst.stage2=hd:LABEL=OEMDRV: is always fast and works offline.
            foreach (var f in _bootSpec.ExtraIsoFiles)
            {
                var src = Path.Combine(isoRoot, f.IsoRelativePath.Replace('/', '\\'));
                if (!File.Exists(src))
                {
                    if (f.Required)
                        _logger.LogWarning("Required ISO file {Path} not found - install may fail", f.IsoRelativePath);
                    continue;
                }
                var dst = Path.Combine(oemDrvRoot, f.OemDrvRelativePath.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                var len = new FileInfo(src).Length;
                Report(prog, DirectInstallPhase.ConfiguringGrub,
                    message: $"Copying installer payload ({len / MiB} MiB)...");
                CopyWithProgress(src, dst, len, prog, ct);
                ct.ThrowIfCancellationRequested();
                _logger.LogInformation("Copied {Src} to OEMDRV {Dst}", f.IsoRelativePath, f.OemDrvRelativePath);
            }
        }
        finally
        {
            if (mountedLetter is not null)
                DismountIso(isoPath);
        }

        // ── 4. Write grub.cfg ──────────────────────────────────────────────────
        // grubx64.efi has a compiled-in prefix that determines where it looks for
        // grub.cfg. Fedora ISOs ship two variants:
        //   • EFI/fedora/grubx64.efi  → prefix /EFI/fedora  → looks for /EFI/fedora/grub.cfg
        //   • EFI/BOOT/grubx64.efi   → prefix /EFI/BOOT    → looks for /EFI/BOOT/grub.cfg
        // Because we don't know at runtime which binary we got (and they can differ
        // even across minor Fedora releases), we write grub.cfg to BOTH locations.
        // grubx64.efi's compiled-in prefix differs per distro (/EFI/fedora,
        // /EFI/debian, /EFI/ubuntu, or /EFI/BOOT). We don't know which binary the
        // ISO shipped, so write grub.cfg to all of them.
        var grubCfgContent = BuildGrubConfig();
        foreach (var cfgDir in new[] { @"EFI\BOOT", @"EFI\fedora", @"EFI\debian", @"EFI\ubuntu" })
        {
            var dir  = Path.Combine(oemDrvRoot, cfgDir);
            var path = Path.Combine(dir, "grub.cfg");
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, grubCfgContent, Encoding.ASCII);
            _logger.LogInformation("grub.cfg written to {Path}", path);
        }
    }

    /// <summary>
    /// Locates the kernel and initrd files on a mounted ISO and returns their
    /// full paths as <c>(kernelPath, initrdPath)</c>.
    ///
    /// Handles naming variations across Fedora releases:
    /// <list type="bullet">
    ///   <item>Fedora 44+ (new lorax): <c>boot/x86_64/loader/linux</c> + <c>boot/x86_64/loader/initrd</c></item>
    ///   <item>Fedora 40–43: <c>images/pxeboot/vmlinuz</c> + <c>images/pxeboot/initrd.img</c></item>
    ///   <item>Fedora ≤39: <c>isolinux/vmlinuz</c> + <c>isolinux/initrd.img</c></item>
    /// </list>
    /// Falls back to a full recursive scan as a safety net.
    /// </summary>
    private string FindKernelFiles(string isoRoot, out string initrdPath)
    {
        // Try the distro's declared kernel/initrd locations (from the boot spec),
        // pairing any existing kernel with any existing initrd.
        foreach (var k in _bootSpec.KernelIsoPaths)
        {
            var kFull = Path.Combine(isoRoot, k.Replace('/', '\\'));
            if (!File.Exists(kFull)) continue;
            foreach (var i in _bootSpec.InitrdIsoPaths)
            {
                var iFull = Path.Combine(isoRoot, i.Replace('/', '\\'));
                if (File.Exists(iFull)) { initrdPath = iFull; return kFull; }
            }
        }

        // Full scan: find any file named "linux" or "vmlinuz" whose sibling is
        // "initrd" or "initrd.img".
        _logger.LogDebug("Kernel not found in standard locations; scanning ISO recursively…");
        var kernelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "vmlinuz", "linux" };
        var initrdNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "initrd.img", "initrd" };

        foreach (var f in Directory.EnumerateFiles(isoRoot, "*", SearchOption.AllDirectories))
        {
            if (!kernelNames.Contains(Path.GetFileName(f))) continue;
            var dir      = Path.GetDirectoryName(f)!;
            var initrdHit = Directory.EnumerateFiles(dir)
                .FirstOrDefault(x => initrdNames.Contains(Path.GetFileName(x)));
            if (initrdHit is null) continue;
            _logger.LogInformation("Found kernel via scan: {K}", f);
            initrdPath = initrdHit;
            return f;
        }

        // Last resort: dump the full file listing so the next iteration can add
        // the correct fast-path candidate.
        var allFiles = Directory
            .EnumerateFiles(isoRoot, "*", SearchOption.AllDirectories)
            .Select(f => f[isoRoot.Length..])
            .OrderBy(f => f);
        throw new InvalidOperationException(
            $"Cannot locate kernel + initrd on the mounted ISO.\n" +
            $"ISO file listing:\n  {string.Join("\n  ", allFiles)}");
    }

    // Thin wrapper so callers use a clean tuple syntax.
    private (string kernel, string initrd) FindKernelFiles(string isoRoot)
    {
        var k = FindKernelFiles(isoRoot, out var i);
        return (k, i);
    }

    /// <summary>
    /// Locates the shim and GRUB EFI binaries on a mounted Fedora ISO.
    ///
    /// Fedora netinstall ISOs ship the shim as <c>EFI/BOOT/BOOTX64.EFI</c>
    /// (the UEFI fallback name) rather than as <c>EFI/fedora/shimx64.efi</c>.
    /// Full desktop/live ISOs and installed systems use the named path.
    /// This method checks the most common locations in priority order.
    /// </summary>
    private (string shimPath, string grubPath) FindEfiFiles(string isoRoot)
    {
        // ── Shim candidates (in priority order) ─────────────────────────────
        string[] shimCandidates =
        [
            Path.Combine(isoRoot, "EFI", "fedora", "shimx64.efi"),   // Fedora live/full ISO
            Path.Combine(isoRoot, "EFI", "debian", "shimx64.efi"),   // Debian
            Path.Combine(isoRoot, "EFI", "ubuntu", "shimx64.efi"),   // Ubuntu / Mint
            Path.Combine(isoRoot, "EFI", "BOOT",   "shimx64.efi"),   // some ISOs
            Path.Combine(isoRoot, "EFI", "BOOT",   "BOOTX64.EFI"),   // UEFI fallback name (most netinst/live)
        ];

        var shimPath = shimCandidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "shimx64.efi not found on ISO. Checked: " +
                string.Join(", ", shimCandidates.Select(p => p[(isoRoot.Length)..])));

        // ── GRUB candidates ──────────────────────────────────────────────────
        string[] grubCandidates =
        [
            Path.Combine(isoRoot, "EFI", "fedora", "grubx64.efi"),   // Fedora
            Path.Combine(isoRoot, "EFI", "debian", "grubx64.efi"),   // Debian
            Path.Combine(isoRoot, "EFI", "ubuntu", "grubx64.efi"),   // Ubuntu / Mint
            Path.Combine(isoRoot, "EFI", "BOOT",   "grubx64.efi"),   // fallback
        ];

        var grubPath = grubCandidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "grubx64.efi not found on ISO. Checked: " +
                string.Join(", ", grubCandidates.Select(p => p[(isoRoot.Length)..])));

        return (shimPath, grubPath);
    }

    /// <summary>
    /// Copies a file using <see cref="FileStream"/> rather than <see cref="File.Copy"/>.
    /// <see cref="File.Copy"/> on Windows inherits source file attributes (e.g. read-only
    /// from ISO mounts) onto the destination, which causes failures on subsequent
    /// overwrites.  Creating the destination with <c>FileMode.Create</c> always starts
    /// with default attributes.
    /// </summary>
    private static void CopyFileRobust(string src, string dst)
    {
        const int bufSize = 65536;
        using var fsIn  = new FileStream(src, FileMode.Open,   FileAccess.Read,  FileShare.ReadWrite, bufSize);
        using var fsOut = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None,      bufSize);
        fsIn.CopyTo(fsOut);
    }

    // ── initrd config injection ──────────────────────────────────────────────

    /// <summary>
    /// Appends <paramref name="fileData"/> as a single-file gzipped cpio (newc)
    /// member to <paramref name="initrdPath"/>. The Linux initramfs loader
    /// concatenates gzip streams, so the file appears at
    /// /<paramref name="nameInInitrd"/> in the initramfs root. This is the
    /// standard fully-unattended preseed delivery for debian-installer / Ubiquity
    /// (referenced by the kernel cmdline as <c>preseed/file=/preseed.cfg</c>).
    /// </summary>
    private static void AppendFileToInitrd(string initrdPath, string nameInInitrd, byte[] fileData)
    {
        var cpio = BuildNewcCpio(nameInInitrd.TrimStart('/'), fileData);
        using var gzBuf = new MemoryStream();
        using (var gz = new GZipStream(gzBuf, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(cpio, 0, cpio.Length);
        var gzBytes = gzBuf.ToArray();
        using var fs = new FileStream(initrdPath, FileMode.Append, FileAccess.Write);
        fs.Write(gzBytes, 0, gzBytes.Length);
    }

    /// <summary>Builds a cpio "newc" archive containing one regular file + the trailer.</summary>
    private static byte[] BuildNewcCpio(string name, byte[] data)
    {
        using var ms = new MemoryStream();
        WriteCpioEntry(ms, name,         data,                mode: 0x81A4, nlink: 1); // 0100644
        WriteCpioEntry(ms, "TRAILER!!!", Array.Empty<byte>(), mode: 0,      nlink: 1);
        return ms.ToArray();
    }

    private static void WriteCpioEntry(MemoryStream ms, string name, byte[] data, uint mode, uint nlink)
    {
        static string H(uint v) => v.ToString("X8");
        var  nameBytes = Encoding.ASCII.GetBytes(name);
        uint namesize  = (uint)nameBytes.Length + 1; // include trailing NUL

        // newc header: 6-byte magic + 13 × 8-hex fields = 110 bytes.
        var header =
            "070701"               // magic
            + H(0)                 // ino
            + H(mode)              // mode
            + H(0) + H(0)          // uid, gid
            + H(nlink)             // nlink
            + H(0)                 // mtime
            + H((uint)data.Length) // filesize
            + H(0) + H(0)          // devmajor, devminor
            + H(0) + H(0)          // rdevmajor, rdevminor
            + H(namesize)          // namesize
            + H(0);                // check

        ms.Write(Encoding.ASCII.GetBytes(header));
        ms.Write(nameBytes);
        ms.WriteByte(0);
        Pad4(ms);          // pad header+name to a 4-byte boundary
        ms.Write(data);
        Pad4(ms);          // pad file data to a 4-byte boundary
    }

    private static void Pad4(MemoryStream ms)
    {
        int pad = (int)((4 - (ms.Length & 3)) & 3);
        for (int i = 0; i < pad; i++) ms.WriteByte(0);
    }

    /// <summary>
    /// Builds the GRUB2 config written to <c>\EFI\fedora\grub.cfg</c> (and
    /// <c>\EFI\BOOT\grub.cfg</c>) on OEMDRV.
    ///
    /// Boots the netinstall Anaconda kernel directly from FAT32 - no loopback,
    /// iso9660, or live-boot dracut parameters.
    ///
    /// <c>inst.stage2=hd:LABEL=OEMDRV:</c> tells Anaconda to load
    /// <c>images/install.img</c> from the OEMDRV partition (copied there from the
    /// ISO during step 3 of <see cref="ConfigureBootFiles"/>).  This replaces the
    /// previous <c>inst.stage2=https://…</c> approach which required an unreliable
    /// 862 MiB network download at boot time.
    ///
    /// Anaconda reads the kickstart from <c>hd:LABEL=OEMDRV:/ks.cfg</c> and
    /// installs packages from the internet, giving full kickstart support including
    /// the dual-boot bootloader setup.
    /// </summary>
    private string BuildGrubConfig()
    {
        var label   = _bootSpec.VolumeLabel;
        var cmdline = _bootSpec.KernelCmdline.Replace("{LABEL}", label);
        return $$"""
            insmod part_gpt
            insmod fat
            insmod linux

            search --no-floppy --set=root --label {{label}}

            set default=0
            set timeout=5

            menuentry "{{_bootSpec.MenuTitle}}" {
                linux  ($root)/{{BootDir}}/{{KernelFile}} {{cmdline}}
                initrd ($root)/{{BootDir}}/{{InitrdFile}}
            }
            """;
    }

    // ISO mounting via PowerShell
    private string MountIso(string isoPath)
    {
        // Dismount first in case a previous run left the ISO mounted.
        // Errors are silently ignored - the image may not be mounted at all.
        DismountIso(isoPath);
        Thread.Sleep(500);

        RunPowerShell($"Mount-DiskImage -ImagePath \"{isoPath}\" -Access ReadOnly");

        // Mount-DiskImage is asynchronous on real hardware; the volume letter is
        // assigned by Windows after the virtual disk is attached.
        Thread.Sleep(2_000);

        // First attempt: poll up to 30 seconds.
        var letter = PollForDriveLetter(isoPath, retries: 60);
        if (letter is not null) return letter;

        _logger.LogWarning("ISO did not appear after 30 s - dismounting and retrying once");

        // Recovery: dismount, remount, and give it 10 more seconds.
        DismountIso(isoPath);
        Thread.Sleep(1_000);
        RunPowerShell($"Mount-DiskImage -ImagePath \"{isoPath}\" -Access ReadOnly");
        Thread.Sleep(2_000);

        letter = PollForDriveLetter(isoPath, retries: 20);
        if (letter is not null) return letter;

        throw new InvalidOperationException("ISO did not mount within 40 seconds.");
    }

    private string? PollForDriveLetter(string isoPath, int retries)
    {
        for (var i = 0; i < retries; i++)
        {
            var letter = RunPowerShell(
                $"(Get-DiskImage -ImagePath \"{isoPath}\" | Get-Volume).DriveLetter");
            letter = letter.Trim();
            if (!string.IsNullOrEmpty(letter))
            {
                _logger.LogDebug("ISO mounted at {L}:", letter);
                return letter;
            }
            Thread.Sleep(500);
        }
        return null;
    }

    private void DismountIso(string isoPath)
    {
        try { RunPowerShell($"Dismount-DiskImage -ImagePath \"{isoPath}\""); }
        catch (Exception ex) { _logger.LogWarning(ex, "Dismount-DiskImage failed (non-fatal)"); }
    }

    private static string RunPowerShell(string command)
    {
        // Use -EncodedCommand (Base64 UTF-16LE) so that paths containing
        // special characters such as apostrophes (e.g. "D'huyvetter") never
        // break PowerShell's argument / string parsing.
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NonInteractive -NoProfile -EncodedCommand {encoded}")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start powershell.exe.");
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(30_000);
        return output;
    }

    // ── RegisterBootEntry ─────────────────────────────────────────────────────

    private void RegisterBootEntry(
        IProgress<DirectInstallProgress>? prog, CancellationToken ct)
    {
        if (_oemDrvLetter is null || _diskNumber is null || _partitionNumber is null)
            throw new InvalidOperationException(
                "PrepareAsync must complete successfully before RegisterBootEntryAsync.");

        Report(prog, DirectInstallPhase.RegisteringBootEntry, message: "Registering UEFI boot entry…");

        EnableFirmwarePrivilege();
        ct.ThrowIfCancellationRequested();

        // Get partition geometry for the EFI_LOAD_OPTION HARDDRIVE device path.
        var (lbaStart, lbaSize, partGuid) = GetPartitionGeometry(
            _diskNumber.Value, _partitionNumber.Value);

        ct.ThrowIfCancellationRequested();

        // Pick an unused Boot#### index.
        ushort idx = FindFreeBootIndex();

        // Build and write Boot####.
        // efiPath → \igloo-boot\shimx64.efi  (Microsoft-signed shim)
        // Shim loads grubx64.efi from the same directory, which reads
        // \EFI\fedora\grub.cfg - our direct-FAT32 config written during PrepareAsync.
        var loadOption = BuildEfiLoadOption(
            _partitionNumber.Value,
            lbaStart, lbaSize, partGuid,
            $@"\{BootDir}\{ShimFile}",
            "Igloo Fedora KDE Installer");

        var bootVar = $"Boot{idx:X4}";
        _logger.LogInformation("Writing UEFI {Var} ({Bytes} bytes)", bootVar, loadOption.Length);

        if (!SetFirmwareEnvironmentVariableW(bootVar, EfiGlobGuid, loadOption, (uint)loadOption.Length))
            throw new InvalidOperationException(
                $"SetFirmwareEnvironmentVariable({bootVar}) failed: Win32 error {Marshal.GetLastWin32Error()}. " +
                "Run Igloo as Administrator to allow UEFI NVRAM writes.");

        // Write BootNext so the firmware uses our entry exactly once.
        var bootNext = BitConverter.GetBytes(idx);
        if (!SetFirmwareEnvironmentVariableW("BootNext", EfiGlobGuid, bootNext, 2))
            throw new InvalidOperationException(
                $"SetFirmwareEnvironmentVariable(BootNext) failed: Win32 error {Marshal.GetLastWin32Error()}.");

        _logger.LogInformation("BootNext set to {Idx:X4}", idx);

        // Belt-and-suspenders: some firmware ignores BootNext but does respect
        // BootOrder.  Prepend our entry to BootOrder so the installer is first
        // in the list.  After the install completes (or if the user aborts), the
        // entry is removed from BootOrder by the %post cleanup or on next Windows
        // boot when the Boot#### variable no longer exists.
        PrependBootOrder(idx);

        _logger.LogInformation("BootNext + BootOrder updated - reboot to install");
        Report(prog, DirectInstallPhase.Complete, message: "UEFI boot entry registered. Ready to reboot.");
    }

    private void PrependBootOrder(ushort idx)
    {
        try
        {
            // Read current BootOrder (array of uint16).
            var buf      = new byte[256];
            var size     = GetFirmwareEnvironmentVariableW("BootOrder", EfiGlobGuid, buf, (uint)buf.Length);
            var existing = new List<ushort>();
            for (var i = 0; i + 1 < size; i += 2)
                existing.Add(BitConverter.ToUInt16(buf, i));

            // Remove our index if already present, then prepend it.
            existing.RemoveAll(e => e == idx);
            existing.Insert(0, idx);

            var newOrder = new byte[existing.Count * 2];
            for (var i = 0; i < existing.Count; i++)
                BitConverter.GetBytes(existing[i]).CopyTo(newOrder, i * 2);

            if (!SetFirmwareEnvironmentVariableW("BootOrder", EfiGlobGuid, newOrder, (uint)newOrder.Length))
                _logger.LogWarning("SetFirmwareEnvironmentVariable(BootOrder) failed: {Err} (non-fatal)",
                    Marshal.GetLastWin32Error());
            else
                _logger.LogInformation("BootOrder prepended with {Idx:X4}", idx);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PrependBootOrder failed (non-fatal)");
        }
    }

    private void EnableFirmwarePrivilege()
    {
        // TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0028, out var token))
        {
            _logger.LogWarning("OpenProcessToken failed: {Err}", Marshal.GetLastWin32Error());
            return; // will fail at SetFirmwareEnvironmentVariable with a clear Win32 error
        }

        try
        {
            if (!LookupPrivilegeValueW(null, "SeSystemEnvironmentPrivilege", out var luid))
            {
                _logger.LogWarning("LookupPrivilegeValue(SeSystemEnvironmentPrivilege) failed: {Err}",
                    Marshal.GetLastWin32Error());
                return;
            }

            var tp = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid           = luid,
                Attributes     = 2,  // SE_PRIVILEGE_ENABLED
            };

            // AdjustTokenPrivileges returns TRUE even when not all privileges were
            // assigned - check GetLastError for ERROR_NOT_ALL_ASSIGNED (1300).
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            var adjustErr = Marshal.GetLastWin32Error();
            if (adjustErr != 0)
                _logger.LogWarning(
                    "AdjustTokenPrivileges(SeSystemEnvironmentPrivilege) returned error {Err} - " +
                    "UEFI NVRAM write will likely fail. Is the process running as Administrator?",
                    adjustErr);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private (ulong lbaStart, ulong lbaSize, Guid partGuid)
        GetPartitionGeometry(int diskNumber, uint partitionNumber)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(StorageNs,
                $"SELECT Offset, Size, Guid FROM MSFT_Partition " +
                $"WHERE DiskNumber = {diskNumber} AND PartitionNumber = {partitionNumber}");
            using var results = searcher.Get();
            var mo = results.Cast<ManagementBaseObject>().First();

            var offset = Convert.ToUInt64(mo["Offset"]);
            var size   = Convert.ToUInt64(mo["Size"]);
            var guid   = Guid.Parse((string)mo["Guid"]);
            return (offset / 512, size / 512, guid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetPartitionGeometry failed - using zeros (boot may not work)");
            return (0, 0, Guid.Empty);
        }
    }

    private ushort FindFreeBootIndex()
    {
        for (ushort i = 0x0080; i < 0x00FF; i++)
        {
            var buf  = new byte[4096];
            var read = GetFirmwareEnvironmentVariableW($"Boot{i:X4}", EfiGlobGuid, buf, (uint)buf.Length);
            if (read == 0) return i; // variable doesn't exist → free slot
        }
        return 0x0090; // fallback - overwrite 0x0090
    }

    private static byte[] BuildEfiLoadOption(
        uint partitionNumber, ulong lbaStart, ulong lbaSize,
        Guid partGuid, string efiPath, string description,
        string? cmdLine = null)
    {
        using var ms = new MemoryStream();

        // Attributes: LOAD_OPTION_ACTIVE (0x00000001)
        ms.Write(BitConverter.GetBytes((uint)1));

        // FilePathListLength placeholder (2 bytes) - patched below.
        int fplLenOffset = (int)ms.Position;
        ms.Write(BitConverter.GetBytes((ushort)0));

        // Description (UCS-2 null-terminated).
        ms.Write(Encoding.Unicode.GetBytes(description + '\0'));

        int devicePathStart = (int)ms.Position;

        // HARDDRIVE media device path node (42 bytes).
        ms.WriteByte(0x04);           // Type
        ms.WriteByte(0x01);           // SubType
        ms.Write(BitConverter.GetBytes((ushort)42));
        ms.Write(BitConverter.GetBytes(partitionNumber));
        ms.Write(BitConverter.GetBytes(lbaStart));
        ms.Write(BitConverter.GetBytes(lbaSize));
        ms.Write(partGuid.ToByteArray()); // 16 bytes, correct endianness for EFI
        ms.WriteByte(0x02);           // MBRType: GPT
        ms.WriteByte(0x02);           // SignatureType: GUID

        // FILE_PATH media device path node.
        var pathBytes = Encoding.Unicode.GetBytes(efiPath + '\0');
        ms.WriteByte(0x04);           // Type
        ms.WriteByte(0x04);           // SubType
        ms.Write(BitConverter.GetBytes((ushort)(4 + pathBytes.Length)));
        ms.Write(pathBytes);

        // End of Hardware Device Path (4 bytes).
        ms.WriteByte(0x7F);
        ms.WriteByte(0xFF);
        ms.Write(BitConverter.GetBytes((ushort)4));

        // FilePathListLength covers only the device path (up to here), NOT optional data.
        int devicePathEnd = (int)ms.Position;

        // OptionalData: kernel command line encoded as UTF-16LE (no NUL terminator).
        // The Linux EFI stub detects UTF-16LE by checking for wide-char encoding and
        // uses this as the kernel command line.  initrd= paths use UEFI backslash
        // convention; the remaining parameters use the standard Linux cmdline format.
        if (cmdLine is not null)
            ms.Write(Encoding.Unicode.GetBytes(cmdLine));

        var result       = ms.ToArray();
        ushort fplLength = (ushort)(devicePathEnd - devicePathStart);
        var fplBytes     = BitConverter.GetBytes(fplLength);
        result[fplLenOffset]     = fplBytes[0];
        result[fplLenOffset + 1] = fplBytes[1];
        return result;
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    private static long RoundUpMiB(long bytes) =>
        ((bytes + MiB - 1) / MiB) * MiB;

    private static void Report(
        IProgress<DirectInstallProgress>? prog,
        DirectInstallPhase phase,
        long written = 0, long total = 0, string? message = null)
        => prog?.Report(new DirectInstallProgress(phase, written, total, message));
}
