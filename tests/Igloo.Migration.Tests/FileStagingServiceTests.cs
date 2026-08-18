using FluentAssertions;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class FileStagingServiceTests : IDisposable
{
    private readonly string _distroId = "igloo-test-" + Guid.NewGuid().ToString("N");
    private readonly string _sourceRoot = Path.Join(
        Path.GetTempPath(), "igloo-staging-src-" + Guid.NewGuid().ToString("N"));

    private string StagingRoot => Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Igloo", "staging", _distroId);

    public void Dispose()
    {
        foreach (var dir in new[] { _sourceRoot, StagingRoot })
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private string CreateSourceFolder(string name, params (string RelPath, string Content)[] files)
    {
        var folder = Path.Join(_sourceRoot, name);
        foreach (var (relPath, content) in files)
        {
            var path = Path.Join(folder, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static FileStagingService Service() => new(NullLogger<FileStagingService>.Instance);

    [Fact]
    public async Task Stages_files_under_files_folder_preserving_relative_structure()
    {
        var docs = CreateSourceFolder("Documents",
            ("a.txt", "alpha"),
            (Path.Join("nested", "b.txt"), "beta"));

        var result = await Service().StageAsync(
            new FileStagingRequest(_distroId, [docs]), progress: null);

        result.StagingDirectory.Should().Be(StagingRoot);
        result.FileCount.Should().Be(2);
        (await File.ReadAllTextAsync(Path.Join(StagingRoot, "files", "Documents", "a.txt")))
            .Should().Be("alpha");
        (await File.ReadAllTextAsync(Path.Join(StagingRoot, "files", "Documents", "nested", "b.txt")))
            .Should().Be("beta");
    }

    [Fact]
    public async Task Reports_bytes_actually_copied()
    {
        var docs = CreateSourceFolder("Documents", ("a.txt", "12345"));

        var result = await Service().StageAsync(
            new FileStagingRequest(_distroId, [docs]), progress: null);

        result.TotalBytesCopied.Should().Be(5);
    }

    [Fact]
    public async Task Nonexistent_source_folders_are_skipped_not_fatal()
    {
        var docs = CreateSourceFolder("Documents", ("a.txt", "x"));
        var missing = Path.Join(_sourceRoot, "DoesNotExist");

        var result = await Service().StageAsync(
            new FileStagingRequest(_distroId, [missing, docs]), progress: null);

        result.FileCount.Should().Be(1);
    }

    [Fact]
    public async Task Previous_staging_run_is_wiped_before_staging()
    {
        var leftover = Path.Join(StagingRoot, "files", "Old");
        Directory.CreateDirectory(leftover);
        await File.WriteAllTextAsync(Path.Join(leftover, "stale.txt"), "old");

        var docs = CreateSourceFolder("Documents", ("a.txt", "x"));
        await Service().StageAsync(new FileStagingRequest(_distroId, [docs]), progress: null);

        Directory.Exists(leftover).Should().BeFalse("a fresh run must not mix with stale files");
    }

    [Fact]
    public async Task Empty_folder_list_yields_an_empty_but_valid_staging_root()
    {
        var result = await Service().StageAsync(
            new FileStagingRequest(_distroId, []), progress: null);

        result.FileCount.Should().Be(0);
        result.TotalBytesCopied.Should().Be(0);
        Directory.Exists(StagingRoot).Should().BeTrue();
    }
}
