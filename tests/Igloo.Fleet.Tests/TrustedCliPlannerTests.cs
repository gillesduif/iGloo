using System.Text.Json;
using Igloo.Command.Contracts;
using Igloo.Fleet.Agent;
using Igloo.Fleet.Contracts;
using Xunit;

namespace Igloo.Fleet.Tests;

public sealed class TrustedCliPlannerTests
{
    private static readonly string MachineRevision = new('A', 64);
    private static readonly string BoundRevision = new('B', 64);
    private static readonly string ProviderRevision = new('C', 64);

    [Fact]
    public async Task TrustedDryRunUsesOnlyTypedCliQueriesAndKeepsFleetEvidenceSanitized()
    {
        using var fixture = new PlanningFixture();
        var identity = PlanningTests.Device(fixture);
        var (work, _) = PlanningTests.Work(fixture, identity);
        var invoked = new List<(string Capability, int Version)>();
        var planner = new ReadOnlyPlanner((request, _) =>
        {
            invoked.Add((request.CapabilityId, request.CapabilityVersion));
            return Task.FromResult(Reply(request));
        });

        var result = await planner.RunAsync(work);

        Assert.Equal(new[] { ("system.inspect", 1), ("migration.plan", 2),
            ("distro.compatibility", 1) }, invoked);
        Assert.Equal(AssessmentOutcome.Assessed, result.Assessment.Outcome);
        Assert.Equal(work.WorkItemId, result.Assessment.AssessmentId);
        Assert.True(result.Planning!.TargetSupported);
        Assert.Equal(ProviderRevision, result.Planning.PluginHash);
        Assert.Equal(20L << 30, result.Planning.RequiredBytes);
        Assert.Equal(100L << 30, result.Planning.AvailableBytes);
        Assert.Empty(result.Planning.Reasons);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        var stored = fixture.Service.Submit(identity, result);
        Assert.Equal(PlanningEligibility.Eligible, stored.Decision.Status);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("compatibility")]
    public async Task StateChangingBetweenCliQueriesFailsClosed(string stage)
    {
        using var fixture = new PlanningFixture();
        var identity = PlanningTests.Device(fixture);
        var (work, _) = PlanningTests.Work(fixture, identity);
        var planner = new ReadOnlyPlanner((request, _) => Task.FromResult(
            Reply(request, stage == "plan" && request.CapabilityId == "migration.plan"
                ? new string('D', 64) : stage == "compatibility" &&
                    request.CapabilityId == "distro.compatibility" ? new string('D', 64) : null)));

        var result = await planner.RunAsync(work);

        Assert.Equal(AssessmentOutcome.LocalExecutionFailed, result.Assessment.Outcome);
        Assert.Null(result.Planning);
        Assert.Equal(PlanningEligibility.Unknown,
            fixture.Service.Submit(identity, result).Decision.Status);
    }

    private static CommandResult Reply(CommandRequest request, string? changedMachineRevision = null)
    {
        var machineRevision = changedMachineRevision ?? MachineRevision;
        object data = request.CapabilityId switch
        {
            "system.inspect" => new SystemInspection(true, true, true, "NotEncrypted",
                8L << 30, 1, []),
            "migration.plan" => new MigrationPlanResultV2(Guid.NewGuid(), "debian",
                20L << 30, 100L << 30, MigrationCapacityDisposition.KnownAvailable,
                [], BoundRevision, ProviderRevision, false, "AdvisoryOnly", machineRevision),
            "distro.compatibility" => new DistroCompatibility("debian", [], false),
            _ => throw new InvalidOperationException("Unexpected capability"),
        };
        return new(CommandProtocol.Version, request.CapabilityId, request.CapabilityVersion,
            request.RequestId, request.CorrelationId, null, true,
            JsonSerializer.SerializeToElement(data, CommandProtocol.JsonOptions), null, [], [],
            request.CapabilityId == "migration.plan" ? BoundRevision : machineRevision);
    }
}
