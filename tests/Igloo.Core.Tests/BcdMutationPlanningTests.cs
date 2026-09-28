using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;
using Xunit;

namespace Igloo.Core.Tests;

public sealed class BcdMutationPlanningTests
{
    private static readonly Guid Destination = new("eeeeeeee-1111-2222-3333-444444444444");
    private static readonly Guid Referenced = new("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly Guid Recovery = new("bbbbbbbb-1111-2222-3333-444444444444");

    [Fact]
    public void ReplicationPreservesTypeAndRetainedValuesAndAppliesTheLegacyFinalTransform()
    {
        var source = Source(
            E(0x11000001, new BcdDeviceElementValue(new BcdPartitionDevice(@"\Device\HarddiskVolume1"))),
            E(0x12000002, new BcdStringValue(@"\EFI\Microsoft\Boot\bootmgfw.efi")),
            E(0x12000004, new BcdStringValue("Windows Boot Manager")),
            E(0x12000005, new BcdStringValue("en-US")),
            E(0x14000006, new BcdObjectListValue([Referenced])),
            E(0x23000003, new BcdObjectValue(Referenced)),
            E(0x24000001, new BcdObjectListValue([Referenced, Recovery])),
            E(0x25000004, new BcdIntegerValue(30)), E(0x16000020, new BcdBooleanValue(true)),
            E(0x17000077, new BcdIntegerListValue([ulong.MaxValue, 0])));
        var result = Clone(source);
        Assert.Equal(ObservationAvailability.Available, result.Availability);
        Assert.Equal(Destination, result.Value.Id);
        Assert.Equal(source.ObjectType, result.Value.ObjectType);
        Assert.DoesNotContain(result.Value.Elements.Value, e => e.Type is 0x12000005 or 0x14000006);
        foreach (var element in source.Elements.Value.Where(e => e.Type is not (0x11000001 or 0x12000002 or 0x12000004 or 0x12000005 or 0x14000006)))
            Assert.True(RecoverySnapshotSerialization.ValueEqual(element, result.Value.Elements.Value.Single(e => e.Type == element.Type)));
        Assert.Equal(new BcdStringValue(@"\EFI\BOOT\BOOTX64.EFI"), result.Value.Elements.Value.Single(e => e.Type == 0x12000002).Value.Value);
        Assert.Equal(new BcdStringValue("installer"), result.Value.Elements.Value.Single(e => e.Type == 0x12000004).Value.Value);
        Assert.Equal(new BcdDeviceElementValue(new BcdQualifiedGptPartitionDevice(Target().Disk.GptDiskGuid, Target().PartitionGuid)), result.Value.Elements.Value.Single(e => e.Type == 0x11000001).Value.Value);
    }

    [Theory]
    [InlineData(ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.Unsupported)]
    [InlineData(ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Ambiguous)]
    public void RetainedFailedElementCannotBeDropped(ObservationAvailability state)
    {
        var result = Clone(Source(new BcdElementSnapshot(0x23000003, Observations.Failure<BcdElementValue>(state, "fixture"))));
        Assert.Equal(state, result.Availability);
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("ramdisk")]
    [InlineData("additional-options")]
    public void UnsupportedRetainedSetterSemanticsRejectReplication(string kind)
    {
        var value = kind switch
        {
            "opaque" => (BcdElementValue)new BcdOpaqueValue("future", Observations.Available<ImmutableArray<byte>>([1])),
            "ramdisk" => new BcdDeviceElementValue(new BcdFileDevice(4, @"\winre.wim", new BcdPartitionDevice(@"\Device\HarddiskVolume4"), Referenced)),
            _ => new BcdDeviceElementValue(new BcdQualifiedGptPartitionDevice(Target().Disk.GptDiskGuid, Target().PartitionGuid, Referenced)),
        };
        Assert.Equal(ObservationAvailability.Unsupported, Clone(Source(E(kind == "opaque" ? 0x18000001u : 0x11000099u, value))).Availability);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("list")]
    [InlineData("device")]
    [InlineData("parent")]
    public void InboundReferencesIncludeAllSupportedReferencePositions(string kind)
    {
        var element = kind switch
        {
            "object" => E(0x23000003, new BcdObjectValue(Referenced)),
            "list" => E(0x24000002, new BcdObjectListValue([Referenced])),
            "device" => E(0x11000001, new BcdDeviceElementValue(new BcdPartitionDevice(@"\Device\HarddiskVolume4", Referenced))),
            _ => E(0x11000001, new BcdDeviceElementValue(new BcdFileDevice(4, @"\Winre.wim", new BcdPartitionDevice(@"\Device\HarddiskVolume4", Referenced)))),
        };
        var result = BcdMutationPlanning.InboundReferences(Graph(Source(element)), Referenced);
        Assert.Equal(ObservationAvailability.Available, result.Availability);
        Assert.Equal(new BcdInboundReference(BcdRecoveryGraphV1.WindowsBootManagerId, element.Type), Assert.Single(result.Value));
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("unavailable")]
    [InlineData("duplicate")]
    public void IncompleteReferenceGraphIsNotAnEmptyReferenceSet(string kind)
    {
        var graph = Graph(Source(E(0x18000001, new BcdOpaqueValue("future", Observations.Available<ImmutableArray<byte>>([1])))));
        graph = kind switch
        {
            "unavailable" => graph with { Objects = Observations.Failure<ImmutableArray<BcdObjectSnapshot>>(ObservationAvailability.AccessDenied, "fixture") },
            "duplicate" => Graph(Source(), Source()),
            _ => graph,
        };
        Assert.NotEqual(ObservationAvailability.Available, BcdMutationPlanning.InboundReferences(graph, Referenced).Availability);
    }

    [Fact]
    public void NativeAndQualifiedRepresentationsCompareOnlyWhenTheirStructuresAgree()
    {
        var gpt = new BcdQualifiedGptPartitionDevice(Target().Disk.GptDiskGuid, Target().PartitionGuid);
        var expected = Source(E(0x11000001, new BcdDeviceElementValue(gpt)));
        var actual = Source(new BcdElementSnapshot(0x11000001, Observations.Available<BcdElementValue>(new BcdDeviceElementValue(new BcdPartitionDevice(@"\Device\HarddiskVolume1"))), Observations.Available<BcdDeviceValue>(gpt)));
        Assert.True(BcdMutationPlanning.SameObject(expected, actual));
        actual = Source(actual.Elements.Value[0] with { QualifiedDevice = Observations.Failure<BcdDeviceValue>(ObservationAvailability.Unavailable, "fixture") });
        Assert.False(BcdMutationPlanning.SameObject(expected, actual));
    }

    [Fact]
    public void RetainedDefaultReferenceConnectsTheCloneToTheActiveRecoveryChain()
    {
        var copied = Clone(Source(E(0x23000003, new BcdObjectValue(Referenced)))).Value;
        var graph = Graph(copied,
            new(Referenced, BcdRecoveryRoles.WindowsLoaderType, Observations.Available<ImmutableArray<BcdElementSnapshot>>([E(0x14000008, new BcdObjectListValue([Recovery]))])),
            new(Recovery, BcdRecoveryRoles.WindowsLoaderType, Observations.Available<ImmutableArray<BcdElementSnapshot>>([E(0x11000001, new BcdDeviceElementValue(new BcdFileDevice(4, @"\Winre.wim", new BcdPartitionDevice(@"\Device\HarddiskVolume4"))))])));
        var closure = BcdDependencyAnalysis.Analyze(graph, [Destination]);
        Assert.Contains(Recovery, closure.RequiredObjectIds);
        Assert.Contains(closure.Issues, i => i.ObjectId == Recovery && i.Code == BcdDependencyIssueCode.NonCanonicalDevice);
    }

    [Fact]
    public void DisconnectedWinReObjectIsOutsideCloneGraphButThisDoesNotCertifyTheWorkflow()
    {
        var copied = Clone(Source()).Value;
        var opaque = new BcdObjectSnapshot(Recovery, BcdRecoveryRoles.WindowsLoaderType, Observations.Available<ImmutableArray<BcdElementSnapshot>>([
            E(0x18000001, new BcdOpaqueValue("unknown", Observations.Available<ImmutableArray<byte>>([1])))]));
        var graph = Graph(copied, opaque);
        var closure = BcdDependencyAnalysis.Analyze(graph, [Destination]);
        Assert.Contains(Recovery, closure.ExcludedObjectIds);
        Assert.DoesNotContain(closure.Issues, i => i.ObjectId == Recovery);
        // A whole-store deletion reference proof still cannot ignore opaque observations.
        Assert.Equal(ObservationAvailability.Unsupported, BcdMutationPlanning.InboundReferences(graph, Destination).Availability);
    }

    private static Observation<BcdObjectSnapshot> Clone(BcdObjectSnapshot source) => BcdMutationPlanning.ReplicateBootManager(source, Destination, Target(), @"\EFI\BOOT\BOOTX64.EFI", "installer");
    private static BcdObjectSnapshot Source(params BcdElementSnapshot[] elements) => new(BcdRecoveryGraphV1.WindowsBootManagerId, BcdRecoveryRoles.WindowsBootManagerType, Observations.Available(elements.ToImmutableArray()));
    private static BcdElementSnapshot E(uint type, BcdElementValue value) => new(type, Observations.Available(value));
    private static BcdRecoveryGraphV1 Graph(params BcdObjectSnapshot[] objects) => new(1, Observations.Available(objects.ToImmutableArray()), Observations.Available(Referenced));
    private static CanonicalVolumeIdentityV1 Target() => new(new("eui.fixture", 8, 17, new("abcdefab-1234-5678-9abc-def012345678"), 2UL * 1024 * 1024 * 1024, 512, 4096),
        new("11111111-aaaa-bbbb-cccc-111111111111"), new("aaaaaaaa-1111-2222-3333-111111111111"),
        new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"), 128UL * 1024 * 1024, 1024UL * 1024 * 1024, "FAT32");
}
