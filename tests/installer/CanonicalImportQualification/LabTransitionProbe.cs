using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;

namespace CanonicalImportQualification;

internal readonly record struct LabTransitionRequest(string Stage, Guid RunId, Guid GenerationId, string HostObservationSha256,
    ImmutableArray<InstallerLabBackingV1> CreatedBackings, ImmutableArray<InstallerLabBackingV1> ReopenedBackings,
    InstallerLabTransitionV1 Transition);

internal static class LabTransitionProbe
{
    internal static async Task<int> RunAsync(string requestPath, string runtimePath)
    {
        var request = LabStorageSmoke.Read<LabTransitionRequest>(requestPath);
        var runtime = LabStorageSmoke.Read<DebianSessionRuntimeV1>(runtimePath);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var fresh = await LabStorageSmoke.CollectAsync(runtime.Collector, runtime.ToolHashes[runtime.Collector], deadline.Token);
        var acquired = InstallerLabAcquisition.Correlate(request.RunId, request.HostObservationSha256,
            request.CreatedBackings, request.ReopenedBackings, fresh, LabStorageSmoke.ReadGuest(fresh));
        if (acquired.Availability != ObservationAvailability.Available) throw new InvalidDataException(acquired.Code);
        var t = request.Transition;
        var before = LinuxInstallerInventoryProtocol.Parse(t.Before);
        var created = LinuxInstallerInventoryProtocol.Parse(t.Created);
        var after = request.Stage == "Create" ? created : LinuxInstallerInventoryProtocol.Parse(t.Formatted);
        if (request.Stage is not ("Create" or "Format") || after.Availability != ObservationAvailability.Available ||
            fresh.Availability != ObservationAvailability.Available || !after.Value.Disks.SequenceEqual(fresh.Value.Disks) ||
            !after.Value.Partitions.SequenceEqual(fresh.Value.Partitions) || !after.Value.ExternalFileSystems.SequenceEqual(fresh.Value.ExternalFileSystems))
            throw new InvalidDataException("Transition current readback changed.");
        var p = created.Value.Partitions.Single(x => x.PartitionGuid == t.Intended.PartitionGuid);
        if (new InstallerLabPartitionIntentV1(p.DevicePath, p.DiskDevicePath, p.PartitionGuid, p.PartitionType, p.OffsetBytes, p.SizeBytes) != t.Intended)
            throw new InvalidDataException("Transition intent changed.");
        var creation = InstallerLabAcquisition.VerifyCreation(acquired.Value, request.GenerationId, p, before, created,
            Hash(t.Before), Hash(t.CreationIntent), Hash(t.Created));
        if (creation.Availability != ObservationAvailability.Available) throw new InvalidDataException(creation.Code);
        if (request.Stage == "Format")
        {
            var format = InstallerLabAcquisition.VerifyFormat(creation.Value, request.GenerationId, t.FileSystem, created, after,
                Hash(t.Created), Hash(t.FormatIntent), Hash(t.Formatted));
            if (format.Availability != ObservationAvailability.Available) throw new InvalidDataException(format.Code);
        }
        // Complete replayable inputs remain in protected provisioning storage. This output
        // binds actual independent readback; it is not a Windows or production receipt.
        Console.WriteLine(JsonSerializer.Serialize(new { Provider = "IsolatedFileBackedLab", Scope = JsonSerializer.Deserialize<InstallerLabCreationIntentV1>(t.CreationIntent)?.Scope ?? "StorageSmoke",
            request.RunId, request.GenerationId, request.Stage, t.Role, t.Intended,
            BeforeSha256 = Hash(t.Before), CreatedSha256 = Hash(t.Created),
            ReadbackSha256 = Hash(request.Stage == "Create" ? t.Created : t.Formatted),
            RequestSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(requestPath))),
            Availability = "Available" }));
        return 0;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
