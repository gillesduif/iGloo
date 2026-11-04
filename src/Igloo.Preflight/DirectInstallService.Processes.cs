using System.ComponentModel;
using System.Diagnostics;

namespace Igloo.Preflight;

public sealed partial class DirectInstallService
{
    internal static async Task<string> RunInstallerProcessAsync(ProcessStartInfo start,
        TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ct.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ??
            throw new InvalidOperationException($"Could not start {start.FileName}.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Preserve the timeout/cancellation result even if the process exited concurrently.
            }
            // Observe both cancelled readers without waiting on an unbounded EOF.
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"{Path.GetFileName(start.FileName)} timed out after {timeout.TotalSeconds:g} seconds.");
        }
        var output = await stdout.ConfigureAwait(false);
        var errorOutput = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(start.FileName)} exited with code {process.ExitCode}.\n" +
                output + (string.IsNullOrWhiteSpace(errorOutput) ? string.Empty : "\n" + errorOutput));
        return output;
    }
}
