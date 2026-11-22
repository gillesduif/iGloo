using System.Collections.Immutable;
using System.Xml.Linq;
using Igloo.Core.Abstractions;
using Igloo.Core.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class BootCleanupTaskTests
{
    private static readonly Guid Installer = Guid.Parse("dddd4e82-820a-4dd7-b745-e91cb5e232a6");
    private static readonly Guid BcdEntry = Guid.Parse("7a757dff-ddf9-11f1-9250-e8c97e719636");

    // bcdedit /enum firmware of the r10 run after the installer partition was removed.
    private const string ListingWithEntry = """
        Firmware Boot Manager
        ---------------------
        identifier              {fwbootmgr}
        displayorder            {bootmgr}

        Windows Boot Manager
        --------------------
        identifier              {7a757dff-ddf9-11f1-9250-e8c97e719636}
        device                  unknown
        path                    \EFI\BOOT\BOOTX64.EFI
        description             iGloo distribution installer
        resumeobject            {7a757dfa-ddf9-11f1-9250-e8c97e719636}
        """;

    private static (BootCleanupOutcome Outcome, List<string> Done) Run(bool? partitionExists, bool? entryRemains = false,
        bool started = false, bool firmwareKeepsAnEntry = false)
    {
        var done = new List<string>();
        var firmware = new List<ushort> { 0x0005, 0x0080 };
        var outcome = BootCleanupTask.Run(partitionExists,
            () => { done.Add("read start mark"); return started; },
            () => done.Add("delete bcd entry"),
            () => { done.Add("list firmware"); return [.. firmware]; },
            index =>
            {
                done.Add($"delete Boot{index:X4}");
                if (!firmwareKeepsAnEntry || index != 0x0080)
                    firmware.Remove(index);
            },
            () => { done.Add("look again"); return entryRemains; },
            () => done.Add("remove task"));
        return (outcome, done);
    }

    [Fact]
    public void Nothing_is_removed_while_the_installer_partition_waits_for_its_first_start()
    {
        var (outcome, done) = Run(partitionExists: true);

        Assert.Equal(BootCleanupOutcome.PartitionPresent, outcome);
        Assert.Equal(["read start mark"], done);
    }

    [Fact]
    public void A_started_installer_that_left_its_partition_loses_its_entries_and_the_task()
    {
        var (outcome, done) = Run(partitionExists: true, started: true);

        // The partition itself is not this task's to remove: a new attempt uses it again.
        Assert.Equal(BootCleanupOutcome.RegistrationUndone, outcome);
        Assert.Equal(["read start mark", "delete bcd entry", "list firmware", "delete Boot0005", "delete Boot0080",
            "look again", "list firmware", "remove task"], done);
    }

    [Fact]
    public void After_a_start_the_task_stays_while_a_firmware_entry_still_names_the_partition()
    {
        var (outcome, done) = Run(partitionExists: true, started: true, firmwareKeepsAnEntry: true);

        // That entry would start the installer again, so the next sign-in tries once more.
        Assert.Equal(BootCleanupOutcome.EntriesRemain, outcome);
        Assert.DoesNotContain("remove task", done);
        // Without the partition a leftover entry points at nothing and does not hold the task.
        Assert.Equal(BootCleanupOutcome.Removed, Run(partitionExists: false, firmwareKeepsAnEntry: true).Outcome);
    }

    [Fact]
    public void The_installer_volume_is_the_volume_path_of_the_one_partition_with_its_guid()
    {
        const string volume = @"\\?\Volume{64132391-9c60-4006-865e-b953b51f1027}\";
        WindowsStorageRow Seed(object? paths) => new(ImmutableDictionary.CreateRange(
            [KeyValuePair.Create("Guid", (object?)Installer.ToString("B").ToUpperInvariant()), KeyValuePair.Create("AccessPaths", paths)]));
        var other = Row("{B332BE0D-EED8-4D71-B802-3A6CB6602282}");

        Assert.Equal(volume, BootCleanupTask.InstallerVolume([other, Seed(new[] { @"I:\", volume })], Installer));
        Assert.Equal(volume, BootCleanupTask.InstallerVolume([Seed(new[] { volume })], Installer));
        // No volume path, no paths at all, another partition, the GUID twice, or no GUID to look for.
        Assert.Null(BootCleanupTask.InstallerVolume([Seed(new[] { @"I:\" })], Installer));
        Assert.Null(BootCleanupTask.InstallerVolume([Seed(null)], Installer));
        Assert.Null(BootCleanupTask.InstallerVolume([Row(Installer.ToString("B"))], Installer));
        Assert.Null(BootCleanupTask.InstallerVolume([other], Installer));
        Assert.Null(BootCleanupTask.InstallerVolume([Seed(new[] { volume }), Seed(new[] { volume })], Installer));
        Assert.Null(BootCleanupTask.InstallerVolume([Seed(new[] { volume })], Guid.Empty));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void After_a_start_the_task_also_stays_until_the_bcd_entry_is_seen_to_be_gone(bool? entryRemains)
    {
        var (outcome, done) = Run(partitionExists: true, entryRemains, started: true);

        Assert.Equal(BootCleanupOutcome.EntriesRemain, outcome);
        Assert.DoesNotContain("remove task", done);
    }

    [Fact]
    public void Nothing_is_removed_when_the_disks_could_not_be_read()
    {
        var (outcome, done) = Run(partitionExists: null, started: true);

        Assert.Equal(BootCleanupOutcome.NotRead, outcome);
        Assert.Empty(done);
    }

    [Fact]
    public void The_entries_and_the_task_go_once_the_partition_is_gone()
    {
        var (outcome, done) = Run(partitionExists: false);

        // Without the partition there is no mark to read.
        Assert.Equal(BootCleanupOutcome.Removed, outcome);
        Assert.Equal(["delete bcd entry", "list firmware", "delete Boot0005", "delete Boot0080", "look again",
            "remove task"], done);
    }

    [Fact]
    public void The_start_mark_is_the_line_grub_writes_into_the_block_this_build_made()
    {
        var fresh = DirectInstallService.AttemptBlock();
        byte[] Written(string entries)
        {
            var block = DirectInstallService.AttemptBlock();
            System.Text.Encoding.ASCII.GetBytes(entries).CopyTo(block, 25);
            return block;
        }

        Assert.False(BootCleanupTask.AttemptMarked(fresh));
        Assert.True(BootCleanupTask.AttemptMarked(Written("igloo_attempted=1\n")));
        Assert.True(BootCleanupTask.AttemptMarked(Written("other=x\nigloo_attempted=1\n")));
        // Another value, another name, a longer name or a line that is not finished is not the mark.
        Assert.False(BootCleanupTask.AttemptMarked(Written("igloo_attempted=0\n")));
        Assert.False(BootCleanupTask.AttemptMarked(Written("igloo_attempted=10\n")));
        Assert.False(BootCleanupTask.AttemptMarked(Written("not_igloo_attempted=1\n")));
        Assert.False(BootCleanupTask.AttemptMarked(Written("igloo_attempted=1")));
        // A file that is not an environment block says nothing.
        Assert.False(BootCleanupTask.AttemptMarked("igloo_attempted=1\n"u8));
        Assert.False(BootCleanupTask.AttemptMarked([]));
    }

    [Fact]
    public void The_start_mark_is_read_from_the_boot_folder_of_the_installer_volume()
    {
        var volume = Directory.CreateTempSubdirectory("igloo-start-mark-").FullName;
        try
        {
            Assert.False(DirectInstallService.ReadAttemptMark(volume));

            var folder = Directory.CreateDirectory(Path.Join(volume, "igloo-boot")).FullName;
            File.WriteAllBytes(Path.Join(folder, "grubenv"), DirectInstallService.AttemptBlock());
            Assert.False(DirectInstallService.ReadAttemptMark(volume));

            var marked = DirectInstallService.AttemptBlock();
            "igloo_attempted=1\n"u8.CopyTo(marked.AsSpan(25));
            File.WriteAllBytes(Path.Join(folder, "grubenv"), marked);
            Assert.True(DirectInstallService.ReadAttemptMark(volume));

            // A file far larger than the block is not the one this build wrote.
            File.WriteAllBytes(Path.Join(folder, "grubenv"), [.. marked, .. new byte[70000]]);
            Assert.False(DirectInstallService.ReadAttemptMark(volume));
        }
        finally
        {
            Directory.Delete(volume, recursive: true);
        }
    }

    [Fact]
    public void Without_one_readable_installer_volume_the_installer_counts_as_not_started()
    {
        // A partition without a volume path, a partition that is not there, and a failed read.
        var noVolume = new Partitions(new WindowsStorageBatch([Row(Installer.ToString("B"))]));
        var absent = new Partitions(new WindowsStorageBatch([Row("{B332BE0D-EED8-4D71-B802-3A6CB6602282}")]));
        var none = new Partitions(new WindowsStorageBatch([], new InvalidOperationException("The query failed.")));

        Assert.False(Service(noVolume).InstallerWasStarted(Installer));
        Assert.False(Service(absent).InstallerWasStarted(Installer));
        Assert.False(Service(none).InstallerWasStarted(Installer));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void The_task_stays_until_the_bcd_entry_is_seen_to_be_gone(bool? entryRemains)
    {
        var (outcome, done) = Run(partitionExists: false, entryRemains);

        Assert.Equal(BootCleanupOutcome.EntriesRemain, outcome);
        Assert.DoesNotContain("remove task", done);
    }

    [Fact]
    public void Only_a_firmware_listing_can_say_that_the_bcd_entry_is_gone()
    {
        var without = ListingWithEntry[..ListingWithEntry.IndexOf("Windows Boot Manager", StringComparison.Ordinal)];

        Assert.True(BootCleanupTask.BcdEntryRemains(ListingWithEntry, BcdEntry));
        Assert.True(BootCleanupTask.BcdEntryRemains(ListingWithEntry.ToUpperInvariant(), BcdEntry));
        Assert.False(BootCleanupTask.BcdEntryRemains(without, BcdEntry));
        // The resume object of the same block is another identifier.
        Assert.False(BootCleanupTask.BcdEntryRemains(without, Guid.Parse("7a757dfa-ddf9-11f1-9250-e8c97e719636")));
        Assert.Null(BootCleanupTask.BcdEntryRemains(null, BcdEntry));
        Assert.Null(BootCleanupTask.BcdEntryRemains("", BcdEntry));
        Assert.Null(BootCleanupTask.BcdEntryRemains("The boot configuration data store could not be opened.", BcdEntry));
    }

    [Fact]
    public void A_partition_is_found_by_its_guid_and_an_empty_answer_is_no_answer()
    {
        WindowsStorageRow[] disk = [Row(null), Row("{B332BE0D-EED8-4D71-B802-3A6CB6602282}"),
            Row("{DDDD4E82-820A-4DD7-B745-E91CB5E232A6}")];

        Assert.True(BootCleanupTask.PartitionExists(disk, Installer));
        Assert.False(BootCleanupTask.PartitionExists(disk[..2], Installer));
        Assert.Null(BootCleanupTask.PartitionExists([], Installer));
        Assert.Null(BootCleanupTask.PartitionExists(null, Installer));
        Assert.Null(BootCleanupTask.PartitionExists(disk, Guid.Empty));
    }

    [Fact]
    public void A_storage_read_that_fails_is_no_answer()
    {
        var present = new Partitions(new WindowsStorageBatch([Row(Installer.ToString("B"))]));
        var absent = new Partitions(new WindowsStorageBatch([Row("{B332BE0D-EED8-4D71-B802-3A6CB6602282}")]));
        var half = new Partitions(new WindowsStorageBatch([Row("{B332BE0D-EED8-4D71-B802-3A6CB6602282}")],
            new FormatException("One row could not be read.")));
        var none = new Partitions(new WindowsStorageBatch([], new InvalidOperationException("The query failed.")));

        Assert.True(Service(present).InstallerPartitionExists(Installer));
        Assert.False(Service(absent).InstallerPartitionExists(Installer));
        Assert.Null(Service(half).InstallerPartitionExists(Installer));
        Assert.Null(Service(none).InstallerPartitionExists(Installer));
    }

    [Theory]
    [InlineData(HostProtectionState.Protected)]
    [InlineData(HostProtectionState.Unreadable)]
    public async Task The_cleanup_refuses_on_a_protected_host_before_it_reads(HostProtectionState state)
    {
        using var scope = HostProtection.UseForCurrentScope(state);
        var storage = new Partitions(new WindowsStorageBatch([Row(Installer.ToString("B"))]));

        await Assert.ThrowsAsync<HostProtectedException>(() =>
            Service(storage).CleanUpBootEntriesAsync(Installer, BcdEntry));
        Assert.Equal(0, storage.Reads);
    }

    [Fact]
    public void The_task_starts_the_wizard_elevated_at_the_sign_in_of_one_account()
    {
        const string application = @"C:\Program Files\iGloo & Co\iGloo.exe";
        const string account = "S-1-5-21-1-2-3-1001";

        var xml = BootCleanupTask.BuildXml(application, Installer, BcdEntry, account);
        const string declaration = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>";
        Assert.StartsWith(declaration, xml, StringComparison.Ordinal);
        var task = XDocument.Parse(xml[declaration.Length..]);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Value(params string[] path) =>
            path.Aggregate(task.Root!, (element, name) => element.Element(ns + name)!).Value;

        Assert.Equal(application, Value("Actions", "Exec", "Command"));
        Assert.Equal("--boot-cleanup dddd4e82-820a-4dd7-b745-e91cb5e232a6 7a757dff-ddf9-11f1-9250-e8c97e719636",
            Value("Actions", "Exec", "Arguments"));
        Assert.Equal(account, Value("Triggers", "LogonTrigger", "UserId"));
        Assert.Equal(account, Value("Principals", "Principal", "UserId"));
        Assert.Equal("InteractiveToken", Value("Principals", "Principal", "LogonType"));
        Assert.Equal("HighestAvailable", Value("Principals", "Principal", "RunLevel"));
        // The trigger is a sequence: Enabled comes before UserId.
        Assert.Equal(["Enabled", "UserId"],
            task.Root!.Element(ns + "Triggers")!.Element(ns + "LogonTrigger")!.Elements().Select(e => e.Name.LocalName));
        // A laptop on battery at sign-in must not skip it.
        Assert.Equal("false", Value("Settings", "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value("Settings", "StopIfGoingOnBatteries"));
    }

    [Fact]
    public void Only_a_wizard_under_Program_Files_gets_a_task()
    {
        var root = Directory.CreateTempSubdirectory("igloo-cleanup-task-").FullName;
        try
        {
            var programFiles = Path.Join(root, "Program Files");
            var installed = Directory.CreateDirectory(Path.Join(programFiles, "iGloo")).FullName;
            Directory.CreateDirectory(Path.Join(installed, "cli"));
            var lookalike = Directory.CreateDirectory(Path.Join(root, "Program Files Copy", "iGloo")).FullName;
            File.WriteAllBytes(Path.Join(lookalike, "iGloo.exe"), []);

            Assert.Null(BootCleanupTask.InstalledApplication(installed, programFiles));
            File.WriteAllBytes(Path.Join(installed, "iGloo.exe"), []);

            Assert.Equal(Path.Join(installed, "iGloo.exe"), BootCleanupTask.InstalledApplication(installed, programFiles));
            Assert.Equal(Path.Join(installed, "iGloo.exe"),
                BootCleanupTask.InstalledApplication(Path.Join(installed, "cli") + Path.DirectorySeparatorChar, programFiles));
            Assert.Null(BootCleanupTask.InstalledApplication(lookalike, programFiles));
            Assert.Null(BootCleanupTask.InstalledApplication(installed, ""));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Registration_schedules_the_task_for_its_own_entry_and_the_uninstaller_removes_it_by_name()
    {
        var repository = AppContext.BaseDirectory;
        while (!File.Exists(Path.Join(repository, "installer", "iGloo.iss")))
            repository = Path.GetDirectoryName(repository) ?? throw new InvalidOperationException("No repository root.");

        var installer = File.ReadAllText(Path.Join(repository, "installer", "iGloo.iss"));
        var registration = File.ReadAllText(Path.Join(repository, "src", "Igloo.Preflight", "DirectInstallService.cs"));
        var cleanup = File.ReadAllText(Path.Join(repository, "src", "Igloo.Preflight", "DirectInstallService.BootCleanup.cs"));

        Assert.Contains($"/Delete /TN \"\"{BootCleanupTask.Name}\"\" /F", installer, StringComparison.Ordinal);
        Assert.Contains("var bcdEntry = RegisterBootEntryViaBcdedit(completed.DriveLetter);", registration, StringComparison.Ordinal);
        Assert.Contains("ScheduleBootCleanup(partGuid, bcdEntry);", registration, StringComparison.Ordinal);
        // An older task goes before anything can end the scheduling early.
        var schedule = cleanup[cleanup.IndexOf("private void ScheduleBootCleanup(", StringComparison.Ordinal)..];
        Assert.True(schedule.IndexOf("RemoveBootCleanupTask(expected: false);", StringComparison.Ordinal) <
                    schedule.IndexOf("return;", StringComparison.Ordinal));
    }

    private static WindowsStorageRow Row(object? guid) =>
        new(ImmutableDictionary.CreateRange([KeyValuePair.Create("Guid", guid)]));

    private static DirectInstallService Service(IWindowsStorageReader storage) =>
        new(new PartitionResizeService(NullLogger<PartitionResizeService>.Instance, storage),
            NullLogger<DirectInstallService>.Instance, storage);

    private sealed class Partitions(WindowsStorageBatch answer) : IWindowsStorageReader
    {
        public int Reads { get; private set; }

        public WindowsStorageBatch ReadDisks() => throw new InvalidOperationException("Not part of this test.");

        public WindowsStorageBatch ReadPartitions(int? diskNumber = null, int? partitionNumber = null, bool efiOnly = false)
        {
            Reads++;
            return answer;
        }

        public WindowsStorageBatch ReadVolumes(char driveLetter) => throw new InvalidOperationException("Not part of this test.");

        public WindowsObservation<WindowsStorageRow> ReadSupportedSize(WindowsStorageRow partition, bool explicitParameters = false) =>
            throw new InvalidOperationException("Not part of this test.");
    }
}
