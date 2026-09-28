using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.TestData;
using Xunit;
using static Igloo.TestData.InstallationOwnershipFixture;

namespace Igloo.Core.Tests;

public sealed class InstallerPayloadVerificationTests
{
    [Theory]
    [InlineData("valid", ObservationAvailability.Available)]
    [InlineData("wrong-source", ObservationAvailability.Ambiguous)]
    [InlineData("missing-source", ObservationAvailability.Ambiguous)]
    [InlineData("wrong-generation", ObservationAvailability.Ambiguous)]
    [InlineData("wrong-root", ObservationAvailability.Ambiguous)]
    public void SourceVerificationContinuesAfterRootFormatting(string change, ObservationAvailability expected)
    {
        var s = TargetRootFixture.Create(); var payload = s.Ownership.Esp.Payload.Volume;
        var manifest = new InstallerPayloadManifestV1(s.Root.GenerationId,
            [new(PreparationRole.Payload, new(payload.VolumeGuid, @"\source.squashfs", 100, new string('A', 64)))]);
        ImmutableArray<InstallerPayloadReadbackV1> files = [new(payload.PartitionGuid, "/source.squashfs", 100, new string('A', 64))];
        if (change == "wrong-source") files = [files[0] with { Sha256 = new string('F', 64) }];
        if (change == "missing-source") files = [];
        if (change == "wrong-generation") manifest = manifest with { GenerationId = Id(900) };
        if (change == "wrong-root") s = s with { Root = s.Root with { FileSystemUuid = Id(900) } };
        Assert.Equal(expected, InstallerPayloadVerification.VerifyForFormattedRoot(s.Ownership, s.Root.GenerationId,
            s.Root, manifest, A(s.Inventory), A(files)).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous,
            InstallerPayloadVerification.Verify(s.Ownership, s.Root.GenerationId, manifest, A(s.Inventory), A(files)).Availability);
    }

    [Theory]
    [InlineData("valid", ObservationAvailability.Available)]
    [InlineData("generation", ObservationAvailability.Ambiguous)]
    [InlineData("iso-hash", ObservationAvailability.Ambiguous)]
    [InlineData("iso-size", ObservationAvailability.Ambiguous)]
    [InlineData("wrong-partition", ObservationAvailability.Absent)]
    [InlineData("wrong-volume", ObservationAvailability.Ambiguous)]
    [InlineData("missing", ObservationAvailability.Ambiguous)]
    [InlineData("failed", ObservationAvailability.Unavailable)]
    public void PayloadReadbackMustMatchPinnedGenerationPartitionAndContent(string change, ObservationAvailability expected)
    {
        var (binding, inventory) = Create(true);
        var iso = binding.Layout.Partitions.Single(r => r.Role == PreparationRole.Iso).Identity;
        var manifest = new InstallerPayloadManifestV1(binding.Esp.GenerationId,
            [new(PreparationRole.Iso, new(iso.VolumeGuid, @"\installer.iso", 100, new string('A', 64)))]);
        ImmutableArray<InstallerPayloadReadbackV1> files = [new(iso.PartitionGuid, "/installer.iso", 100, new string('A', 64))];
        if (change == "generation") manifest = manifest with { GenerationId = Id(999) };
        if (change == "wrong-volume") manifest = manifest with { Files = [manifest.Files[0] with { File = manifest.Files[0].File with { VolumeGuid = Id(999) } }] };
        files = change switch
        {
            "iso-hash" => [files[0] with { Sha256 = new string('B', 64) }],
            "iso-size" => [files[0] with { Length = 101 }],
            "wrong-partition" => [files[0] with { PartitionGuid = binding.Esp.WindowsEsp.Volume.PartitionGuid }],
            "missing" => [],
            _ => files,
        };
        var observed = change == "failed" ? Observations.Failure<ImmutableArray<InstallerPayloadReadbackV1>>(ObservationAvailability.Unavailable, "probe") : A(files);
        Assert.Equal(expected, InstallerPayloadVerification.Verify(binding, binding.Esp.GenerationId, manifest, A(inventory), observed).Availability);
    }
}
