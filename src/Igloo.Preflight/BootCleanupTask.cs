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

    /// <summary>Whether the firmware listing still shows the BCD entry; null when the text is not such a listing.</summary>
    internal static bool? BcdEntryRemains(string? firmwareListing, Guid bcdEntry)
    {
        // Every firmware listing names the firmware boot manager by this alias, in any display language.
        if (string.IsNullOrEmpty(firmwareListing) ||
            !firmwareListing.Contains("{fwbootmgr}", StringComparison.OrdinalIgnoreCase))
            return null;
        return firmwareListing.Contains(bcdEntry.ToString("B"), StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildXml(string application, Guid partitionGuid, Guid bcdEntry, string userSid) =>
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
              <Arguments>{BootCleanup.Argument} {partitionGuid:D} {bcdEntry:D}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>Removes what one registration created, and only when its installer partition is known to be gone.</summary>
    internal static BootCleanupOutcome Run(bool? partitionExists, Action deleteBcdEntry,
        Func<IReadOnlyList<ushort>> firmwareEntries, Action<ushort> deleteFirmwareEntry,
        Func<bool?> bcdEntryRemains, Action removeTask)
    {
        if (partitionExists is null)
            return BootCleanupOutcome.NotRead;
        if (partitionExists.Value)
            return BootCleanupOutcome.PartitionPresent;
        // The BCD entry goes first: while it exists Windows writes the firmware entry again.
        deleteBcdEntry();
        foreach (var index in firmwareEntries())
            deleteFirmwareEntry(index);
        // The task stays until the entry is seen to be gone, so a failed delete is tried again.
        if (bcdEntryRemains() != false)
            return BootCleanupOutcome.EntriesRemain;
        removeTask();
        return BootCleanupOutcome.Removed;
    }
}
