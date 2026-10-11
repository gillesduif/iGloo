using System.Text.Json;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Igloo.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

// An isolated process fixture for the real CLI client reader. It does not
// register capabilities in the product executable or perform native effects.
await WriteAsync(new CommandStreamEvent("hello", CommandProtocol.Version,
    Guid.Empty, Guid.Empty, "Igloo.Cli", "test-fixture"));
var line = await Console.In.ReadLineAsync();
if (line is null) return 2;
var request = JsonSerializer.Deserialize<CommandRequest>(line, CommandProtocol.JsonOptions);
if (request is null) return 2;
await WriteAsync(new CommandStreamEvent("accepted", CommandProtocol.Version,
    request.RequestId, request.CorrelationId));

CommandResult result;
if (request.CapabilityId == "fixture.unexpected-handler")
{
    var dispatcher = new CommandDispatcher(new UnusedChecker(),
        new DistroLoader(NullLogger<DistroLoader>.Instance),
        new DistroRegistry(NullLogger<DistroRegistry>.Instance),
        [new UnexpectedMutationHandler()]);
    result = await dispatcher.ExecuteAsync(request, new ImmediateProgress(request));
}
else
{
    // Deliberately contradictory terminal frames. The real client must reject
    // both even though the envelope and process handshake are otherwise valid.
    var success = request.CapabilityId == "fixture.inconsistent-success";
    result = new CommandResult(CommandProtocol.Version, request.CapabilityId,
        request.CapabilityVersion, request.RequestId, request.CorrelationId,
        request.OperationId, success, null,
        success ? new(CommandErrorCode.InternalFailure, "fixture contradiction") : null,
        [], []);
}
await WriteAsync(new CommandStreamEvent("result", CommandProtocol.Version,
    request.RequestId, request.CorrelationId, Result: result));
return result.Succeeded ? 0 : 1;

static async Task WriteAsync(CommandStreamEvent frame)
{
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(frame, CommandProtocol.JsonOptions));
    await Console.Out.FlushAsync();
}

sealed class ImmediateProgress(CommandRequest request) : IProgress<CommandProgress>
{
    public void Report(CommandProgress value)
    {
        var frame = new CommandStreamEvent("progress", CommandProtocol.Version,
            request.RequestId, request.CorrelationId, Progress: value);
        Console.Out.WriteLine(JsonSerializer.Serialize(frame, CommandProtocol.JsonOptions));
        Console.Out.Flush();
    }
}

sealed class UnexpectedMutationHandler : ICommandHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("fixture.unexpected-handler",
        1, 1, 1, CommandKind.Mutation, RiskClass.Low, false,
        AuthorityRequirement.User, []);

    public Task<object> ExecuteAsync(CommandRequest request,
        IProgress<CommandProgress>? progress, CancellationToken cancellationToken) =>
        Task.FromException<object>(new FormatException("PRIVATE_EXCEPTION"));
}

sealed class UnusedChecker : IPreflightChecker
{
    public Task<PreflightReport> RunAsync(CancellationToken ct = default) =>
        Task.FromException<PreflightReport>(new InvalidOperationException(
            "This fixture must not inspect a live endpoint."));
}
