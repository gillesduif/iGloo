using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Microsoft.Extensions.Logging;

namespace Igloo.Preflight;

// Read-only checks that run before the NTFS shrink (Beta Track B-02). Each mirrors a
// requirement RegisterBootEntry or the seed writer enforces later, so a refusal
// stops the run while the disk is still untouched.
public sealed partial class DirectInstallService
{
    // Same window FindFreeBootIndex scans at registration time.
    private const ushort FirstCandidateBootIndex = 0x0080;
    private const ushort LastCandidateBootIndex = 0x00FE;
    private const string CandidateDriveLetters = "IJKLMNOPQRSTUVWXYZ";

    internal static void RequireBootRegistrationReadiness(IWindowsFirmwareReader firmware,
        IWindowsBcdReader bcd, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(firmware);
        ArgumentNullException.ThrowIfNull(bcd);
        ArgumentNullException.ThrowIfNull(logger);
        var bootOrder = firmware.ReadBootOrder();
        switch (bootOrder.Availability)
        {
            case ObservationAvailability.Available:
            case ObservationAvailability.Absent:
                break;
            case ObservationAvailability.AccessDenied:
                throw new InvalidOperationException(
                    $"UEFI NVRAM is not accessible from this process (Win32 error {bootOrder.NativeError}). " +
                    "Run iGloo as Administrator before preparing the installation.");
            case ObservationAvailability.Unsupported:
                throw new InvalidOperationException(
                    "Windows reports no UEFI firmware variables (legacy BIOS boot or an unsupported session). " +
                    "iGloo cannot register an installer boot entry on this system.");
            default:
                throw new InvalidOperationException(
                    $"UEFI boot variables could not be read (Win32 error {bootOrder.NativeError}).");
        }
        var free = FindFreeBootIndexObserved(firmware) ?? throw new InvalidOperationException(
            "The firmware has no free Boot#### slot between 0080 and 00FE. " +
            "Remove unused firmware boot entries before preparing the installation.");
        var listing = bcd.ReadFirmware();
        if (listing.Status != BcdListingStatus.Unparsed)
        {
            var detail = string.IsNullOrWhiteSpace(listing.StandardError) ? string.Empty : "\n" + listing.StandardError.Trim();
            throw new InvalidOperationException(
                "bcdedit could not enumerate the firmware boot store" +
                (listing.ExitCode is { } code ? $" (exit code {code})" : string.Empty) +
                ". Run iGloo as Administrator before preparing the installation." + detail);
        }
        logger.LogInformation("Boot registration readiness: NVRAM readable, Boot{Index:X4} free, bcdedit reachable", free);
    }

    // Only a slot the firmware reports absent counts; a denied read never does.
    internal static ushort? FindFreeBootIndexObserved(IWindowsFirmwareReader firmware)
    {
        ArgumentNullException.ThrowIfNull(firmware);
        for (var index = FirstCandidateBootIndex; index <= LastCandidateBootIndex; index++)
            if (firmware.ReadBootEntry(index).Availability == ObservationAvailability.Absent)
                return index;
        return null;
    }

    internal static string[] StagedAgentPaths(IReadOnlyList<StagedFile> stagedFiles)
    {
        ArgumentNullException.ThrowIfNull(stagedFiles);
        return stagedFiles
            .Where(file => file.RelativePath.StartsWith("igloo-agent" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .Select(file => file.RelativePath.Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    // Applies SeedIdentityWriter's inventory bounds to the staged tree before any write.
    internal static void RequirePlannedSeedWithinBounds(string stagingDirectory,
        IReadOnlyList<StagedFile> stagedFiles, IReadOnlyCollection<string> stagedAgentPaths)
    {
        ArgumentNullException.ThrowIfNull(stagedFiles);
        ArgumentNullException.ThrowIfNull(stagedAgentPaths);
        if (stagedAgentPaths.Count == 0)
            throw new InvalidDataException("The installer seed has no bound first-boot agent files.");
        var dataFiles = stagedFiles.Where(file => IsUserDataPath(file.RelativePath))
            .Select(file => file.RelativePath.Replace('\\', '/')).ToArray();
        var dataDirectories = dataFiles.Length == 0 ? 0 : SeedUserDataDirectories
            .Select(name => Path.Join(stagingDirectory, name)).Where(Directory.Exists)
            .Sum(directory => Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).Count());
        SeedIdentityWriter.RequirePlannedInventoryWithinBounds(stagedAgentPaths, dataFiles.Length,
            checked(dataFiles.Length + dataDirectories), dataFiles.Sum(path => (long)path.Length));
    }

    // ObservePreparedTarget pins the seed from this inventory after creation; an
    // unreadable inventory would otherwise fail only after the shrink.
    private void RequireStorageIdentityReadable(int diskNumber)
    {
        var observed = _storage.ReadIdentitySnapshot();
        if (observed.Availability != ObservationAvailability.Available ||
            !observed.Value.Disks.Any(disk => disk.Number.Availability == ObservationAvailability.Available &&
                disk.Number.Value == checked((uint)diskNumber)))
            throw new InvalidOperationException(
                "The storage identity inventory does not expose the selected disk; the installer partition could not be pinned after creation. Run the system check again.");
    }

    internal static int CountFreeCandidateLetters(IEnumerable<char> usedLetters)
    {
        ArgumentNullException.ThrowIfNull(usedLetters);
        var used = usedLetters.Select(char.ToUpperInvariant).ToHashSet();
        return CandidateDriveLetters.Count(letter => !used.Contains(letter));
    }

    private static void RequireAvailableDriveLetters(int needed)
    {
        var free = CountFreeCandidateLetters(DriveInfo.GetDrives().Select(drive => drive.Name[0]));
        if (free < needed)
            throw new InvalidOperationException(
                $"Only {free} drive letter(s) between I: and Z: are free; the installer partitions need {needed}. " +
                "Free a drive letter before preparing the installation.");
    }
}
