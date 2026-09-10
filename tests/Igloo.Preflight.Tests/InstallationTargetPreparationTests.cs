using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Igloo.Core.Models;
using Igloo.Core.Services;
using Xunit;

namespace Igloo.Preflight.Tests;

/// <summary>All storage is synthetic. No test constructs the Windows WMI adapter.</summary>
public sealed class InstallationTargetPreparationTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;
    private static readonly Guid DiskGuid = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EspGuid = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid WindowsGuid = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RecoveryGuid = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid SiblingLinuxGuid = new("55555555-5555-5555-5555-555555555555");
    private static readonly Guid RootGuid = new("99999999-9999-9999-9999-999999999999");
    private static readonly Guid InstallationId = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public async Task Exact_creation_publishes_returned_identity_and_preserves_existing_partitions(int sectorSize)
    {
        var request = Request(sectorSize);
        // Sector-aligned geometry is valid even when it is not MiB-aligned.
        request = request with { RootOffsetBytes = request.RootOffsetBytes + sectorSize };
        using var fixture = new Fixture(request);
        var claim = await fixture.Preparer.PrepareAsync(request, fixture.ManifestPath);

        claim.RootPartitionGuid.Should().Be(RootGuid);
        claim.EspPartitionGuid.Should().Be(EspGuid);
        claim.InstallationId.Should().Be(InstallationId);
        fixture.Storage.Opened.Should().Equal(DiskGuid);
        fixture.Disk.Creates.Should().Equal((request.RootOffsetBytes, request.RootLengthBytes));
        fixture.Disk.ReadCount.Should().Be(2);
        fixture.Disk.Disposed.Should().BeTrue();
        claim.Disk.Partitions.Where(p => p.PartitionGuid != RootGuid)
            .Should().BeEquivalentTo(request.ExpectedLayout.Partitions);
        claim.Disk.Partitions.Should().Contain(p => p.PartitionGuid == SiblingLinuxGuid);

        var json = await File.ReadAllTextAsync(fixture.ManifestPath);
        InstallationTargetManifest.ReadClaim(json, InstallationId).Should().BeEquivalentTo(claim);
        JsonNode.Parse(json)!["futureField"]!["retain"]!.GetValue<string>().Should().Be("unchanged");
    }

    [Fact]
    public async Task Reading_a_snapshot_never_creates_a_partition()
    {
        using var fixture = new Fixture(Request());

        var actual = await fixture.Preparer.ReadLayoutAsync(DiskGuid);

        actual.Should().BeEquivalentTo(fixture.Disk.Before);
        fixture.Storage.Opened.Should().Equal(DiskGuid);
        fixture.Disk.ReadCount.Should().Be(1);
        fixture.Disk.Creates.Should().BeEmpty();
        fixture.Disk.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Reading_refuses_a_provider_snapshot_for_another_disk()
    {
        using var fixture = new Fixture(Request());
        fixture.Disk.Before = fixture.Disk.Before with { DiskGuid = Guid.NewGuid() };

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Preparer.ReadLayoutAsync(DiskGuid));

        fixture.Disk.Creates.Should().BeEmpty();
        fixture.Disk.Disposed.Should().BeTrue();
    }

    [Theory]
    [InlineData("missing-id")]
    [InlineData("stale-id")]
    [InlineData("unsupported-version")]
    [InlineData("existing-claim")]
    [InlineData("malformed")]
    [InlineData("duplicate-id")]
    public async Task Invalid_pending_manifest_aborts_before_opening_storage(string fault)
    {
        using var fixture = new Fixture(Request());
        var document = JsonNode.Parse(fixture.OriginalManifest)!.AsObject();
        switch (fault)
        {
            case "missing-id": document.Remove("installationId"); break;
            case "stale-id": document["installationId"] = Guid.NewGuid().ToString("D"); break;
            case "unsupported-version": document["schemaVersion"] = 2; break;
            case "existing-claim": document["installationTarget"] = new JsonObject(); break;
        }
        var json = fault switch
        {
            "malformed" => "{broken",
            "duplicate-id" => "{\"schemaVersion\":1,\"installationId\":\"" + InstallationId.ToString("D")
                + "\",\"installationId\":\"" + InstallationId.ToString("D") + "\"}",
            _ => document.ToJsonString(),
        };
        await File.WriteAllTextAsync(fixture.ManifestPath, json);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Preparer.PrepareAsync(Request(), fixture.ManifestPath));

        fixture.Storage.Opened.Should().BeEmpty();
        fixture.Disk.Creates.Should().BeEmpty();
        (await File.ReadAllTextAsync(fixture.ManifestPath)).Should().Be(json);
    }

    [Fact]
    public async Task Missing_manifest_aborts_before_opening_storage()
    {
        using var fixture = new Fixture(Request());
        File.Delete(fixture.ManifestPath);

        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Preparer.PrepareAsync(Request(), fixture.ManifestPath));

        fixture.Storage.Opened.Should().BeEmpty();
        fixture.Disk.Creates.Should().BeEmpty();
    }

    [Theory]
    [InlineData("occupied-linux")]
    [InlineData("occupied-windows")]
    [InlineData("empty-installation")]
    [InlineData("missing-layout")]
    [InlineData("missing-partitions")]
    [InlineData("out-of-bounds")]
    [InlineData("unknown-esp")]
    public async Task An_invalid_or_reused_extent_cannot_reach_storage(string fault)
    {
        var request = Request();
        using var fixture = new Fixture(request);
        request = fault switch
        {
            "occupied-linux" => request with { RootOffsetBytes = 60 * GiB },
            "occupied-windows" => request with { RootOffsetBytes = 10 * GiB },
            "empty-installation" => request with { InstallationId = Guid.Empty },
            "missing-layout" => request with { ExpectedLayout = null! },
            "missing-partitions" => request with { ExpectedLayout = request.ExpectedLayout with { Partitions = null! } },
            "out-of-bounds" => request with { RootLengthBytes = long.MaxValue },
            "unknown-esp" => request with { EspPartitionGuid = Guid.NewGuid() },
            _ => throw new InvalidOperationException(),
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Preparer.PrepareAsync(request, fixture.ManifestPath));

        fixture.Storage.Opened.Should().BeEmpty();
        fixture.Disk.Creates.Should().BeEmpty();
        (await File.ReadAllTextAsync(fixture.ManifestPath)).Should().Be(fixture.OriginalManifest);
    }

    [Theory]
    [InlineData("different-disk")]
    [InlineData("different-size")]
    [InlineData("different-sector-size")]
    [InlineData("removed-windows")]
    [InlineData("changed-windows-guid")]
    [InlineData("changed-recovery-offset")]
    [InlineData("changed-esp-size")]
    [InlineData("extra-partition")]
    [InlineData("duplicate-guid")]
    public async Task Changed_layout_before_creation_aborts_without_mutation(string fault)
    {
        using var fixture = new Fixture(Request());
        fixture.Disk.Before = Alter(fixture.Disk.Before, fault);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Preparer.PrepareAsync(Request(), fixture.ManifestPath));

        fixture.Storage.Opened.Should().ContainSingle();
        fixture.Disk.Creates.Should().BeEmpty();
        fixture.Disk.Disposed.Should().BeTrue();
        (await File.ReadAllTextAsync(fixture.ManifestPath)).Should().Be(fixture.OriginalManifest);
    }

    [Theory]
    [InlineData("different-disk")]
    [InlineData("different-size")]
    [InlineData("removed-windows")]
    [InlineData("changed-windows-guid")]
    [InlineData("changed-recovery-offset")]
    [InlineData("changed-esp-size")]
    [InlineData("extra-partition")]
    [InlineData("duplicate-guid")]
    [InlineData("missing-root")]
    [InlineData("wrong-root-type")]
    [InlineData("wrong-root-offset")]
    [InlineData("wrong-root-size")]
    public async Task Failed_creation_postconditions_never_publish_a_claim_or_retry(string fault)
    {
        using var fixture = new Fixture(Request());
        fixture.Disk.After = Alter(fixture.Disk.After, fault);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Preparer.PrepareAsync(Request(), fixture.ManifestPath));

        fixture.Disk.Creates.Should().ContainSingle();
        fixture.Storage.Opened.Should().ContainSingle();
        fixture.Disk.ReadCount.Should().Be(2);
        fixture.Disk.Disposed.Should().BeTrue();
        (await File.ReadAllTextAsync(fixture.ManifestPath)).Should().Be(fixture.OriginalManifest);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unobserved")]
    [InlineData("existing-linux")]
    [InlineData("existing-windows")]
    public async Task An_invalid_returned_GUID_is_not_inferred_from_the_post_layout(string fault)
    {
        using var fixture = new Fixture(Request());
        fixture.Disk.ReturnedGuid = fault switch
        {
            "empty" => Guid.Empty,
            "unobserved" => Guid.NewGuid(),
            "existing-linux" => SiblingLinuxGuid,
            "existing-windows" => WindowsGuid,
            _ => throw new InvalidOperationException(),
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Preparer.PrepareAsync(Request(), fixture.ManifestPath));

        fixture.Disk.Creates.Should().ContainSingle();
        fixture.Storage.Opened.Should().ContainSingle();
        (await File.ReadAllTextAsync(fixture.ManifestPath)).Should().Be(fixture.OriginalManifest);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("ambiguous")]
    public async Task Missing_or_ambiguous_storage_resolution_has_no_fallback(string fault)
    {
        using var fixture = new Fixture(Request());
        var error = new InvalidDataException("Synthetic " + fault + " disk identity");
        fixture.Storage.OpenFailure = error;

        var actual = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Preparer.PrepareAsync(Request(), fixture.ManifestPath));

        actual.Should().BeSameAs(error);
        fixture.Storage.Opened.Should().Equal(DiskGuid);
        fixture.Disk.Creates.Should().BeEmpty();
        (await File.ReadAllTextAsync(fixture.ManifestPath)).Should().Be(fixture.OriginalManifest);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Read_failures_stop_without_publishing_or_retrying(int failingRead)
    {
        using var fixture = new Fixture(Request());
        var error = new IOException("Synthetic storage read failure");
        fixture.Disk.BeforeRead = count => { if (count == failingRead) throw error; };

        var actual = await Assert.ThrowsAsync<IOException>(() => fixture.Preparer.PrepareAsync(Request(), fixture.ManifestPath));

        actual.Should().BeSameAs(error);
        fixture.Disk.Creates.Should().HaveCount(failingRead - 1);
        fixture.Disk.ReadCount.Should().Be(failingRead);
        fixture.Disk.Disposed.Should().BeTrue();
        (await File.ReadAllTextAsync(fixture.ManifestPath)).Should().Be(fixture.OriginalManifest);
    }

    [Fact]
    public async Task A_failed_create_is_not_retried_or_compensated()
    {
        using var fixture = new Fixture(Request());
        var error = new IOException("Synthetic CreatePartition error after possible mutation");
        fixture.Disk.OnCreate = () => throw error;

        var actual = await Assert.ThrowsAsync<IOException>(() => fixture.Preparer.PrepareAsync(Request(), fixture.ManifestPath));

        actual.Should().BeSameAs(error);
        fixture.Disk.Creates.Should().ContainSingle();
        fixture.Disk.ReadCount.Should().Be(1);
        fixture.Disk.Disposed.Should().BeTrue();
        (await File.ReadAllTextAsync(fixture.ManifestPath)).Should().Be(fixture.OriginalManifest);
    }

    [Fact]
    public async Task A_changed_manifest_after_creation_is_preserved_without_a_claim()
    {
        using var fixture = new Fixture(Request());
        var changed = fixture.OriginalManifest.Replace("unchanged", "concurrent edit", StringComparison.Ordinal);
        fixture.Disk.OnCreate = () => File.WriteAllText(fixture.ManifestPath, changed);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Preparer.PrepareAsync(Request(), fixture.ManifestPath));

        fixture.Disk.Creates.Should().ContainSingle();
        (await File.ReadAllTextAsync(fixture.ManifestPath)).Should().Be(changed);
    }

    [Theory]
    [InlineData("before-start")]
    [InlineData("before-create")]
    [InlineData("after-create")]
    public async Task Cancellation_never_publishes_a_partial_claim_or_retries(string stage)
    {
        using var fixture = new Fixture(Request());
        using var cancellation = new CancellationTokenSource();
        if (stage == "before-start") await cancellation.CancelAsync();
        if (stage == "before-create") fixture.Disk.BeforeRead = _ => cancellation.Cancel();
        if (stage == "after-create") fixture.Disk.OnCreate = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Preparer.PrepareAsync(Request(), fixture.ManifestPath, cancellation.Token));

        fixture.Disk.Creates.Should().HaveCount(stage == "after-create" ? 1 : 0);
        fixture.Storage.Opened.Should().HaveCount(stage == "before-start" ? 0 : 1);
        (await File.ReadAllTextAsync(fixture.ManifestPath)).Should().Be(fixture.OriginalManifest);
    }

    [Fact]
    public async Task Mutating_the_callers_collection_cannot_change_the_approved_snapshot()
    {
        var request = Request();
        var parts = request.ExpectedLayout.Partitions.ToArray();
        request = request with { ExpectedLayout = request.ExpectedLayout with { Partitions = parts } };
        using var fixture = new Fixture(request);
        fixture.Storage.OnOpen = () =>
        {
            parts[0] = parts[0] with { PartitionGuid = Guid.NewGuid() };
        };

        var claim = await fixture.Preparer.PrepareAsync(request, fixture.ManifestPath);

        claim.EspPartitionGuid.Should().Be(EspGuid);
        claim.Disk.Partitions.Should().Contain(p => p.PartitionGuid == EspGuid);
        fixture.Disk.Creates.Should().ContainSingle();
    }

    private static GptDiskLayout Alter(GptDiskLayout source, string fault)
    {
        if (fault == "different-disk") return source with { DiskGuid = Guid.NewGuid() };
        if (fault == "different-size") return source with { DiskSizeBytes = source.DiskSizeBytes + GiB };
        if (fault == "different-sector-size") return source with { LogicalSectorSize = 4096 };
        if (fault == "removed-windows") return source with { Partitions = source.Partitions.Where(p => p.PartitionGuid != WindowsGuid).ToArray() };
        if (fault == "missing-root") return source with { Partitions = source.Partitions.Where(p => p.PartitionGuid != RootGuid).ToArray() };
        if (fault == "extra-partition") return source with { Partitions = [.. source.Partitions, Part(Guid.NewGuid(), InstallationTargetValidation.LinuxFileSystemType, 95 * GiB, GiB)] };
        if (fault == "duplicate-guid") return source with { Partitions = [.. source.Partitions, Part(WindowsGuid, InstallationTargetValidation.LinuxFileSystemType, 95 * GiB, GiB)] };
        return source with
        {
            Partitions = source.Partitions.Select(p => fault switch
            {
                "changed-windows-guid" when p.PartitionGuid == WindowsGuid => p with { PartitionGuid = Guid.NewGuid() },
                "changed-recovery-offset" when p.PartitionGuid == RecoveryGuid => p with { OffsetBytes = p.OffsetBytes + MiB },
                "changed-esp-size" when p.PartitionGuid == EspGuid => p with { LengthBytes = p.LengthBytes - MiB },
                "wrong-root-type" when p.PartitionGuid == RootGuid => p with { GptType = InstallationTargetValidation.EfiSystemType },
                "wrong-root-offset" when p.PartitionGuid == RootGuid => p with { OffsetBytes = p.OffsetBytes + MiB },
                "wrong-root-size" when p.PartitionGuid == RootGuid => p with { LengthBytes = p.LengthBytes - MiB },
                _ => p,
            }).ToArray(),
        };
    }

    private static InstallationTargetRequest Request(int sectorSize = 512) => new()
    {
        InstallationId = InstallationId,
        RootOffsetBytes = 70 * GiB,
        RootLengthBytes = 20 * GiB,
        EspPartitionGuid = EspGuid,
        ExpectedLayout = new GptDiskLayout
        {
            DiskGuid = DiskGuid,
            DiskSizeBytes = 100 * GiB,
            LogicalSectorSize = sectorSize,
            Partitions =
            [
                Part(EspGuid, InstallationTargetValidation.EfiSystemType, MiB, 256 * MiB),
                Part(WindowsGuid, new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"), GiB, 50 * GiB),
                Part(RecoveryGuid, new Guid("de94bba4-06d1-4d40-a16a-bfd50179d6ac"), 52 * GiB, 2 * GiB),
                Part(SiblingLinuxGuid, InstallationTargetValidation.LinuxFileSystemType, 60 * GiB, 5 * GiB),
            ],
        },
    };

    private static GptPartitionIdentity Part(Guid id, Guid type, long offset, long length) => new()
    {
        PartitionGuid = id, GptType = type, OffsetBytes = offset, LengthBytes = length,
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Join(Path.GetTempPath(), "igloo-target-prepare-" + Guid.NewGuid().ToString("N"));
        internal string ManifestPath { get; }
        internal string OriginalManifest { get; }
        internal FakeDisk Disk { get; }
        internal FakeStorage Storage { get; }
        internal WindowsInstallationTargetPreparer Preparer { get; }

        internal Fixture(InstallationTargetRequest request)
        {
            Directory.CreateDirectory(_directory);
            ManifestPath = Path.Join(_directory, "manifest.json");
            OriginalManifest = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                installationId = request.InstallationId,
                futureField = new { retain = "unchanged" },
            });
            File.WriteAllText(ManifestPath, OriginalManifest);
            var before = request.ExpectedLayout with { Partitions = request.ExpectedLayout.Partitions.ToArray() };
            Disk = new FakeDisk
            {
                Before = before,
                After = before with
                {
                    Partitions = [.. before.Partitions, Part(RootGuid, InstallationTargetValidation.LinuxFileSystemType,
                        request.RootOffsetBytes, request.RootLengthBytes)],
                },
            };
            Storage = new FakeStorage(Disk);
            Preparer = new WindowsInstallationTargetPreparer(Storage);
        }

        public void Dispose()
        {
            File.Delete(ManifestPath);
            Directory.Delete(_directory);
        }
    }

    private sealed class FakeStorage(FakeDisk disk) : IInstallationTargetStorage
    {
        internal List<Guid> Opened { get; } = [];
        internal Exception? OpenFailure { get; set; }
        internal Action? OnOpen { get; set; }

        public IInstallationTargetDisk OpenDisk(Guid diskGuid)
        {
            Opened.Add(diskGuid);
            OnOpen?.Invoke();
            if (OpenFailure is not null) throw OpenFailure;
            return disk;
        }
    }

    private sealed class FakeDisk : IInstallationTargetDisk
    {
        internal required GptDiskLayout Before { get; set; }
        internal required GptDiskLayout After { get; set; }
        internal List<(long Offset, long Length)> Creates { get; } = [];
        internal int ReadCount { get; private set; }
        internal bool Disposed { get; private set; }
        internal Guid ReturnedGuid { get; set; } = RootGuid;
        internal Action<int>? BeforeRead { get; set; }
        internal Action? OnCreate { get; set; }

        public GptDiskLayout ReadLayout()
        {
            ReadCount++;
            BeforeRead?.Invoke(ReadCount);
            return Creates.Count == 0 ? Before : After;
        }

        public Guid CreateRootPartition(long offsetBytes, long lengthBytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Creates.Add((offsetBytes, lengthBytes));
            OnCreate?.Invoke();
            return ReturnedGuid;
        }

        public void Dispose() => Disposed = true;
    }
}
