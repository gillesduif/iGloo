using System.Diagnostics;
using Igloo.Preflight;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class DirectInstallProcessTests
{
    [Fact]
    public async Task StderrIsDrainedWhileStdoutRemainsOpen()
    {
        var output = await DirectInstallService.RunInstallerProcessAsync(
            PowerShell("[Console]::Error.Write(('x' * 200000)); [Console]::Out.Write('completed')"),
            TimeSpan.FromSeconds(15));
        Assert.Equal("completed", output);
    }

    [Fact]
    public async Task NonzeroExitIncludesTheActualFailure()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DirectInstallService.RunInstallerProcessAsync(
                PowerShell("[Console]::Error.Write('mount rejected'); exit 7"), TimeSpan.FromSeconds(15)));
        Assert.Contains("code 7", error.Message, StringComparison.Ordinal);
        Assert.Contains("mount rejected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutDoesNotWaitForOutputEof()
    {
        var timer = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DirectInstallService.RunInstallerProcessAsync(PowerShell("Start-Sleep -Seconds 30"),
                TimeSpan.FromMilliseconds(500)));
        Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), timer.Elapsed.ToString());
    }

    [Fact]
    public async Task CancellationStopsTheHelper()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DirectInstallService.RunInstallerProcessAsync(PowerShell("Start-Sleep -Seconds 30"),
                TimeSpan.FromSeconds(30), cancellation.Token));
    }

    private static ProcessStartInfo PowerShell(string script)
    {
        var start = new ProcessStartInfo("powershell.exe");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        return start;
    }
}
