using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;

namespace CanonicalImportQualification;

internal static class InventoryProbe
{
    // Read-only runtime qualification, not a source of ownership/preparation receipts.
    internal static async Task<int> RunAsync(string collector, string expectedHash, string output, string? hostEvidence = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux probe required.");
        DebianSessionToolProtection.Verify(collector);
        var bytes = await File.ReadAllBytesAsync(collector);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != expectedHash)
            throw new InvalidDataException("Collector identity changed.");
        var start = new ProcessStartInfo("/usr/bin/python3") { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-I", "-B", collector }) start.ArgumentList.Add(argument);
        start.Environment.Clear(); start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        start.Environment["LC_ALL"] = "C";
        using var process = Process.Start(start) ?? throw new IOException("Collector did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
        try { await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr); }
        finally { if (!process.HasExited) process.Kill(true); }
        var raw = await stdout;
        _ = await stderr;
        if (raw.Length > 4 * 1024 * 1024) throw new InvalidDataException("Inventory output excessive.");
        var observed = LinuxInstallerInventoryProtocol.Parse(raw);
        Observation<InstallerLabAcquisitionV1>? lab = null;
        if (hostEvidence is not null)
        {
            DebianSessionToolProtection.Verify(hostEvidence);
            var hostBytes = await File.ReadAllBytesAsync(hostEvidence);
            if (hostBytes.Length > 65536) throw new InvalidDataException("Host evidence excessive.");
            var host = JsonSerializer.Deserialize<LabHostBinding>(hostBytes);
            if (observed.Availability != ObservationAvailability.Available) throw new InvalidDataException(observed.Code);
            var guest = ImmutableArray.CreateBuilder<InstallerLabGuestDiskV1>();
            foreach (var disk in observed.Value.Disks)
            {
                var name = Path.GetFileName(disk.DevicePath);
                var serial = (await File.ReadAllTextAsync($"/sys/class/block/{name}/serial")).Trim();
                var sector = uint.Parse((await File.ReadAllTextAsync($"/sys/class/block/{name}/queue/physical_block_size")).Trim(),
                    System.Globalization.CultureInfo.InvariantCulture);
                guest.Add(new(disk.DevicePath, serial, sector));
            }
            lab = InstallerLabAcquisition.Correlate(host.LabRunId, host.HostObservationSha256, host.Created,
                host.ReopenedHost, observed, Observations.Available(guest.ToImmutable()));
        }
        var result = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1,
            Scope = "ReadOnlyLabRuntimeProbe", AtUtc = DateTimeOffset.UtcNow,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            CollectorSha256 = expectedHash, ExitCode = process.ExitCode,
            Availability = observed.Availability.ToString(), observed.Code, RawInventory = raw,
            LabCorrelation = lab is null ? null : new { Availability = lab.Availability.ToString(), lab.Code,
                Value = lab.Availability == ObservationAvailability.Available ? lab.Value : null },
            Ownership = "NotAcquired", Import = "NotStarted", ProductionAuthentication = "Unsupported" });
        // Diagnostic evidence only; session/import durability has its own verified store contract.
        Publish(output, result);
        if (!result.SequenceEqual(await File.ReadAllBytesAsync(output))) throw new IOException("Probe reopen differs.");
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(result));
        return process.ExitCode == 0 && observed.Availability == ObservationAvailability.Available &&
            (lab is null || lab.Availability == ObservationAvailability.Available) ? 0 : 2;
    }

    private static void Publish(string output, byte[] result)
    {
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(result); file.Flush(true);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            if (text.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("Inventory output excessive.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private readonly record struct LabHostBinding(Guid LabRunId, string HostObservationSha256,
        ImmutableArray<InstallerLabBackingV1> Created, ImmutableArray<InstallerLabBackingV1> ReopenedHost);
}
