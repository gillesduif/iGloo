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
    private readonly string _browserSourceRoot = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "igloo-staging-browser-" + Guid.NewGuid().ToString("N"));

    private string StagingRoot => Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Igloo", "staging", _distroId);

    public void Dispose()
    {
        foreach (var dir in new[] { _sourceRoot, _browserSourceRoot, StagingRoot }.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
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

        Path.GetDirectoryName(result.StagingDirectory).Should().Be(StagingRoot);
        result.FileCount.Should().Be(2);
        (await File.ReadAllTextAsync(Path.Join(result.StagingDirectory, "files", "Documents", "a.txt")))
            .Should().Be("alpha");
        (await File.ReadAllTextAsync(Path.Join(result.StagingDirectory, "files", "Documents", "nested", "b.txt")))
            .Should().Be("beta");
    }

    [Fact]
    public async Task Redirected_known_folder_uses_its_selected_logical_name()
    {
        var redirected = CreateSourceFolder("Custom Cloud Location", ("record.txt", "keep me"));

        var result = await Service().StageAsync(new FileStagingRequest(_distroId, [redirected])
        {
            FolderNames = ["Documents"],
        }, progress: null);

        (await File.ReadAllTextAsync(Path.Join(result.StagingDirectory, "files", "Documents", "record.txt")))
            .Should().Be("keep me");
        Directory.Exists(Path.Join(result.StagingDirectory, "files", "Custom Cloud Location"))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("other/folder")]
    [InlineData("C:drive")]
    public async Task Unsafe_logical_folder_name_cannot_escape_staging_root(string name)
    {
        var redirected = CreateSourceFolder("Cloud Location", ("record.txt", "keep me"));

        await Assert.ThrowsAsync<ArgumentException>(() => Service().StageAsync(
            new FileStagingRequest(_distroId, [redirected]) { FolderNames = [name] }, progress: null));
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
    public async Task Missing_selected_source_folder_fails_staging()
    {
        var docs = CreateSourceFolder("Documents", ("a.txt", "x"));
        var missing = Path.Join(_sourceRoot, "DoesNotExist");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Service().StageAsync(
            new FileStagingRequest(_distroId, [missing, docs]), progress: null));

        Directory.GetDirectories(StagingRoot).Should().BeEmpty();
        (await File.ReadAllTextAsync(Path.Join(docs, "a.txt"))).Should().Be("x");
    }

    [Fact]
    public async Task New_staging_run_preserves_previous_generation()
    {
        var leftover = Path.Join(StagingRoot, "files", "Old");
        Directory.CreateDirectory(leftover);
        await File.WriteAllTextAsync(Path.Join(leftover, "stale.txt"), "old");

        var docs = CreateSourceFolder("Documents", ("a.txt", "x"));
        var result = await Service().StageAsync(new FileStagingRequest(_distroId, [docs]), progress: null);

        Directory.Exists(leftover).Should().BeTrue("a new run must not erase previous user data");
        File.Exists(Path.Join(result.StagingDirectory, "files", "Documents", "a.txt")).Should().BeTrue();
        Directory.Exists(Path.Join(result.StagingDirectory, "files", "Old")).Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_after_first_copy_removes_only_new_generation()
    {
        var docs = CreateSourceFolder("Documents", ("a.txt", "alpha"), ("b.txt", "beta"));
        var previous = Path.Join(StagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(previous);
        await File.WriteAllTextAsync(Path.Join(previous, "keep.txt"), "older stage");

        using var cancellation = new CancellationTokenSource();
        var copyReports = 0;
        var firstCopyBytes = 0L;
        var progress = new InlineProgress(p =>
        {
            if (p.Phase != FileStagingPhase.Copying) return;
            copyReports++;
            if (copyReports == 2)
            {
                firstCopyBytes = p.BytesCopied;
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().StageAsync(
            new FileStagingRequest(_distroId, [docs]), progress, cancellation.Token));

        copyReports.Should().Be(2);
        firstCopyBytes.Should().BeGreaterThan(0);
        Directory.GetDirectories(StagingRoot).Should().Equal(previous);
        (await File.ReadAllTextAsync(Path.Join(previous, "keep.txt"))).Should().Be("older stage");
        (await File.ReadAllTextAsync(Path.Join(docs, "a.txt"))).Should().Be("alpha");
        (await File.ReadAllTextAsync(Path.Join(docs, "b.txt"))).Should().Be("beta");
    }

    [Fact]
    public async Task Windows_locked_orphan_reports_exact_path_and_both_failures()
    {
        if (!OperatingSystem.IsWindows()) return;

        var docs = CreateSourceFolder("Documents", ("a.txt", "alpha"));
        var previous = Path.Join(StagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(previous);
        await File.WriteAllTextAsync(Path.Join(previous, "keep.txt"), "older stage");

        FileStream? lockedFile = null;
        string? generation = null;
        try
        {
            var progress = new InlineProgress(p =>
            {
                if (p.Phase != FileStagingPhase.Copying) return;
                generation = Directory.GetDirectories(StagingRoot).Single(path => path != previous);
                lockedFile = new FileStream(Path.Join(generation, "locked.txt"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                throw new InvalidOperationException("injected staging failure");
            });

            var failure = await Assert.ThrowsAsync<IncompleteFileStagingCleanupException>(() =>
                Service().StageAsync(new FileStagingRequest(_distroId, [docs]), progress));

            failure.StagingDirectory.Should().Be(generation);
            failure.Message.Should().Contain(generation);
            failure.OriginalError.Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().Be("injected staging failure");
            failure.InnerException.Should().BeOfType<AggregateException>()
                .Which.InnerExceptions.Should().ContainInOrder(failure.OriginalError, failure.CleanupError);
            Directory.Exists(generation).Should().BeTrue();
            (await File.ReadAllTextAsync(Path.Join(previous, "keep.txt"))).Should().Be("older stage");
        }
        finally
        {
            if (lockedFile is not null)
                await lockedFile.DisposeAsync();
        }
    }

    [Fact]
    public async Task Selected_folder_containing_staging_root_fails_before_copying()
    {
        Directory.CreateDirectory(StagingRoot);
        await File.WriteAllTextAsync(Path.Join(StagingRoot, "original.txt"), "keep me");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().StageAsync(
            new FileStagingRequest(_distroId, [StagingRoot]), progress: null));

        (await File.ReadAllTextAsync(Path.Join(StagingRoot, "original.txt"))).Should().Be("keep me");
        Directory.EnumerateDirectories(StagingRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_folder_list_yields_an_empty_but_valid_staging_root()
    {
        var result = await Service().StageAsync(
            new FileStagingRequest(_distroId, []), progress: null);

        result.FileCount.Should().Be(0);
        result.TotalBytesCopied.Should().Be(0);
        Directory.Exists(result.StagingDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task Selected_empty_folder_is_carried_to_the_installer_seed()
    {
        var docs = CreateSourceFolder("Documents");

        var result = await Service().StageAsync(new FileStagingRequest(_distroId, [docs]), progress: null);

        Directory.Exists(Path.Join(result.StagingDirectory, "files", "Documents")).Should().BeTrue();
        result.FileCount.Should().Be(0);
    }

    [Fact]
    public async Task Unsafe_distro_id_cannot_escape_staging_directory()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().StageAsync(
            new FileStagingRequest("..", []), progress: null));
    }

    [Fact]
    public async Task Duplicate_source_names_cannot_overwrite_each_other()
    {
        var one = CreateSourceFolder(Path.Join("one", "Documents"), ("a.txt", "a"));
        var two = CreateSourceFolder(Path.Join("two", "Documents"), ("b.txt", "b"));

        await Assert.ThrowsAsync<ArgumentException>(() => Service().StageAsync(
            new FileStagingRequest(_distroId, [one, two]), progress: null));
    }

    [Fact]
    public async Task Source_removed_after_scan_fails_the_operation()
    {
        var docs = CreateSourceFolder("Documents", ("a.txt", "a"));
        var selectedFile = Path.Join(docs, "a.txt");
        var progress = new InlineProgress(p =>
        {
            if (p.Phase == FileStagingPhase.Copying)
                File.Delete(selectedFile);
        });

        await Assert.ThrowsAsync<FileNotFoundException>(() => Service().StageAsync(
            new FileStagingRequest(_distroId, [docs]), progress));
    }

    [Fact]
    public async Task Trailing_separator_keeps_selected_folder_name()
    {
        var docs = CreateSourceFolder("Documents", ("a.txt", "a"));

        var result = await Service().StageAsync(
            new FileStagingRequest(_distroId, [docs + Path.DirectorySeparatorChar]), progress: null);

        File.Exists(Path.Join(result.StagingDirectory, "files", "Documents", "a.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Source_growth_after_scan_fails_the_operation()
    {
        var docs = CreateSourceFolder("Documents", ("a.txt", "a"));
        var selectedFile = Path.Join(docs, "a.txt");
        var progress = new InlineProgress(p =>
        {
            if (p.Phase == FileStagingPhase.Copying)
                File.AppendAllText(selectedFile, "b");
        });

        await Assert.ThrowsAsync<IOException>(() => Service().StageAsync(
            new FileStagingRequest(_distroId, [docs]), progress));
    }

    [Fact]
    public async Task Opposing_source_size_changes_cannot_cancel_out_in_the_total()
    {
        var docs = CreateSourceFolder("Documents", ("a.txt", "a"), ("b.txt", "bb"));
        var changed = false;
        var progress = new InlineProgress(p =>
        {
            if (p.Phase != FileStagingPhase.Copying || changed) return;
            changed = true;
            File.WriteAllText(Path.Join(docs, "a.txt"), "aa");
            File.WriteAllText(Path.Join(docs, "b.txt"), "b");
        });

        await Assert.ThrowsAsync<IOException>(() => Service().StageAsync(
            new FileStagingRequest(_distroId, [docs]), progress));
    }

    [Fact]
    public async Task Selected_browser_profile_is_staged_under_seed_relative_path()
    {
        var browser = Path.Join(_browserSourceRoot, "User Data");
        Directory.CreateDirectory(Path.Join(browser, "Default"));
        await File.WriteAllTextAsync(Path.Join(browser, "Default", "Bookmarks"), "selected profile");
        var relative = Path.GetRelativePath(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), browser);

        var result = await Service().StageAsync(new FileStagingRequest(_distroId, [])
        {
            BrowserRelativePaths = [relative]
        }, progress: null);

        var staged = Path.Join(result.StagingDirectory,
            FileStagingService.BrowserProfileStagingPath(relative).Replace('/', Path.DirectorySeparatorChar),
            "Default", "Bookmarks");
        (await File.ReadAllTextAsync(staged)).Should().Be("selected profile");
        result.TotalBytesCopied.Should().Be("selected profile".Length);
    }

    [Fact]
    public async Task Selected_browser_source_containing_staging_root_fails_before_copying()
    {
        Directory.CreateDirectory(StagingRoot);
        await File.WriteAllTextAsync(Path.Join(StagingRoot, "original.txt"), "keep me");
        var relative = Path.GetRelativePath(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StagingRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().StageAsync(
            new FileStagingRequest(_distroId, []) { BrowserRelativePaths = [relative] },
            progress: null));

        (await File.ReadAllTextAsync(Path.Join(StagingRoot, "original.txt"))).Should().Be("keep me");
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:/Windows")]
    [InlineData("a//b")]
    [InlineData("")]
    public async Task Unsafe_browser_source_cannot_escape_user_profile(string relative)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().StageAsync(
            new FileStagingRequest(_distroId, []) { BrowserRelativePaths = [relative] },
            progress: null));
    }

    private sealed class InlineProgress(Action<FileStagingProgress> report) : IProgress<FileStagingProgress>
    {
        public void Report(FileStagingProgress value) => report(value);
    }
}
