using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;

namespace Igloo.Core.Runtime;

internal static class PublicInspectionMapper
{
    // Public V1 finding codes are reviewed identifiers, never arbitrary provider text.
    private static readonly HashSet<string> PublicFindingCodes = new(StringComparer.Ordinal)
    {
        "BIOS_LEGACY", "SECURE_BOOT_ON", "BITLOCKER_ACTIVE", "BITLOCKER_DECRYPTING",
        "NO_TPM", "LOW_RAM", "SecureBootUnsupported", "BITLOCKER_LOCKED",
        "RAM_BELOW_RECOMMENDED", "UBUNTU_NVIDIA", "UBUNTU_RAM_TORAM",
        "MINT_NVIDIA", "FEDORA_NVIDIA_RPMFUSION", "DEBIAN_NVIDIA_NONFREE",
    };

    public static SystemInspection MapSummary(PreflightReport report) => new(
        report.IsUefi, report.SecureBootEnabled, report.TpmPresent,
        report.BitLocker.ToString(), report.TotalRamBytes, report.Disks.Count,
        report.Findings.Select(MapFinding).ToArray());

    public static DetailedSystemInspection MapDetail(PreflightReport report) => new(
        MapSummary(report),
        report.Disks.Select((disk, ordinal) => new InspectionDisk(
            ordinal, Math.Max(0, disk.TotalBytes), Math.Max(0, disk.FreeBytes),
            disk.PartitionStyle.ToUpperInvariant() is "GPT" or "MBR"
                ? disk.PartitionStyle.ToUpperInvariant() : "Unknown",
            disk.Partitions.Any(p => p.IsBoot), disk.Partitions.Any(p => p.IsSystem),
            disk.Partitions.Select(p => new InspectionPartition(p.Index,
                Math.Max(0, p.SizeBytes), p.OffsetBytes, p.IsBoot, p.IsSystem,
                Math.Max(0, p.ShrinkableBytes), FileSystemCategory(p.FileSystem))).ToArray()))
            .ToArray(), report.LinuxInstallations.Count, report.SeedLeftovers.Count);

    public static InspectionFinding MapFinding(PreflightFinding finding) =>
        new(finding.Code is not null && PublicFindingCodes.Contains(finding.Code)
                ? finding.Code : "ProviderFinding",
            finding.Severity.ToString());

    private static string FileSystemCategory(string? fileSystem) =>
        fileSystem?.ToUpperInvariant() switch
        {
            "NTFS" => "NTFS",
            "FAT32" => "FAT32",
            "EXFAT" => "exFAT",
            "EXT4" => "ext4",
            "BTRFS" => "btrfs",
            "XFS" => "xfs",
            _ => "OtherOrUnknown",
        };
}
