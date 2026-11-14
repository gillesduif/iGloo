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
    public Task<BootCleanupOutcome> CleanUpBootEntriesAsync(Guid installerPartitionGuid, CancellationToken ct = default)
    {
        HostProtection.RequireWritable("boot entry cleanup");
        return Task.Run(() => CleanUpBootEntries(installerPartitionGuid), ct);
    }

    private BootCleanupOutcome CleanUpBootEntries(Guid partitionGuid)
    {
        HostProtection.RequireWritable("boot entry cleanup");
        var outcome = BootCleanupTask.Run(
            InstallerPartitionExists(partitionGuid),
            () => ParseStaleBcdIds(RunBcdedit(BcdFirmwareListing), BootEntryDescription),
            id =>
            {
                RunBcdedit($"/delete {id} /f");
                _logger.LogInformation("Removed the BCD boot entry {Guid} of the finished installation", id);
            },
            () => EfiBootEntries.FindEntriesTargetingPartition(partitionGuid, _logger),
            index => EfiBootEntries.Delete(index, _logger),
            RemoveBootCleanupTask);
        _logger.LogInformation("Boot entry cleanup for installer partition {Guid}: {Outcome}", partitionGuid, outcome);
        return outcome;
    }

    private bool? InstallerPartitionExists(Guid partitionGuid)
    {
        try
        {
            return BootCleanupTask.PartitionExists([.. _storage.ReadPartitions().RowsOrThrow()], partitionGuid);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or InvalidOperationException
                                       or UnauthorizedAccessException or KeyNotFoundException)
        {
            _logger.LogWarning(ex, "The partitions could not be read; no boot entry is removed");
            return null;
        }
    }

    // Best-effort: the handoff does not depend on it, and the next registration removes the same entries.
    private void ScheduleBootCleanup(Guid partitionGuid)
    {
        try
        {
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
                BootCleanupTask.BuildXml(application, partitionGuid, identity.User.Value))];
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
                if (RunSchtasks($"/Create /TN \"{BootCleanupTask.Name}\" /XML \"{definition}\" /F"))
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
                                       or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "Could not schedule the boot entry cleanup (non-fatal) - " +
                                   "the next registration removes the leftover entry");
        }
    }

    private void RemoveBootCleanupTask()
    {
        try
        {
            if (RunSchtasks($"/Delete /TN \"{BootCleanupTask.Name}\" /F"))
                _logger.LogInformation("Removed the boot entry cleanup task");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Could not remove the boot entry cleanup task (non-fatal)");
        }
    }

    private bool RunSchtasks(string arguments)
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

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        if (proc.ExitCode != 0)
            _logger.LogWarning("schtasks {Args} exited {Code}: {Err}", arguments, proc.ExitCode, (stderr + stdout).Trim());
        return proc.ExitCode == 0;
    }
}
