using System.Globalization;
using Igloo.Core.Abstractions;

namespace Igloo.Preflight;

// One ownership rule for System Check and for the removal itself; a label only nominates a partition (Beta Track B-01).
internal static class InstallerSeedOwnership
{
    private const string BasicDataGptType = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";

    private static readonly string[] Labels = ["OEMDRV", "CIDATA", "IGLOOISO"];

    internal static bool IsSeedLabel(string? label) =>
        label is not null && Labels.Contains(label, StringComparer.OrdinalIgnoreCase);

    // Volume root of a partition row: its volume GUID path, else its drive letter.
    internal static string? VolumeRoot(WindowsStorageRow partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (partition.Properties.TryGetValue("AccessPaths", out var raw) && raw is string[] paths &&
            paths.FirstOrDefault(path => path.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase)) is { } volume)
            return volume;
        var letter = partition.Properties.TryGetValue("DriveLetter", out var value) ? WmiValues.ToDriveLetter(value) : '\0';
        return char.IsAsciiLetter(letter) ? $"{letter}:\\" : null;
    }

    /// <summary>The data partition at this offset and size on a disk that boots Windows, when its volume carries iGloo's marker files.</summary>
    internal static WindowsStorageRow? FindOwnedSeed(IEnumerable<WindowsStorageRow> diskPartitions,
        long offsetBytes, long sizeBytes, Func<string, bool> hasMarkers)
    {
        ArgumentNullException.ThrowIfNull(diskPartitions);
        ArgumentNullException.ThrowIfNull(hasMarkers);
        var rows = diskPartitions.ToArray();
        if (!rows.Any(row => Flag(row, "IsBoot")))
            return null;
        var matches = rows.Where(row => Number(row, "Offset") == offsetBytes && Number(row, "Size") == sizeBytes).ToArray();
        if (matches.Length != 1)
            return null;
        var candidate = matches[0];
        if (Flag(candidate, "IsBoot") || Flag(candidate, "IsSystem") ||
            !candidate.Properties.TryGetValue("GptType", out var type) ||
            !string.Equals((type as string)?.Trim(), BasicDataGptType, StringComparison.OrdinalIgnoreCase))
            return null;
        return VolumeRoot(candidate) is { } root && hasMarkers(root) ? candidate : null;
    }

    private static bool Flag(WindowsStorageRow row, string name) =>
        row.Properties.TryGetValue(name, out var value) && value is true;

    private static long Number(WindowsStorageRow row, string name) =>
        row.Properties.TryGetValue(name, out var value) && value is not null
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : -1;
}
