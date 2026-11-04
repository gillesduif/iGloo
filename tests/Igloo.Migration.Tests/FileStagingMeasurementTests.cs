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
