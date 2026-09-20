using Igloo.Core.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class ObservationCompatibilityTests
{
    [Fact]
    public void DiskProjectionKeepsOrdinalPathAndUnallocatedCapacity()
    {
        var disk = WindowsPreflightChecker.ProjectDisk(7, "disk", 1000, 800, "GPT", []);
        Assert.Equal(@"\\.\PHYSICALDRIVE7", disk.DeviceId);
        Assert.Equal(200, disk.FreeBytes);
        Assert.Equal(-100, WindowsPreflightChecker.ProjectDisk(7, "disk", 1000, 1100, "GPT", []).FreeBytes);
    }

    [Fact]
    public void PartitionProjectionKeepsMissingFactsAndStableOffsetOrdering()
    {
        Assert.Equal(-1, WindowsPreflightChecker.ProjectOffset(null));
        Assert.Null(WindowsPreflightChecker.ProjectGptType(null));
        Assert.Equal("{type}", WindowsPreflightChecker.ProjectGptType(" {type} "));
        PartitionInfo Part(int number, long offset) => new(number, "Unknown", 100, null, false, false, OffsetBytes: offset);
        Assert.Equal(new[] { 2, 3, 1 }, WindowsPreflightChecker.OrderPartitions([Part(1, -1), Part(2, 50), Part(3, 50)]).Select(p => p.Index));
    }

    [Theory]
    [InlineData(null, null, 0)]
    [InlineData(100L, 90L, 0)]
    [InlineData(100L, 200L, 100)]
    public void ShrinkProjectionPreservesZeroFallback(long? minimum, long? maximum, long expected) =>
        Assert.Equal(expected, WindowsPreflightChecker.ProjectShrinkable(minimum, maximum));

    [Theory]
    [InlineData(null, null, BitLockerState.Unknown)]
    [InlineData(0U, 1U, BitLockerState.NotEncrypted)]
    [InlineData(3U, 1U, BitLockerState.DecryptionInProgress)]
    [InlineData(5U, 0U, BitLockerState.DecryptionInProgress)]
    [InlineData(2U, 1U, BitLockerState.EncryptedAndUnlocked)]
    [InlineData(1U, 0U, BitLockerState.SuspendedProtection)]
    public void BitLockerProjectionRetainsLegacyInterpretation(uint? conversion, uint? protection, BitLockerState expected) =>
        Assert.Equal(expected, WindowsPreflightChecker.ProjectBitLocker(conversion, protection));

    [Fact]
    public void ResizePolicyUsesLetterAndStrictlyLargerDeltaOnly()
    {
        Assert.False(PartitionResizeService.HasCandidateLetter('\0'));
        Assert.True(PartitionResizeService.HasCandidateLetter('D'));
        Assert.True(PartitionResizeService.ImprovesCandidate(101, 100));
        Assert.False(PartitionResizeService.ImprovesCandidate(100, 100));
        Assert.False(PartitionResizeService.ImprovesCandidate(0, 0));
    }

    [Fact]
    public void EfiCompatibilityRetainsExclusionsAndUnterminatedDescription()
    {
        Assert.True(EfiBootEntries.IsLinuxDescription("Fedora"));
        Assert.False(EfiBootEntries.IsLinuxDescription("Windows Linux"));
        Assert.False(EfiBootEntries.IsLinuxDescription("iGloo Linux"));
        Assert.True(EfiBootEntries.IsIglooDescription("IGLOO installer"));
        Assert.Equal("A", EfiBootEntries.ParseDescription([0, 0, 0, 0, 0, 0, 32, 0, 65, 0, 32, 0]));
    }

    [Fact]
    public void BcdCompatibilityRetainsCaseSensitivitySubstringAndDuplicateIds()
    {
        const string id = "{11111111-1111-1111-1111-111111111111}";
        var block = $"identifier {id}\r\ndescription prefix iGloo suffix";
        Assert.Equal(new[] { id, id }, DirectInstallService.ParseStaleBcdIds(block + "\r\n \t\r\n" + block, "iGloo"));
        Assert.Empty(DirectInstallService.ParseStaleBcdIds(block, "IGLOO"));
        Assert.Empty(DirectInstallService.ParseStaleBcdIds(block.Replace("identifier", "Identifier", StringComparison.Ordinal), "iGloo"));
    }
}
