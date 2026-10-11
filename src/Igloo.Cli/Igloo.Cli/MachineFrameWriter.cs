using System.Text;
using System.Threading.Channels;

namespace Igloo.Cli;

/// <summary>One ordered, bounded machine-output channel; a blocked consumer cannot block Core callbacks.</summary>
internal sealed class MachineFrameWriter : IAsyncDisposable
{
#pragma warning disable CA2213 // The caller owns Console.Out or its test TextWriter.
    private readonly TextWriter _output;
#pragma warning restore CA2213
    private readonly Channel<Frame> _frames = Channel.CreateBounded<Frame>(
        new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _writeTimeout;
    private readonly Task _pump;

    internal MachineFrameWriter(TextWriter output, TimeSpan? writeTimeout = null)
    {
        _output = output;
        _writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(5);
        if (_writeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(writeTimeout));
        _pump = PumpAsync();
    }

    internal bool TryWriteProgress(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) > MachineInputLines.MaxLineBytes)
            return false;
        return _frames.Writer.TryWrite(new Frame(line, null));
    }

    internal async Task WriteRequiredAsync(string line, CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(line) > MachineInputLines.MaxLineBytes)
            throw new InvalidDataException("The machine frame exceeds its size limit.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_writeTimeout);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _frames.Writer.WriteAsync(new Frame(line, completion), budget.Token)
            .ConfigureAwait(false);
        await completion.Task.WaitAsync(budget.Token).ConfigureAwait(false);
    }

    internal async Task CompleteAsync()
    {
        _frames.Writer.TryComplete();
        await _pump.WaitAsync(_writeTimeout).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(_lifetime.Token)
                               .ConfigureAwait(false))
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                budget.CancelAfter(_writeTimeout);
                await _output.WriteLineAsync(frame.Line.AsMemory(), budget.Token)
                    .WaitAsync(budget.Token).ConfigureAwait(false);
                await _output.FlushAsync(budget.Token).WaitAsync(budget.Token)
                    .ConfigureAwait(false);
                frame.Completion?.TrySetResult();
            }
        }
        catch (Exception ex)
        {
            _frames.Writer.TryComplete(ex);
            while (_frames.Reader.TryRead(out var pending))
                pending.Completion?.TrySetException(ex);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _frames.Writer.TryComplete();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _pump.WaitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or
            IOException or ChannelClosedException) { }
        _lifetime.Dispose();
    }

    private sealed record Frame(string Line, TaskCompletionSource? Completion);
}
