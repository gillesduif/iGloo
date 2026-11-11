using System.Diagnostics;
using Igloo.Core.Host;
using Igloo.Core.Safety;
using Xunit;

namespace Igloo.Command.Tests;

public sealed class HostProtectionGuardTests
{
    private const string Guard = "HostProtection.RequireWritable(";

    // Every raw primitive that changes disks, partitions, boot configuration, firmware variables,
    // BitLocker or power state. Each occurrence under src/ must sit behind the guard.
    private static readonly string[] WritePrimitives =
    [
        "WriteVariableNative(", "WriteVariableExNative(", "CreateFileNative(",
        "\"diskpart.exe\"", "\"bcdedit.exe\"", "\"manage-bde.exe\"", "\"shutdown.exe\"",
        "InvokeMethod(\"Resize\"", "InvokeMethod(\"DeleteObject\"", "InvokeMethod(\"CreatePartition\"",
        "InvokeMethod(\"Format\"", "InvokeMethod(call.Method",
        "Registry.LocalMachine.CreateSubKey(", "writable: true",
    ];

    // Occurrences that are declarations or reads, with the reason they need no guard.
    private static readonly (string File, string Primitive, string Reason)[] Exempt =
    [
        ("FirmwareNative.cs", "private static partial bool WriteVariableNative(", "P/Invoke declaration"),
        ("FirmwareNative.cs", "private static partial bool WriteVariableExNative(", "P/Invoke declaration"),
        ("NativeMethods.cs", "private static partial SafeFileHandle CreateFileNative(", "P/Invoke declaration"),
        ("WindowsBcdReader.cs", "\"bcdedit.exe\"), \"/enum firmware\")", "read-only listing"),
        ("ShellNoiseSuppressor.cs", "Registry.CurrentUser.CreateSubKey(PolicyKey, writable: true)",
            "per-user AutoPlay policy, restored on dispose"),
    ];

    [Theory]
    [InlineData(HostProtectionState.Protected)]
    [InlineData(HostProtectionState.Unreadable)]
    public async Task Windows_maintenance_refuses_and_starts_no_process(HostProtectionState state)
    {
        using var scope = HostProtection.UseForCurrentScope(state);
        var launched = new List<ProcessStartInfo>();
        var maintenance = new WindowsMaintenance(info => { launched.Add(info); return null; });

        await Assert.ThrowsAsync<HostProtectedException>(() => maintenance.StartBitLockerDecryptionAsync("C:"));
        Assert.Throws<HostProtectedException>(maintenance.RestartToFirmware);
        Assert.Throws<HostProtectedException>(maintenance.RestartToInstaller);
        Assert.Empty(launched);
    }

    [Fact]
    public void Without_the_marker_a_restart_reaches_the_launcher()
    {
        var launched = new List<ProcessStartInfo>();

        new WindowsMaintenance(info => { launched.Add(info); return null; }).RestartToInstaller();

        Assert.EndsWith("shutdown.exe", Assert.Single(launched).FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_write_primitive_in_the_platform_sits_behind_the_guard()
    {
        var unguarded = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in PlatformSources())
        {
            var text = File.ReadAllText(file);
            foreach (var primitive in WritePrimitives)
                for (var at = text.IndexOf(primitive, StringComparison.Ordinal); at >= 0;
                     at = text.IndexOf(primitive, at + primitive.Length, StringComparison.Ordinal))
                {
                    seen.Add(primitive);
                    if (!IsExempt(file, text, at) && !GuardEncloses(text, at))
                        unguarded.Add($"{Path.GetFileName(file)}:{LineOf(text, at)} {primitive}");
                }
        }

        Assert.Empty(unguarded);
        // A renamed primitive must not make this test pass by matching nothing.
        Assert.Equal(WritePrimitives.Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("void A() { HostProtection.RequireWritable(\"x\"); if (a) { Run(\"diskpart.exe\"); } }", true)]
    [InlineData("void A() { HostProtection.RequireWritable(\"x\"); } void B() { Run(\"diskpart.exe\"); }", false)]
    [InlineData("void A() { if (a) { HostProtection.RequireWritable(\"x\"); } Run(\"diskpart.exe\"); }", false)]
    [InlineData("void B() { Run(\"diskpart.exe\"); }", false)]
    public void The_guard_counts_only_inside_the_block_that_called_it(string source, bool guarded) =>
        Assert.Equal(guarded, GuardEncloses(source, source.IndexOf("\"diskpart.exe\"", StringComparison.Ordinal)));

    [Fact]
    public void Product_code_reads_the_marker_and_neither_writes_nor_overrides_it()
    {
        var offenders = PlatformSources()
            .Where(file => !file.EndsWith(Path.Join("Safety", "HostProtection.cs"), StringComparison.OrdinalIgnoreCase))
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains("UseForCurrentScope", StringComparison.Ordinal) ||
                       text.Contains("UseForTestProcess", StringComparison.Ordinal) ||
                       text.Contains("HostProtection.RegistryKeyPath", StringComparison.Ordinal) ||
                       text.Contains("HostProtection.RegistryValueName", StringComparison.Ordinal) ||
                       text.Contains(@"Policies\iGloo", StringComparison.OrdinalIgnoreCase);
            })
            .Select(Path.GetFileName).ToArray();

        Assert.Empty(offenders);

        var marker = File.ReadAllText(Path.Join(RepositoryRoot(), "src", "Igloo.Core", "Safety", "HostProtection.cs"));
        Assert.Contains("writable: false", marker, StringComparison.Ordinal);
        foreach (var write in new[] { "CreateSubKey", "SetValue", "DeleteValue", "DeleteSubKey", "writable: true" })
            Assert.DoesNotContain(write, marker, StringComparison.Ordinal);
    }

    private static bool IsExempt(string file, string text, int at) => Exempt.Any(exempt =>
        file.EndsWith(exempt.File, StringComparison.OrdinalIgnoreCase) &&
        LineText(text, at).Contains(exempt.Primitive, StringComparison.Ordinal));

    // The nearest guard call before the primitive counts only when the primitive is still inside
    // the block that made the call: between the two, no brace closes that block.
    private static bool GuardEncloses(string text, int at)
    {
        var guard = text.LastIndexOf(Guard, at, StringComparison.Ordinal);
        if (guard < 0) return false;
        var depth = 0;
        for (var index = guard; index < at; index++)
        {
            if (text[index] == '{') depth++;
            else if (text[index] == '}' && --depth < 0) return false;
        }
        return true;
    }

    private static int LineOf(string text, int at) => text.AsSpan(0, at).Count('\n') + 1;

    private static string LineText(string text, int at)
    {
        var start = text.LastIndexOf('\n', at) + 1;
        var end = text.IndexOf('\n', at);
        return text[start..(end < 0 ? text.Length : end)];
    }

    private static IEnumerable<string> PlatformSources()
    {
        var source = Path.Join(RepositoryRoot(), "src");
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase) &&
                           !file.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Igloo.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
