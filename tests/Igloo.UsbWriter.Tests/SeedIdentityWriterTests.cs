using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Preparation;
using Xunit;

namespace Igloo.UsbWriter.Tests;

public sealed class SeedIdentityWriterTests
{
    [Fact]
    public async Task BuildBytesMatchesWriterWithoutPublishingAFile()
    {
        var root = Path.Join(Path.GetTempPath(),
            "igloo-seed-identity-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(root, "igloo-agent"));
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "migration-manifest.json"), "{}");
            await File.WriteAllTextAsync(Path.Join(root, "ks.cfg"), "user --name=fixture");
            await File.WriteAllTextAsync(Path.Join(root, "igloo-agent", "agent.py"),
                "print('bound')");
            var disk = Guid.NewGuid();
            var partition = Guid.NewGuid();
            var bytes = await SeedIdentityWriter.BuildBytesAsync(root, disk, partition,
                ["igloo-agent/agent.py"]);
            Assert.False(File.Exists(Path.Join(root, SeedIdentityWriter.FileName)));

            var identity = await SeedIdentityWriter.WriteAsync(root, disk, partition,
                ["igloo-agent/agent.py"]);
            Assert.Equal(await File.ReadAllBytesAsync(Path.Join(root, SeedIdentityWriter.FileName)),
                bytes);
            using var json = JsonDocument.Parse(bytes);
            Assert.Equal(identity.Files.Count,
                json.RootElement.GetProperty("files").GetArrayLength());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IdentityBindsTheExactCopiedInstallerInputs()
    {
        var root = Path.Join(Path.GetTempPath(), "igloo-seed-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(root, "igloo-boot"));
        try
        {
            var manifest = "{}"u8.ToArray();
            var config = new byte[] { 4, 5, 6 };
            var initrd = new byte[] { 7, 8, 9 };
            await File.WriteAllBytesAsync(Path.Join(root, "migration-manifest.json"), manifest);
            await File.WriteAllBytesAsync(Path.Join(root, "preseed.cfg"), config);
            await File.WriteAllBytesAsync(Path.Join(root, "igloo-boot", "initrd"), initrd);
            var disk = Guid.NewGuid();
            var partition = Guid.NewGuid();

            var identity = await SeedIdentityWriter.WriteAsync(root, disk, partition);
            Assert.Equal(1, identity.Version);
            Assert.Equal(disk, identity.DiskGuid);
            Assert.Equal(partition, identity.PartitionGuid);
            Assert.Equal(new[] { "migration-manifest.json", "preseed.cfg", "igloo-boot/initrd" },
                identity.Files.Select(file => file.Path));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(config)), identity.Files[1].Sha256);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Join(root, SeedIdentityWriter.FileName)));
            Assert.Equal(partition.ToString("D"),
                json.RootElement.GetProperty("partitionGuid").GetString());
            Assert.Equal(3, json.RootElement.GetProperty("files").GetArrayLength());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingInstallerConfigDoesNotPublishAnIdentity()
    {
        var root = Path.Join(Path.GetTempPath(), "igloo-seed-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "migration-manifest.json"), "{}");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SeedIdentityWriter.WriteAsync(root, Guid.NewGuid(), Guid.NewGuid()));
            Assert.False(File.Exists(Path.Join(root, SeedIdentityWriter.FileName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FedoraProviderAgentBytesAreBoundAndUnsafePathsRejected()
    {
        var root = Path.Join(Path.GetTempPath(),
            "igloo-fedora-seed-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(root, "igloo-agent"));
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "migration-manifest.json"), "{}");
            await File.WriteAllTextAsync(Path.Join(root, "ks.cfg"), "user --name=fixture");
            var agent = Path.Join(root, "igloo-agent", "agent.py");
            await File.WriteAllTextAsync(agent, "print('bound')");
            var disk = Guid.NewGuid();
            var partition = Guid.NewGuid();
            var unsafeWrite = () => SeedIdentityWriter.WriteAsync(root, disk, partition,
                additionalRelativePaths: ["../agent.py"]);
            await Assert.ThrowsAsync<InvalidDataException>(unsafeWrite);
            Assert.False(File.Exists(Path.Join(root, SeedIdentityWriter.FileName)));

            var identity = await SeedIdentityWriter.WriteAsync(root, disk, partition,
                additionalRelativePaths: ["igloo-agent/agent.py"]);
            var bound = Assert.Single(identity.Files.Where(file =>
                file.Path == "igloo-agent/agent.py"));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(
                await File.ReadAllBytesAsync(agent))), bound.Sha256);
            await UsbWriterService.VerifyFatSeedIdentityReadBackAsync(root, identity);
            await File.WriteAllTextAsync(agent, "print('tampered')");
            await Assert.ThrowsAsync<IOException>(() =>
                UsbWriterService.VerifyFatSeedIdentityReadBackAsync(root, identity));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SelectedWallpaperAndAvatarRequireExactSeedBytes()
    {
        var root = Path.Join(Path.GetTempPath(),
            "igloo-media-seed-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "migration-manifest.json"),
                """
                {"wallpaper":{"fileName":"igloo-wallpaper.png"},
                 "accountPicture":{"fileName":"igloo-avatar.jpg"}}
                """);
            await File.WriteAllTextAsync(Path.Join(root, "ks.cfg"), "user --name=fixture");
            var wallpaper = Path.Join(root, "igloo-wallpaper.png");
            var avatar = Path.Join(root, "igloo-avatar.jpg");
            await File.WriteAllBytesAsync(wallpaper, [1, 2, 3]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SeedIdentityWriter.WriteAsync(root, Guid.NewGuid(), Guid.NewGuid()));
            Assert.False(File.Exists(Path.Join(root, SeedIdentityWriter.FileName)));

            await File.WriteAllBytesAsync(avatar, [4, 5, 6]);
            var identity = await SeedIdentityWriter.WriteAsync(root,
                Guid.NewGuid(), Guid.NewGuid());
            Assert.Equal(new[] { "igloo-wallpaper.png", "igloo-avatar.jpg" },
                identity.Files.Select(file => file.Path).Where(path =>
                    path.StartsWith("igloo-wallpaper", StringComparison.Ordinal) ||
                    path.StartsWith("igloo-avatar", StringComparison.Ordinal)));
            await UsbWriterService.VerifyFatSeedIdentityReadBackAsync(root, identity);
            await File.WriteAllBytesAsync(avatar, [4, 5, 7]);
            await Assert.ThrowsAsync<IOException>(() =>
                UsbWriterService.VerifyFatSeedIdentityReadBackAsync(root, identity));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SelectedMediaPathCannotEscapeTheSeed()
    {
        var root = Path.Join(Path.GetTempPath(),
            "igloo-media-seed-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "migration-manifest.json"),
                """{"wallpaper":{"fileName":"../outside.png"}}""");
            await File.WriteAllTextAsync(Path.Join(root, "ks.cfg"), "user --name=fixture");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SeedIdentityWriter.WriteAsync(root, Guid.NewGuid(), Guid.NewGuid()));
            Assert.False(File.Exists(Path.Join(root, SeedIdentityWriter.FileName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SelectedUserAndBrowserTreesBindEveryFileIncludingAnEmptyFile()
    {
        var root = Path.Join(Path.GetTempPath(),
            "igloo-user-data-seed-" + Guid.NewGuid().ToString("N"));
        var selectedFolder = Path.Join(root, "files", "Documents");
        var browser = Path.Join(root, "browser-profiles", "AppData", "Local",
            "Google", "Chrome", "User Data");
        Directory.CreateDirectory(selectedFolder);
        Directory.CreateDirectory(Path.Join(browser, "Default"));
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "migration-manifest.json"),
                """
                {"files":{"stagingPath":"selected","folders":[{"name":"Documents"}]},
                 "browsers":[{"sourceRelativePath":"AppData/Local/Google/Chrome/User Data",
                              "profileStagingPath":"browser-profiles/AppData/Local/Google/Chrome/User Data"}]}
                """);
            await File.WriteAllTextAsync(Path.Join(root, "preseed.cfg"), "bound installer");
            await File.WriteAllBytesAsync(Path.Join(selectedFolder, "empty.txt"), []);
            var bookmarks = Path.Join(browser, "Default", "Bookmarks");
            await File.WriteAllTextAsync(bookmarks, "selected bookmarks");

            var identity = await SeedIdentityWriter.WriteAsync(root,
                Guid.NewGuid(), Guid.NewGuid());
            Assert.Contains(identity.Files, file => file.Path == "files/Documents/empty.txt" &&
                file.Sha256 == Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())));
            Assert.Contains(identity.Files, file => file.Path ==
                "browser-profiles/AppData/Local/Google/Chrome/User Data/Default/Bookmarks");
            await UsbWriterService.VerifyFatSeedIdentityReadBackAsync(root, identity);
            await File.WriteAllTextAsync(bookmarks, "changed bookmarks");
            await Assert.ThrowsAsync<IOException>(() =>
                UsbWriterService.VerifyFatSeedIdentityReadBackAsync(root, identity));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SelectedUserDataRejectsUnselectedFilesAndUnsafeAliases()
    {
        var root = Path.Join(Path.GetTempPath(),
            "igloo-user-data-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(root, "files", "Documents"));
        try
        {
            var manifest = Path.Join(root, "migration-manifest.json");
            await File.WriteAllTextAsync(manifest,
                """{"files":{"stagingPath":"selected","folders":[{"name":"Documents"}]}}""");
            await File.WriteAllTextAsync(Path.Join(root, "preseed.cfg"), "bound installer");
            var outsider = Path.Join(root, "files", "Pictures");
            Directory.CreateDirectory(outsider);
            await File.WriteAllTextAsync(Path.Join(outsider, "extra.jpg"), "unselected");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SeedIdentityWriter.WriteAsync(root, Guid.NewGuid(), Guid.NewGuid()));
            Directory.Delete(outsider, recursive: true);
            var emptyExtra = Path.Join(root, "files", "Documents", "unbound-empty");
            Directory.CreateDirectory(emptyExtra);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SeedIdentityWriter.WriteAsync(root, Guid.NewGuid(), Guid.NewGuid()));
            Directory.Delete(emptyExtra);
            await File.WriteAllTextAsync(manifest,
                """{"files":{"stagingPath":"selected","folders":[{"name":"Documents."}]}}""");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SeedIdentityWriter.WriteAsync(root, Guid.NewGuid(), Guid.NewGuid()));
            await File.WriteAllTextAsync(manifest,
                """{"files":{"stagingPath":"selected","folders":[{"name":"Documents"},{"name":"documents"}]}}""");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SeedIdentityWriter.WriteAsync(root, Guid.NewGuid(), Guid.NewGuid()));
            Assert.False(File.Exists(Path.Join(root, SeedIdentityWriter.FileName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EmptySelectedFolderRootIsRequiredWithoutAFileRecord()
    {
        var root = Path.Join(Path.GetTempPath(),
            "igloo-empty-user-root-" + Guid.NewGuid().ToString("N"));
        var selected = Path.Join(root, "files", "Documents");
        Directory.CreateDirectory(selected);
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "migration-manifest.json"),
                """{"files":{"stagingPath":"selected","folders":[{"name":"Documents"}]}}""");
            await File.WriteAllTextAsync(Path.Join(root, "preseed.cfg"), "bound installer");
            var disk = Guid.NewGuid();
            var partition = Guid.NewGuid();
            var identity = await SeedIdentityWriter.WriteAsync(root, disk, partition);
            Assert.DoesNotContain(identity.Files, file =>
                file.Path.StartsWith("files/", StringComparison.Ordinal));
            Directory.Delete(selected);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SeedIdentityWriter.BuildBytesAsync(root, disk, partition, []));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
