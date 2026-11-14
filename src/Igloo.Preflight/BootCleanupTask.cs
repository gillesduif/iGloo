using System.Security;
using Igloo.Core.Abstractions;

namespace Igloo.Preflight;

/// <summary>The one-shot Windows task that removes the installer's boot entries after the installation.</summary>
internal static class BootCleanupTask
{
    internal const string Name = "iGloo boot entry cleanup";
    private const string ApplicationFile = "iGloo.exe";

    /// <summary>The installed wizard, or null for a build that does not sit under Program Files.</summary>
    internal static string? InstalledApplication(string baseDirectory, string programFiles)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory) || string.IsNullOrWhiteSpace(programFiles))
            return null;
        // An elevated task may only start a file that needs an administrator to replace.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFiles)) + Path.DirectorySeparatorChar;
        var directory = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        // The CLI lives one folder below the wizard.
        foreach (var candidate in new[] { directory, directory.Parent })
        {
            if (candidate is null || !(candidate.FullName + Path.DirectorySeparatorChar)
                    .StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            var application = Path.Join(candidate.FullName, ApplicationFile);
            if (File.Exists(application))
                return application;
        }
        return null;
    }

    /// <summary>Whether a partition with this GPT GUID exists; null when the rows cannot say.</summary>
    internal static bool? PartitionExists(IEnumerable<WindowsStorageRow>? partitions, Guid partitionGuid)
    {
        if (partitions is null || partitionGuid == Guid.Empty)
            return null;
        var read = 0;
        foreach (var row in partitions)
        {
            read++;
            if (Guid.TryParse(row["Guid"]?.ToString(), out var guid) && guid == partitionGuid)
                return true;
        }
        // A machine that runs Windows has partitions; an empty answer is a failed read.
        return read == 0 ? null : false;
    }

    internal static string BuildXml(string application, Guid partitionGuid, string userSid) =>
        $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Removes the iGloo installer entry from the boot menu once the installer partition is gone, then removes itself.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(userSid)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(userSid)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(application)}</Command>
              <Arguments>{BootCleanup.Argument} {partitionGuid:D}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>Removes the entries only when the installer partition is known to be gone.</summary>
    internal static BootCleanupOutcome Run(bool? partitionExists, Func<IReadOnlyList<string>> staleBcdEntries,
        Action<string> deleteBcdEntry, Func<IReadOnlyList<ushort>> firmwareEntries, Action<ushort> deleteFirmwareEntry,
        Action removeTask)
    {
        if (partitionExists is null)
            return BootCleanupOutcome.NotRead;
        if (partitionExists.Value)
            return BootCleanupOutcome.PartitionPresent;
        foreach (var id in staleBcdEntries())
            deleteBcdEntry(id);
        // Windows drops the firmware entry with its BCD object; the wizard's own raw entry has none.
        foreach (var index in firmwareEntries())
            deleteFirmwareEntry(index);
        removeTask();
        return BootCleanupOutcome.Removed;
    }
}
