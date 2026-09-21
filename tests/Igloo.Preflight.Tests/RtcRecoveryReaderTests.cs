using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class RtcRecoveryReaderTests
{
    [Fact]
    public void ValueAbsenceIsCapturedWithoutInventingZero()
    {
        var expected = new RtcRegistryStateV1(Observations.Available(true),
            Observations.Failure<RegistryValueV1>(ObservationAvailability.Absent, "RtcRegistryValueAbsent"));
        var result = new WindowsRtcRecoveryReader(() => expected).Capture();
        Assert.True(result.KeyPresent.Value);
        Assert.Equal(ObservationAvailability.Absent, result.RealTimeIsUniversal.Availability);
        Assert.Throws<InvalidOperationException>(() => result.RealTimeIsUniversal.Value);
    }

    [Fact]
    public void MissingKeyIsDistinctFromMissingValue()
    {
        var result = new WindowsRtcRecoveryReader(() => new(Observations.Available(false),
            Observations.Failure<RegistryValueV1>(ObservationAvailability.Absent, "RtcRegistryKeyAbsent"))).Capture();
        Assert.False(result.KeyPresent.Value);
        Assert.Equal(ObservationAvailability.Absent, result.RealTimeIsUniversal.Availability);
    }

    [Theory]
    [InlineData(4U, new byte[] { 1, 0, 0, 0 })]
    [InlineData(11U, new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(1U, new byte[] { 49, 0 })]
    [InlineData(999U, new byte[] { 255, 0, 137 })]
    [InlineData(3U, new byte[] { })]
    public void NativeTypeAndRawBytesArePreserved(uint nativeType, byte[] bytes)
    {
        var reads = 0;
        var result = new WindowsRtcRecoveryReader(() =>
        {
            reads++;
            return new(Observations.Available(true),
                Observations.Available(new RegistryValueV1(nativeType, [.. bytes])));
        }).Capture();
        Assert.Equal(2, reads);
        Assert.Equal(nativeType, result.RealTimeIsUniversal.Value.NativeType);
        Assert.Equal(bytes, result.RealTimeIsUniversal.Value.RawData);
    }

    [Fact]
    public void RecaptureChangingTypeOrContentIsAmbiguous()
    {
        var reads = 0;
        var result = new WindowsRtcRecoveryReader(() => new(Observations.Available(true),
            Observations.Available(new RegistryValueV1(++reads == 1 ? 4U : 11U, [1, 0, 0, 0])))).Capture();
        Assert.Equal(ObservationAvailability.Ambiguous, result.RealTimeIsUniversal.Availability);
    }

    [Theory]
    [InlineData(2, ObservationAvailability.Absent)]
    [InlineData(5, ObservationAvailability.AccessDenied)]
    [InlineData(1314, ObservationAvailability.AccessDenied)]
    [InlineData(50, ObservationAvailability.Unsupported)]
    [InlineData(234, ObservationAvailability.Ambiguous)]
    [InlineData(1018, ObservationAvailability.Ambiguous)]
    [InlineData(1117, ObservationAvailability.Unavailable)]
    public void NativeFailuresRetainTheirDistinctMeaning(int status, ObservationAvailability availability) =>
        Assert.Equal(availability, WindowsRtcRecoveryReader.ClassifyStatus(status));

    [Fact]
    public void DenialIsNotReportedAsAbsentOrZero()
    {
        var result = new WindowsRtcRecoveryReader(() => new(
            Observations.Failure<bool>(ObservationAvailability.AccessDenied, "Denied"),
            Observations.Failure<RegistryValueV1>(ObservationAvailability.AccessDenied, "Denied"))).Capture();
        Assert.Equal(ObservationAvailability.AccessDenied, result.KeyPresent.Availability);
        Assert.Equal(ObservationAvailability.AccessDenied, result.RealTimeIsUniversal.Availability);
        Assert.Throws<InvalidOperationException>(() => result.RealTimeIsUniversal.Value);
    }
}
