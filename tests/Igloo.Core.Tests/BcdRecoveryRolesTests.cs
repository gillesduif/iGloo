using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;
using Xunit;

namespace Igloo.Core.Tests;

public sealed class BcdRecoveryRolesTests
{
    private static readonly Guid Loader = new("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly Guid Recovery = new("bbbbbbbb-1111-2222-3333-444444444444");
    private static readonly Guid Resume = new("cccccccc-1111-2222-3333-444444444444");
    private static readonly Guid Options = new("dddddddd-1111-2222-3333-444444444444");
    private static readonly Guid Inherited = new("eeeeeeee-1111-2222-3333-444444444444");
    private static readonly Guid Unrelated = new("ffffffff-1111-2222-3333-444444444444");

    [Fact]
    public void CorrectRolesIncludeResumeNestedDeviceOptionsAndInheritedSettings() =>
        Assert.Empty(Validate(Fixture()));

    [Theory]
    [InlineData(BcdRecoveryObjectRole.FirmwareBootManager)]
    [InlineData(BcdRecoveryObjectRole.WindowsBootManager)]
    [InlineData(BcdRecoveryObjectRole.WindowsLoader)]
    [InlineData(BcdRecoveryObjectRole.WindowsResume)]
    [InlineData(BcdRecoveryObjectRole.DeviceOptions)]
    [InlineData(BcdRecoveryObjectRole.InheritedSettings)]
    public void WrongRoleIsAmbiguous(BcdRecoveryObjectRole role)
    {
        var graph = Fixture();
        var id = role switch
        {
            BcdRecoveryObjectRole.FirmwareBootManager => BcdRecoveryGraphV1.FirmwareBootManagerId,
            BcdRecoveryObjectRole.WindowsBootManager => BcdRecoveryGraphV1.WindowsBootManagerId,
            BcdRecoveryObjectRole.WindowsLoader => Loader,
            BcdRecoveryObjectRole.WindowsResume => Resume,
            BcdRecoveryObjectRole.DeviceOptions => Options,
            _ => Inherited,
        };
        graph = ReplaceType(graph, id, role == BcdRecoveryObjectRole.WindowsBootManager ?
            BcdRecoveryRoles.WindowsLoaderType : BcdRecoveryRoles.WindowsBootManagerType);
        Assert.Contains(Validate(graph), i => i.ObjectId == id && i.Role == role && i.Availability == ObservationAvailability.Ambiguous);
    }

    [Fact]
    public void ConfiguredWinReIdentifierMustNameAnOsLoader()
    {
        var graph = ReplaceType(Fixture(), Recovery, BcdRecoveryRoles.WindowsResumeType);
        Assert.Contains(Validate(graph), i => i.ObjectId == Recovery && i.Role == BcdRecoveryObjectRole.WindowsLoader);
    }

    [Fact]
    public void DefaultObjectInBootManagerIsNotInterpretedAsResumeObject()
    {
        var graph = Fixture();
        Assert.Empty(Validate(graph)); // Both default and resume use element type 0x23000003.
    }

    [Fact]
    public void UnknownUnrelatedObjectDoesNotAddAnExactnessRequirement()
    {
        var graph = Fixture();
        Assert.Empty(Validate(graph));
        Assert.Contains(Validate(graph, includeUnrelated: true), i => i.ObjectId == Unrelated && i.Availability == ObservationAvailability.Unsupported);
    }

    [Fact]
    public void RequiredMissingRoleIsAbsentAndDuplicateRoleIsAmbiguous()
    {
        var graph = Fixture();
        var missing = graph with { Objects = A(graph.Objects.Value.Where(o => o.Id != Resume).ToImmutableArray()) };
        Assert.Contains(Validate(missing), i => i.ObjectId == Resume && i.Availability == ObservationAvailability.Absent);
        var duplicate = graph with { Objects = A(graph.Objects.Value.Add(graph.Objects.Value.Single(o => o.Id == Resume))) };
        Assert.Contains(Validate(duplicate), i => i.ObjectId == Resume && i.Availability == ObservationAvailability.Ambiguous);
    }

    [Theory]
    [InlineData(ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.Unsupported)]
    public void FailedEnumerationPreservesItsObservationState(ObservationAvailability state)
    {
        var graph = Fixture() with { Objects = Observations.Failure<ImmutableArray<BcdObjectSnapshot>>(state, "FixtureFailure") };
        Assert.Equal(state, Assert.Single(BcdRecoveryRoles.Validate(graph, [Loader], A(Recovery))).Availability);
    }

    private static ImmutableArray<BcdRecoveryRoleIssue> Validate(BcdRecoveryGraphV1 graph, bool includeUnrelated = false) =>
        BcdRecoveryRoles.Validate(graph, graph.Objects.Value.Where(o => includeUnrelated || o.Id != Unrelated).Select(o => o.Id), A(Recovery));

    private static BcdRecoveryGraphV1 ReplaceType(BcdRecoveryGraphV1 graph, Guid id, uint type) => graph with
    { Objects = A(graph.Objects.Value.Select(o => o.Id == id ? o with { ObjectType = type } : o).ToImmutableArray()) };

    private static BcdRecoveryGraphV1 Fixture() => new(1, A<ImmutableArray<BcdObjectSnapshot>>([
        Object(BcdRecoveryGraphV1.FirmwareBootManagerId, BcdRecoveryRoles.FirmwareBootManagerType),
        Object(BcdRecoveryGraphV1.WindowsBootManagerId, BcdRecoveryRoles.WindowsBootManagerType,
            Element(0x23000003, new BcdObjectValue(Loader))),
        Object(Loader, BcdRecoveryRoles.WindowsLoaderType,
            Element(0x23000003, new BcdObjectValue(Resume)), Element(0x14000006, new BcdObjectListValue([Inherited]))),
        Object(Recovery, BcdRecoveryRoles.WindowsLoaderType,
            Element(0x11000001, new BcdDeviceElementValue(new BcdFileDevice(4, @"\Recovery\WindowsRE\Winre.wim",
                new BcdQualifiedGptPartitionDevice(new("11111111-2222-3333-4444-555555555555"),
                    new("22222222-3333-4444-5555-666666666666"), Options))))),
        Object(Resume, BcdRecoveryRoles.WindowsResumeType),
        Object(Options, BcdRecoveryRoles.DeviceOptionsType),
        Object(Inherited, 0x20100000),
        Object(Unrelated, 0xf0000000),
    ]), A(Loader));

    private static BcdObjectSnapshot Object(Guid id, uint type, params BcdElementSnapshot[] elements) => new(id, type, A(elements.ToImmutableArray()));
    private static BcdElementSnapshot Element(uint type, BcdElementValue value) => new(type, A(value));
    private static Observation<T> A<T>(T value) => Observations.Available(value);
}
