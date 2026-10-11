using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Distro.Debian.Deployment;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class DebianAgentStageReadbackTests
{
    private static readonly string[] Actions = ["CreatePrivateSupervisor", "AcquireLeases", "PrepareImportMountpoints",
        "MountRoot", "MountPayload", "StageOfflineAgent", "UnmountPayload", "UnmountRoot", "Close"];

    private static ImmutableArray<JsonElement> Records(IEnumerable<(string Action, string State)> values) =>
        values.Select(value => JsonSerializer.SerializeToElement(new { value.Action, value.State })).ToImmutableArray();

    [Fact]
    public void CompleteStageAndExactCloseActionOrderRequired()
    {
        var complete = Records(Actions.SelectMany(action => new[] {
            (action, "IntentDurable"), (action, "AppliedAndVerified") }));
        DebianVerifiedAgentStage.VerifySessionActions(complete);
        Assert.Throws<InvalidDataException>(() => DebianVerifiedAgentStage.VerifySessionActions(complete.RemoveAt(complete.Length - 1)));
        Assert.Throws<InvalidDataException>(() => DebianVerifiedAgentStage.VerifySessionActions(
            complete.SetItem(11, JsonSerializer.SerializeToElement(new { Action = "StageOfflineAgent", State = "OutcomeUnknown" }))));
        Assert.Throws<InvalidDataException>(() => DebianVerifiedAgentStage.VerifySessionActions(
            complete.SetItem(15, JsonSerializer.SerializeToElement(new { Action = "UnmountPayload", State = "AppliedAndVerified" }))));
    }
}
