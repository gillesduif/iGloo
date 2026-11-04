using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Preflight;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class ExistingInstallerSeedTests
{
    [Fact]
    public async Task Exact_owned_seed_is_admitted_without_changing_its_files()
    {
        using var fixture = await SeedFixture.CreateAsync();
        var before = await File.ReadAllBytesAsync(fixture.Marker);

        var admitted = fixture.Admit();

        Assert.Equal(fixture.DiskGuid, admitted.Disk.GptDiskGuid);
        Assert.Equal(fixture.PartitionGuid, admitted.PartitionGuid);
        Assert.Equal(fixture.VolumeGuid, admitted.VolumeGuid);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Marker));
        Assert.Equal("personal document", await File.ReadAllTextAsync(fixture.Document));
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("system")]
    [InlineData("esp")]
    [InlineData("recovery")]
    [InlineData("ntfs")]
    [InlineData("unknown-protection")]
    [InlineData("active")]
    [InlineData("ambiguous-letter")]
    public async Task Protected_or_ambiguous_label_collision_is_rejected_before_seed_access(string kind)
    {
        using var fixture = await SeedFixture.CreateAsync();
        var snapshot = fixture.Snapshot();
        var partition = snapshot.Partitions[0];
        var volume = snapshot.Volumes[0];
        partition = kind switch
        {
            "windows" => partition with { IsBoot = A(true) },
            "system" => partition with { IsSystem = A(true) },
            "esp" => partition with { GptType = A(new Guid("c12a7328-f81f-11d2-ba4b-00a0c93ec93b")) },
            "recovery" => partition with { GptType = A(new Guid("de94bba4-06d1-4d40-a16a-bfd50179d6ac")) },
            "active" => partition with { IsActive = A(true) },
            "unknown-protection" => partition with
            {
                IsBoot = Observations.Failure<bool>(ObservationAvailability.Unavailable, "FixtureUnavailable"),
            },
            _ => partition,
        };
        if (kind == "ntfs") volume = volume with { FileSystem = A("NTFS") };
        snapshot = snapshot with { Partitions = [partition], Volumes = [volume] };
        if (kind == "ambiguous-letter")
            snapshot = snapshot with { Partitions = [partition, partition with
            {
                DiskNumber = A(3U), PartitionGuid = A(Guid.NewGuid()),
            }] };

        var rejected = Assert.Throws<InvalidOperationException>(() =>
            DirectInstallService.RequireReusableInstallerSeed(A(snapshot), 2, 4,
                'I', "OEMDRV", Path.Join(fixture.Root, "does-not-exist"), [], [], CancellationToken.None));

        // No filesystem exception: protected-state admission stopped first.
        Assert.Null(rejected.InnerException);
        Assert.Contains("preserved", rejected.Message, StringComparison.Ordinal);
        Assert.Equal("personal document", await File.ReadAllTextAsync(fixture.Document));
    }

    [Theory]
    [InlineData("files/Documents/keep.txt")]
    [InlineData("igloo-agent/first-boot.sh")]
    public async Task Changed_selection_rejects_old_data_or_agents_before_any_copy(string removedPath)
    {
        using var fixture = await SeedFixture.CreateAsync();
        var incoming = fixture.StagedPaths.Where(path => path != removedPath).ToArray();
        var marker = await File.ReadAllBytesAsync(fixture.Marker);

        var rejected = Assert.Throws<InvalidOperationException>(() =>
            DirectInstallService.RequireReusableInstallerSeed(A(fixture.Snapshot()), 2, 4,
                'I', "OEMDRV", fixture.Root, incoming, fixture.OutputPaths, CancellationToken.None));

        Assert.Contains("absent from the new selection", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(marker, await File.ReadAllBytesAsync(fixture.Marker));
        Assert.Equal("personal document", await File.ReadAllTextAsync(fixture.Document));
    }

    [Fact]
    public async Task Deselecting_a_previously_empty_folder_rejects_reuse_before_any_copy()
    {
        using var fixture = await SeedFixture.CreateAsync();
        File.Delete(fixture.Document);
        await SeedIdentityWriter.WriteAsync(fixture.Root, fixture.DiskGuid, fixture.PartitionGuid,
            ["igloo-agent/first-boot.sh"]);
        var incoming = fixture.StagedPaths.Where(path => !path.StartsWith("files/", StringComparison.Ordinal)).ToArray();
        var marker = await File.ReadAllBytesAsync(fixture.Marker);

        var rejected = Assert.Throws<InvalidOperationException>(() =>
            DirectInstallService.RequireReusableInstallerSeed(A(fixture.Snapshot()), 2, 4,
                'I', "OEMDRV", fixture.Root, incoming, fixture.OutputPaths, CancellationToken.None));

        Assert.Contains("absent from the new selection", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(marker, await File.ReadAllBytesAsync(fixture.Marker));
        Assert.True(Directory.Exists(Path.GetDirectoryName(fixture.Document)));
    }

    [Theory]
    [InlineData("igloo-boot/linux")]
    [InlineData("EFI/BOOT/BOOTX64.EFI")]
    [InlineData("EFI/debian/grub.cfg")]
    [InlineData("debian.iso")]
    public async Task Modified_boot_or_iso_content_rejects_reuse_even_when_seed_identity_still_matches(string path)
    {
        using var fixture = await SeedFixture.CreateAsync();
        var fullPath = Path.Join(fixture.Root, path.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(fullPath, "unrelated contents");

        var rejected = Assert.Throws<InvalidOperationException>(() => fixture.Admit());

        Assert.Contains("Ownership", rejected.Message, StringComparison.Ordinal);
        Assert.Equal("unrelated contents", await File.ReadAllTextAsync(fullPath));
    }

    [Fact]
    public async Task Historical_seed_without_complete_receipt_is_preserved()
    {
        using var fixture = await SeedFixture.CreateAsync();
        File.Delete(Path.Join(fixture.Root, DirectInstallService.SeedReuseReceiptFileName));

        var rejected = Assert.Throws<InvalidOperationException>(() => fixture.Admit());

        Assert.Contains("no complete reuse receipt", rejected.Message, StringComparison.Ordinal);
        Assert.Equal("personal document", await File.ReadAllTextAsync(fixture.Document));
    }

    [Fact]
    public async Task New_output_collision_cannot_claim_an_unowned_existing_file()
    {
        using var fixture = await SeedFixture.CreateAsync();
        var path = Path.Join(fixture.Root, "new-provider-config");
        await File.WriteAllTextAsync(path, "keep this file");

        var rejected = Assert.Throws<InvalidOperationException>(() =>
            DirectInstallService.RequireReusableInstallerSeed(A(fixture.Snapshot()), 2, 4,
                'I', "OEMDRV", fixture.Root, fixture.StagedPaths,
                fixture.OutputPaths.Append("new-provider-config").ToArray(), CancellationToken.None));

        Assert.Contains("unowned file", rejected.Message, StringComparison.Ordinal);
        Assert.Equal("keep this file", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("missing-marker")]
    [InlineData("different-disk")]
    [InlineData("different-partition")]
    [InlineData("modified-config")]
    [InlineData("modified-user-file")]
    [InlineData("missing-initrd")]
    [InlineData("duplicate-json-property")]
    [InlineData("unselected-user-directory")]
    public async Task Label_and_stale_or_incomplete_marker_never_authorize_reuse(string kind)
    {
        using var fixture = await SeedFixture.CreateAsync();
        var marker = await File.ReadAllTextAsync(fixture.Marker);
        switch (kind)
        {
            case "missing-marker": File.Delete(fixture.Marker); break;
            case "different-disk":
                await File.WriteAllTextAsync(fixture.Marker,
                    marker.Replace(fixture.DiskGuid.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal));
                break;
            case "different-partition":
                await File.WriteAllTextAsync(fixture.Marker,
                    marker.Replace(fixture.PartitionGuid.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal));
                break;
            case "modified-config":
                await File.AppendAllTextAsync(Path.Join(fixture.Root, "preseed.cfg"), " changed");
                break;
            case "modified-user-file": await File.WriteAllTextAsync(fixture.Document, "changed document"); break;
            case "missing-initrd": File.Delete(Path.Join(fixture.Root, "igloo-boot", "initrd")); break;
            case "duplicate-json-property":
                await File.WriteAllTextAsync(fixture.Marker,
                    marker.Replace("\"version\": 1", "\"version\": 1, \"version\": 1", StringComparison.Ordinal));
                break;
            case "unselected-user-directory":
                Directory.CreateDirectory(Path.Join(fixture.Root, "files", "Private"));
                await File.WriteAllTextAsync(Path.Join(fixture.Root, "files", "Private", "keep.txt"), "keep");
                break;
        }
        var before = await File.ReadAllBytesAsync(fixture.Document);

        var rejected = Assert.Throws<InvalidOperationException>(() => fixture.Admit());

        Assert.Contains("Ownership", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("preserved", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Document));
    }

    private static Observation<T> A<T>(T value) => Observations.Available(value);

    private sealed class SeedFixture : IDisposable
    {
        public string Root { get; } = Path.Join(Path.GetTempPath(), "igloo-owned-seed-" + Guid.NewGuid().ToString("N"));
        public string Marker => Path.Join(Root, SeedIdentityWriter.FileName);
        public string Document => Path.Join(Root, "files", "Documents", "keep.txt");
        public Guid DiskGuid { get; } = Guid.NewGuid();
        public Guid PartitionGuid { get; } = Guid.NewGuid();
        public Guid VolumeGuid { get; } = Guid.NewGuid();
        public string[] StagedPaths { get; } =
            ["migration-manifest.json", "preseed.cfg", "igloo-agent/first-boot.sh", "files/Documents/keep.txt",
                "files/", "files/Documents/"];
        public string[] OutputPaths => DirectInstallService.SeedOutputPaths(new InstallerBootSpec
        {
            MenuTitle = "fixture", KernelCmdline = "fixture", CopyFullIsoToVolume = true,
            IsoVolumeFileName = "debian.iso",
        }, StagedPaths.Where(path => !path.EndsWith('/')), isoOnOwnPartition: false);

        public static async Task<SeedFixture> CreateAsync()
        {
            var fixture = new SeedFixture();
            try
            {
                Directory.CreateDirectory(Path.Join(fixture.Root, "igloo-agent"));
                Directory.CreateDirectory(Path.Join(fixture.Root, "igloo-boot"));
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.Document)!);
                await File.WriteAllTextAsync(Path.Join(fixture.Root, "migration-manifest.json"),
                    "{\"files\":{\"stagingPath\":\"files\",\"includedFolders\":[\"Documents\"]}}");
                await File.WriteAllTextAsync(Path.Join(fixture.Root, "preseed.cfg"), "d-i fixture true");
                await File.WriteAllTextAsync(Path.Join(fixture.Root, "igloo-boot", "initrd"), "fixture initrd");
                await File.WriteAllTextAsync(Path.Join(fixture.Root, "igloo-agent", "first-boot.sh"), "#!/bin/sh\n");
                await File.WriteAllTextAsync(fixture.Document, "personal document");
                await SeedIdentityWriter.WriteAsync(fixture.Root, fixture.DiskGuid, fixture.PartitionGuid,
                    ["igloo-agent/first-boot.sh"]);
                foreach (var relative in fixture.OutputPaths)
                {
                    var path = Path.Join(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(path)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, "fixture boot content");
                }
                var target = DirectInstallService.RequireReusableSeedTarget(A(fixture.Snapshot()), 2, 4, 'I', "OEMDRV");
                DirectInstallService.WriteSeedReuseReceipt(fixture.Root, target, fixture.OutputPaths, CancellationToken.None);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public Igloo.Core.Recovery.CanonicalVolumeIdentityV1 Admit() =>
            DirectInstallService.RequireReusableInstallerSeed(A(Snapshot()), 2, 4,
                'I', "OEMDRV", Root, StagedPaths, OutputPaths, CancellationToken.None);

        public WindowsStorageSnapshot Snapshot() => new(
            [new(A(2U), A("fixture-seed-disk"), A(3U), A("fixture"), A(17U), A("VM disk"),
                A(80UL << 30), A(2U), A(DiskGuid), A(512U), A(4096U))],
            [new(A(2U), A(4U), A(PartitionGuid), A(new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7")),
                A(40UL << 30), A(8UL << 30), A(false), A(false), A(false), A('I'),
                A(ImmutableArray.Create($@"\\?\Volume{{{VolumeGuid:D}}}\")))],
            [new(A(VolumeGuid), A(PartitionGuid), A("FAT32"), A("OEMDRV"), A('I'), A("OK"), A(false))]);

        public void Dispose()
        {
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                Path.GetFullPath(Path.GetDirectoryName(Root)!));
            Assert.StartsWith("igloo-owned-seed-", Path.GetFileName(Root), StringComparison.Ordinal);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
