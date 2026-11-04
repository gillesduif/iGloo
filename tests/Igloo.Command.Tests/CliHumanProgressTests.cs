using System.Text.Json;
using Igloo.Cli;
using Igloo.Command.Contracts;
using Xunit;

namespace Igloo.Command.Tests;

public sealed class CliHumanProgressTests
{
    [Fact]
    public async Task CachedImageVerificationReportsPhasesAndDecilesWithoutFloodingTheTerminal()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var dispatcher = new CachedImageDispatcher();
        var exit = await CliApplication.RunAsync(["iso", "acquire", "fedora-kde"],
            dispatcher, new StringReader(string.Empty), output, error);

        Assert.Equal(0, exit);
        Assert.Equal(2, dispatcher.RequestedVersion);
        var lines = error.ToString().Split(Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        Assert.InRange(lines.Length, 5, 17);
        Assert.Equal("validating", lines[0]);
        Assert.Contains("VerifyingSha256", lines);
        Assert.Contains("VerifyingSha256 100%", lines);
        Assert.Equal(11, lines.Count(line => line.StartsWith("VerifyingSha256",
            StringComparison.Ordinal)));
        Assert.Equal("Complete", lines[^1]);
        Assert.Contains(CachedImageDispatcher.ImagePath, output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(CachedImageDispatcher.Digest, output.ToString(),
            StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        exit = await CliApplication.RunAsync(["iso", "acquire", "fedora-kde", "--json"],
            dispatcher, new StringReader(string.Empty), output, error);
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error.ToString());
        var result = JsonSerializer.Deserialize<CommandResult>(output.ToString(),
            CommandProtocol.JsonOptions)!;
        Assert.Equal(2, result.CapabilityVersion);
        Assert.True(result.Succeeded);
    }

    private sealed class CachedImageDispatcher : ICommandDispatcher
    {
        internal const string ImagePath = @"C:\igloo-fixture\fedora.iso";
        internal const string Digest = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        public int RequestedVersion { get; private set; }
        public IReadOnlyList<CapabilityDescriptor> Capabilities { get; } =
        [
            new("iso.acquire", 2, 1, 2, CommandKind.Mutation, RiskClass.Low,
                false, AuthorityRequirement.User, ["fedora-kde"]),
        ];

        public Task<CommandResult> ExecuteAsync(CommandRequest request,
            IProgress<CommandProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            RequestedVersion = request.CapabilityVersion;
            progress?.Report(new(request.RequestId, null, "validating"));
            progress?.Report(new(request.RequestId, null, "ResolvingMirror"));
            progress?.Report(new(request.RequestId, null, "VerifyingSha256"));
            for (var i = 0; i <= 7_437; i++)
                progress?.Report(new(request.RequestId, null, "VerifyingSha256",
                    i, 7_437));
            progress?.Report(new(request.RequestId, null, "VerifyingGpg"));
            progress?.Report(new(request.RequestId, null, "Complete"));
            return Task.FromResult(new CommandResult(CommandProtocol.Version, request.CapabilityId,
                request.CapabilityVersion, request.RequestId, request.CorrelationId,
                Guid.NewGuid(), true,
                JsonSerializer.SerializeToElement(new IsoAcquiredV2("fedora-kde",
                    ImagePath, Digest, true, true, 1_217_000_000), CommandProtocol.JsonOptions),
                null, [], []));
        }
    }
}
