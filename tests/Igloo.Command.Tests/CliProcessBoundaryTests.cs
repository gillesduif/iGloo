using System.Diagnostics;
using System.Text.Json;
using Igloo.Cli;
using Igloo.Cli.Client;
using Igloo.Command.Contracts;
using Xunit;

namespace Igloo.Command.Tests;

public sealed class CliProcessBoundaryTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Igloo.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static string Executable() => CliExecutableLocator.Resolve(RepositoryRoot());

    [Fact]
    public async Task MachineModeHasOnlyJsonFramesAndOneTerminalResult()
    {
        var request = CliCommandClient.Request("system.version", new { });
        var start = new ProcessStartInfo(Executable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("invoke");
        start.ArgumentList.Add("--machine");
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, CommandProtocol.JsonOptions));
        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, stderr);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var frames = lines.Select(line => JsonSerializer.Deserialize<CommandStreamEvent>(line,
            CommandProtocol.JsonOptions)!).ToArray();
        Assert.Equal("hello", frames[0].Type);
        Assert.Equal("Igloo.Cli", frames[0].Product);
        Assert.Equal(Guid.Empty, frames[0].RequestId);
        Assert.Equal("accepted", frames[1].Type);
        Assert.Equal("result", frames[^1].Type);
        Assert.Single(frames, frame => frame.Type == "result");
        Assert.All(frames.Skip(1), frame => Assert.Equal(request.RequestId, frame.RequestId));
        Assert.True(frames[^1].Result!.Succeeded);
    }

    [Fact]
    public async Task TypedClientDiscoversCapabilitiesAndKeepsStructuredErrors()
    {
        var client = new CliCommandClient(Executable());
        var catalogue = await client.DiscoverAsync();
        Assert.Contains(catalogue, capability => capability.Id == "system.version");
        Assert.DoesNotContain(catalogue, capability => capability.Id == "migration.execute");
        var distros = await client.ExecuteAsync(CliCommandClient.Request("distro.list", new { }));
        Assert.Contains(distros.Data!.Value.Deserialize<DistroList>(CommandProtocol.JsonOptions)!.Distros,
            distro => distro.Id == "fedora-kde");

        var progress = new CapturedProgress();
        var result = await client.ExecuteAsync(CliCommandClient.Request("system.version", new { }), progress);
        Assert.True(result.Succeeded);
        Assert.Equal(CommandProtocol.Version, result.ProtocolVersion);
        Assert.Contains(progress.Events, item => item.Phase == "validating");

        var unsupported = await client.ExecuteAsync(CliCommandClient.Request("migration.execute", new { }));
        Assert.Equal(CommandErrorCode.UnsupportedCapability, unsupported.Error!.Code);
        var wrongVersion = await client.ExecuteAsync(CliCommandClient.Request("system.version", new { })
            with { CapabilityVersion = 2 });
        Assert.Equal(CommandErrorCode.UnsupportedVersion, wrongVersion.Error!.Code);
        var unknownOperation = await client.ExecuteAsync(CliCommandClient.Request("operation.status",
            new OperationSelection(Guid.NewGuid())));
        Assert.Equal(CommandErrorCode.NotSupported, unknownOperation.Error!.Code);
    }

    [Fact]
    public async Task ClientScopesDistroDirectoryToItsCliProcess()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-cli-empty-providers-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var client = new CliCommandClient(Executable(), distrosDirectory: directory);
            var result = await client.ExecuteAsync(CliCommandClient.Request("distro.list", new { }));
            Assert.True(result.Succeeded);
            Assert.Empty(result.Data!.Value.Deserialize<DistroList>(CommandProtocol.JsonOptions)!.Distros);
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task HumanAndMachineVersionUseTheSameStructuredSemantics()
    {
        var machine = await new CliCommandClient(Executable()).ExecuteAsync(
            CliCommandClient.Request("system.version", new { }));
        var start = new ProcessStartInfo(Executable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("version");
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var human = JsonSerializer.Deserialize<CommandResult>(stdout, CommandProtocol.JsonOptions)!;
        Assert.True(human.Succeeded);
        Assert.Equal(machine.CapabilityId, human.CapabilityId);
        Assert.Equal(machine.CapabilityVersion, human.CapabilityVersion);
        Assert.Equal(machine.Data!.Value.GetRawText(), human.Data!.Value.GetRawText());
        Assert.Equal(machine.Warnings, human.Warnings);
    }

    [Fact]
    public void LocatorRejectsMissingOrWrongExecutableWithoutSearchingPath()
    {
        var missing = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Igloo.Cli.exe");
        Assert.Equal(CliTransportError.ExecutableUnavailable,
            Assert.Throws<CliTransportException>(() => CliExecutableLocator.Validate(missing)).Code);
        Assert.Equal(CliTransportError.IdentityMismatch,
            Assert.Throws<CliTransportException>(() => CliExecutableLocator.Validate(
                Path.Join(RepositoryRoot(), "iGloo.exe"))).Code);
    }

    [Fact]
    public async Task MachineCancellationYieldsOneStructuredCancelledResult()
    {
        var request = CliCommandClient.Request("fixture.wait", new { });
        var input = JsonSerializer.Serialize(request, CommandProtocol.JsonOptions) + Environment.NewLine +
            JsonSerializer.Serialize(new { type = "cancel", requestId = request.RequestId },
                CommandProtocol.JsonOptions) + Environment.NewLine;
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApplication.RunAsync(["invoke", "--machine"], new WaitingDispatcher(),
            new StringReader(input), output, error);
        Assert.Equal(130, exit);
        var frames = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries).Select(line => JsonSerializer.Deserialize<CommandStreamEvent>(line,
                CommandProtocol.JsonOptions)!).ToArray();
        Assert.Equal(CommandErrorCode.Cancelled, Assert.Single(frames,
            frame => frame.Type == "result").Result!.Error!.Code);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task ClientRejectsIncompatibleOrDuplicatedMachineFrames()
    {
        var request = CliCommandClient.Request("system.version", new { });
        string Frame(CommandStreamEvent value) => JsonSerializer.Serialize(value, CommandProtocol.JsonOptions);
        var hello = new CommandStreamEvent("hello", CommandProtocol.Version,
            Guid.Empty, Guid.Empty, "Igloo.Cli", "1.0.0.0");
        var accepted = new CommandStreamEvent("accepted", CommandProtocol.Version,
            request.RequestId, request.CorrelationId);
        var outcome = new CommandResult(CommandProtocol.Version, request.CapabilityId,
            request.CapabilityVersion, request.RequestId, request.CorrelationId, null,
            true, JsonSerializer.SerializeToElement(new { protocolVersion = 1 }), null, [], []);
        var terminal = new CommandStreamEvent("result", CommandProtocol.Version,
            request.RequestId, request.CorrelationId, Result: outcome);

        var incompatible = await Assert.ThrowsAsync<CliTransportException>(() =>
            CliCommandClient.ReadStreamAsync(new StringReader(Frame(hello with { ProtocolVersion = 2 })),
                request, null));
        Assert.Equal(CliTransportError.IncompatibleProtocol, incompatible.Code);

        var duplicated = await Assert.ThrowsAsync<CliTransportException>(() =>
            CliCommandClient.ReadStreamAsync(new StringReader(string.Join(Environment.NewLine,
                [Frame(hello), Frame(accepted), Frame(terminal), Frame(terminal)])), request, null));
        Assert.Equal(CliTransportError.MalformedStream, duplicated.Code);
        var crashed = await Assert.ThrowsAsync<CliTransportException>(() =>
            CliCommandClient.ReadStreamAsync(new StringReader(string.Empty), request, null));
        Assert.Equal(CliTransportError.MalformedStream, crashed.Code);
    }

    private sealed class CapturedProgress : IProgress<CommandProgress>
    {
        public List<CommandProgress> Events { get; } = [];
        public void Report(CommandProgress value) => Events.Add(value);
    }

    private sealed class WaitingDispatcher : ICommandDispatcher
    {
        public IReadOnlyList<CapabilityDescriptor> Capabilities => [];
        public async Task<CommandResult> ExecuteAsync(CommandRequest request,
            IProgress<CommandProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
