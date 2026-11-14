using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class FastStartupTests
{
    private static (bool Changed, int? Remembered, int? Written) TurnOff(int? current, int? remembered)
    {
        int? written = null;
        var changed = FastStartup.TurnOff(current, remembered, earlier => remembered = earlier, value => written = value);
        return (changed, remembered, written);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(null, FastStartup.NotSet)]
    public void Turning_it_off_remembers_what_Windows_had(int? current, int expected)
    {
        var (changed, remembered, written) = TurnOff(current, remembered: null);

        Assert.True(changed);
        Assert.Equal(expected, remembered);
        Assert.Equal(0, written);
    }

    [Fact]
    public void What_was_already_off_is_neither_written_nor_remembered()
    {
        var (changed, remembered, written) = TurnOff(current: 0, remembered: null);

        Assert.False(changed);
        Assert.Null(remembered);
        Assert.Null(written);
    }

    [Fact]
    public void A_second_installation_keeps_the_value_from_before_the_first()
    {
        // Windows or the user turned it on again after the first installation.
        var (changed, remembered, written) = TurnOff(current: 1, remembered: FastStartup.NotSet);

        Assert.True(changed);
        Assert.Equal(FastStartup.NotSet, remembered);
        Assert.Equal(0, written);
    }

    [Fact]
    public void Removing_Linux_puts_back_only_what_iGloo_changed()
    {
        var done = new List<string>();
        bool Restore(int? remembered) => FastStartup.Restore(remembered,
            value => done.Add("write " + value), () => done.Add("delete"), () => done.Add("forget"));

        Assert.False(Restore(null));
        Assert.Empty(done);

        Assert.True(Restore(1));
        Assert.Equal(["write 1", "forget"], done);

        done.Clear();
        Assert.True(Restore(FastStartup.NotSet));
        Assert.Equal(["delete", "forget"], done);
    }

    [Fact]
    public void Registration_turns_it_off_and_the_removal_of_the_last_Linux_puts_it_back()
    {
        var repository = AppContext.BaseDirectory;
        while (!File.Exists(Path.Join(repository, "installer", "iGloo.iss")))
            repository = Path.GetDirectoryName(repository) ?? throw new InvalidOperationException("No repository root.");
        var preflight = Path.Join(repository, "src", "Igloo.Preflight");

        var registration = File.ReadAllText(Path.Join(preflight, "DirectInstallService.cs"));
        var removal = File.ReadAllText(Path.Join(preflight, "LinuxRemovalService.cs"));
        var installer = File.ReadAllText(Path.Join(repository, "installer", "iGloo.iss"));

        var register = registration[registration.IndexOf("private void RegisterBootEntry(", StringComparison.Ordinal)..];
        Assert.Contains("FastStartup.TurnOffForDualBoot(_logger);",
            register[..register.IndexOf("private void SetRtcUniversalTime", StringComparison.Ordinal)], StringComparison.Ordinal);
        // Together with the clock: only when no Linux can be left.
        var clock = removal.IndexOf("RestoreRtcLocalTime();", StringComparison.Ordinal);
        Assert.Equal("FastStartup.RestoreAfterLinux(_logger);",
            removal[(clock + "RestoreRtcLocalTime();".Length)..].TrimStart().Split('\n')[0].Trim());
        Assert.Contains("uninsdeletekeyifempty", installer, StringComparison.Ordinal);
    }
}
