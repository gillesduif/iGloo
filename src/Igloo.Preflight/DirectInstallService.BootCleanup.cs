using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Igloo.Core.Abstractions;
using Igloo.Core.Safety;
using Microsoft.Extensions.Logging;

namespace Igloo.Preflight;

public sealed partial class DirectInstallService
{
    public Task<BootCleanupOutcome> CleanUpBootEntriesAsync(Guid installerPartitionGuid, Guid bcdEntry,
        CancellationToken ct = default)
    {
        HostProtection.RequireWritable("boot entry cleanup");
        return Task.Run(() => CleanUpBootEntries(installerPartitionGuid, bcdEntry), ct);
    }

    private BootCleanupOutcome CleanUpBootEntries(Guid partitionGuid, Guid bcdEntry)
    {
        HostProtection.RequireWritable("boot entry cleanup");
        try
        {
            var outcome = BootCleanupTask.Run(
                InstallerPartitionExists(partitionGuid),
                () => RunBcdedit($"/delete {bcdEntry:B} /f"),
                () => EfiBootEntries.FindEntriesTargetingPartition(partitionGuid, _logger),
                index => EfiBootEntries.Delete(index, _logger),
                () => BootCleanupTask.BcdEntryRemains(RunBcdedit(BcdFirmwareListing), bcdEntry),
                () => RemoveBootCleanupTask(expected: true));
            _logger.LogInformation("Boot entry cleanup for installer partition {Partition} and BCD entry {Entry}: {Outcome}",
                partitionGuid, bcdEntry, outcome);
            return outcome;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            // The task is removed last, so a run that stopped halfway repeats at the next sign-in.
            _logger.LogWarning(ex, "Boot entry cleanup for installer partition {Partition} stopped", partitionGuid);
            return BootCleanupOutcome.NotRead;
        }
    }

    internal bool? InstallerPartitionExists(Guid partitionGuid)
    {
        try
        {
            return BootCleanupTask.PartitionExists([.. _storage.ReadPartitions().RowsOrThrow()], partitionGuid);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or InvalidOperationException
                                       or UnauthorizedAccessException or KeyNotFoundException
                                       or FormatException or OverflowException or InvalidCastException)
        {
            _logger.LogWarning(ex, "The partitions could not be read; no boot entry is removed");
            return null;
        }
    }

    // Best-effort: the handoff does not depend on it, and the next registration removes the same entry.
    private void ScheduleBootCleanup(Guid partitionGuid, string? bcdEntry)
    {
        try
        {
            // A task of an earlier registration must not outlive this one, whether or not a new one follows.
            RemoveBootCleanupTask(expected: false);
            if (!Guid.TryParse(bcdEntry, out var entry))
            {
                _logger.LogInformation("No boot entry cleanup task: no BCD entry was registered");
                return;
            }

            var application = BootCleanupTask.InstalledApplication(AppContext.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            using var identity = WindowsIdentity.GetCurrent();
            if (application is null || identity.User is null)
            {
                _logger.LogInformation("No boot entry cleanup task: this iGloo is not installed under Program Files");
                return;
            }

            var definition = Path.Join(Path.GetTempPath(), $"igloo-boot-cleanup-{Guid.NewGuid():N}.xml");
            byte[] written = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(
                BootCleanupTask.BuildXml(application, partitionGuid, entry, identity.User.Value))];
            try
            {
                using (var created = new FileStream(definition, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    created.Write(written);
                // Held open for reading only, and compared, so the task Windows reads is the one written here.
                using var held = new FileStream(definition, FileMode.Open, FileAccess.Read, FileShare.Read);
                var read = new byte[written.Length + 1];
                if (held.ReadAtLeast(read, read.Length, throwOnEndOfStream: false) != written.Length ||
                    !read.AsSpan(0, written.Length).SequenceEqual(written))
                    throw new InvalidOperationException("The task definition changed after it was written.");
                if (RunSchtasks($"/Create /TN \"{BootCleanupTask.Name}\" /XML \"{definition}\" /F", expected: true))
                    _logger.LogInformation(
                        "Scheduled the boot entry cleanup for the next sign-in of this account ({Task})",
                        BootCleanupTask.Name);
            }
            finally
            {
                File.Delete(definition);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
                                       or UnauthorizedAccessException or SecurityException or HostProtectedException)
        {
            _logger.LogWarning(ex, "Could not schedule the boot entry cleanup (non-fatal) - " +
                                   "the next registration removes the leftover entry");
        }
    }

    private void RemoveBootCleanupTask(bool expected)
    {
        if (RunSchtasks($"/Delete /TN \"{BootCleanupTask.Name}\" /F", expected))
            _logger.LogInformation("Removed the boot entry cleanup task");
    }

    private bool RunSchtasks(string arguments, bool expected)
    {
        HostProtection.RequireWritable("schtasks " + arguments.Split(' ')[0]);
        using var proc = Process.Start(new ProcessStartInfo(WindowsBcdReader.FindNativeExecutable("schtasks.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (proc is null)
            return false;

        // Read while waiting: the restart must not wait on a schtasks that never returns.
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(20_000))
        {
            proc.Kill(entireProcessTree: true);
            _logger.LogWarning("schtasks {Args} did not finish in 20 s and was ended", arguments);
            return false;
        }

        if (proc.ExitCode == 0)
            return true;
        var said = Task.WaitAll([stdout, stderr], 5_000) ? (stderr.Result + stdout.Result).Trim() : "";
        // Deleting a task that does not exist is the normal case before the first registration.
        _logger.Log(expected ? LogLevel.Warning : LogLevel.Debug,
            "schtasks {Args} exited {Code}: {Err}", arguments, proc.ExitCode, said);
        return false;
    }
}
