namespace Igloo.Preflight;

// A mounted installer image: the root it is read through, and its drive letter when Windows gave it one.
internal readonly record struct MountedIso(string Root, char? Letter)
{
    // PowerShell that attaches the image read-only, with or without a drive letter.
    internal static string MountCommand(string isoLiteral, bool driveLetter) =>
        $"Mount-DiskImage -ImagePath {isoLiteral} -Access ReadOnly" + (driveLetter ? "" : " -NoDriveLetter");

    // PowerShell that prints the volume GUID path of each volume of the attached image.
    internal static string VolumePathQuery(string isoLiteral) =>
        $"(Get-DiskImage -ImagePath {isoLiteral} | Get-Volume).Path";

    // The one volume GUID root in PowerShell's answer; null for none, several or anything else.
    internal static string? VolumeRoot(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        const string prefix = @"\\?\Volume{";
        var root = output.Trim();
        return root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            root.Length == prefix.Length + 36 + 2 &&
            root.EndsWith("}\\", StringComparison.Ordinal) &&
            Guid.TryParse(root.AsSpan(prefix.Length, 36), out var volume) && volume != Guid.Empty
            ? root
            : null;
    }
}
