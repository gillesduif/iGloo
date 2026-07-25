using FluentAssertions;
using Igloo.Core.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

public class LinuxInstallNamingTests
{
    [Fact]
    public void Single_install_with_single_entry_is_named_from_that_entry()
    {
        var entries = new[] { new EfiBootEntries.BootEntry(0x0003, "Fedora") };

        var (name, entryIndex) = WindowsPreflightChecker.ResolveInstallIdentity(
            groupCount: 1, linuxEntries: entries);

        name.Should().Be("Fedora");
        entryIndex.Should().Be(0x0003);
    }

    [Fact]
    public void Lowercase_entry_descriptions_are_capitalised()
    {
        var entries = new[] { new EfiBootEntries.BootEntry(0x0001, "ubuntu") };

        var (name, _) = WindowsPreflightChecker.ResolveInstallIdentity(1, entries);

        name.Should().Be("Ubuntu");
    }

    // The exact regression: one Fedora install on a VM that still carries a stale "ubuntu"
    // entry from an earlier test. Index-pairing named Fedora "ubuntu"; it must now stay generic.
    [Fact]
    public void Single_install_with_a_stale_extra_entry_does_not_borrow_a_name()
    {
        var entries = new[]
        {
            new EfiBootEntries.BootEntry(0x0001, "ubuntu"),   // stale leftover, lower boot index
            new EfiBootEntries.BootEntry(0x0003, "Fedora"),   // the install that is actually present
        };

        var (name, entryIndex) = WindowsPreflightChecker.ResolveInstallIdentity(1, entries);

        name.Should().Be("Linux installation");
        entryIndex.Should().BeNull();
    }

    [Fact]
    public void Multiple_installs_are_not_paired_to_entries_by_index()
    {
        var entries = new[]
        {
            new EfiBootEntries.BootEntry(0x0001, "ubuntu"),
            new EfiBootEntries.BootEntry(0x0003, "Fedora"),
        };

        var (name, entryIndex) = WindowsPreflightChecker.ResolveInstallIdentity(
            groupCount: 2, linuxEntries: entries);

        name.Should().Be("Linux installation");
        entryIndex.Should().BeNull();
    }

    [Fact]
    public void Install_with_no_matching_entry_stays_generic()
    {
        var (name, entryIndex) = WindowsPreflightChecker.ResolveInstallIdentity(
            groupCount: 1, linuxEntries: []);

        name.Should().Be("Linux installation");
        entryIndex.Should().BeNull();
    }

    //   Splitting a merged run into per-distro installs

    private const long GiB = 1024L * 1024 * 1024;
    private const string LinuxFs = "{0fc63daf-8483-4772-8e79-3d69d8477de4}";
    private const string LinuxLvm = "{e6d6d379-f507-44c2-a23c-238f2a3df928}";

    private static PartitionInfo Part(int index, long sizeBytes, string gptType) =>
        new(index, "Unknown", sizeBytes, null, false, false, GptType: gptType);

    // The real VM layout: Fedora's /boot (2 GiB) + LVM root (48 GiB), then Mint's root (50 GiB).
    [Fact]
    public void A_fedora_boot_plus_lvm_then_a_mint_root_splits_into_two()
    {
        var run = new List<PartitionInfo>
        {
            Part(5, 2 * GiB, LinuxFs),    // Fedora /boot
            Part(6, 48 * GiB, LinuxLvm),  // Fedora root (LVM)
            Part(8, 50 * GiB, LinuxFs),   // Mint root
        };

        var groups = WindowsPreflightChecker.SplitRunByDistro(run);

        groups.Should().HaveCount(2);
        groups[0].Select(p => p.Index).Should().Equal(5, 6);
        groups[1].Select(p => p.Index).Should().Equal(8);
    }

    [Fact]
    public void The_split_is_independent_of_partition_order()
    {
        var run = new List<PartitionInfo>
        {
            Part(8, 50 * GiB, LinuxFs),   // Mint root first
            Part(5, 2 * GiB, LinuxFs),    // Fedora /boot
            Part(6, 48 * GiB, LinuxLvm),  // Fedora root (LVM)
        };

        var groups = WindowsPreflightChecker.SplitRunByDistro(run);

        groups.Should().HaveCount(2);
        groups[0].Select(p => p.Index).Should().Equal(8);
        groups[1].Select(p => p.Index).Should().Equal(5, 6);
    }

    [Fact]
    public void The_lvm_group_is_named_fedora_and_the_plain_group_ubuntu()
    {
        var groups = new List<List<PartitionInfo>>
        {
            new() { Part(5, 2 * GiB, LinuxFs), Part(6, 48 * GiB, LinuxLvm) },
            new() { Part(8, 50 * GiB, LinuxFs) },
        };

        var ok = WindowsPreflightChecker.TryAttributeDistros(
            groups, new[] { "Fedora", "Ubuntu" }, out var attributed);

        ok.Should().BeTrue();
        attributed.Should().HaveCount(2);
        attributed.Single(a => a.Parts.Any(p => p.GptType == LinuxLvm)).Name.Should().Be("Fedora");
        attributed.Single(a => a.Parts.All(p => p.GptType != LinuxLvm)).Name.Should().Be("Ubuntu");
    }

    [Fact]
    public void Attribution_refuses_when_neither_group_has_lvm()
    {
        var groups = new List<List<PartitionInfo>>
        {
            new() { Part(5, 50 * GiB, LinuxFs) },
            new() { Part(6, 50 * GiB, LinuxFs) },
        };

        WindowsPreflightChecker.TryAttributeDistros(
            groups, new[] { "Ubuntu", "Debian" }, out _).Should().BeFalse();
    }
}
