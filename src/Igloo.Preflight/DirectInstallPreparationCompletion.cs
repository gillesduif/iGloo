using Igloo.Core.Recovery;

namespace Igloo.Preflight;

internal sealed record CompletedDirectInstallPreparation(
    int DiskNumber, uint PartitionNumber, char DriveLetter, CanonicalVolumeIdentityV1 Target);

// Work-in-progress seed identity is not evidence that copying finished. Only
// the accepted preparation operation can publish completion. A second operation
// is rejected while one is active, including during boot registration.
internal sealed class DirectInstallPreparationCompletion
{
    private readonly object _sync = new();
    private bool _operationActive;
    private CompletedDirectInstallPreparation? _completed;

    internal async Task PrepareAsync(Action reset, Action prepare,
        Func<CompletedDirectInstallPreparation?> capture, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_operationActive)
                throw new InvalidOperationException("An installation operation is already running.");
            _operationActive = true;
            _completed = null;
        }
        try
        {
            // Reset before scheduling: a pre-cancelled token must also invalidate
            // a previous successful preparation whose worker will never run.
            reset();
            await Task.Run(prepare, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var completed = capture() ?? throw new InvalidOperationException(
                "Preparation finished without an exact installer target.");
            lock (_sync) _completed = completed;
        }
        catch
        {
            reset();
            throw;
        }
        finally
        {
            lock (_sync) _operationActive = false;
        }
    }

    internal async Task RegisterAsync(Func<CompletedDirectInstallPreparation?> capture,
        Action<CompletedDirectInstallPreparation> register, CancellationToken ct = default)
    {
        CompletedDirectInstallPreparation completed;
        lock (_sync)
        {
            if (_operationActive || _completed is null)
                throw new InvalidOperationException("PrepareAsync must complete successfully before RegisterBootEntryAsync.");
            if (capture() != _completed)
            {
                _completed = null;
                throw new InvalidOperationException("The completed installer target changed; prepare the installation again.");
            }
            completed = _completed;
            _operationActive = true;
        }
        // Reserve the exact completion before queuing work. A request made while
        // preparation is active must not become valid by waiting in Task.Run.
        try { await Task.Run(() => register(completed), ct).ConfigureAwait(false); }
        finally { lock (_sync) _operationActive = false; }
    }
}
