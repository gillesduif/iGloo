using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Preparation;
using Microsoft.Extensions.Logging;

namespace Igloo.UsbWriter;

public sealed partial class UsbWriterService
{
    private const long MiB = 1024L * 1024;
    private static readonly Guid EfiPartitionType =
        Guid.Parse("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    private static readonly Guid MicrosoftReservedPartitionType =
        Guid.Parse("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
    private static readonly System.Text.Json.JsonSerializerOptions SeedJsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    // This route writes the layout booted by the Fedora disposable VM: one GPT
    // EFI/FAT32 OEMDRV partition with signed shim, GRUB, kernel, initrd, stage2,
    // kickstart, migration data, and an exact seed identity. The raw hybrid ISO
    // route remains in WriteAsync for providers that use it.
    public async Task WriteBootableFatSeedAsync(UsbDriveInfo drive, string isoPath,
        string stagingDirectory, string expectedIsoSha256,
        UsbStagingIdentity stagingIdentity, InstallerBootSpec bootSpec,
        IProgress<UsbWriteProgress>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(stagingIdentity);
        RequireFedoraFatBootSpec(bootSpec, expectedIsoSha256);
        if (drive.LogicalSectorBytes != 512)
            throw new InvalidOperationException("The selected USB drive must report 512-byte logical sectors.");
        if (!IsCurrentProcessElevated())
            throw new UnauthorizedAccessException("Writing to a physical USB drive requires administrator rights.");
        RequireSourceOutsideSelectedDrive(drive, isoPath);
        RequireSourceOutsideSelectedDrive(drive, stagingDirectory);
        RequireNonSystemUsbDisk(drive);
        var stagingSize = InspectStagingDirectory(stagingDirectory);
        var stagedFiles = GetStagingFiles(stagingDirectory);
        var stagedDirectories = GetStagingDirectories(stagingDirectory);
        RequireSafeFatStagingPaths(stagedFiles, stagedDirectories);
        RequireFedoraStage(stagingDirectory, stagedFiles, stagedDirectories);
        using var pinnedManifest = await OpenVerifiedStagingFileAsync(
            Path.Join(stagingDirectory, "migration-manifest.json"),
            stagingIdentity.ManifestSha256, ct).ConfigureAwait(false);
        using var pinnedKickstart = await OpenVerifiedStagingFileAsync(
            Path.Join(stagingDirectory, "ks.cfg"),
            stagingIdentity.KickstartSha256, ct).ConfigureAwait(false);
        var pinnedProviderFiles = await OpenVerifiedProviderArtifactsAsync(
            stagingDirectory, stagedFiles, stagingIdentity.ProviderArtifacts, ct)
            .ConfigureAwait(false);
        await using var pinnedProviderFilesLease = pinnedProviderFiles.ConfigureAwait(false);
        MigrationManifest stagedManifest;
        try
        {
            stagedManifest = await JsonSerializer.DeserializeAsync<MigrationManifest>(
                pinnedManifest, SeedJsonOptions, ct).ConfigureAwait(false) ??
                throw new JsonException("The staged Fedora manifest is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The staged Fedora manifest is invalid.", error);
        }
        // OEMDRV is FAT and has no access control. The manifest is copied there
        // verbatim, so reject every selected PSK before touching the USB drive.
        if (stagedManifest.WifiNetworks?.Any(network =>
                !string.IsNullOrEmpty(network?.Psk)) == true)
            throw new NotSupportedException("Turn off saved Wi-Fi migration and restage before writing the USB drive. Use wired Ethernet or an open Wi-Fi network for this Fedora installation.");
        pinnedManifest.Position = 0;
        var selectedMedia = RequireSelectedStagingPaths(stagedManifest,
            stagedDirectories, stagedFiles, pinnedProviderFiles.RelativePaths);
        var pinnedSelectedMedia = await OpenSelectedMediaAsync(stagedFiles,
            selectedMedia, ct).ConfigureAwait(false);
        await using var pinnedSelectedMediaLease = pinnedSelectedMedia.ConfigureAwait(false);
        var heldPaths = pinnedProviderFiles.RelativePaths.Concat(
            pinnedSelectedMedia.RelativePaths).Append("migration-manifest.json")
            .Append("ks.cfg").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pinnedUserFiles = OpenReadLockedStagingFiles(stagingDirectory,
            stagedFiles, heldPaths);
        await using var pinnedUserFilesLease = pinnedUserFiles.ConfigureAwait(false);
        if (stagedFiles.Sum(file => file.SizeBytes) != stagingSize ||
            stagedFiles.Single(file => string.Equals(file.RelativeDestination,
                "migration-manifest.json", StringComparison.OrdinalIgnoreCase)).SizeBytes !=
                pinnedManifest.Length ||
            stagedFiles.Single(file => string.Equals(file.RelativeDestination,
                "ks.cfg", StringComparison.OrdinalIgnoreCase)).SizeBytes !=
                pinnedKickstart.Length)
            throw new IOException("The validated Fedora staging files changed before USB preparation.");
        await EnsureCurrentDriveAsync(drive, ct).ConfigureAwait(false);

        if ((File.GetAttributes(isoPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The verified installer image identity is unavailable.");
        using var isoStream = new FileStream(isoPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferSize, useAsync: true);
        await VerifyIsoAsync(isoStream, expectedIsoSha256, ct).ConfigureAwait(false);

        // Hold the verified ISO handle while Windows mounts and copies from this
        // path. FileShare.Read prevents replacement or modification during use.
        var isoRoot = await MountVerifiedIsoAsync(isoPath, ct).ConfigureAwait(false);
        try
        {
            var bootFiles = ResolveFedoraBootFiles(isoRoot, bootSpec);
            var pinnedBootFiles = OpenReadLockedBootFiles(bootFiles);
            await using var pinnedBootFilesLease = pinnedBootFiles.ConfigureAwait(false);
            var bootSize = bootFiles.Sum(file => file.SizeBytes);
            var bootDirectories = bootFiles.Select(file =>
                Path.GetDirectoryName(file.RelativeDestination)?.Replace('\\', '/'))
                .Where(path => path is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Worst-case FAT32 cluster slack and long directory entries are
            // counted before diskpart clean, not discovered during the copy.
            var stagedAllocation = EstimateFat32AllocatedBytes(stagedFiles,
                stagedDirectories.Length);
            var bootAllocation = checked(EstimateFat32AllocatedBytes(bootFiles,
                bootDirectories.Count + 1) + 3 * (64L * 1024 + 1024));
            var partSizeMb = PlanFatSeedPartitionSizeMb(drive, stagedAllocation,
                bootAllocation);
            var driveLetter = FindFreeDriveLetter();
            await RequireMountedIsoRootAsync(isoPath, isoRoot, ct).ConfigureAwait(false);
            if (!stagedFiles.Select(file => (file.RelativeDestination, file.SizeBytes))
                    .SequenceEqual(GetStagingFiles(stagingDirectory).Select(file =>
                        (file.RelativeDestination, file.SizeBytes))) ||
                !stagedDirectories.SequenceEqual(GetStagingDirectories(stagingDirectory),
                    StringComparer.OrdinalIgnoreCase))
                throw new IOException("The Fedora staging tree changed before USB preparation.");
            ct.ThrowIfCancellationRequested();
            await EnsureCurrentDriveAsync(drive, ct).ConfigureAwait(false);
            RequireNonSystemUsbDisk(drive);

            // Diskpart's clean/convert/create sequence is deliberately not
            // cancellable halfway through a partition-table mutation.
            using var heldVolumes = LockAndDismountVolumesOnDisk(drive.DriveIndex);
            await EnsureCurrentDriveAsync(drive, ct).ConfigureAwait(false);
            RequireNonSystemUsbDisk(drive);
            progress?.Report(new UsbWriteProgress(UsbWritePhase.CreatingOemdrv, 0,
                0, "Creating the signed Fedora installer seed…"));
            await RunFatSeedDiskpartAsync(drive.DriveIndex, partSizeMb, driveLetter)
                .ConfigureAwait(false);
            await Task.Delay(2500, CancellationToken.None).ConfigureAwait(false);
            await EnsureCurrentDriveAsync(drive, ct).ConfigureAwait(false);
            var seed = ValidateFatSeedMounted(drive, driveLetter, partSizeMb);
            var seedRoot = $"{driveLetter}:\\";
            var copyTotal = checked(stagingSize + bootSize);
            long copied = 0;
            progress?.Report(new UsbWriteProgress(UsbWritePhase.CopyingFiles, 0,
                copyTotal, "Copying signed installer and migration files…"));

            foreach (var directory in stagedDirectories)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(ResolveDestinationPath(seedRoot, directory));
            }

            foreach (var file in bootFiles)
            {
                ct.ThrowIfCancellationRequested();
                var target = ResolveDestinationPath(seedRoot,
                    file.RelativeDestination.Replace('/', Path.DirectorySeparatorChar));
                if (!pinnedBootFiles.TryGetStream(file.RelativeDestination, out var source))
                    throw new IOException("A signed Fedora boot file handle is unavailable.");
                await CopyAndReadBackAsync(source, target, file.SizeBytes, ct)
                    .ConfigureAwait(false);
                copied = checked(copied + file.SizeBytes);
                progress?.Report(new UsbWriteProgress(UsbWritePhase.CopyingFiles,
                    copied, copyTotal, file.RelativeDestination));
            }

            foreach (var file in stagedFiles)
            {
                ct.ThrowIfCancellationRequested();
                var target = ResolveDestinationPath(seedRoot, file.RelativeDestination);
                if (string.Equals(file.RelativeDestination, "migration-manifest.json",
                        StringComparison.OrdinalIgnoreCase))
                    await CopyAndReadBackAsync(pinnedManifest, target, file.SizeBytes, ct)
                        .ConfigureAwait(false);
                else if (string.Equals(file.RelativeDestination, "ks.cfg",
                             StringComparison.OrdinalIgnoreCase))
                    await CopyAndReadBackAsync(pinnedKickstart, target, file.SizeBytes, ct)
                        .ConfigureAwait(false);
                else if (pinnedProviderFiles.TryGetStream(file.RelativeDestination,
                             out var pinnedProviderFile))
                    await CopyAndReadBackAsync(pinnedProviderFile, target, file.SizeBytes, ct)
                        .ConfigureAwait(false);
                else if (pinnedSelectedMedia.TryGetStream(file.RelativeDestination,
                             out var pinnedSelectedImage))
                    await CopyAndReadBackAsync(pinnedSelectedImage, target, file.SizeBytes, ct)
                        .ConfigureAwait(false);
                else if (pinnedUserFiles.TryGetStream(file.RelativeDestination,
                             out var pinnedUserFile))
                    await CopyAndReadBackAsync(pinnedUserFile, target, file.SizeBytes, ct)
                        .ConfigureAwait(false);
                else
                    throw new IOException("A staged Fedora migration file handle is unavailable.");
                copied = checked(copied + file.SizeBytes);
                progress?.Report(new UsbWriteProgress(UsbWritePhase.CopyingFiles,
                    copied, copyTotal, file.RelativeDestination));
            }
            if (copied != copyTotal || InspectStagingDirectory(stagingDirectory) != stagingSize ||
                !stagedFiles.Select(file => (file.RelativeDestination, file.SizeBytes)).SequenceEqual(
                    GetStagingFiles(stagingDirectory).Select(file =>
                        (file.RelativeDestination, file.SizeBytes))) ||
                !stagedDirectories.SequenceEqual(GetStagingDirectories(stagingDirectory),
                    StringComparer.OrdinalIgnoreCase))
                throw new IOException("The migration staging directory changed during USB preparation.");
            await RequireMountedIsoRootAsync(isoPath, isoRoot, ct).ConfigureAwait(false);
            foreach (var directory in stagedDirectories)
                if (!Directory.Exists(ResolveDestinationPath(seedRoot, directory)))
                    throw new IOException("A selected empty migration directory is missing on USB readback.");

            await EnsureCurrentDriveAsync(drive, ct).ConfigureAwait(false);
            if (ValidateFatSeedMounted(drive, driveLetter, partSizeMb) != seed)
                throw new InvalidOperationException("The installer seed GPT identity changed while files were copied.");
            var identity = await SeedIdentityWriter.WriteAsync(seedRoot, seed.DiskGuid,
                seed.PartitionGuid, pinnedProviderFiles.RelativePaths, ct).ConfigureAwait(false);
            if (!identity.Files.Any(file => file.Path == "ks.cfg") ||
                !identity.Files.Any(file => file.Path == "migration-manifest.json"))
                throw new InvalidDataException("The Fedora seed identity is incomplete.");

            var grub = BuildFedoraFatSeedGrubConfig(bootSpec, seed.DiskGuid,
                seed.PartitionGuid);
            foreach (var relative in new[] { "EFI/BOOT/grub.cfg", "EFI/fedora/grub.cfg" })
            {
                var path = ResolveDestinationPath(seedRoot,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var output = new FileStream(path, FileMode.Create,
                    FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
                {
                    await output.WriteAsync(Encoding.ASCII.GetBytes(grub), ct)
                        .ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false);
#pragma warning disable CA1849 // Physical seed handoff needs Flush(true).
                    output.Flush(flushToDisk: true);
#pragma warning restore CA1849
                }
                var readBack = await File.ReadAllTextAsync(path, Encoding.ASCII, ct)
                    .ConfigureAwait(false);
                if (!string.Equals(readBack, grub, StringComparison.Ordinal))
                    throw new IOException("The Fedora EFI boot configuration differs on readback.");
            }
            await VerifyFatSeedIdentityReadBackAsync(seedRoot, identity, ct)
                .ConfigureAwait(false);
            await EnsureCurrentDriveAsync(drive, ct).ConfigureAwait(false);
            if (ValidateFatSeedMounted(drive, driveLetter, partSizeMb) != seed)
                throw new InvalidOperationException("The completed installer seed GPT identity changed.");
            progress?.Report(new UsbWriteProgress(UsbWritePhase.Complete,
                copyTotal, copyTotal, "Fedora USB installer seed is ready."));
        }
        finally
        {
            await DismountVerifiedIsoAsync(isoPath).ConfigureAwait(false);
        }
    }

    internal static void RequireFedoraFatBootSpec(InstallerBootSpec bootSpec,
        string expectedIsoSha256)
    {
        ArgumentNullException.ThrowIfNull(bootSpec);
        if (expectedIsoSha256 is not { Length: 64 } ||
            !expectedIsoSha256.All(Uri.IsHexDigit) ||
            !string.Equals(bootSpec.IsoSha256, expectedIsoSha256,
                StringComparison.OrdinalIgnoreCase) ||
            bootSpec.ConfigDelivery != ConfigDelivery.OemDrvLabel ||
            bootSpec.CopyFullIsoToVolume || bootSpec.PreCreateRootPartition ||
            !string.Equals(bootSpec.VolumeLabel, "OEMDRV", StringComparison.Ordinal) ||
            bootSpec.KernelUrl is not null || bootSpec.InitrdUrl is not null ||
            bootSpec.ExtraIsoFiles.Count != 1 ||
            bootSpec.ExtraIsoFiles[0] is not
                { IsoRelativePath: "images/install.img", OemDrvRelativePath: "images/install.img", Required: true } ||
            bootSpec.KernelIsoPaths.Count == 0 || bootSpec.InitrdIsoPaths.Count == 0 ||
            !bootSpec.KernelCmdline.Contains(
                "inst.stage2=hd:/dev/disk/by-partuuid/{SEED_PARTUUID}:",
                StringComparison.Ordinal) ||
            !bootSpec.KernelCmdline.Contains(
                "inst.ks=hd:/dev/disk/by-partuuid/{SEED_PARTUUID}:/ks.cfg",
                StringComparison.Ordinal) ||
            Regex.Matches(bootSpec.KernelCmdline, "inst.stage2=", RegexOptions.CultureInvariant).Count != 1 ||
            Regex.Matches(bootSpec.KernelCmdline, "inst.ks=", RegexOptions.CultureInvariant).Count != 1 ||
            Regex.Matches(bootSpec.KernelCmdline, "\\{SEED_PARTUUID\\}", RegexOptions.CultureInvariant).Count != 2 ||
            bootSpec.KernelCmdline.Replace("{SEED_PARTUUID}", string.Empty,
                StringComparison.Ordinal).Contains('{', StringComparison.Ordinal) ||
            bootSpec.KernelCmdline.Contains("igloo.seedpartuuid=", StringComparison.Ordinal) ||
            bootSpec.KernelCmdline.Contains("igloo.seeddiskguid=", StringComparison.Ordinal) ||
            bootSpec.KernelCmdline.Contains("LABEL=", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(bootSpec.KernelCmdline, @"^[A-Za-z0-9_./:={} \-]+$") ||
            !Regex.IsMatch(bootSpec.MenuTitle, @"^[A-Za-z0-9 ()\-]+$"))
            throw new NotSupportedException("The selected provider has no supported signed FAT USB boot recipe.");
        foreach (var relative in bootSpec.KernelIsoPaths.Concat(bootSpec.InitrdIsoPaths))
            RequireSafeIsoRelativePath(relative);
    }

    internal static int PlanFatSeedPartitionSizeMb(UsbDriveInfo drive,
        long stagingBytes, long bootBytes)
    {
        if (stagingBytes < 0 || bootBytes <= 0 || drive.SizeBytes <= 0)
            throw new InvalidDataException("The Fedora seed or selected USB capacity is unavailable.");
        var required = checked(stagingBytes + bootBytes + 256 * MiB);
        var megabytes = Math.Max(512L, checked((required + MiB - 1) / MiB));
        // Windows can create a 16 MiB MSR before the EFI partition. Keep
        // additional space for alignment and the primary/backup GPT tables.
        if (megabytes > 32 * 1024 || megabytes > int.MaxValue ||
            checked((megabytes + 32) * MiB) > drive.SizeBytes)
            throw new InvalidOperationException("The selected USB drive is too small for the FAT32 Fedora installer seed.");
        return checked((int)megabytes);
    }

    internal static long EstimateFat32AllocatedBytes(
        IReadOnlyList<FatSeedFile> files, int directoryCount)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (directoryCount < 0)
            throw new InvalidDataException("The Fedora seed directory count is invalid.");
        const long worstCaseCluster = 64L * 1024;
        const long longNameEntryReserve = 1024;
        var total = checked((long)directoryCount * worstCaseCluster);
        foreach (var file in files)
        {
            ValidateFat32FileSize(file.SizeBytes);
            var clusters = checked((file.SizeBytes + worstCaseCluster - 1) /
                                   worstCaseCluster);
            total = checked(total + Math.Max(1, clusters) * worstCaseCluster +
                            longNameEntryReserve);
        }
        return total;
    }

    internal static string BuildFedoraFatSeedGrubConfig(InstallerBootSpec spec,
        Guid diskGuid, Guid partitionGuid)
    {
        if (diskGuid == Guid.Empty || partitionGuid == Guid.Empty)
            throw new InvalidDataException("The Fedora USB seed has no exact GPT identity.");
        if (spec.IsoSha256 is not { Length: 64 } digest)
            throw new InvalidDataException("The Fedora installer image identity is unavailable.");
        RequireFedoraFatBootSpec(spec, digest);
        var commandLine = spec.KernelCmdline.Replace("{SEED_PARTUUID}",
            partitionGuid.ToString("D"), StringComparison.Ordinal);
        if (commandLine.Contains('{', StringComparison.Ordinal) ||
            commandLine.Contains('}', StringComparison.Ordinal))
            throw new InvalidDataException("The Fedora USB boot arguments contain an unbound value.");
        commandLine += " igloo.seedpartuuid=" + partitionGuid.ToString("D") +
            " igloo.seeddiskguid=" + diskGuid.ToString("D");
        return $$"""
            insmod part_gpt
            insmod fat
            insmod linux

            set igloo_origin="$root"
            if [ -z "$igloo_origin" ]; then
                echo "The installer seed origin is unavailable."
                halt
            fi
            set igloo_candidate=
            search --no-floppy --set=igloo_candidate --file /{{SeedIdentityWriter.FileName}}
            if [ -z "$igloo_candidate" ]; then
                echo "The installer seed identity is unavailable."
                halt
            fi
            if [ "$igloo_candidate" != "$igloo_origin" ]; then
                echo "The installer seed identity resolved to another partition."
                halt
            fi
            set root="$igloo_origin"

            set default=0
            set timeout=5
            menuentry "{{spec.MenuTitle}}" {
                linux ($root)/igloo-boot/linux {{commandLine}}
                initrd ($root)/igloo-boot/initrd
            }
            """;
    }

    internal sealed record FatSeedFile(string Source, string RelativeDestination,
        long SizeBytes);

    private static List<FatSeedFile> ResolveFedoraBootFiles(string isoRoot,
        InstallerBootSpec spec)
    {
        var result = new List<FatSeedFile>();
        foreach (var (source, destination) in new[]
        {
            ("EFI/BOOT/BOOTX64.EFI", "EFI/BOOT/BOOTX64.EFI"),
            ("EFI/BOOT/grubx64.efi", "EFI/BOOT/grubx64.efi"),
            ("EFI/BOOT/mmx64.efi", "EFI/BOOT/mmx64.efi"),
        })
            result.Add(RequiredIsoFile(isoRoot, source, destination));
        var kernel = spec.KernelIsoPaths.FirstOrDefault(relative =>
            File.Exists(IsoPath(isoRoot, relative))) ??
            throw new FileNotFoundException("The signed Fedora installer kernel is unavailable.");
        var initrd = spec.InitrdIsoPaths.FirstOrDefault(relative =>
            File.Exists(IsoPath(isoRoot, relative))) ??
            throw new FileNotFoundException("The signed Fedora installer initrd is unavailable.");
        result.Add(RequiredIsoFile(isoRoot, kernel, "igloo-boot/linux"));
        result.Add(RequiredIsoFile(isoRoot, initrd, "igloo-boot/initrd"));
        result.Add(RequiredIsoFile(isoRoot, "images/install.img", "images/install.img"));
        return result;
    }

    private static PinnedProviderArtifacts OpenReadLockedBootFiles(
        IReadOnlyList<FatSeedFile> bootFiles)
    {
        var streams = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in bootFiles)
            {
                if ((File.GetAttributes(file.Source) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A signed Fedora boot file is redirected.");
                var stream = new FileStream(file.Source, FileMode.Open, FileAccess.Read,
                    FileShare.Read, BufferSize, useAsync: true);
                streams.Add(file.RelativeDestination, stream);
                if (stream.Length != file.SizeBytes)
                    throw new IOException("A signed Fedora boot file changed size before USB preparation.");
            }
            return new PinnedProviderArtifacts(streams);
        }
        catch
        {
            foreach (var stream in streams.Values)
                stream.Dispose();
            throw;
        }
    }

    internal static PinnedProviderArtifacts OpenReadLockedStagingFiles(
        string stagingDirectory, IReadOnlyList<FatSeedFile> files,
        IReadOnlySet<string> excludedPaths)
    {
        var root = Path.GetFullPath(stagingDirectory);
        var streams = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in files)
            {
                var relative = file.RelativeDestination.Replace('\\', '/');
                if (excludedPaths.Contains(relative)) continue;
                var current = Path.GetDirectoryName(file.Source);
                while (current is not null &&
                       !string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("A selected Fedora migration folder is redirected.");
                    current = Path.GetDirectoryName(current);
                }
                if (current is null ||
                    (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
                    (File.GetAttributes(file.Source) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A selected Fedora migration file is redirected.");
                var stream = new FileStream(file.Source, FileMode.Open, FileAccess.Read,
                    FileShare.Read, BufferSize, useAsync: true);
                streams.Add(relative, stream);
                if (stream.Length != file.SizeBytes)
                    throw new IOException("A selected Fedora migration file changed size before USB preparation.");
            }
            return new PinnedProviderArtifacts(streams);
        }
        catch
        {
            foreach (var stream in streams.Values)
                stream.Dispose();
            throw;
        }
    }

    private static FatSeedFile RequiredIsoFile(string isoRoot, string relative,
        string destination)
    {
        var source = IsoPath(isoRoot, relative);
        if (!File.Exists(source) ||
            (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("A required signed Fedora boot file is unavailable.", source);
        var size = new FileInfo(source).Length;
        if (size <= 0) throw new InvalidDataException("A signed Fedora boot file is empty.");
        ValidateFat32FileSize(size);
        return new FatSeedFile(source, destination, size);
    }

    private static string IsoPath(string isoRoot, string relative)
    {
        RequireSafeIsoRelativePath(relative);
        return Path.Join(isoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void RequireSafeIsoRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\', StringComparison.Ordinal) ||
            relative.Contains(':', StringComparison.Ordinal) || Path.IsPathRooted(relative) ||
            relative.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." || segment.EndsWith('.') ||
                segment.EndsWith(' ') ||
                Regex.IsMatch(segment.Split('.')[0],
                    @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("A signed installer file path is unsafe.");
    }

    internal static FatSeedFile[] GetStagingFiles(string stagingDirectory) =>
        EnumerateStagingEntries(stagingDirectory).Files
            .OrderBy(file => file.RelativeDestination, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal sealed class PinnedProviderArtifacts : IAsyncDisposable
    {
        private readonly Dictionary<string, FileStream> streams;

        internal PinnedProviderArtifacts(Dictionary<string, FileStream> streams) =>
            this.streams = streams;

        internal bool TryGetStream(string relativePath, out FileStream stream) =>
            streams.TryGetValue(relativePath.Replace('\\', '/'), out stream!);

        internal IReadOnlyCollection<string> RelativePaths => streams.Keys;

        public async ValueTask DisposeAsync()
        {
            foreach (var stream in streams.Values)
                await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static async Task<PinnedProviderArtifacts> OpenVerifiedProviderArtifactsAsync(
        string stagingDirectory, FatSeedFile[] stagedFiles,
        System.Collections.Immutable.ImmutableArray<UsbStagedArtifactIdentity> identities,
        CancellationToken ct = default)
    {
        if (identities.IsDefaultOrEmpty)
            throw new InvalidDataException("The loaded Fedora provider artifact identities are unavailable.");
        var files = stagedFiles.ToDictionary(file =>
            file.RelativeDestination.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        var expectedAgentFiles = files.Keys.Where(path => path.StartsWith(
            "igloo-agent/", StringComparison.OrdinalIgnoreCase)).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var suppliedAgentFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var streams = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var identity in identities)
            {
                ct.ThrowIfCancellationRequested();
                if (identity is null)
                    throw new InvalidDataException("A Fedora provider artifact identity is empty.");
                var relative = identity.RelativePath;
                RequireSafeIsoRelativePath(relative);
                if (!relative.Contains('/', StringComparison.Ordinal) ||
                    relative.Split('/').Any(segment => segment.Any(c =>
                        !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '.' and not '-')) ||
                    !paths.Add(relative) ||
                    relative.Equals("ks.cfg", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("migration-manifest.json", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals(SeedIdentityWriter.FileName, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("browser-profiles/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("EFI/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("igloo-boot/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("images/", StringComparison.OrdinalIgnoreCase) ||
                    !files.TryGetValue(relative, out var file) ||
                    !string.Equals(relative, file.RelativeDestination.Replace('\\', '/'),
                        StringComparison.Ordinal) ||
                    (relative.StartsWith("igloo-agent/", StringComparison.OrdinalIgnoreCase) &&
                     relative["igloo-agent/".Length..].Contains('/', StringComparison.Ordinal)))
                    throw new InvalidDataException("The Fedora provider artifact list contains a duplicate or unexpected path.");
                var stream = await OpenVerifiedStagingFileAsync(file.Source,
                    identity.Sha256, ct).ConfigureAwait(false);
                if (stream.Length != file.SizeBytes)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw new IOException("A validated Fedora provider artifact changed size before USB preparation.");
                }
                streams.Add(relative, stream);
                if (relative.StartsWith("igloo-agent/", StringComparison.OrdinalIgnoreCase))
                    suppliedAgentFiles.Add(relative);
            }
            if (!suppliedAgentFiles.SetEquals(expectedAgentFiles))
                throw new InvalidDataException("The loaded Fedora provider artifact list omits an agent file.");
            return new PinnedProviderArtifacts(streams);
        }
        catch
        {
            foreach (var stream in streams.Values)
                await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task<PinnedProviderArtifacts> OpenObservedAgentArtifactsAsync(
        string stagingDirectory, CancellationToken ct = default)
    {
        var agentDirectory = Path.Join(stagingDirectory, "igloo-agent");
        if (!Directory.Exists(agentDirectory) ||
            (File.GetAttributes(agentDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The staged first-boot agent directory is unavailable.");
        var agents = GetStagingFiles(agentDirectory);
        if (agents.Length is 0 or > 128)
            throw new InvalidDataException("The staged first-boot agent inventory is unavailable.");
        var streams = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var agent in agents)
            {
                ct.ThrowIfCancellationRequested();
                var relative = "igloo-agent/" + agent.RelativeDestination.Replace('\\', '/');
                RequireSafeIsoRelativePath(relative);
                if (relative["igloo-agent/".Length..].Contains('/', StringComparison.Ordinal) ||
                    relative.Split('/').Any(segment => segment.Any(c =>
                        !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '.' and not '-')) ||
                    agent.SizeBytes <= 0 || streams.ContainsKey(relative) ||
                    (File.GetAttributes(agent.Source) & FileAttributes.ReparsePoint) != 0 ||
                    (File.GetAttributes(agentDirectory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The staged first-boot agent inventory is unsafe.");
                var stream = new FileStream(agent.Source, FileMode.Open, FileAccess.Read,
                    FileShare.Read, BufferSize, useAsync: true);
                streams.Add(relative, stream);
                if (stream.Length != agent.SizeBytes)
                    throw new IOException("A staged first-boot agent file changed size.");
                await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
                stream.Position = 0;
            }
            return new PinnedProviderArtifacts(streams);
        }
        catch
        {
            foreach (var stream in streams.Values)
                await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task VerifyProviderAgentCopyAsync(string stagingDirectory,
        string seedRoot, PinnedProviderArtifacts pinned, CancellationToken ct = default)
    {
        var expected = pinned.RelativePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { stagingDirectory, seedRoot })
        {
            var agentRoot = Path.Join(root, "igloo-agent");
            if (!Directory.Exists(agentRoot) ||
                (File.GetAttributes(agentRoot) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The first-boot agent directory changed during USB preparation.");
            var observed = GetStagingFiles(agentRoot).Select(file =>
                "igloo-agent/" + file.RelativeDestination.Replace('\\', '/')).ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            if (!observed.SetEquals(expected))
                throw new IOException("The first-boot agent inventory changed during USB preparation.");
        }
        foreach (var relative in pinned.RelativePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (!pinned.TryGetStream(relative, out var source))
                throw new IOException("A held first-boot agent file is unavailable.");
            source.Position = 0;
            var expectedHash = await SHA256.HashDataAsync(source, ct).ConfigureAwait(false);
            source.Position = 0;
            using var readBack = new FileStream(Path.Join(seedRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)), FileMode.Open,
                FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            var actualHash = await SHA256.HashDataAsync(readBack, ct).ConfigureAwait(false);
            if (readBack.Length != source.Length || !actualHash.SequenceEqual(expectedHash))
                throw new IOException("A first-boot agent file differs on USB readback.");
        }
    }

    private static string[] GetStagingDirectories(string stagingDirectory) =>
        EnumerateStagingEntries(stagingDirectory).Directories
            .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static void RequireSafeFatStagingPaths(
        IReadOnlyList<FatSeedFile> files, IReadOnlyList<string> directories)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(directories);
        var nodes = new Dictionary<string, (string Original, bool IsFile)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, isFile) in directories.Select(path => (path, false))
                     .Concat(files.Select(file => (file.RelativeDestination, true))))
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
                throw new InvalidDataException("A staged Fedora FAT path is unsafe.");
            var segments = relative.Replace('\\', '/').Split('/');
            var original = new StringBuilder();
            var canonical = new StringBuilder();
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                if (segment.Length is 0 or > 255 || segment is "." or ".." ||
                    segment.EndsWith('.') || segment.EndsWith(' ') ||
                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    segment.Any(char.IsControl) ||
                    Regex.IsMatch(segment.Split('.')[0].TrimEnd(' '),
                        @"\A(?:CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])\z",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                    Regex.IsMatch(segment,
                        @"\A[^.~]{1,6}~[1-9][0-9]*(?:\.[^.]*)?\z",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    throw new InvalidDataException("A staged Fedora FAT path has an unsafe component.");
                if (index != 0)
                {
                    original.Append('/');
                    canonical.Append('/');
                }
                original.Append(segment);
                canonical.Append(segment.Normalize(NormalizationForm.FormC));
                var key = canonical.ToString();
                var terminalFile = isFile && index == segments.Length - 1;
                if (nodes.TryGetValue(key, out var node))
                {
                    if (!string.Equals(node.Original, original.ToString(),
                            StringComparison.Ordinal) || node.IsFile || terminalFile &&
                        index == segments.Length - 1)
                        throw new InvalidDataException("The staged Fedora payload has FAT component collisions.");
                }
                else
                    nodes.Add(key, (original.ToString(), terminalFile));
            }
        }
    }

    private static (List<FatSeedFile> Files, List<string> Directories)
        EnumerateStagingEntries(string stagingDirectory)
    {
        var files = new List<FatSeedFile>();
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(stagingDirectory);
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A staged installer directory is redirected.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A staged installer entry is redirected.");
                var relative = Path.GetRelativePath(stagingDirectory, entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(relative);
                    pending.Push(entry);
                }
                else
                    files.Add(new FatSeedFile(entry, relative, new FileInfo(entry).Length));
            }
        }
        return (files, directories);
    }

    private static void RequireFedoraStage(string stagingDirectory,
        FatSeedFile[] files, IReadOnlyList<string> directories)
    {
        var names = files.Select(file => file.RelativeDestination.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count != files.Length)
            throw new InvalidDataException("The staged Fedora payload has FAT name collisions.");
        foreach (var required in new[]
        {
            "ks.cfg", "migration-manifest.json", "igloo-agent/agent.py",
            "igloo-agent/first-boot.sh", "igloo-agent/display-apply.py",
            "igloo-agent/igloo_boot.py",
        })
            if (!names.Contains(required) ||
                new FileInfo(Path.Join(stagingDirectory,
                    required.Replace('/', Path.DirectorySeparatorChar))).Length == 0)
                throw new InvalidDataException("The staged Fedora installer payload is incomplete.");
        foreach (var name in names.Concat(directories.Select(directory =>
                     directory.Replace('\\', '/'))))
            if (name.Equals(SeedIdentityWriter.FileName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals("EFI", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("images", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("igloo-boot", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("EFI/", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("images/", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("igloo-boot/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staged payload conflicts with signed boot files.");
    }

    internal static string[] RequireSelectedStagingPaths(MigrationManifest manifest,
        IReadOnlyList<string> stagedDirectories, IReadOnlyList<FatSeedFile> stagedFiles,
        IReadOnlyCollection<string>? providerArtifacts = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(stagedDirectories);
        ArgumentNullException.ThrowIfNull(stagedFiles);
        if (manifest.SchemaVersion != 1 || manifest.DistroId != "fedora-kde" ||
            manifest.Hardware is null || manifest.User is null || manifest.Files is null ||
            manifest.Hardware.InstallMode != "replace" ||
            manifest.Hardware.TargetDiskGuid is not { } targetGuid ||
            targetGuid == Guid.Empty || manifest.Hardware.TargetDiskBytes <= 0 ||
            manifest.Files.IncludedFolders is null || manifest.Browsers is null)
            throw new InvalidDataException("The staged Fedora installer manifest is incomplete.");
        var directories = stagedDirectories.Select(path => path.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in manifest.Files.IncludedFolders)
        {
            RequireSafeIsoRelativePath(folder);
            if (folder.Contains('/', StringComparison.Ordinal) ||
                !directories.Contains("files/" + folder))
                throw new InvalidDataException("A selected Fedora migration folder is absent from staging.");
        }
        foreach (var browser in manifest.Browsers)
        {
            RequireSafeIsoRelativePath(browser.SourceRelativePath);
            RequireSafeIsoRelativePath(browser.ProfileStagingPath);
            if (!string.Equals(browser.ProfileStagingPath,
                    "browser-profiles/" + browser.SourceRelativePath,
                    StringComparison.Ordinal) ||
                !directories.Contains(browser.ProfileStagingPath))
                throw new InvalidDataException("A selected Fedora browser profile is absent from staging.");
        }
        var selectedMedia = new List<string>(2);
        if (manifest.Wallpaper is { } wallpaper)
        {
            if (wallpaper.FileName is not { } name ||
                !Regex.IsMatch(name, @"\Aigloo-wallpaper\.[A-Za-z0-9]{2,8}\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("The selected Fedora wallpaper has an unsafe staged file name.");
            selectedMedia.Add(name);
        }
        if (manifest.AccountPicture is { } picture)
        {
            if (!string.Equals(picture.FileName, "igloo-avatar.jpg", StringComparison.Ordinal))
                throw new InvalidDataException("The selected Fedora avatar has an unsafe staged file name.");
            selectedMedia.Add(picture.FileName);
        }
        foreach (var name in selectedMedia)
            if (!stagedFiles.Any(file =>
                    string.Equals(file.RelativeDestination, name, StringComparison.OrdinalIgnoreCase) &&
                    file.SizeBytes > 0 && File.Exists(file.Source) &&
                    (File.GetAttributes(file.Source) & FileAttributes.ReparsePoint) == 0))
                throw new InvalidDataException("A selected Fedora image is absent from staging.");

        // FAT exposes every staged byte. A stale or injected file outside the
        // selected folders must not be copied just because it shares the stage.
        var selectedRoots = manifest.Files.IncludedFolders.Select(folder => "files/" + folder)
            .Concat(manifest.Browsers.Select(browser => browser.ProfileStagingPath))
            .ToArray();
        var exactFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "migration-manifest.json", "ks.cfg",
        };
        exactFiles.UnionWith(selectedMedia);
        if (providerArtifacts is not null)
            exactFiles.UnionWith(providerArtifacts);
        var parentDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in selectedRoots.Concat(exactFiles))
        {
            var slash = path.IndexOf('/', StringComparison.Ordinal);
            while (slash >= 0)
            {
                parentDirectories.Add(path[..slash]);
                slash = path.IndexOf('/', slash + 1);
            }
        }
        foreach (var root in selectedRoots)
            parentDirectories.Add(root);
        foreach (var file in stagedFiles)
        {
            var path = file.RelativeDestination.Replace('\\', '/');
            if (!exactFiles.Contains(path) && !selectedRoots.Any(root =>
                    path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("An unselected Fedora migration file is present in staging.");
        }
        foreach (var stagedDirectory in stagedDirectories)
        {
            var path = stagedDirectory.Replace('\\', '/');
            if (!parentDirectories.Contains(path) && !selectedRoots.Any(root =>
                    path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("An unselected Fedora migration folder is present in staging.");
        }
        return [.. selectedMedia];
    }

    private static async Task<PinnedProviderArtifacts> OpenSelectedMediaAsync(
        IReadOnlyList<FatSeedFile> stagedFiles, IReadOnlyList<string> selectedMedia,
        CancellationToken ct)
    {
        var streams = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var name in selectedMedia)
            {
                ct.ThrowIfCancellationRequested();
                var file = stagedFiles.Single(file => string.Equals(
                    file.RelativeDestination, name, StringComparison.OrdinalIgnoreCase));
                if (!File.Exists(file.Source) ||
                    (File.GetAttributes(file.Source) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A selected Fedora image changed before USB preparation.");
                var stream = new FileStream(file.Source, FileMode.Open, FileAccess.Read,
                    FileShare.Read, BufferSize, useAsync: true);
                streams.Add(name, stream);
                if (stream.Length != file.SizeBytes || stream.Length <= 0)
                    throw new IOException("A selected Fedora image changed size before USB preparation.");
                await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
                stream.Position = 0;
            }
            return new PinnedProviderArtifacts(streams);
        }
        catch
        {
            foreach (var stream in streams.Values)
                await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task CopyAndReadBackAsync(string source, string destination,
        long expectedSize, CancellationToken ct)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("An installer source file changed to a redirected path.");
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferSize, useAsync: true);
        await CopyAndReadBackAsync(src, destination, expectedSize, ct)
            .ConfigureAwait(false);
    }

    private static async Task CopyAndReadBackAsync(FileStream src, string destination,
        long expectedSize, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (src.Length != expectedSize)
            throw new IOException("An installer source file changed size during USB preparation.");
        src.Position = 0;
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var dest = new FileStream(destination, FileMode.Create, FileAccess.Write,
            FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            long copied = 0;
            int count;
            while ((count = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                incremental.AppendData(buffer, 0, count);
                await dest.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                copied = checked(copied + count);
            }
            await dest.FlushAsync(ct).ConfigureAwait(false);
#pragma warning disable CA1849 // Physical seed handoff needs Flush(true).
            dest.Flush(flushToDisk: true);
#pragma warning restore CA1849
            if (copied != expectedSize)
                throw new IOException("An installer source file ended during USB preparation.");
        }
        using var readBack = new FileStream(destination, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferSize, useAsync: true);
        var writtenHash = await SHA256.HashDataAsync(readBack, ct).ConfigureAwait(false);
        if (readBack.Length != expectedSize ||
            !incremental.GetHashAndReset().SequenceEqual(writtenHash))
            throw new IOException("An installer file differs on USB readback.");
    }

    internal static async Task<FileStream> OpenVerifiedStagingFileAsync(string path,
        string expectedSha256, CancellationToken ct = default)
    {
        if (expectedSha256 is not { Length: 64 } || !expectedSha256.All(Uri.IsHexDigit) ||
            !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A validated Fedora staging file is unavailable.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferSize, useAsync: true);
        try
        {
            if (stream.Length <= 0)
                throw new InvalidDataException("A validated Fedora staging file is empty.");
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)
                .ConfigureAwait(false));
            if (!string.Equals(digest, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A validated Fedora staging file changed after target review.");
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task VerifyFatSeedIdentityReadBackAsync(string root,
        SeedIdentityV1 expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var path = Path.Join(root, SeedIdentityWriter.FileName);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 16 * 1024, useAsync: true);
        var identity = await System.Text.Json.JsonSerializer.DeserializeAsync<SeedIdentityV1>(
            stream, SeedJsonOptions, ct).ConfigureAwait(false);
        if (identity is not { Version: 1 } ||
            identity.DiskGuid != expected.DiskGuid ||
            identity.PartitionGuid != expected.PartitionGuid ||
            identity.Files.Count < 2 ||
            !identity.Files.SequenceEqual(expected.Files))
            throw new IOException("The Fedora seed identity differs on readback.");
        foreach (var file in identity.Files)
        {
            var relative = file.Path.Replace('/', Path.DirectorySeparatorChar);
            var destination = ResolveDestinationPath(root, relative);
            using var input = new FileStream(destination, FileMode.Open, FileAccess.Read,
                FileShare.Read, BufferSize, useAsync: true);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)
                .ConfigureAwait(false));
            if (!string.Equals(digest, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A bound Fedora seed file differs on readback.");
        }
    }

    internal static (Guid DiskGuid, Guid PartitionGuid) ValidateFatSeedMounted(
        UsbDriveInfo drive, char driveLetter, int partSizeMb)
    {
        var root = $"{driveLetter}:\\";
        if (!Directory.Exists(root))
            throw new InvalidOperationException("The new EFI seed mount point is unavailable.");
        var mountedVolumePath = GetMountedVolumePath(root);
        var volume = new DriveInfo(root);
        if (!volume.IsReady ||
            !string.Equals(volume.DriveFormat, "FAT32", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(volume.VolumeLabel, "OEMDRV", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The bound EFI seed is not a FAT32 OEMDRV volume.");
        using var query = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
            FormattableString.Invariant($"SELECT DiskNumber, PartitionNumber, Guid, GptType, AccessPaths, Size FROM MSFT_Partition WHERE DiskNumber = {drive.DriveIndex}"));
        using var results = query.Get();
        var partitions = results.Cast<ManagementObject>().ToArray();
        if (partitions.Length is < 1 or > 2)
            throw new InvalidOperationException("The selected USB disk has an unexpected partition layout.");
        var seedPartitions = partitions.Where(partition =>
            Guid.TryParse(partition["GptType"]?.ToString(), out var type) &&
            type == EfiPartitionType &&
            partition["AccessPaths"] is string[] paths &&
            paths.Contains(mountedVolumePath, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (seedPartitions.Length != 1 ||
            !Guid.TryParse(seedPartitions[0]["Guid"]?.ToString(), out var partitionGuid) ||
            partitionGuid == Guid.Empty ||
            Convert.ToInt64(seedPartitions[0]["Size"], CultureInfo.InvariantCulture) <
                (long)partSizeMb * MiB - MiB ||
            Convert.ToInt64(seedPartitions[0]["Size"], CultureInfo.InvariantCulture) >
                (long)partSizeMb * MiB + MiB)
            throw new InvalidOperationException("The selected USB disk has no uniquely bound EFI seed partition.");
        foreach (var partition in partitions)
        {
            if (ReferenceEquals(partition, seedPartitions[0])) continue;
            if (!Guid.TryParse(partition["GptType"]?.ToString(), out var type) ||
                type != MicrosoftReservedPartitionType ||
                Convert.ToInt64(partition["Size"], CultureInfo.InvariantCulture) > 32 * MiB ||
                partition["AccessPaths"] is string[] { Length: > 0 })
                throw new InvalidOperationException("The selected USB disk has an unexpected non-seed partition.");
        }
        using var diskQuery = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
            FormattableString.Invariant($"SELECT Number, Guid, PartitionStyle FROM MSFT_Disk WHERE Number = {drive.DriveIndex}"));
        using var diskResults = diskQuery.Get();
        var disks = diskResults.Cast<ManagementObject>().ToArray();
        if (disks.Length != 1 ||
            Convert.ToInt32(disks[0]["PartitionStyle"], CultureInfo.InvariantCulture) != 2 ||
            !Guid.TryParse(disks[0]["Guid"]?.ToString(), out var diskGuid) ||
            diskGuid == Guid.Empty)
            throw new InvalidOperationException("The Fedora installer seed disk has no unique GPT identity.");
        return (diskGuid, partitionGuid);
    }

    internal static string GetMountedVolumePath(string mountPoint)
    {
        var buffer = new char[128];
        if (!NativeMethods.GetVolumeNameForVolumeMountPoint(mountPoint, buffer,
                checked((uint)buffer.Length)))
            throw new IOException("The EFI seed drive letter has no exact volume binding " +
                $"(Win32 error {Marshal.GetLastWin32Error()}).");
        var terminator = Array.IndexOf(buffer, '\0');
        if (terminator <= 0)
            throw new IOException("The EFI seed volume binding is malformed.");
        var path = new string(buffer, 0, terminator);
        if (!path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith("}\\", StringComparison.Ordinal))
            throw new IOException("The EFI seed volume binding is unavailable.");
        return path;
    }

    private static void RequireNonSystemUsbDisk(UsbDriveInfo drive)
    {
        using var query = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
            FormattableString.Invariant($"SELECT Number, IsBoot, IsSystem, IsReadOnly, IsOffline FROM MSFT_Disk WHERE Number = {drive.DriveIndex}"));
        using var results = query.Get();
        var disks = results.Cast<ManagementObject>().ToArray();
        if (disks.Length != 1 || disks[0]["IsBoot"] is not bool boot || boot ||
            disks[0]["IsSystem"] is not bool system || system ||
            disks[0]["IsReadOnly"] is not bool readOnly || readOnly ||
            disks[0]["IsOffline"] is not bool offline || offline)
            throw new InvalidOperationException("The selected USB is unavailable or contains a Windows boot or system volume.");
    }

    private static void RequireSourceOutsideSelectedDrive(UsbDriveInfo drive, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("An installer source path is unavailable.");
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (root is not { Length: 3 } || root[1] != ':' ||
            new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new InvalidDataException("Installer sources must be on a local fixed volume.");
        // A directory junction in any ancestor could make a C: source resolve
        // onto the selected USB, even though the apparent drive letter is C:.
        for (var current = fullPath; current is not null &&
             !string.Equals(current, root, StringComparison.OrdinalIgnoreCase);
             current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("An installer source path is redirected.");
        var letter = char.ToUpperInvariant(root[0]);
        using var query = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
            "SELECT DiskNumber, DriveLetter FROM MSFT_Partition");
        using var results = query.Get();
        var matches = results.Cast<ManagementObject>()
            .Where(partition => string.Equals(partition["DriveLetter"]?.ToString(),
                letter.ToString(CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 ||
            Convert.ToInt32(matches[0]["DiskNumber"], CultureInfo.InvariantCulture) == drive.DriveIndex)
            throw new InvalidOperationException("An installer source is on the selected USB disk or has no exact volume binding.");
        var sourceDisk = Convert.ToInt32(matches[0]["DiskNumber"], CultureInfo.InvariantCulture);
        using var diskQuery = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
            FormattableString.Invariant($"SELECT Number, BusType FROM MSFT_Disk WHERE Number = {sourceDisk}"));
        using var diskResults = diskQuery.Get();
        var disks = diskResults.Cast<ManagementObject>().ToArray();
        var busType = disks.Length == 1
            ? Convert.ToInt32(disks[0]["BusType"], CultureInfo.InvariantCulture)
            : -1;
        if (busType is not (1 or 3 or 6 or 8 or 10 or 11 or 17))
            throw new InvalidOperationException("Installer sources must be on independently observed local storage.");
    }

    private async Task RunFatSeedDiskpartAsync(int diskIndex, int partSizeMb,
        char driveLetter)
    {
        var script = FormattableString.Invariant($"""
            select disk {diskIndex}
            clean
            convert gpt
            create partition efi size={partSizeMb}
            format fs=fat32 label=OEMDRV quick
            assign letter={driveLetter}
            """);
        var scriptPath = Path.Join(Path.GetTempPath(),
            $"igloo-fat-seed-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(scriptPath, script, CancellationToken.None)
                .ConfigureAwait(false);
            var psi = new ProcessStartInfo("diskpart.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add(scriptPath);
            using var process = Process.Start(psi) ??
                throw new InvalidOperationException("Failed to start diskpart.exe.");
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug("FAT seed diskpart output: {Output}", await stdout.ConfigureAwait(false));
            _logger.LogDebug("FAT seed diskpart diagnostic: {Diagnostic}", await stderr.ConfigureAwait(false));
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Diskpart failed while creating the Fedora USB seed.");
        }
        finally
        {
            TryDeleteFile(scriptPath);
        }
    }

    private static async Task<string> MountVerifiedIsoAsync(string isoPath,
        CancellationToken ct)
    {
        var literal = "'" + isoPath.Replace("'", "''", StringComparison.Ordinal) + "'";
        var attached = await RunFatSeedPowerShellAsync(
            $"$ErrorActionPreference='Stop'; (Get-DiskImage -ImagePath {literal}).Attached",
            ct).ConfigureAwait(false);
        if (!string.Equals(attached.Trim(), "False", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The verified ISO is already mounted or its mount state is unavailable.");
        try
        {
            // Once attempted, the image may have mounted even if PowerShell
            // later reports a failure. The catch always retires this mount.
            await RunFatSeedPowerShellAsync(
                $"$ErrorActionPreference='Stop'; Mount-DiskImage -ImagePath {literal} -Access ReadOnly | Out-Null",
                CancellationToken.None).ConfigureAwait(false);
            for (var attempt = 0; attempt < 60; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var result = await GetMountedIsoRootAsync(literal, ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result))
                    return RequireIsoVolumeRoot(result);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            throw new IOException("The verified Fedora ISO did not expose one unique volume.");
        }
        catch
        {
            await DismountVerifiedIsoAsync(isoPath).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string> GetMountedIsoRootAsync(string isoLiteral,
        CancellationToken ct) => await RunFatSeedPowerShellAsync(
            "$ErrorActionPreference='Stop'; " +
            $"$image=Get-DiskImage -ImagePath {isoLiteral}; " +
            "if (-not $image.Attached) { throw 'Verified ISO is detached' }; " +
            "$volumes=@($image | Get-Volume); " +
            "if ($volumes.Count -gt 1) { throw 'Verified ISO has ambiguous volumes' }; " +
            "if ($volumes.Count -eq 1) { $volumes[0].Path }",
            ct).ConfigureAwait(false);

    internal static string RequireIsoVolumeRoot(string output)
    {
        var root = output.Trim();
        const string prefix = @"\\?\Volume{";
        if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            root.Length != prefix.Length + 36 + 2 ||
            !root.EndsWith("}\\", StringComparison.Ordinal) ||
            !Guid.TryParse(root.AsSpan(prefix.Length, 36), out var guid) ||
            guid == Guid.Empty)
            throw new InvalidDataException("The verified ISO has no unique volume GUID path.");
        return root;
    }

    private static async Task RequireMountedIsoRootAsync(string isoPath,
        string expectedRoot, CancellationToken ct)
    {
        var literal = "'" + isoPath.Replace("'", "''", StringComparison.Ordinal) + "'";
        var observed = RequireIsoVolumeRoot(await GetMountedIsoRootAsync(literal, ct)
            .ConfigureAwait(false));
        if (!string.Equals(observed, expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The verified ISO volume changed during USB preparation.");
    }

    private static async Task DismountVerifiedIsoAsync(string isoPath)
    {
        var literal = "'" + isoPath.Replace("'", "''", StringComparison.Ordinal) + "'";
        await RunFatSeedPowerShellAsync(
            $"$ErrorActionPreference='Stop'; $image=Get-DiskImage -ImagePath {literal}; " +
            "if ($image.Attached) { $image | Dismount-DiskImage }",
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<string> RunFatSeedPowerShellAsync(string command,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);
        using var process = Process.Start(psi) ??
            throw new InvalidOperationException("Failed to start Windows PowerShell.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        var output = await stdout.ConfigureAwait(false);
        var diagnostic = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException("Windows ISO mount failed: " + diagnostic.Trim());
        return output;
    }
}
