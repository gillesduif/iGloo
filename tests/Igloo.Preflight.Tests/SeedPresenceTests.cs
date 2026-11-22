using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Xunit;

namespace Igloo.Preflight.Tests;

// A file a virus scanner took from the installer partition stops the preparation and the boot registration, by name.
public sealed class SeedPresenceTests : IDisposable
{
    private const string Activator = "files/Downloads/Uninstall/Launch-Activator.exe";
    private const string Script = "files/Downloads/Uninstall/down2.ps1";
    private readonly string _root = Directory.CreateTempSubdirectory("igloo-seed-presence-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Put(string relative, string text = "bytes")
    {
        var path = Path.Join(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return relative;
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private void WriteIdentity(params string[] paths) =>
        File.WriteAllBytes(Path.Join(_root, SeedIdentityWriter.FileName), JsonSerializer.SerializeToUtf8Bytes(
            new SeedIdentityV1(1, Guid.NewGuid(), Guid.NewGuid(),
                paths.Select(path => new SeedIdentityFileV1(path, new string('A', 64))).ToArray()), Web));

    [Fact]
    public void A_copied_file_that_opens_with_its_length_is_kept()
    {
        var expected = new (string, long?)[] { (Put("ks.cfg"), 5), (Put(Script, ""), 0), (Put(Activator), null) };

        Assert.Empty(DirectInstallService.FindLostSeedFiles(_root, expected));
    }

    [Fact]
    public void A_file_that_is_gone_or_has_another_length_is_named_by_its_path_on_the_partition()
    {
        Put("ks.cfg");
        Put(Script);
        var expected = new (string, long?)[] { ("ks.cfg", 5), (Activator, 9), (Script.Replace('/', '\\'), 400), ("files/Gone/a.txt", null) };

        Assert.Equal(new[] { Activator, Script, "files/Gone/a.txt" }, DirectInstallService.FindLostSeedFiles(_root, expected));
    }

    [Fact]
    public void A_file_another_program_has_open_counts_as_present()
    {
        Put(Activator);
        using var held = new FileStream(Path.Join(_root, Activator.Replace('/', Path.DirectorySeparatorChar)),
            FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.Empty(DirectInstallService.FindLostSeedFiles(_root, [(Activator, 5)]));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("files//double.txt")]
    [InlineData("files/./same.txt")]
    public void A_listed_path_that_leaves_the_partition_stops_the_check(string path) =>
        Assert.Throws<InvalidDataException>(() => DirectInstallService.FindLostSeedFiles(_root, [(path, null)]));

    [Fact]
    public void A_refused_open_counts_as_lost_and_any_other_failure_does_not()
    {
        // ERROR_VIRUS_INFECTED and ERROR_VIRUS_DELETED, as Windows returns them for a file a scanner reports.
        Assert.True(DirectInstallService.IsLostFile(new IOException("reported", unchecked((int)0x800700E1))));
        Assert.True(DirectInstallService.IsLostFile(new IOException("removed", unchecked((int)0x800700E2))));
        Assert.True(DirectInstallService.IsLostFile(new FileNotFoundException()));
        Assert.True(DirectInstallService.IsLostFile(new DirectoryNotFoundException()));
        Assert.True(DirectInstallService.IsLostFile(new UnauthorizedAccessException()));
        // A sharing violation or a device error is not this condition and reaches the caller as it is.
        Assert.False(DirectInstallService.IsLostFile(new IOException("in use", unchecked((int)0x80070020))));
        Assert.False(DirectInstallService.IsLostFile(new InvalidOperationException()));
    }

    [Fact]
    public void The_registration_check_passes_while_every_listed_file_opens()
    {
        WriteIdentity(Put("ks.cfg"), Put(Activator), Put(Script));

        DirectInstallService.RequireSeedHoldsItsFiles(_root);
    }

    [Fact]
    public void The_registration_check_names_the_files_the_partition_lacks_and_says_what_to_do()
    {
        WriteIdentity(Put("ks.cfg"), Activator, Script);

        var refusal = Assert.Throws<InstallationRefusedException>(() => DirectInstallService.RequireSeedHoldsItsFiles(_root));

        Assert.StartsWith("Windows no longer gives back 2 of the files iGloo copied to the installer partition: " +
            @"'Downloads\Uninstall\Launch-Activator.exe', 'Downloads\Uninstall\down2.ps1'. ", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Windows Security lists it under Virus & threat protection › Protection history.", refusal.Message, StringComparison.Ordinal);
        Assert.EndsWith("Go back to Configuration and leave out the folder or browser that holds them, or move the files elsewhere, then continue.",
            refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_or_empty_file_list_stops_the_registration_check()
    {
        File.WriteAllText(Path.Join(_root, SeedIdentityWriter.FileName), "{ not json");
        Assert.Throws<InvalidDataException>(() => DirectInstallService.RequireSeedHoldsItsFiles(_root));

        WriteIdentity();
        Assert.Throws<InvalidDataException>(() => DirectInstallService.RequireSeedHoldsItsFiles(_root));
    }

    [Fact]
    public void The_refusal_shows_five_names_counts_the_rest_and_knows_the_installers_own_files()
    {
        var many = Enumerable.Range(1, 7).Select(number => $"files/Documents/{number}.txt").ToArray();
        var message = InstallationRefusedException.FilesLostFromInstallerPartition(many).Message;
        Assert.Contains(@"'Documents\5.txt' and 2 more. ", message, StringComparison.Ordinal);
        Assert.DoesNotContain("6.txt", message, StringComparison.Ordinal);

        var profile = InstallationRefusedException.FilesLostFromInstallerPartition(
            ["browser-profiles/AppData/Local/Microsoft/Edge/User Data/Default/History"]).Message;
        Assert.Contains(@"'browser-profiles\AppData\Local\Microsoft\Edge\User Data\Default\History'", profile, StringComparison.Ordinal);
        Assert.EndsWith("then continue.", profile, StringComparison.Ordinal);

        var own = InstallationRefusedException.FilesLostFromInstallerPartition(["igloo-agent/agent.py", "files/Documents/1.txt"]).Message;
        Assert.EndsWith("The installer cannot start without them. Read the reason there before you prepare again.", own, StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_runs_after_the_partition_is_marked_and_before_the_firmware_is_written()
    {
        var repository = AppContext.BaseDirectory;
        while (!File.Exists(Path.Join(repository, "installer", "iGloo.iss")))
            repository = Path.GetDirectoryName(repository) ?? throw new InvalidOperationException("No repository root.");
        var source = File.ReadAllText(Path.Join(repository, "src", "Igloo.Preflight", "DirectInstallService.cs"));

        int At(string text, int from = 0)
        {
            var index = source.IndexOf(text, from, StringComparison.Ordinal);
            Assert.True(index >= 0, text);
            return index;
        }

        // Preparation: after the identity and the receipt, so a stopped attempt leaves a partition the next one recognises.
        var receipt = At("WriteSeedReuseReceipt($\"{driveLetter}:\\\\\", _preparedBootTarget.Value,");
        var copied = At("RequireCopiedFilesReadable($\"{driveLetter}:\\\\\", stagedFiles, writtenHashes, ct);");
        Assert.InRange(copied, receipt, At("message: \"Installer partition ready.\"", receipt));

        // Registration: before the privilege, which itself comes before the line that announces the first firmware variable.
        var register = At("private void RegisterBootEntry(");
        var held = At("RequireSeedHoldsItsFiles($\"{completed.DriveLetter}:\\\\\", ct);", register);
        var privilege = At("FirmwareNative.EnablePrivilege(_logger, reportAssignmentFailures: true);", register);
        Assert.True(held < privilege);
        Assert.True(privilege < At("_logger.LogInformation(\"Writing UEFI {Var} ({Bytes} bytes)", register));
    }
}
