using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Fleet.Agent.Targets;
using Igloo.Fleet.Contracts;
using Xunit;

namespace Igloo.Fleet.Tests;

public sealed class TargetRevalidationTests
{
    [Fact]
    public void RevalidatorHasNoHostObservationOrExecutionDependencies()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Igloo.sln"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var path in Directory.EnumerateFiles(Path.Join(root.FullName, "src", "Igloo.Fleet.Agent", "Targets"), "*.cs"))
        {
            var source = File.ReadAllText(path);
            foreach (var forbidden in new[] { "System.Management", "DllImport", "LibraryImport", "System.Diagnostics", "Process.",
                "IWindowsStorageReader", "IWindowsBitLockerReader", "FirmwareNative", "IMutationAdapter", "DirectInstallService" })
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("geometry")]
    [InlineData("filesystem")]
    [InlineData("guid")]
    public void RequiredFactCannotBeReplacedWithAHeuristic(string missing)
    {
        var f = new Fixture();
        switch (missing)
        {
            case "owner": f.Volume = f.Volume with { PartitionGuid = Observations.Failure<Guid>(ObservationAvailability.Unavailable, "unknown") }; break;
            case "geometry": f.Partition = f.Partition with { Size = Observations.Failure<ulong>(ObservationAvailability.Unavailable, "unknown") }; break;
            case "filesystem": f.Volume = f.Volume with { FileSystem = Observations.Failure<string>(ObservationAvailability.Unavailable, "unknown") }; break;
            case "guid": f.Partition = f.Partition with { PartitionGuid = Observations.Failure<Guid>(ObservationAvailability.Unavailable, "unknown") }; break;
        }
        Assert.Equal(TargetMatch.ObservationUnavailable, f.Compare().Outcome);
    }

    [Fact]
    public void AmbiguousOwnerAssociationIsRejected()
    {
        var f = new Fixture();
        var snapshot = f.Snapshot() with { Disks = [f.Disk, f.Disk with { UniqueId = A("other") }] };
        Assert.Equal(TargetMatch.Ambiguous, f.Compare(A(snapshot)).Outcome);
        snapshot = f.Snapshot() with { Volumes = [f.Volume, f.Volume with { VolumeGuid = A(Guid.NewGuid()) }] };
        Assert.Equal(TargetMatch.Ambiguous, f.Compare(A(snapshot)).Outcome);
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("endpoint")]
    [InlineData("profile")]
    [InlineData("plan")]
    public void BindingMustReferToTheExplicitApproval(string changed)
    {
        var f = new Fixture();
        var plan = changed switch
        {
            "approval" => f.Plan with { ApprovalId = Guid.NewGuid() },
            "endpoint" => f.Plan with { Identity = new(Guid.NewGuid(), Guid.NewGuid()) },
            "profile" => f.Plan with { ProfileRevisionId = Guid.NewGuid() },
            _ => f.Plan with { PlanId = Guid.NewGuid() },
        };
        Assert.Equal(TargetMismatchReason.PlanBindingChanged, TargetRevalidator.Compare(plan, f.Binding, A(f.Snapshot()), A(f.BitLocker)).Reason);
    }

    [Fact]
    public void ExactIdentityAndCorrelatedBitLockerMatch() => Assert.Equal(TargetMatch.ExactMatch, new Fixture().Compare().Outcome);

    [Fact]
    public void LocatorsAndBenignFactsDoNotDefineIdentity()
    {
        var f = new Fixture();
        f.Disk = f.Disk with { Number = A(7U), FriendlyName = A("another name"), SerialNumber = A("metadata") };
        f.Partition = f.Partition with { DiskNumber = A(7U), Number = A(9U), DriveLetter = A('D') };
        f.Volume = f.Volume with { DriveLetter = A('D') };
        Assert.Equal(TargetMatch.ExactMatch, f.Compare().Outcome);
        Assert.Equal(f.Binding.TargetFingerprint, (f.Binding with { Informational = new(7, 9, 'D') }).TargetFingerprint);
        Assert.NotEqual(f.Binding.TargetFingerprint, (f.Binding with { Structure = f.Binding.Structure with { PartitionSize = 99 } }).TargetFingerprint);
    }

    [Theory]
    [InlineData("disk", TargetMismatchReason.DiskIdentityChanged)]
    [InlineData("partition", TargetMismatchReason.PartitionIdentityChanged)]
    [InlineData("volume", TargetMismatchReason.VolumeIdentityChanged)]
    [InlineData("size", TargetMismatchReason.PartitionGeometryChanged)]
    [InlineData("offset", TargetMismatchReason.PartitionGeometryChanged)]
    [InlineData("type", TargetMismatchReason.PartitionGeometryChanged)]
    [InlineData("filesystem", TargetMismatchReason.FileSystemChanged)]
    [InlineData("label", TargetMismatchReason.FileSystemChanged)]
    public void StableOrStructuralDriftIsRejected(string change, TargetMismatchReason reason)
    {
        var f = new Fixture();
        switch (change)
        {
            case "disk": f.Disk = f.Disk with { UniqueId = A("replacement") }; break;
            case "partition": f.Partition = f.Partition with { PartitionGuid = A(Guid.NewGuid()) }; break;
            case "volume": f.Volume = f.Volume with { VolumeGuid = A(Guid.NewGuid()) }; break;
            case "size": f.Partition = f.Partition with { Size = A(99UL) }; break;
            case "offset": f.Partition = f.Partition with { Offset = A(99UL) }; break;
            case "type": f.Partition = f.Partition with { GptType = A(Guid.NewGuid()) }; break;
            case "filesystem": f.Volume = f.Volume with { FileSystem = A("ReFS") }; break;
            case "label": f.Volume = f.Volume with { Label = A("renamed") }; break;
        }
        Assert.Equal(new TargetRevalidation(TargetMatch.Changed, reason), f.Compare());
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("partition")]
    [InlineData("volume")]
    public void MissingTargetsAreExplicit(string missing)
    {
        var f = new Fixture();
        var snapshot = f.Snapshot();
        snapshot = missing switch { "disk" => snapshot with { Disks = [] }, "partition" => snapshot with { Partitions = [] }, _ => snapshot with { Volumes = [] } };
        Assert.Equal(TargetMatch.Missing, f.Compare(A(snapshot)).Outcome);
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("partition")]
    [InlineData("volume")]
    public void DuplicatedIdentityIsAmbiguous(string duplicate)
    {
        var f = new Fixture();
        var snapshot = f.Snapshot();
        snapshot = duplicate switch { "disk" => snapshot with { Disks = [f.Disk, f.Disk] }, "partition" => snapshot with { Partitions = [f.Partition, f.Partition] }, _ => snapshot with { Volumes = [f.Volume, f.Volume] } };
        Assert.Equal(TargetMatch.Ambiguous, f.Compare(A(snapshot)).Outcome);
    }

    [Theory]
    [InlineData(ObservationAvailability.AccessDenied, TargetMatch.ObservationUnavailable)]
    [InlineData(ObservationAvailability.Unavailable, TargetMatch.ObservationUnavailable)]
    [InlineData(ObservationAvailability.Unsupported, TargetMatch.Unsupported)]
    [InlineData(ObservationAvailability.Ambiguous, TargetMatch.Ambiguous)]
    public void InventoryFailureCannotProveMissingOrExact(ObservationAvailability availability, TargetMatch expected) =>
        Assert.Equal(expected, new Fixture().Compare(Observations.Failure<WindowsStorageSnapshot>(availability, "test")).Outcome);

    [Fact]
    public void MissingUniqueIdentityDoesNotBecomeHeuristicMatch()
    {
        var f = new Fixture();
        f.Disk = f.Disk with { UniqueId = Observations.Failure<string>(ObservationAvailability.Unavailable, "missing") };
        Assert.Equal(StorageIdentityStrength.Unavailable, f.Disk.IdentityStrength);
        Assert.Equal(TargetMatch.ObservationUnavailable, f.Compare().Outcome);
    }

    [Theory]
    [InlineData(0, 17)]
    [InlineData(2, 14)]
    [InlineData(2, 15)]
    public void ReducedIdentityRemainsUnsupported(uint format, uint bus)
    {
        var f = new Fixture();
        f.Disk = f.Disk with { UniqueIdFormat = A(format), BusType = A(bus) };
        Assert.Equal(StorageIdentityStrength.Reduced, f.Disk.IdentityStrength);
        Assert.Equal(TargetMatch.Unsupported, f.Compare().Outcome);
    }

    [Fact]
    public void MbrDoesNotAcquireGptSemantics()
    {
        var f = new Fixture();
        f.Disk = f.Disk with { PartitionStyle = A(1U) };
        Assert.Equal(TargetMatch.Unsupported, f.Compare().Outcome);
    }

    [Fact]
    public void BitLockerForDifferentVolumeIsRejectedEvenWithSameLetter()
    {
        var f = new Fixture();
        f.BitLocker = f.BitLocker with { VolumeGuid = Guid.NewGuid() };
        Assert.Equal(TargetMismatchReason.BitLockerWrongVolume, f.Compare().Reason);
    }

    [Fact]
    public void BitLockerDeniedAndUnknownAreBlocking()
    {
        var f = new Fixture();
        Assert.Equal(TargetMatch.ObservationUnavailable, TargetRevalidator.Compare(f.Plan, f.Binding, A(f.Snapshot()),
            Observations.Failure<BitLockerVolumeObservation>(ObservationAvailability.AccessDenied, "denied")).Outcome);
        f.BitLocker = f.BitLocker with { ProtectionStatus = A(2U) };
        Assert.Equal(TargetMatch.ObservationUnavailable, f.Compare().Outcome);
    }

    [Fact]
    public void OldPlanCannotAcquireBindingImplicitly()
    {
        var f = new Fixture();
        Assert.Equal(TargetMismatchReason.BindingRequired, TargetRevalidator.Compare(f.Plan, null, A(f.Snapshot()), A(f.BitLocker)).Reason);
        Assert.Equal(TargetMatch.Unsupported, TargetRevalidator.Compare(f.Plan, f.Binding with { SchemaVersion = 99 }, A(f.Snapshot()), A(f.BitLocker)).Outcome);
        Assert.Equal(TargetMismatchReason.PlanBindingChanged, TargetRevalidator.Compare(f.Plan with { EvidenceHash = "new" }, f.Binding, A(f.Snapshot()), A(f.BitLocker)).Reason);
    }

    private static Observation<T> A<T>(T value) => Observations.Available(value);

    internal sealed class Fixture
    {
        public PreparedMigrationPlan Plan { get; }
        public ExactTargetBinding Binding { get; }
        public DiskObservation Disk { get; set; }
        public PartitionObservation Partition { get; set; }
        public VolumeObservation Volume { get; set; }
        public BitLockerVolumeObservation BitLocker { get; set; }
        public Fixture()
        {
            var diskId = Guid.NewGuid(); var partitionId = Guid.NewGuid(); var volumeId = Guid.NewGuid(); var type = Guid.NewGuid();
            Plan = new(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "evidence", DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue, "test", "debian", "plugin", PreparedPlanState.Prepared, "Non-executable");
            Binding = new(1, Plan.PlanId, Plan.ApprovalId, Plan.Identity, Plan.ProfileRevisionId, Plan.EvidenceHash,
                new("eui-identity", 2, diskId, partitionId, volumeId), new(1000, 2, 512, 4096, 100, 800, type, "NTFS", "Windows"), new(0, 1, 'C'));
            Disk = new(A(0U), A("eui-identity"), A(2U), A("serial"), A(17U), A("disk"), A(1000UL), A(2U), A(diskId), A(512U), A(4096U));
            Partition = new(A(0U), A(1U), A(partitionId), A(type), A(100UL), A(800UL), A(false), A(true), A(false), A('C'), A(ImmutableArray<string>.Empty));
            Volume = new(A(volumeId), A(partitionId), A("NTFS"), A("Windows"), A('C'), A("OK"), A(false));
            BitLocker = new(volumeId, A('C'), A(0U), A(0U), A(0U), A(0U));
        }
        public WindowsStorageSnapshot Snapshot() => new([Disk], [Partition], [Volume]);
        public TargetRevalidation Compare(Observation<WindowsStorageSnapshot>? fresh = null) =>
            TargetRevalidator.Compare(Plan, Binding, fresh ?? A(Snapshot()), A(BitLocker));
    }
}
