using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

// Beta Track B-00d: a provider whose first boot reads user data from the Windows
// volume gets a seed without the files/ and browser-profiles/ trees.
public sealed class SeedStagingCopyTests
{
    private const string Browser = "browser-profiles/AppData/Local/Microsoft/Edge/User Data";

    [Fact]
    public async Task Seed_without_user_data_carries_no_user_data_tree_and_binds()
    {
        using var fixture = StagedTree.Create();
        var seedFiles = fixture.Files().Where(file => !DirectInstallService.IsUserDataPath(file.RelativePath)).ToArray();

        var hashes = fixture.Service.CopyStagingArtefacts(fixture.Staging, seedFiles, fixture.Seed,
            userDataOnSeed: false, null, CancellationToken.None);
        var identity = await SeedIdentityWriter.WriteAsync(fixture.Seed, Guid.NewGuid(), Guid.NewGuid(),
            DirectInstallService.StagedAgentPaths(seedFiles), hashes);

        Assert.False(Directory.Exists(Path.Join(fixture.Seed, "files")));
        Assert.False(Directory.Exists(Path.Join(fixture.Seed, "browser-profiles")));
        Assert.True(File.Exists(Path.Join(fixture.Seed, "igloo-agent", "first-boot.sh")));
        Assert.DoesNotContain(identity.Files, file => DirectInstallService.IsUserDataPath(file.Path));
    }

    [Fact]
    public async Task Seed_with_user_data_keeps_nested_and_empty_selected_folders_and_binds_them()
    {
        using var fixture = StagedTree.Create();
        var seedFiles = fixture.Files();

        var hashes = fixture.Service.CopyStagingArtefacts(fixture.Staging, seedFiles, fixture.Seed,
            userDataOnSeed: true, null, CancellationToken.None);
        var identity = await SeedIdentityWriter.WriteAsync(fixture.Seed, Guid.NewGuid(), Guid.NewGuid(),
            DirectInstallService.StagedAgentPaths(seedFiles), hashes);

        Assert.True(Directory.Exists(Path.Join(fixture.Seed, "files", "Desktop")));
        Assert.Equal("nested", await File.ReadAllTextAsync(Path.Join(fixture.Seed, "files", "Documents", "Sub", "b.txt")));
        Assert.Contains(identity.Files, file => file.Path == "files/Documents/Sub/b.txt");
        Assert.Contains(identity.Files, file => file.Path == Browser + "/Default/Network/Cookies");
    }

    [Fact]
    public void Seed_directories_leave_out_both_user_data_trees_unless_the_seed_carries_them()
    {
        using var fixture = StagedTree.Create();

        var without = Relative(fixture, userDataOnSeed: false);
        var with = Relative(fixture, userDataOnSeed: true);

        Assert.Equal(new[] { "igloo-agent" }, without);
        Assert.Contains("files", with);
        Assert.Contains("files/Documents/Sub", with);
        Assert.Contains("files/Desktop", with);
        Assert.Contains(Browser + "/Default/Network", with);
    }

    private static string[] Relative(StagedTree fixture, bool userDataOnSeed) =>
        DirectInstallService.SeedDirectories(fixture.Staging, userDataOnSeed)
            .Select(directory => Path.GetRelativePath(fixture.Staging, directory).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();

    private sealed class StagedTree : IDisposable
    {
        public string Root { get; } = Path.Join(Path.GetTempPath(), "igloo-seed-copy-" + Guid.NewGuid().ToString("N"));
        public string Staging => Path.Join(Root, "staging");
        public string Seed => Path.Join(Root, "seed");
        public DirectInstallService Service { get; } =
            new(new UnusedResizer(), NullLogger<DirectInstallService>.Instance);

        public static StagedTree Create()
        {
            var tree = new StagedTree();
            var profile = Path.Join(tree.Staging, Browser.Replace('/', Path.DirectorySeparatorChar), "Default");
            Directory.CreateDirectory(Path.Join(tree.Staging, "igloo-agent"));
            Directory.CreateDirectory(Path.Join(tree.Staging, "files", "Documents", "Sub"));
            Directory.CreateDirectory(Path.Join(tree.Staging, "files", "Desktop"));
            Directory.CreateDirectory(Path.Join(profile, "Network"));
            Directory.CreateDirectory(tree.Seed);
            File.WriteAllText(Path.Join(tree.Staging, "migration-manifest.json"),
                "{\"files\":{\"stagingPath\":\"C:\\\\Users\\\\lab\\\\AppData\\\\Local\\\\Igloo\\\\staging\\\\debian\\\\0\"," +
                "\"folders\":[{\"name\":\"Documents\"},{\"name\":\"Desktop\"}]}," +
                "\"browsers\":[{\"engine\":\"chromium\",\"sourceRelativePath\":\"AppData/Local/Microsoft/Edge/User Data\"," +
                "\"profileStagingPath\":\"" + Browser + "\"}]}");
            File.WriteAllText(Path.Join(tree.Staging, "preseed.cfg"), "d-i fixture true");
            File.WriteAllText(Path.Join(tree.Staging, "igloo-agent", "first-boot.sh"), "#!/bin/sh\n");
            File.WriteAllText(Path.Join(tree.Staging, "files", "Documents", "a.txt"), "flat");
            File.WriteAllText(Path.Join(tree.Staging, "files", "Documents", "Sub", "b.txt"), "nested");
            File.WriteAllText(Path.Join(profile, "Bookmarks"), "bookmarks");
            File.WriteAllText(Path.Join(profile, "Network", "Cookies"), "cookies");
            return tree;
        }

        public IReadOnlyList<DirectInstallService.StagedFile> Files() =>
            DirectInstallService.InspectStagingFiles(Staging, requireFat32Fit: false);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class UnusedResizer : IPartitionResizeService
    {
        public Task<long> GetShrinkableSpaceAsync(int diskNumber, CancellationToken ct = default) =>
            throw new InvalidOperationException("The staging copy must not query the resizer.");

        public Task ShrinkAsync(int diskNumber, long linuxSizeBytes, IProgress<string>? progress = null,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("The staging copy must not shrink anything.");
    }
}
