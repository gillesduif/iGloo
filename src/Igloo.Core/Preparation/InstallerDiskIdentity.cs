using Igloo.Core.Abstractions;

namespace Igloo.Core.Preparation;

// GPT identity used by the Community installer. Provider hardware identifiers
// are optional here; this observation never qualifies a recovery snapshot.
public static class InstallerDiskIdentity
{
    public static Observation<DiskObservation> Observe(IReadOnlyList<DiskObservation> inventory, uint number)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var matches = inventory.Where(d => d.Number.Availability == ObservationAvailability.Available &&
            d.Number.Value == number).ToArray();
        if (matches.Length != 1)
            return Failure("disk-number-missing-or-ambiguous", matches.Length > 1
                ? ObservationAvailability.Ambiguous : ObservationAvailability.Unavailable);
        var disk = matches[0];
        if (disk.GptGuid.Availability != ObservationAvailability.Available)
            return Failure("gpt-disk-guid-unavailable", disk.GptGuid.Availability);
        if (disk.GptGuid.Value == Guid.Empty)
            return Failure("gpt-disk-guid-unavailable");
        if (disk.PartitionStyle.Availability != ObservationAvailability.Available)
            return Failure("disk-is-not-confirmed-gpt", disk.PartitionStyle.Availability);
        if (disk.PartitionStyle.Value != 2)
            return Failure("disk-is-not-confirmed-gpt", ObservationAvailability.Unsupported);
        if (disk.Size.Availability != ObservationAvailability.Available)
            return Failure("disk-size-unavailable", disk.Size.Availability);
        if (disk.Size.Value == 0)
            return Failure("disk-size-unavailable");
        if (inventory.Count(d => d.GptGuid.Availability == ObservationAvailability.Available &&
                d.GptGuid.Value == disk.GptGuid.Value) != 1)
            return Failure("gpt-disk-guid-ambiguous", ObservationAvailability.Ambiguous);
        if (disk.UniqueId.Availability == ObservationAvailability.Available &&
            !string.IsNullOrWhiteSpace(disk.UniqueId.Value) &&
            inventory.Count(d => d.UniqueId.Availability == ObservationAvailability.Available &&
                string.Equals(d.UniqueId.Value, disk.UniqueId.Value, StringComparison.OrdinalIgnoreCase)) != 1)
            return Failure("provider-unique-id-ambiguous", ObservationAvailability.Ambiguous);
        return Observations.Available(disk);
    }

    private static Observation<DiskObservation> Failure(string reason,
        ObservationAvailability availability = ObservationAvailability.Unavailable) =>
        Observations.Failure<DiskObservation>(availability, reason);
}
