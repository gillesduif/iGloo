using System.Collections.Immutable;
using System.Security.Cryptography;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

// Beta Track B-02: every check that can refuse runs read-only before the NTFS shrink.
public sealed class DirectInstallReadinessTests
{
    [Fact]
    public async Task Denied_nvram_access_stops_before_the_iso_mount_and_the_shrink()
    {
        using var fixture = PrepareFixture.Create();
        var firmware = new FirmwareFake { OrderError = 1300 };
        var resizer = new ResizerFake();
        var service = new DirectInstallService(resizer, NullLogger<DirectInstallService>.Instance,
            new StorageFake(), new BcdFake(), firmware);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareAsync(2, 20L << 30, fixture.IsoPath, fixture.StagingDirectory, fixture.BootSpec));

        Assert.Contains("Administrator", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, firmware.OrderReads);
        Assert.Equal(0, resizer.ShrinkCalls);
    }

    [Fact]
    public async Task Unmountable_iso_stops_after_the_boot_checks_and_before_the_shrink()
    {
        using var fixture = PrepareFixture.Create();
        var firmware = new FirmwareFake();
        var resizer = new ResizerFake();
        var storage = new StorageFake();
        var service = new DirectInstallService(resizer, NullLogger<DirectInstallService>.Instance,
            storage, new BcdFake(), firmware);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareAsync(2, 20L << 30, fixture.IsoPath, fixture.StagingDirectory, fixture.BootSpec));

        Assert.DoesNotContain("Administrator", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, firmware.OrderReads);
        Assert.Equal(1, storage.IdentityReads);
        Assert.Equal(0, resizer.ShrinkableReads);
        Assert.Equal(0, resizer.ShrinkCalls);
    }

    [Theory]
    [InlineData(1300, "Administrator")]
    [InlineData(1, "UEFI")]
    [InlineData(31, "could not be read")]
    public void Boot_order_read_failures_refuse_with_a_remedy(int error, string expected)
    {
        var refused = Assert.Throws<InvalidOperationException>(() =>
            DirectInstallService.RequireBootRegistrationReadiness(new FirmwareFake { OrderError = error },
                new BcdFake(), NullLogger.Instance));

        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_boot_slot_window_refuses_before_any_write()
    {
        var firmware = new FirmwareFake { Present = Enumerable.Range(0x0080, 0x007F).Select(i => (ushort)i).ToHashSet() };

        var refused = Assert.Throws<InvalidOperationException>(() =>
            DirectInstallService.RequireBootRegistrationReadiness(firmware, new BcdFake(), NullLogger.Instance));

        Assert.Contains("Boot####", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreachable_bcdedit_refuses_before_any_write()
    {
        var refused = Assert.Throws<InvalidOperationException>(() =>
            DirectInstallService.RequireBootRegistrationReadiness(new FirmwareFake(),
                new BcdFake { ExitCode = 1, Error = "Access is denied." }, NullLogger.Instance));

        Assert.Contains("bcdedit", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Access is denied.", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Readiness_passes_with_readable_nvram_a_free_slot_and_bcdedit()
    {
        var firmware = new FirmwareFake { Present = [0x0080, 0x0081] };

        DirectInstallService.RequireBootRegistrationReadiness(firmware, new BcdFake(), NullLogger.Instance);

        Assert.Equal((ushort)0x0082, DirectInstallService.FindFreeBootIndexObserved(firmware));
    }

    [Fact]
    public void A_denied_slot_read_is_never_a_free_slot()
    {
        var firmware = new FirmwareFake { EntryError = 1314 };

        Assert.Null(DirectInstallService.FindFreeBootIndexObserved(firmware));
    }

    [Fact]
    public void Seed_inventory_bounds_are_applied_to_the_plan()
    {
        SeedIdentityWriter.RequirePlannedInventoryWithinBounds(["igloo-agent/first-boot.sh"], 1_000_000, 2_000_000, 256_000_000);

        Assert.Contains("too large", Assert.Throws<InvalidDataException>(() =>
            SeedIdentityWriter.RequirePlannedInventoryWithinBounds(["igloo-agent/first-boot.sh"], 1_000_001, 1, 1)).Message,
            StringComparison.Ordinal);
        Assert.Contains("too large", Assert.Throws<InvalidDataException>(() =>
            SeedIdentityWriter.RequirePlannedInventoryWithinBounds(["igloo-agent/first-boot.sh"], 1, 2_000_001, 1)).Message,
            StringComparison.Ordinal);
        Assert.Contains("too large", Assert.Throws<InvalidDataException>(() =>
            SeedIdentityWriter.RequirePlannedInventoryWithinBounds(["igloo-agent/first-boot.sh"], 1, 1, 256_000_001)).Message,
            StringComparison.Ordinal);
        Assert.Contains("too many", Assert.Throws<InvalidDataException>(() =>
            SeedIdentityWriter.RequirePlannedInventoryWithinBounds(
                Enumerable.Range(0, 129).Select(i => $"igloo-agent/file{i}.sh").ToArray(), 0, 0, 0)).Message,
            StringComparison.Ordinal);
        Assert.Contains("unsafe", Assert.Throws<InvalidDataException>(() =>
            SeedIdentityWriter.RequirePlannedInventoryWithinBounds(["files/Documents/keep.txt"], 0, 0, 0)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Planned_seed_counts_only_user_data_that_goes_onto_the_seed()
    {
        using var fixture = PrepareFixture.Create();
        Directory.CreateDirectory(Path.Join(fixture.StagingDirectory, "files", "Documents", "Nested"));
        File.WriteAllText(Path.Join(fixture.StagingDirectory, "files", "Documents", "Nested", "a.txt"), "a");
        var staged = DirectInstallService.InspectStagingFiles(fixture.StagingDirectory, requireFat32Fit: false);
        var agents = DirectInstallService.StagedAgentPaths(staged);

        DirectInstallService.RequirePlannedSeedWithinBounds(fixture.StagingDirectory, staged, agents);

        Assert.Equal(new[] { "igloo-agent/first-boot.sh" }, agents);
        Assert.Contains("first-boot", Assert.Throws<InvalidDataException>(() =>
            DirectInstallService.RequirePlannedSeedWithinBounds(fixture.StagingDirectory, staged, [])).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Free_candidate_letters_are_counted_case_insensitively()
    {
        Assert.Equal(18, DirectInstallService.CountFreeCandidateLetters(['C', 'D']));
        Assert.Equal(16, DirectInstallService.CountFreeCandidateLetters(['c', 'i', 'J']));
        Assert.Equal(0, DirectInstallService.CountFreeCandidateLetters("IJKLMNOPQRSTUVWXYZ"));
    }

    private static Observation<T> A<T>(T value) => Observations.Available(value);

    private sealed class PrepareFixture : IDisposable
    {
        public string Root { get; } = Path.Join(Path.GetTempPath(), "igloo-readiness-" + Guid.NewGuid().ToString("N"));
        public string StagingDirectory => Path.Join(Root, "staging");
        public string IsoPath => Path.Join(Root, "fixture.iso");
        public InstallerBootSpec BootSpec { get; private set; } = null!;

        public static PrepareFixture Create()
        {
            var fixture = new PrepareFixture();
            Directory.CreateDirectory(Path.Join(fixture.StagingDirectory, "igloo-agent"));
            File.WriteAllText(Path.Join(fixture.StagingDirectory, "migration-manifest.json"),
                "{\"user\":{\"locale\":\"nl_BE.UTF-8\",\"keymap\":\"be\"}}");
            File.WriteAllText(Path.Join(fixture.StagingDirectory, "igloo-agent", "first-boot.sh"), "#!/bin/sh\n");
            var bytes = RandomNumberGenerator.GetBytes(4096);
            File.WriteAllBytes(fixture.IsoPath, bytes);
            fixture.BootSpec = new InstallerBootSpec
            {
                MenuTitle = "fixture", KernelCmdline = "fixture",
                IsoSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            };
            return fixture;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FirmwareFake : IWindowsFirmwareReader
    {
        public int OrderError { get; init; }
        public int EntryError { get; init; } = 203;
        public HashSet<ushort> Present { get; init; } = [];
        public int OrderReads { get; private set; }

        public FirmwareVariableObservation ReadBootOrder(int bufferBytes = 4096)
        {
            OrderReads++;
            return OrderError == 0
                ? new(ImmutableArray.Create<byte>(0x01, 0x00), 0)
                : new(null, OrderError);
        }

        public FirmwareVariableObservation ReadBootEntry(ushort index, int bufferBytes = 4096) =>
            Present.Contains(index)
                ? new(ImmutableArray.Create<byte>(0x01, 0x00, 0x00, 0x00), 0)
                : new(null, EntryError);
    }

    private sealed class BcdFake : IWindowsBcdReader
    {
        public int? ExitCode { get; init; } = 0;
        public string? Error { get; init; }
        public BcdListingObservation ReadFirmware() => new("Firmware Boot Manager\n", Error, ExitCode);
    }

    private sealed class StorageFake : IWindowsStorageReader
    {
        public int IdentityReads { get; private set; }

        public Observation<WindowsStorageSnapshot> ReadIdentitySnapshot()
        {
            IdentityReads++;
            return A(new WindowsStorageSnapshot(
                [new(A(2U), A("fixture-disk"), A(3U), A("fixture"), A(17U), A("VM disk"),
                    A(80UL << 30), A(2U), A(Guid.NewGuid()), A(512U), A(4096U))], [], []));
        }

        public WindowsStorageBatch ReadDisks() => throw new InvalidOperationException("Disk inventory must not be read before the ISO is measured.");
        public WindowsStorageBatch ReadPartitions(int? diskNumber = null, int? partitionNumber = null, bool efiOnly = false) =>
            throw new InvalidOperationException("Partitions must not be read before the ISO is measured.");
        public WindowsStorageBatch ReadVolumes(char driveLetter) => throw new InvalidOperationException("Volumes must not be read.");
        public WindowsObservation<WindowsStorageRow> ReadSupportedSize(WindowsStorageRow partition, bool explicitParameters = false) =>
            throw new InvalidOperationException("Supported size must not be read.");
    }

    private sealed class ResizerFake : IPartitionResizeService
    {
        public int ShrinkableReads { get; private set; }
        public int ShrinkCalls { get; private set; }

        public Task<long> GetShrinkableSpaceAsync(int diskNumber, CancellationToken ct = default)
        {
            ShrinkableReads++;
            return Task.FromResult(100L << 30);
        }

        public Task ShrinkAsync(int diskNumber, long linuxSizeBytes, IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            ShrinkCalls++;
            return Task.CompletedTask;
        }
    }
}
