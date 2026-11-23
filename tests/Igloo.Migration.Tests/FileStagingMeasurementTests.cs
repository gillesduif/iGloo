using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class FileStagingMeasurementTests : IDisposable
{
    private readonly string _distroId = "measure-" + Guid.NewGuid().ToString("N");
    private readonly string _sourceRoot = Path.Join(Path.GetTempPath(),
        "igloo-measure-" + Guid.NewGuid().ToString("N"));
    private readonly string _browserName = "igloo-measure-browser-" + Guid.NewGuid().ToString("N");
    private string BrowserRoot => Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _browserName);
    private string StagingRoot => Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Igloo", "staging", _distroId);

    [Fact]
    public async Task MeasurementUsesTheSameSourceScanWithoutCreatingAStagingGeneration()
    {
        Write(_sourceRoot, "document.txt", new byte[123]);
        Write(_sourceRoot, Path.Join("nested", "photo.bin"), new byte[456]);
        var request = new FileStagingRequest(_distroId, [_sourceRoot])
        { FolderNames = ["Documents"] };

        var measured = await FileStagingService.MeasureSelectedBytesAsync(request);

        Assert.Equal(579, measured);
        Assert.False(Directory.Exists(StagingRoot));
        var staged = await new FileStagingService(NullLogger<FileStagingService>.Instance)
            .StageAsync(request, null);
        Assert.Equal(measured, staged.TotalBytesCopied);
    }

    [Fact]
    public async Task BrowserMeasurementRetainsWriterInventoryAndDuplicateRootHandling()
    {
        Write(BrowserRoot, "Preferences", new byte[137]);
        Write(BrowserRoot, Path.Join("Cache", "nested.bin"), new byte[1000]);
        var request = new FileStagingRequest(_distroId, [])
        { BrowserRelativePaths = [_browserName, _browserName] };

        var measured = await FileStagingService.MeasureSelectedBytesAsync(request);

        Assert.Equal(1137, measured);
        Assert.False(Directory.Exists(StagingRoot));
        var staged = await new FileStagingService(NullLogger<FileStagingService>.Instance)
            .StageAsync(request, null);
        Assert.Equal(measured, staged.TotalBytesCopied);
    }

    [Fact]
    public async Task UnavailableSelectedFolderFailsWithoutCreatingAStagingGeneration()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            FileStagingService.MeasureSelectedBytesAsync(
                new FileStagingRequest(_distroId, [_sourceRoot])));
        Assert.False(Directory.Exists(StagingRoot));
    }

    [Fact]
    public async Task AFileTooLargeForTheInstallerPartitionIsRefusedByName()
    {
        Write(_sourceRoot, "small.txt", new byte[10]);
        var large = Path.Join(_sourceRoot, "Videos", "holiday.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(large)!);
        // Length only: no byte is written, and the measurement reads sizes, not content.
        await using (var stream = new FileStream(large, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(4L * 1024 * 1024 * 1024);

        var refused = await Assert.ThrowsAsync<InstallationRefusedException>(() =>
            FileStagingService.MeasureSelectedBytesAsync(new FileStagingRequest(_distroId, [_sourceRoot])
            { FolderNames = ["Documents"] }));

        Assert.StartsWith($"'{large}' is 4.0 GB.", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Exclude the file and try again", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(StagingRoot));
    }

    [Fact]
    public void RunningBrowsersAreFoundByTheirProcessNameOnly()
    {
        var asked = new List<string>();

        var running = RunningBrowsers.Find(["Microsoft Edge", "microsoft edge", "Google Chrome", "Unlisted Browser"],
            process => { asked.Add(process); return process == "msedge"; });

        Assert.Equal(["Microsoft Edge"], running);
        Assert.Equal(["msedge", "chrome"], asked);
        Assert.Empty(RunningBrowsers.Find([], _ => throw new InvalidOperationException("No process may be read.")));
    }

    [Fact]
    public void Only_a_browser_without_any_window_is_ended()
    {
        var asked = new List<string>();
        BrowserProcess Fixture(string tag, bool window, bool ends = true) =>
            new(window, () => { asked.Add(tag); return ends; });
        var processes = new Dictionary<string, BrowserProcess[]>
        {
            ["msedge"] = [Fixture("edge-1", window: false), Fixture("edge-2", window: false)],
            ["chrome"] = [Fixture("chrome-1", window: false), Fixture("chrome-window", window: true)],
            ["brave"] = [Fixture("brave-refuses", window: false, ends: false), Fixture("brave-2", window: false)],
        };

        var waitedFor = new List<string>();
        var ended = RunningBrowsers.EndWindowless(
            ["Microsoft Edge", "microsoft edge", "Google Chrome", "Brave", "Mozilla Firefox", "Unlisted Browser"],
            name => processes.TryGetValue(name, out var found) ? found : [],
            names => { waitedFor.AddRange(names); return true; });

        Assert.Equal(["Microsoft Edge"], ended);
        Assert.Equal(["edge-1", "edge-2", "brave-refuses", "brave-2"], asked);
        // Only a browser whose processes all ended is waited for.
        Assert.Equal(["msedge"], waitedFor);
    }

    [Fact]
    public void A_browser_counts_as_in_use_when_any_of_its_processes_has_a_window()
    {
        var processes = new Dictionary<string, BrowserProcess[]>
        {
            ["msedge"] = [new(false, () => true), new(false, () => true)],
            ["chrome"] = [new(false, () => true), new(true, () => true)],
        };

        var inUse = RunningBrowsers.WithWindows(
            ["Microsoft Edge", "Google Chrome", "google chrome", "Mozilla Firefox", "Unlisted Browser"],
            name => processes.TryGetValue(name, out var found) ? found : []);

        Assert.Equal(["Google Chrome"], inUse);
    }

    [Fact]
    public void A_browser_that_is_still_closing_is_not_reported_as_ended()
    {
        BrowserProcess[] edge = [new(false, () => true)];

        var ended = RunningBrowsers.EndWindowless(["Microsoft Edge"], _ => edge, _ => false);

        Assert.Empty(ended);
    }

    [Fact]
    public async Task CanceledMeasurementDoesNotCreateAStagingGeneration()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileStagingService.MeasureSelectedBytesAsync(
                new FileStagingRequest(_distroId, []), cancellation.Token));
        Assert.False(Directory.Exists(StagingRoot));
    }

    private static void Write(string root, string relative, byte[] bytes)
    {
        var path = Path.Join(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _sourceRoot, BrowserRoot, StagingRoot })
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
