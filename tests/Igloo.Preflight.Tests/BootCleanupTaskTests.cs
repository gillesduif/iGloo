using System.Collections.Immutable;
using System.Xml.Linq;
using Igloo.Core.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class BootCleanupTaskTests
{
    private static readonly Guid Installer = Guid.Parse("dddd4e82-820a-4dd7-b745-e91cb5e232a6");

    private static (BootCleanupOutcome Outcome, List<string> Done) Run(bool? partitionExists)
    {
        var done = new List<string>();
        var outcome = BootCleanupTask.Run(partitionExists,
            () => { done.Add("list bcd"); return ["{7a757dff-ddf9-11f1-9250-e8c97e719636}"]; },
            id => done.Add("delete bcd " + id),
            () => { done.Add("list firmware"); return [0x0005, 0x0080]; },
            index => done.Add($"delete Boot{index:X4}"),
            () => done.Add("remove task"));
        return (outcome, done);
    }

    [Fact]
    public void Nothing_is_removed_while_the_installer_partition_exists()
    {
        var (outcome, done) = Run(partitionExists: true);

        Assert.Equal(BootCleanupOutcome.PartitionPresent, outcome);
        Assert.Empty(done);
    }

    [Fact]
    public void Nothing_is_removed_when_the_disks_could_not_be_read()
    {
        var (outcome, done) = Run(partitionExists: null);

        Assert.Equal(BootCleanupOutcome.NotRead, outcome);
        Assert.Empty(done);
    }

    [Fact]
    public void The_entries_and_the_task_go_once_the_partition_is_gone()
    {
        var (outcome, done) = Run(partitionExists: false);

        Assert.Equal(BootCleanupOutcome.Removed, outcome);
        Assert.Equal(["list bcd", "delete bcd {7a757dff-ddf9-11f1-9250-e8c97e719636}", "list firmware",
            "delete Boot0005", "delete Boot0080", "remove task"], done);
    }

    [Fact]
    public void A_partition_is_found_by_its_guid_and_an_empty_answer_is_no_answer()
    {
        static WindowsStorageRow Row(object? guid) =>
            new(ImmutableDictionary.CreateRange([KeyValuePair.Create("Guid", guid)]));
        WindowsStorageRow[] disk = [Row(null), Row("{B332BE0D-EED8-4D71-B802-3A6CB6602282}"),
            Row("{DDDD4E82-820A-4DD7-B745-E91CB5E232A6}")];

        Assert.True(BootCleanupTask.PartitionExists(disk, Installer));
        Assert.False(BootCleanupTask.PartitionExists(disk[..2], Installer));
        Assert.Null(BootCleanupTask.PartitionExists([], Installer));
        Assert.Null(BootCleanupTask.PartitionExists(null, Installer));
        Assert.Null(BootCleanupTask.PartitionExists(disk, Guid.Empty));
    }

    [Fact]
    public void The_task_starts_the_wizard_elevated_at_the_sign_in_of_one_account()
    {
        const string application = @"C:\Program Files\iGloo & Co\iGloo.exe";
        const string account = "S-1-5-21-1-2-3-1001";

        var task = XDocument.Parse(BootCleanupTask.BuildXml(application, Installer, account)[
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>".Length..]);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Value(params string[] path) =>
            path.Aggregate(task.Root!, (element, name) => element.Element(ns + name)!).Value;

        Assert.Equal(application, Value("Actions", "Exec", "Command"));
        Assert.Equal("--boot-cleanup dddd4e82-820a-4dd7-b745-e91cb5e232a6", Value("Actions", "Exec", "Arguments"));
        Assert.Equal(account, Value("Triggers", "LogonTrigger", "UserId"));
        Assert.Equal(account, Value("Principals", "Principal", "UserId"));
        Assert.Equal("InteractiveToken", Value("Principals", "Principal", "LogonType"));
        Assert.Equal("HighestAvailable", Value("Principals", "Principal", "RunLevel"));
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
    public void Registration_schedules_the_task_and_the_uninstaller_removes_it_by_the_same_name()
    {
        var repository = AppContext.BaseDirectory;
        while (!File.Exists(Path.Join(repository, "installer", "iGloo.iss")))
            repository = Path.GetDirectoryName(repository) ?? throw new InvalidOperationException("No repository root.");

        var installer = File.ReadAllText(Path.Join(repository, "installer", "iGloo.iss"));
        var registration = File.ReadAllText(Path.Join(repository, "src", "Igloo.Preflight", "DirectInstallService.cs"));

        Assert.Contains($"/Delete /TN \"\"{BootCleanupTask.Name}\"\" /F", installer, StringComparison.Ordinal);
        Assert.Contains("ScheduleBootCleanup(partGuid);", registration, StringComparison.Ordinal);
    }
}
