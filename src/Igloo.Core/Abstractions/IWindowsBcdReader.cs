namespace Igloo.Core.Abstractions;

// A raw listing, not a complete parsed boot snapshot. Exit failure and unparsed output
// remain visible; callers must not infer missing boot entries from an empty listing.
public enum BcdListingStatus { Unparsed, Unavailable, CommandFailed }
public sealed record BcdListingObservation(string? StandardOutput, string? StandardError, int? ExitCode)
{
    public BcdListingStatus Status => ExitCode is null ? BcdListingStatus.Unavailable :
        ExitCode == 0 ? BcdListingStatus.Unparsed : BcdListingStatus.CommandFailed;
}

public interface IWindowsBcdReader
{
    BcdListingObservation ReadFirmware();
}
