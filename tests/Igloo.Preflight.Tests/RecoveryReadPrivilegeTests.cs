using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class RecoveryReadPrivilegeTests
{
    [Theory]
    [InlineData(true, 0, 0)]
    [InlineData(true, 1300, 1300)]
    [InlineData(false, 5, 5)]
    [InlineData(false, 0, 31)]
    public void PrivilegeAdjustmentRequiresBothSuccessAndZeroError(bool adjusted, int error, int expected)
        => Assert.Equal(expected, FirmwareNative.CheckPrivilegeAdjustment(adjusted, error));

    [Theory]
    [InlineData(5, ObservationAvailability.AccessDenied)]
    [InlineData(1300, ObservationAvailability.AccessDenied)]
    [InlineData(1314, ObservationAvailability.AccessDenied)]
    [InlineData(50, ObservationAvailability.Unsupported)]
    [InlineData(203, ObservationAvailability.Unavailable)]
    [InlineData(31, ObservationAvailability.Unavailable)]
    public void PrivilegeFailureNeverCallsGetterOrProvesVariableAbsence(int error, ObservationAvailability state)
    {
        var reads = 0;
        var reader = new WindowsFirmwareReader(() => error, (_, _) => { reads++; return new([0, 0], 0); });
        foreach (var raw in new[] { reader.ReadBootOrder(), reader.ReadBootNext(), reader.ReadBootEntry(0) })
        {
            Assert.Equal(state, raw.Availability);
            Assert.Equal(error, raw.NativeError);
            Assert.Equal(state, EfiRecoveryParser.Observe(raw).Bytes.Availability);
            Assert.Equal(state, raw.VariableAttributes.Availability);
            Assert.Null(raw.Data);
        }
        Assert.Equal(0, reads);
    }

    [Fact]
    public void EveryReadRechecksPrivilegeAndRetainsTheActualGetterResult()
    {
        var calls = new List<string>();
        var reader = new WindowsFirmwareReader(() => { calls.Add("privilege"); return 0; }, (name, _) =>
        { calls.Add(name); return new(null, 203); });
        Assert.Equal(ObservationAvailability.Absent, reader.ReadBootNext().Availability);
        reader.ReadBootOrder(); reader.ReadBootEntry(0x12);
        Assert.Equal(new[] { "privilege", "BootNext", "privilege", "BootOrder", "privilege", "Boot0012" }, calls);
    }
}
