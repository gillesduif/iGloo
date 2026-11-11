using System.Security;
using Microsoft.Win32;

namespace Igloo.Core.Safety;

/// <summary>What the machine-wide protected-host marker says about this computer.</summary>
public enum HostProtectionState
{
    NotProtected,
    Protected,
    Unreadable,
}

/// <summary>Raised in front of a disk, boot or firmware write that the protected-host marker forbids.</summary>
public sealed class HostProtectedException : Exception
{
    public HostProtectedException() : this(HostProtection.Describe(HostProtectionState.Protected)!) { }

    public HostProtectedException(string message) : base(message) { }

    public HostProtectedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// The machine-wide marker that keeps iGloo from changing a computer. iGloo only reads it;
/// an administrator creates and removes it.
/// </summary>
public static class HostProtection
{
    public const string RegistryKeyPath = @"SOFTWARE\Policies\iGloo";
    public const string RegistryValueName = "ProtectedHost";

    private static readonly AsyncLocal<Func<HostProtectionState>?> ScopedProbe = new();
    private static Func<HostProtectionState>? s_processProbe;

    public static HostProtectionState ReadState() =>
        (ScopedProbe.Value ?? Volatile.Read(ref s_processProbe) ?? ReadMachineMarker)();

    /// <summary>Call immediately before a write below the user profile; an unreadable marker refuses too.</summary>
    public static void RequireWritable(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var state = ReadState();
        if (state != HostProtectionState.NotProtected)
            throw new HostProtectedException($"{Describe(state)} Not started: {operation}.");
    }

    /// <summary>The sentence the wizard and the CLI show while the marker is active; null when it is not.</summary>
    public static string? Describe(HostProtectionState state) => state switch
    {
        HostProtectionState.Protected =>
            @"This computer is marked as a protected host (HKLM\" + RegistryKeyPath + ", " + RegistryValueName + "). " +
            "iGloo does not change disks, partitions, boot entries or firmware settings on it. " +
            "Use a disposable virtual machine or a test computer.",
        HostProtectionState.Unreadable =>
            @"The protected-host marker (HKLM\" + RegistryKeyPath + ", " + RegistryValueName + ") could not be read. " +
            "iGloo does not change disks, partitions, boot entries or firmware settings until it can. " +
            "Check the value in Registry Editor.",
        _ => null,
    };

    // Absent or integer 0: not protected. Any other integer: protected. Anything else is not a marker iGloo understands.
    internal static HostProtectionState Classify(object? value) => value switch
    {
        null => HostProtectionState.NotProtected,
        int number => number == 0 ? HostProtectionState.NotProtected : HostProtectionState.Protected,
        long number => number == 0 ? HostProtectionState.NotProtected : HostProtectionState.Protected,
        _ => HostProtectionState.Unreadable,
    };

    internal static HostProtectionState ReadMachineMarker()
    {
        if (!OperatingSystem.IsWindows())
            return HostProtectionState.NotProtected;
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(RegistryKeyPath, writable: false);
            return key is null
                ? HostProtectionState.NotProtected
                : Classify(key.GetValue(RegistryValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException
                                       or ObjectDisposedException)
        {
            return HostProtectionState.Unreadable;
        }
    }

    // Test seams. Product assemblies cannot see them; HostProtectionSourceTests pins that.
    internal static IDisposable UseForCurrentScope(HostProtectionState state)
    {
        var previous = ScopedProbe.Value;
        ScopedProbe.Value = () => state;
        return new ScopeReset(previous);
    }

    internal static void UseForTestProcess(HostProtectionState state) =>
        Volatile.Write(ref s_processProbe, () => state);

    private sealed class ScopeReset(Func<HostProtectionState>? previous) : IDisposable
    {
        public void Dispose() => ScopedProbe.Value = previous;
    }
}
