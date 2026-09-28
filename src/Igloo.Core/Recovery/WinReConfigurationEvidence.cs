using System.Collections.Immutable;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Igloo.Core.Abstractions;

namespace Igloo.Core.Recovery;

// Source evidence, not another recovery snapshot or a claim that InstallState means Enabled.
public sealed record WinReLocationEvidence(Guid DiskGuid, ulong PartitionOffset, string Directory);
public sealed record WinReConfigurationEvidence(ImmutableArray<byte> RawXml, uint InstallState,
    Observation<Guid> RecoveryLoaderId, Observation<WinReLocationEvidence> Location);

public static class WinReConfigurationParser
{
    public const int MaximumBytes = 1024 * 1024;

    public static Observation<WinReConfigurationEvidence> Parse(Observation<ImmutableArray<byte>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Availability != ObservationAvailability.Available)
            return Observations.Failure<WinReConfigurationEvidence>(source.Availability, source.Code ?? "WinReConfigurationReadFailed");
        if (source.Value.IsDefaultOrEmpty || source.Value.Length > MaximumBytes) return Failed(ObservationAvailability.Ambiguous, "WinReXmlSize");
        try
        {
            using var stream = new MemoryStream(source.Value.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != "WindowsRE" || (string?)root.Attribute("version") != "2.0")
                return Failed(ObservationAvailability.Unsupported, "WinReXmlSchemaUnsupported");
            if (root.Elements().GroupBy(e => e.Name).Any(g => g.Count() != 1)) return Failed(ObservationAvailability.Ambiguous, "WinReXmlDuplicateElement");
            var bcdText = Attribute(root, "WinreBCD", "id");
            var loader = bcdText.Length == 0 || Guid.TryParse(bcdText, out var empty) && empty == Guid.Empty
                ? Observations.Failure<Guid>(ObservationAvailability.Absent, "WinReConfiguredLoaderAbsent")
                : Observations.Available(Guid.Parse(bcdText));
            var state = uint.Parse(Attribute(root, "InstallState", "state"), NumberStyles.None, CultureInfo.InvariantCulture);
            var directory = Attribute(root, "WinreLocation", "path");
            var disk = Guid.Parse(Attribute(root, "WinreLocation", "guid"));
            var offset = ulong.Parse(Attribute(root, "WinreLocation", "offset"), NumberStyles.None, CultureInfo.InvariantCulture);
            Observation<WinReLocationEvidence> location;
            if (directory.Length == 0 && disk == Guid.Empty && offset == 0)
                location = Observations.Failure<WinReLocationEvidence>(ObservationAvailability.Absent, "WinReConfiguredLocationAbsent");
            else if (disk == Guid.Empty || offset == 0 || !IsRelativePath(directory))
                return Failed(ObservationAvailability.Ambiguous, "WinReConfiguredLocationInvalid");
            else location = Observations.Available(new WinReLocationEvidence(disk, offset, directory));
            return Observations.Available(new WinReConfigurationEvidence(source.Value, state, loader, location));
        }
        catch (Exception error) when (error is XmlException or FormatException or OverflowException or InvalidOperationException)
        { return Failed(ObservationAvailability.Ambiguous, "WinReXmlMalformed"); }
    }

    public static bool IsRelativePath(string path) => !string.IsNullOrWhiteSpace(path) && path.StartsWith('\\') &&
        !path.StartsWith(@"\\", StringComparison.Ordinal) && !path.Contains(':', StringComparison.Ordinal) &&
        !path.Contains('/', StringComparison.Ordinal) && !path.Contains('\0', StringComparison.Ordinal) &&
        !path.Split('\\').Skip(1).Any(p => p is "" or "." or "..");

    private static string Attribute(XElement root, string element, string attribute)
    {
        var child = root.Element(element);
        return child is not null && !child.HasElements && child.Attribute(attribute) is { } value
            ? value.Value : throw new FormatException("Required WinRE configuration field missing.");
    }
    private static Observation<WinReConfigurationEvidence> Failed(ObservationAvailability state, string code) => Observations.Failure<WinReConfigurationEvidence>(state, code);
}
