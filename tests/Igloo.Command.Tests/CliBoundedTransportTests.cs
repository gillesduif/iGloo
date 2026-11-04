using System.Text.Json;
using System.Diagnostics;
using System.Text;
using Igloo.Cli;
using Igloo.Cli.Client;
using Igloo.Command.Contracts;
using Xunit;

namespace Igloo.Command.Tests;

public sealed class CliBoundedTransportTests
{
    [Fact]
    public async Task ReaderRejectsOversizedLineBeforeAllocatingTheRemainder()
    {
        using var source = new ChunkReader(new string('x',
            BoundedMachineLineReader.MaxLineBytes + 1), 4096);
        var reader = new BoundedMachineLineReader(source);
        var failure = await Assert.ThrowsAsync<CliTransportException>(() =>
            reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal(CliTransportError.MalformedStream, failure.Code);
        Assert.InRange(source.LargestRequestedBuffer, 1, 4096);
    }

    [Fact]
    public async Task ReaderCountsUtf8WireBytesRatherThanOnlyCharacters()
    {
        using var source = new ChunkReader(new string('é',
            BoundedMachineLineReader.MaxLineBytes / 2 + 1), 4096);
        var failure = await Assert.ThrowsAsync<CliTransportException>(() =>
            new BoundedMachineLineReader(source).ReadLineAsync(CancellationToken.None));
        Assert.Equal(CliTransportError.MalformedStream, failure.Code);
    }

    [Fact]
    public async Task ReadersRejectSeparatedSurrogatePair()
    {
        const string invalid = "\uD800A\uDC00\n";
        var client = await Assert.ThrowsAsync<CliTransportException>(() =>
            new BoundedMachineLineReader(new StringReader(invalid))
                .ReadLineAsync(CancellationToken.None));
        Assert.Equal(CliTransportError.MalformedStream, client.Code);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new MachineInputLines(new StringReader(invalid))
                .ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReaderPreservesFragmentedCrLfAndCoalescedLines()
    {
        foreach (var chunkSize in new[] { 1, 4096 })
        {
            using var source = new ChunkReader("first\r\nsecond\nthird\rfourth\r\n",
                chunkSize);
            var reader = new BoundedMachineLineReader(source);
            Assert.Equal("first", await reader.ReadLineAsync(CancellationToken.None));
            Assert.Equal("second", await reader.ReadLineAsync(CancellationToken.None));
            Assert.Equal("third", await reader.ReadLineAsync(CancellationToken.None));
            Assert.Equal("fourth", await reader.ReadLineAsync(CancellationToken.None));
            Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task MachineReaderRequiresPromptClosureAfterTheTerminalResult()
    {
        var request = CliCommandClient.Request("system.version", new { });
        var hello = new CommandStreamEvent("hello", CommandProtocol.Version,
            Guid.Empty, Guid.Empty, "Igloo.Cli", "1.0.0.0");
        var accepted = new CommandStreamEvent("accepted", CommandProtocol.Version,
            request.RequestId, request.CorrelationId);
        var operationId = Guid.NewGuid();
        var result = new CommandResult(CommandProtocol.Version, request.CapabilityId,
            request.CapabilityVersion, request.RequestId, request.CorrelationId,
            operationId, true, JsonSerializer.SerializeToElement(new { version = 1 }), null, [], []);
        var terminal = new CommandStreamEvent("result", CommandProtocol.Version,
            request.RequestId, request.CorrelationId, Result: result);
        var frames = string.Join("\r\n", new[] { hello, accepted, terminal }
            .Select(frame => JsonSerializer.Serialize(frame, CommandProtocol.JsonOptions))) + "\r\n";

        using var source = new BlockingAfterContentReader(frames);
        var correlation = new CliCommandClient.OperationCorrelation(request.OperationId);
        var failure = await Assert.ThrowsAsync<CliTransportException>(() =>
            CliCommandClient.ReadStreamAsync(source, request, null,
                terminalGrace: TimeSpan.FromMilliseconds(40), correlation: correlation));
        Assert.Equal(CliTransportError.ProcessFailed, failure.Code);
        Assert.Equal(operationId, correlation.KnownOperationId);
        Assert.Equal(operationId, CliCommandClient.BindTransportFailure(failure,
            request, correlation.KnownOperationId).OperationId);
    }

    [Fact]
    public async Task ProgressIdentitySurvivesIncompleteStreamAndRejectsContradictions()
    {
        var request = CliCommandClient.Request("fixture.mutation.fail", new { });
        var operationId = Guid.NewGuid();
        var hello = new CommandStreamEvent("hello", CommandProtocol.Version,
            Guid.Empty, Guid.Empty, "Igloo.Cli", "1.0.0.0");
        var accepted = new CommandStreamEvent("accepted", CommandProtocol.Version,
            request.RequestId, request.CorrelationId);
        CommandStreamEvent Progress(Guid? id) => new("progress", CommandProtocol.Version,
            request.RequestId, request.CorrelationId,
            Progress: new CommandProgress(request.RequestId, id, "validating"));
        string Wire(params CommandStreamEvent[] events) => string.Join("\n", events.Select(item =>
            JsonSerializer.Serialize(item, CommandProtocol.JsonOptions))) + "\n";

        var correlation = new CliCommandClient.OperationCorrelation(request.OperationId);
        var incomplete = await Assert.ThrowsAsync<CliTransportException>(() =>
            CliCommandClient.ReadStreamAsync(new StringReader(Wire(hello, accepted,
                Progress(operationId))), request, null, correlation: correlation));
        Assert.Equal(CliTransportError.MalformedStream, incomplete.Code);
        Assert.Equal(operationId, correlation.KnownOperationId);
        var bound = CliCommandClient.BindTransportFailure(incomplete, request,
            correlation.KnownOperationId);
        Assert.Equal(operationId, bound.OperationId);
        Assert.Equal(request.RequestId, bound.RequestId);
        Assert.Equal(request.CorrelationId, bound.CorrelationId);

        foreach (var next in new[] { Progress(Guid.NewGuid()), Progress(null) })
        {
            var rejected = await Assert.ThrowsAsync<CliTransportException>(() =>
                CliCommandClient.ReadStreamAsync(new StringReader(Wire(hello, accepted,
                    Progress(operationId), next)), request, null));
            Assert.Equal(CliTransportError.MalformedStream, rejected.Code);
        }
        foreach (var invalid in new[]
                 {
                     Progress(operationId) with { CorrelationId = Guid.NewGuid() },
                     Progress(operationId) with
                     {
                         Progress = new CommandProgress(Guid.NewGuid(), operationId, "validating"),
                     },
                 })
        {
            var untrusted = new CliCommandClient.OperationCorrelation(request.OperationId);
            var rejected = await Assert.ThrowsAsync<CliTransportException>(() =>
                CliCommandClient.ReadStreamAsync(new StringReader(Wire(hello, accepted,
                    invalid)), request, null, correlation: untrusted));
            Assert.Equal(CliTransportError.MalformedStream, rejected.Code);
            Assert.Null(untrusted.KnownOperationId);
        }
        var foreign = new CliTransportException(CliTransportError.MalformedStream,
            "Untrusted error.", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Null(CliCommandClient.BindTransportFailure(foreign, request).OperationId);
    }

    [Fact]
    public async Task TerminalMustAgreeWithLearnedOperationAndSuccessErrorState()
    {
        var request = CliCommandClient.Request("fixture.mutation.fail", new { });
        var operationId = Guid.NewGuid();
        var hello = new CommandStreamEvent("hello", CommandProtocol.Version,
            Guid.Empty, Guid.Empty, "Igloo.Cli", "1.0.0.0");
        var accepted = new CommandStreamEvent("accepted", CommandProtocol.Version,
            request.RequestId, request.CorrelationId);
        var progress = new CommandStreamEvent("progress", CommandProtocol.Version,
            request.RequestId, request.CorrelationId,
            Progress: new CommandProgress(request.RequestId, operationId, "validating"));
        var result = new CommandResult(CommandProtocol.Version, request.CapabilityId,
            request.CapabilityVersion, request.RequestId, request.CorrelationId,
            operationId, true, JsonSerializer.SerializeToElement(new { value = 1 }), null, [], []);
        string Wire(CommandResult terminal) => string.Join("\n", new[]
        {
            hello, accepted, progress,
            new CommandStreamEvent("result", CommandProtocol.Version,
                request.RequestId, request.CorrelationId, Result: terminal),
        }.Select(item => JsonSerializer.Serialize(item, CommandProtocol.JsonOptions))) + "\n";

        foreach (var invalid in new[]
                 {
                     result with { OperationId = Guid.NewGuid() },
                     result with { Succeeded = true,
                         Error = new CommandError(CommandErrorCode.InternalFailure, "Failed.") },
                     result with { Succeeded = false, Error = null },
                 })
        {
            var rejected = await Assert.ThrowsAsync<CliTransportException>(() =>
                CliCommandClient.ReadStreamAsync(new StringReader(Wire(invalid)), request, null));
            Assert.Equal(CliTransportError.MalformedStream, rejected.Code);
        }
        var completed = await CliCommandClient.ReadStreamAsync(
            new StringReader(Wire(result)), request, null);
        Assert.Equal(operationId, completed.OperationId);
    }

    [Fact]
    public async Task NominatedOperationCanFailBeforeAdmissionButNotLoseIdentityAfterProgress()
    {
        var request = CliCommandClient.Request("iso.acquire", new { distroId = "debian" }) with
        {
            OperationId = Guid.NewGuid(),
        };
        var hello = new CommandStreamEvent("hello", CommandProtocol.Version,
            Guid.Empty, Guid.Empty, "Igloo.Cli", "1.0.0.0");
        var accepted = new CommandStreamEvent("accepted", CommandProtocol.Version,
            request.RequestId, request.CorrelationId);
        var progress = new CommandStreamEvent("progress", CommandProtocol.Version,
            request.RequestId, request.CorrelationId,
            Progress: new CommandProgress(request.RequestId, request.OperationId, "validating"));
        var preAdmission = new CommandResult(CommandProtocol.Version, request.CapabilityId,
            request.CapabilityVersion, request.RequestId, request.CorrelationId,
            null, false, null,
            new CommandError(CommandErrorCode.PreconditionFailed, "Admission failed."), [], []);
        string Wire(CommandResult result, bool includeProgress = false) =>
            string.Join("\n", new[]
            {
                hello, accepted,
            }.Concat(includeProgress ? [progress] : [])
                .Append(new CommandStreamEvent("result", CommandProtocol.Version,
                    request.RequestId, request.CorrelationId, Result: result))
                .Select(frame => JsonSerializer.Serialize(frame, CommandProtocol.JsonOptions))) + "\n";

        var observed = await CliCommandClient.ReadStreamAsync(
            new StringReader(Wire(preAdmission)), request, null);
        Assert.False(observed.Succeeded);
        Assert.Null(observed.OperationId);

        foreach (var (result, withProgress) in new[]
                 {
                     (preAdmission with { Succeeded = true, Error = null }, false),
                     (preAdmission, true),
                     (preAdmission with { OperationId = Guid.NewGuid() }, false),
                 })
        {
            var rejected = await Assert.ThrowsAsync<CliTransportException>(() =>
                CliCommandClient.ReadStreamAsync(new StringReader(Wire(result, withProgress)),
                    request, null));
            Assert.Equal(CliTransportError.MalformedStream, rejected.Code);
        }
    }

    [Fact]
    public async Task BlockedHelloHonorsCancellationWithoutWritingARequest()
    {
        using var source = new BlockingAfterContentReader(string.Empty);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CliCommandClient.ReadHelloAsync(source, cancellation.Token));
    }

    [Fact]
    public async Task DeadlineClassifiesBlockedPreRequestWorkWithoutTreatingItAsCompletion()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = await Assert.ThrowsAsync<CliTransportException>(() =>
            CliCommandClient.BeforeDeadlineAsync(blocked.Task,
                Task.Delay(Timeout.InfiniteTimeSpan), Task.CompletedTask,
                CancellationToken.None));
        Assert.Equal(CliTransportError.TimedOut, timeout.Code);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cancelled = await Assert.ThrowsAsync<CliTransportException>(() =>
            CliCommandClient.BeforeDeadlineAsync(blocked.Task,
                Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token),
                Task.Delay(Timeout.InfiniteTimeSpan), cancellation.Token));
        Assert.Equal(CliTransportError.Cancelled, cancelled.Code);
    }

    [Fact]
    public async Task BlockedRequestFlushHonorsItsDeadlineToken()
    {
        await using var blocked = new BlockedWriteStream();
        await using var writer = new StreamWriter(blocked);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CliCommandClient.WriteLineAndFlushAsync(writer, "request", cancellation.Token));
    }

    [Fact]
    public async Task BrokenRequestPipeRetainsKnownOperationIdentityAsUncertain()
    {
        var request = CliCommandClient.Request("operation.status", new { }) with
        {
            OperationId = Guid.NewGuid(),
        };
        await using var stream = new BrokenWriteStream();
        await using var writer = new StreamWriter(stream);
        var broken = await Assert.ThrowsAsync<IOException>(() =>
            CliCommandClient.WriteLineAndFlushAsync(writer, "request", CancellationToken.None));
        var failure = CliCommandClient.BindTransportFailure(broken, request);
        Assert.Equal(CliTransportError.ProcessFailed, failure.Code);
        Assert.Equal(request.RequestId, failure.RequestId);
        Assert.Equal(request.CorrelationId, failure.CorrelationId);
        Assert.Equal(request.OperationId, failure.OperationId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task BlockedMachineOutputNeverClaimsATerminalSuccess(int blockedWrite)
    {
        var request = CliCommandClient.Request("system.version", new { });
        using var output = new SelectivelyBlockedWriter(blockedWrite);
        using var error = new StringWriter();
        var input = JsonSerializer.Serialize(request, CommandProtocol.JsonOptions) + "\n";
        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CliApplication.RunAsync(["invoke", "--machine"], new SuccessDispatcher(),
                new StringReader(input), output, error,
                machineOutputTimeout: TimeSpan.FromMilliseconds(40)));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(output.CompletedLines, line => line.Contains("\"type\":\"result\"",
            StringComparison.Ordinal));
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task ProgressQueueIsBoundedWhenMachineConsumerStopsReading()
    {
        using var output = new SelectivelyBlockedWriter(1);
        var writer = new MachineFrameWriter(output, TimeSpan.FromMilliseconds(40));
        await using var disposal = writer.ConfigureAwait(false);
        var accepted = 0;
        var saturated = false;
        while (accepted < 1024)
        {
            if (!writer.TryWriteProgress("{}"))
            {
                saturated = true;
                break;
            }
            accepted++;
        }
        Assert.InRange(accepted, 1, 129);
        Assert.True(saturated);
    }

    [Fact]
    public async Task ProgressBurstDoesNotSuppressRequiredTerminalResult()
    {
        var request = CliCommandClient.Request("fixture.mutation", new { });
        using var output = new GatedLineWriter(3);
        using var error = new StringWriter();
        var dispatcher = new BurstProgressDispatcher();
        var input = JsonSerializer.Serialize(request, CommandProtocol.JsonOptions) + "\n";
        using var inputReader = new StringReader(input);

        // The finally block releases the gated writer and awaits this task before disposal.
#pragma warning disable CA2025
        var run = CliApplication.RunAsync(["invoke", "--machine"], dispatcher,
            inputReader, output, error,
            machineOutputTimeout: TimeSpan.FromSeconds(5));
#pragma warning restore CA2025
        var exitCode = -1;
        try
        {
            await dispatcher.BurstReported.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            output.Release();
            exitCode = await run;
        }

        Assert.Equal(0, exitCode);
        var wire = string.Join("\n", output.CompletedLines) + "\n";
        var result = await CliCommandClient.ReadStreamAsync(new StringReader(wire),
            request, null);
        Assert.True(result.Succeeded);
        Assert.Equal(dispatcher.OperationId, result.OperationId);
        Assert.InRange(output.CompletedLines.Count, 3, 132);
        Assert.Equal(string.Empty, error.ToString());
    }

    private sealed class ChunkReader(string content, int chunkSize) : TextReader
    {
        private int _position;
        public int LargestRequestedBuffer { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LargestRequestedBuffer = Math.Max(LargestRequestedBuffer, buffer.Length);
            var count = Math.Min(Math.Min(buffer.Length, chunkSize), content.Length - _position);
            if (count == 0) return ValueTask.FromResult(0);
            content.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class BlockingAfterContentReader(string content) : TextReader
    {
        private int _position;

        public override async ValueTask<int> ReadAsync(Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position == content.Length)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            var count = Math.Min(buffer.Length, content.Length - _position);
            content.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }

    private sealed class BlockedWriteStream : Stream
    {
        private int _writes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class BrokenWriteStream : Stream
    {
        private bool _failed;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                throw new IOException("The test pipe closed.");
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SelectivelyBlockedWriter(int blockedWrite) : TextWriter
    {
        private int _writes;
        public List<string> CompletedLines { get; } = [];
        public override Encoding Encoding => Encoding.UTF8;
        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) == blockedWrite)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            else
                CompletedLines.Add(buffer.ToString());
        }
    }

    private sealed class GatedLineWriter(int gatedWrite) : TextWriter
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;
        public List<string> CompletedLines { get; } = [];
        public override Encoding Encoding => Encoding.UTF8;

        public void Release() => _release.TrySetResult();

        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) == gatedWrite)
                await _release.Task.WaitAsync(cancellationToken);
            CompletedLines.Add(buffer.ToString());
        }
    }

    private sealed class BurstProgressDispatcher : ICommandDispatcher
    {
        private readonly TaskCompletionSource _burstReported =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task BurstReported => _burstReported.Task;
        public Guid OperationId { get; } = Guid.NewGuid();
        public IReadOnlyList<CapabilityDescriptor> Capabilities => [];

        public Task<CommandResult> ExecuteAsync(CommandRequest request,
            IProgress<CommandProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < 2048; i++)
                progress?.Report(new CommandProgress(request.RequestId,
                    OperationId, "verifying", i, 2048));
            _burstReported.TrySetResult();
            return Task.FromResult(new CommandResult(CommandProtocol.Version,
                request.CapabilityId, request.CapabilityVersion, request.RequestId,
                request.CorrelationId, OperationId, true,
                JsonSerializer.SerializeToElement(new { value = 1 }), null, [], []));
        }
    }

    private sealed class SuccessDispatcher : ICommandDispatcher
    {
        public IReadOnlyList<CapabilityDescriptor> Capabilities => [];
        public Task<CommandResult> ExecuteAsync(CommandRequest request,
            IProgress<CommandProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new CommandResult(
                CommandProtocol.Version, request.CapabilityId, request.CapabilityVersion,
                request.RequestId, request.CorrelationId, null, true,
                JsonSerializer.SerializeToElement(new { version = 1 }), null, [], []));
    }
}
