using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;

namespace CanonicalImportQualification;

internal static class LabStorageSmoke
{
    private static readonly JsonSerializerOptions StrictJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static async Task<int> RunAsync(string evidencePath, string runtimePath, string journalsPath)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Isolated Linux lab required.");
        var started = false;
        try
        {
            var evidence = Read<InstallerLabStorageEvidenceV1>(evidencePath);
            var runtime = Read<DebianSessionRuntimeV1>(runtimePath);
            var journals = Read<JournalDeclaration>(journalsPath);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var inventory = await CollectAsync(runtime.Collector, runtime.ToolHashes[runtime.Collector], deadline.Token);
            var guest = ReadGuest(inventory);
            var validated = InstallationStorage.VerifyLab(evidence, inventory, guest);
            if (validated.Availability != ObservationAvailability.Available)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { Phase = "Ownership", Availability = validated.Availability.ToString(),
                    validated.Code, Execution = "NotStarted", Import = "NotInvoked" }));
                return 2;
            }
            var storage = validated.Value;
            var witnesses = await DebianNativeMountSession.ObserveImportJournalsAsync(runtime, storage,
                journals.SessionStore, journals.ImportStore, journals.RuntimeFileSystemUuid, deadline.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { Phase = "JournalPlacement", storage.Provenance, Stores = witnesses }));
            var journal = new DebianLinuxDeploymentJournal(journals.SessionStore, journals.ToolPath, journals.ToolSha256, witnesses[0], runtime);
            var authority = DebianMountSessionAuthority.ForLabStorageSmoke(storage, journal);
            await using var session = new DebianNativeMountSession(authority);
            started = true;
            await session.StartAsync(runtime, deadline.Token);
            foreach (var action in new[] { DebianMountSessionAction.AcquireLeases, DebianMountSessionAction.PrepareImportMountpoints,
                DebianMountSessionAction.MountRoot, DebianMountSessionAction.MountPayload, DebianMountSessionAction.Inspect,
                DebianMountSessionAction.UnmountPayload, DebianMountSessionAction.UnmountRoot, DebianMountSessionAction.Close })
            {
                var result = await session.PerformAsync(action, deadline.Token);
                Console.WriteLine(JsonSerializer.Serialize(new { Result = result, authority.LastResultReference, authority.LastResultSha256 }));
            }
            var reopened = await journal.ReopenAsync(authority.LastResultReference!, deadline.Token);
            if (Convert.ToHexString(SHA256.HashData(reopened)) != authority.LastResultSha256 || authority.State != DebianMountSessionState.Closed)
                throw new IOException("Final storage result reopen failed.");
            Console.WriteLine(JsonSerializer.Serialize(new { Scope = "StorageSmoke", authority.SessionId, authority.GenerationId,
                authority.PlanSha256, storage.Provenance, authority.LastResultReference, authority.LastResultSha256,
                Session = "AppliedAndVerified", Teardown = "AppliedAndVerified", Import = "NotInvoked", NativeSupported = 0 }));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or
            ArgumentException or OperationCanceledException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Scope = "StorageSmoke", Outcome = started ? "OutcomeUnknown" : "NotStarted",
                ErrorType = error.GetType().Name, Code = error.Message, Import = "NotInvoked" }));
            return 2;
        }
    }

    internal static T Read<T>(string path)
    {
        DebianSessionToolProtection.Verify(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Lab declaration oversized.");
        RequireUnambiguousJson(bytes);
        return JsonSerializer.Deserialize<T>(bytes, StrictJson)
            ?? throw new InvalidDataException("Missing lab declaration.");
    }

    internal static void RequireUnambiguousJson(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicates(document.RootElement);
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (!keys.Add(p.Name)) throw new InvalidDataException("Duplicate lab declaration property.");
                RejectDuplicates(p.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) RejectDuplicates(child);
    }

    internal static Observation<ImmutableArray<InstallerLabGuestDiskV1>> ReadGuest(Observation<InstallerRuntimeInventoryV1> inventory)
    {
        if (inventory.Availability != ObservationAvailability.Available)
            return Observations.Failure<ImmutableArray<InstallerLabGuestDiskV1>>(inventory.Availability, "LabWholeInventoryUnavailable");
        return Observations.Available(inventory.Value.Disks.Select(d => new InstallerLabGuestDiskV1(d.DevicePath,
            File.ReadAllText($"/sys/class/block/{Path.GetFileName(d.DevicePath)}/serial").Trim(),
            uint.Parse(File.ReadAllText($"/sys/class/block/{Path.GetFileName(d.DevicePath)}/queue/physical_block_size").Trim(),
                System.Globalization.CultureInfo.InvariantCulture))).ToImmutableArray());
    }

    internal static async Task<Observation<InstallerRuntimeInventoryV1>> CollectAsync(string collector, string hash, CancellationToken ct)
    {
        DebianSessionToolProtection.Verify(collector);
        if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(collector, ct))) != hash)
            throw new InvalidDataException("Collector identity changed.");
        var start = new ProcessStartInfo("/usr/bin/python3") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-I", "-B", collector }) start.ArgumentList.Add(arg);
        start.Environment.Clear(); start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin"; start.Environment["LC_ALL"] = "C";
        using var process = Process.Start(start) ?? throw new IOException("Collector unavailable.");
        var output = ReadBoundedAsync(process.StandardOutput, ct);
        var errors = ReadBoundedAsync(process.StandardError, ct);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(ct), output, errors);
            var observed = LinuxInstallerInventoryProtocol.Parse(await output);
            if (process.ExitCode != 0 && observed.Availability == ObservationAvailability.Available)
                throw new IOException("Collector exit contradicts observation.");
            return observed;
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            if (result.Length + count > 4 * 1024 * 1024) throw new IOException("Collector output oversized.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
