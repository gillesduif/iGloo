using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityRecovery;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class BootRegistrationBcdProjectionTests
{
    private static readonly Guid NewId = new("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid ProjectionId = new("dddddddd-aaaa-bbbb-cccc-111111111111");
    private static readonly Guid OtherId = new("eeeeeeee-aaaa-bbbb-cccc-111111111111");

    [Fact]
    public void ProviderMayDelayProjectionWithoutChangingTheExpectedGraph()
    {
        var plan = Plan();
        var reconciler = new BootRegistrationBcdProjection(plan);
        Assert.Same(plan.BeforeBcd, reconciler.Reconcile(plan.BeforeBcd, plan.BeforeBcd,
            EntryWrite(plan)));
        var projected = Add(plan.BeforeBcd, Projection(plan));
        Assert.Same(projected, reconciler.Reconcile(plan.BeforeBcd, projected, EntryWrite(plan)));
    }

    [Fact]
    public void UnchangedUnavailableCurrentLoaderDoesNotAddANewExecutionGate()
    {
        var plan = Plan(currentLoaderUnavailable: true);
        var actual = Add(plan.BeforeBcd, Projection(plan));
        Assert.Same(actual, new BootRegistrationBcdProjection(plan)
            .Reconcile(plan.BeforeBcd, actual, EntryWrite(plan)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactFirmwareProjectionMayAppendItsIdWithoutChangingOtherReferences(bool appendOrder)
    {
        var plan = Plan();
        var actual = Add(plan.BeforeBcd, Projection(plan));
        if (appendOrder)
            actual = SetList(actual, 0x24000001, Order(plan.BeforeBcd).Add(ProjectionId));
        Assert.Same(actual, new BootRegistrationBcdProjection(plan)
            .Reconcile(plan.BeforeBcd, actual, EntryWrite(plan)));
    }

    [Fact]
    public void NativePartitionReadRequiresMatchingQualifiedGptObservation()
    {
        var plan = Plan();
        var projection = Projection(plan);
        var qualified = Assert.IsType<BcdDeviceElementValue>(
            projection.Elements.Value.Single(e => e.Type == 0x11000001).Value.Value).Device;
        var native = Element(0x11000001, new BcdDeviceElementValue(
            new BcdPartitionDevice(@"\Device\HarddiskVolume7"))) with
        { QualifiedDevice = Observations.Available(qualified) };
        projection = projection with { Elements = Observations.Available(projection.Elements.Value
            .Select(e => e.Type == 0x11000001 ? native : e).ToImmutableArray()) };
        var actual = Add(plan.BeforeBcd, projection);
        Assert.Same(actual, new BootRegistrationBcdProjection(plan)
            .Reconcile(plan.BeforeBcd, actual, EntryWrite(plan)));

        var unqualified = projection with { Elements = Observations.Available(projection.Elements.Value
            .Select(e => e.Type == 0x11000001 ? e with { QualifiedDevice = null } : e).ToImmutableArray()) };
        Assert.Throws<InvalidOperationException>(() => new BootRegistrationBcdProjection(plan)
            .Reconcile(plan.BeforeBcd, Add(plan.BeforeBcd, unqualified), EntryWrite(plan)));
    }

    [Fact]
    public void BootOrderMovesOnlyPinnedProjectionAndBootNextSelectsOnlyThatId()
    {
        var plan = Plan();
        var reconciler = new BootRegistrationBcdProjection(plan);
        var afterEntry = SetList(Add(plan.BeforeBcd, Projection(plan)), 0x24000001,
            Order(plan.BeforeBcd).Add(ProjectionId));
        reconciler.Reconcile(plan.BeforeBcd, afterEntry, EntryWrite(plan));
        var afterOrder = SetList(afterEntry, 0x24000001, [ProjectionId, .. Order(plan.BeforeBcd)]);
        Assert.Same(afterOrder, reconciler.Reconcile(afterEntry, afterOrder, Write(plan, "BootOrder")));
        var afterNext = SetList(afterOrder, 0x24000002, [ProjectionId]);
        Assert.Same(afterNext, reconciler.Reconcile(afterOrder, afterNext, Write(plan, "BootNext")));
    }

    [Fact]
    public void RetryPinsTheUniqueExactProjectionAlreadyPresentInTheBaseline()
    {
        var plan = Plan(baselineProjectionCount: 1);
        var reconciler = new BootRegistrationBcdProjection(plan);
        Assert.Same(plan.BeforeBcd, reconciler.Reconcile(plan.BeforeBcd, plan.BeforeBcd, EntryWrite(plan)));
        var actual = SetList(plan.BeforeBcd, 0x24000001,
            [ProjectionId, .. Order(plan.BeforeBcd).Where(id => id != ProjectionId)]);
        Assert.Same(actual, reconciler.Reconcile(plan.BeforeBcd, actual, Write(plan, "BootOrder")));
    }

    [Fact]
    public void MultipleMatchingBaselineProjectionsCannotChooseAnArbitraryIdentity()
    {
        var plan = Plan(baselineProjectionCount: 2);
        Assert.Throws<InvalidOperationException>(() => new BootRegistrationBcdProjection(plan));
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("partition")]
    [InlineData("path")]
    [InlineData("description")]
    [InlineData("object-type")]
    [InlineData("extra-element")]
    [InlineData("missing-element")]
    [InlineData("failed-element")]
    public void UnrelatedOrIncompleteFirmwareObjectCannotBeAdopted(string fault)
    {
        var plan = Plan();
        var projection = Projection(plan);
        projection = fault switch
        {
            "disk" => Replace(projection, Element(0x11000001, new BcdDeviceElementValue(
                new BcdQualifiedGptPartitionDevice(OtherId, plan.Target.PartitionGuid)))),
            "partition" => Replace(projection, Element(0x11000001, new BcdDeviceElementValue(
                new BcdQualifiedGptPartitionDevice(plan.Target.Disk.GptDiskGuid, OtherId)))),
            "path" => Replace(projection, Element(0x12000002, new BcdStringValue(@"\EFI\other\bootx64.efi"))),
            "description" => Replace(projection, Element(0x12000004, new BcdStringValue("Other installer"))),
            "object-type" => projection with { ObjectType = BcdRecoveryRoles.WindowsBootManagerType },
            "extra-element" => projection with { Elements = Observations.Available(
                projection.Elements.Value.Add(Element(0x14000008, new BcdObjectListValue([OtherId])))) },
            "missing-element" => projection with { Elements = Observations.Available(
                projection.Elements.Value.Where(e => e.Type != 0x11000001).ToImmutableArray()) },
            _ => Replace(projection, new BcdElementSnapshot(0x11000001,
                Observations.Failure<BcdElementValue>(ObservationAvailability.Unavailable, "test"))),
        };
        Assert.Throws<InvalidOperationException>(() => new BootRegistrationBcdProjection(plan)
            .Reconcile(plan.BeforeBcd, Add(plan.BeforeBcd, projection), EntryWrite(plan)));
    }

    [Theory]
    [InlineData("unrelated-object-added")]
    [InlineData("existing-object-changed")]
    [InlineData("existing-object-removed")]
    [InlineData("current-loader-changed")]
    [InlineData("current-loader-unavailable")]
    [InlineData("unexpected-reference")]
    [InlineData("duplicate-projection")]
    public void ExactProjectionDoesNotHideUnrelatedGraphChanges(string fault)
    {
        var plan = Plan();
        var actual = Add(plan.BeforeBcd, Projection(plan));
        actual = fault switch
        {
            "unrelated-object-added" => Add(actual, new BcdObjectSnapshot(OtherId, 0x1010000a,
                Observations.Available<ImmutableArray<BcdElementSnapshot>>([]))),
            "existing-object-changed" => ReplaceObject(actual, Replace(actual.Objects.Value
                .Single(o => o.Id == BcdRecoveryGraphV1.WindowsBootManagerId),
                Element(0x12000004, new BcdStringValue("Changed Windows entry")))),
            "existing-object-removed" => actual with { Objects = Observations.Available(
                actual.Objects.Value.Where(o => o.Id != BcdRecoveryGraphV1.WindowsBootManagerId).ToImmutableArray()) },
            "current-loader-changed" => actual with { CurrentLoaderId = Observations.Available(OtherId) },
            "current-loader-unavailable" => actual with { CurrentLoaderId =
                Observations.Failure<Guid>(ObservationAvailability.Unavailable, "test") },
            "unexpected-reference" => SetList(actual, 0x24000002, [ProjectionId]),
            _ => Add(actual, Projection(plan) with { Id = OtherId }),
        };
        Assert.Throws<InvalidOperationException>(() => new BootRegistrationBcdProjection(plan)
            .Reconcile(plan.BeforeBcd, actual, EntryWrite(plan)));
    }

    [Fact]
    public void ChangedElementDiagnosticIdentifiesTheBoundaryWithoutIncludingItsValue()
    {
        var plan = Plan();
        const string changedValue = "provider-value-that-must-not-appear-in-the-error";
        var bootManager = plan.BeforeBcd.Objects.Value.Single(o => o.Id == BcdRecoveryGraphV1.WindowsBootManagerId);
        var actual = ReplaceObject(Add(plan.BeforeBcd, Projection(plan)),
            Replace(bootManager, Element(0x12000002, new BcdStringValue(changedValue))));
        var error = Assert.Throws<InvalidOperationException>(() => new BootRegistrationBcdProjection(plan)
            .Reconcile(plan.BeforeBcd, actual, EntryWrite(plan)));
        Assert.Contains(BcdRecoveryGraphV1.WindowsBootManagerId.ToString("D"), error.Message, StringComparison.Ordinal);
        Assert.Contains("12000002", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(changedValue, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("order-deleted")]
    [InlineData("order-reordered")]
    [InlineData("next-other")]
    [InlineData("next-additional")]
    [InlineData("projection-substituted")]
    public void SubsequentWritesCannotChangeUnrelatedOrderOrSubstituteThePinnedObject(string fault)
    {
        var plan = Plan();
        var reconciler = new BootRegistrationBcdProjection(plan);
        var expected = Add(plan.BeforeBcd, Projection(plan));
        reconciler.Reconcile(plan.BeforeBcd, expected, EntryWrite(plan));
        var actual = fault switch
        {
            "order-deleted" => SetList(expected, 0x24000001, [ProjectionId]),
            "order-reordered" => SetList(expected, 0x24000001, [ProjectionId, .. Order(plan.BeforeBcd).Reverse()]),
            "next-other" => SetList(expected, 0x24000002, [OtherId]),
            "next-additional" => SetList(expected, 0x24000002, [ProjectionId, OtherId]),
            _ => expected with { Objects = Observations.Available(expected.Objects.Value
                .Select(o => o.Id == ProjectionId ? o with { Id = OtherId } : o).ToImmutableArray()) },
        };
        var variable = fault.StartsWith("next", StringComparison.Ordinal) ? "BootNext" : "BootOrder";
        Assert.Throws<InvalidOperationException>(() => reconciler.Reconcile(expected, actual, Write(plan, variable)));
    }

    private static CommunityBootRegistrationPlan Plan(int baselineProjectionCount = 0,
        bool currentLoaderUnavailable = false)
    {
        var snapshot = CommunityRecoveryFixture.Exact();
        var entries = Enumerable.Range(0, 256).Select(i =>
            snapshot.Firmware.BootEntries.SingleOrDefault(e => e.Index == i) ??
            EfiRecoveryParser.ParseBootEntry((ushort)i, snapshot.Firmware.BootNextRaw)).ToImmutableArray();
        var evidence = new BootRegistrationEvidence(snapshot.Binding, 3, snapshot.BootConfiguration,
            snapshot.Firmware with { BootEntries = entries }, snapshot.Rtc!);
        if (currentLoaderUnavailable)
            evidence = evidence with { Bcd = evidence.Bcd with
            { CurrentLoaderId = Observations.Failure<Guid>(ObservationAvailability.Unavailable, "test-current-loader") } };
        var result = CommunityBootRegistrationPlan.Create(evidence, NewId);
        Assert.Equal(ObservationAvailability.Available, result.Availability);
        if (baselineProjectionCount > 0)
        {
            var projection = Projection(result.Value);
            var graph = Add(evidence.Bcd, projection);
            if (baselineProjectionCount > 1) graph = Add(graph, projection with { Id = OtherId });
            graph = SetList(graph, 0x24000001, Order(evidence.Bcd).Add(ProjectionId));
            var entry = EntryWrite(result.Value);
            var raw = new FirmwareVariableV1(Observations.Available(entry.Bytes), 0, Observations.Available(7u));
            evidence = evidence with
            {
                Bcd = graph,
                Firmware = evidence.Firmware with { BootEntries = evidence.Firmware.BootEntries
                    .Select(e => e.Index == result.Value.NewBootEntry
                        ? EfiRecoveryParser.ParseBootEntry(e.Index, raw) : e).ToImmutableArray() },
            };
            result = CommunityBootRegistrationPlan.Create(evidence, NewId);
            Assert.Equal(ObservationAvailability.Available, result.Availability);
        }
        return result.Value;
    }

    private static BcdObjectSnapshot Projection(CommunityBootRegistrationPlan plan) =>
        new(ProjectionId, 0x101fffff, Observations.Available<ImmutableArray<BcdElementSnapshot>>([
            Element(0x11000001, new BcdDeviceElementValue(new BcdQualifiedGptPartitionDevice(
                plan.Target.Disk.GptDiskGuid, plan.Target.PartitionGuid))),
            Element(0x12000002, new BcdStringValue(plan.PlannedLoaderPath)),
            Element(0x12000004, new BcdStringValue(CommunityBootRegistrationPlan.Description)),
        ]));
    private static BootRegistrationMutation EntryWrite(CommunityBootRegistrationPlan plan) =>
        Write(plan, $"Boot{plan.NewBootEntry:X4}");
    private static BootRegistrationMutation Write(CommunityBootRegistrationPlan plan, string slot) =>
        plan.Mutations.Single(m => m.Kind == BootRegistrationMutationKind.Firmware && m.Slot == slot);
    private static BcdElementSnapshot Element(uint type, BcdElementValue value) => new(type, Observations.Available(value));
    private static BcdObjectSnapshot Replace(BcdObjectSnapshot obj, BcdElementSnapshot element) =>
        obj with { Elements = Observations.Available(obj.Elements.Value
            .Where(e => e.Type != element.Type).Append(element).ToImmutableArray()) };
    private static BcdRecoveryGraphV1 Add(BcdRecoveryGraphV1 graph, BcdObjectSnapshot obj) =>
        graph with { Objects = Observations.Available(graph.Objects.Value.Add(obj)) };
    private static BcdRecoveryGraphV1 ReplaceObject(BcdRecoveryGraphV1 graph, BcdObjectSnapshot obj) =>
        graph with { Objects = Observations.Available(graph.Objects.Value.Select(o => o.Id == obj.Id ? obj : o).ToImmutableArray()) };
    private static ImmutableArray<Guid> Order(BcdRecoveryGraphV1 graph) =>
        Assert.IsType<BcdObjectListValue>(graph.Objects.Value
            .Single(o => o.Id == BcdRecoveryGraphV1.FirmwareBootManagerId)
            .Elements.Value.Single(e => e.Type == 0x24000001).Value.Value).ObjectIds;
    private static BcdRecoveryGraphV1 SetList(BcdRecoveryGraphV1 graph, uint type, ImmutableArray<Guid> ids) =>
        ReplaceObject(graph, Replace(graph.Objects.Value.Single(o => o.Id == BcdRecoveryGraphV1.FirmwareBootManagerId),
            Element(type, new BcdObjectListValue(ids))));
}
