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
    [InlineData(200L, null, null, 0)]
    [InlineData(200L, 100L, 90L, 0)]
    [InlineData(200L, 100L, 200L, 100)]
    [InlineData(200L, 100L, 250L, 100)]
    public void ShrinkProjectionUsesCurrentSizeRatherThanMaximum(long current, long? minimum,
        long? maximum, long expected) =>
        Assert.Equal(expected, WindowsPreflightChecker.ProjectShrinkable(current, minimum, maximum));

    [Theory]
    [InlineData(null, null, BitLockerState.Unknown)]
    [InlineData(0U, 1U, BitLockerState.NotEncrypted)]
    [InlineData(3U, 1U, BitLockerState.DecryptionInProgress)]
    [InlineData(5U, 0U, BitLockerState.DecryptionInProgress)]
    [InlineData(2U, 1U, BitLockerState.EncryptedAndUnlocked)]
    [InlineData(1U, 0U, BitLockerState.SuspendedProtection)]
    public void BitLockerProjectionRetainsLegacyInterpretation(uint? conversion, uint? protection, BitLockerState expected) =>
        Assert.Equal(expected, WindowsPreflightChecker.ProjectBitLocker(conversion, protection));

    [Theory]
    [InlineData(BitLockerState.EncryptedAndUnlocked)]
    [InlineData(BitLockerState.EncryptedAndLocked)]
    [InlineData(BitLockerState.SuspendedProtection)]
    [InlineData(BitLockerState.DecryptionInProgress)]
    [InlineData(BitLockerState.Unknown)]
    public void Unverified_or_incomplete_decryption_blocks_install_preflight(BitLockerState state)
    {
        var findings = WindowsPreflightChecker.BuildFindings(
            isUefi: true, secureBoot: false, tpmPresent: true, bitLocker: state, totalRam: 8L << 30);

        Assert.Contains(findings, finding => finding.Severity == FindingSeverity.Blocker &&
            finding.Code.StartsWith("BITLOCKER_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    [InlineData("0")]
    public void UnknownSecureBootStateCannotBecomeDisabled(object? state) =>
        Assert.Throws<InvalidOperationException>(() =>
            WindowsPreflightChecker.RequireObservedSecureBootState(state));

    [Theory]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, true)]
    public void ObservedFirmwareTypeDistinguishesBiosAndUefi(bool observed, int type, bool expected) =>
        Assert.Equal(expected, WindowsPreflightChecker.RequireObservedUefiFirmware(observed, type));

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 3)]
    public void UnknownFirmwareTypeCannotBeReportedAsLegacyBios(bool observed, int type) =>
        Assert.Throws<InvalidOperationException>(() =>
            WindowsPreflightChecker.RequireObservedUefiFirmware(observed, type));

    [Fact]
    public void ObservedSecureBootStateUsesOnlyExplicitRegistryValues()
    {
        Assert.False(WindowsPreflightChecker.RequireObservedSecureBootState(0));
        Assert.True(WindowsPreflightChecker.RequireObservedSecureBootState(1));
    }

    [Theory]
    [InlineData((byte)0, false)]
    [InlineData((byte)1, true)]
    public void MissingRegistryStateCanUseObservedUefiSecureBootByte(byte value, bool expected) =>
        Assert.Equal(expected, WindowsPreflightChecker.RequireObservedFirmwareSecureBootState(
            new FirmwareVariableObservation([value], 0)));

    [Fact]
    public void MissingRegistryStateDoesNotTurnFailedOrMalformedFirmwareReadsIntoDisabled()
    {
        FirmwareVariableObservation[] unverified =
        [
            new(null, 203), // Variable undefined is different from an observed zero.
            new(null, 1314), // Read privilege denied.
            new(null, 50), // API unsupported is not an observed Secure Boot state.
            new([2], 0),
            new([0, 1], 0),
            new([], 0),
            new([0], 5), // Contradictory data/error is not a valid read.
        ];

        foreach (var state in unverified)
        {
            var failure = Assert.Throws<InvalidOperationException>(() =>
                WindowsPreflightChecker.RequireObservedFirmwareSecureBootState(state));
            Assert.Contains("check Secure Boot State", failure.Message, StringComparison.Ordinal);
        }
    }

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
