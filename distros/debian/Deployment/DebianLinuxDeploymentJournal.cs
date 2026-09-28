using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Igloo.Distro.Debian.Deployment;

// Only native filesystem journaling is implemented here. This never mounts, deploys, invokes
// package scripts, or claims that a directory/file fsync is supported without executing it.
public sealed record DebianJournalStoreWitnessV1(string Path, ulong Device, ulong Inode, ulong MountId, uint Major, uint Minor, string FileSystem);

public sealed class DebianLinuxDeploymentJournal(string privateStore, string journalToolPath, string journalToolSha256,
    DebianJournalStoreWitnessV1? storeWitness = null, DebianSessionRuntimeV1? runtime = null) : IDebianDeploymentJournal
{
    private Guid _generation;
    private string? _planHash;
    public bool HasPersistentStoreWitness => storeWitness is not null && storeWitness.Path == privateStore && storeWitness.FileSystem == "EXT4" &&
        runtime is not null && journalToolPath == Path.Combine(Path.GetDirectoryName(runtime.Entry)!, "deployment_journal.py") &&
        runtime.ToolHashes.TryGetValue(journalToolPath, out var expected) && expected == journalToolSha256;

    public void RequirePersistentStoreWitness()
    {
        if (!HasPersistentStoreWitness) throw new NotSupportedException("PersistentImportJournalStoreObservationRequired");
    }

    public async Task BeginNewAsync(Guid generation, string planSha256, CancellationToken ct)
    {
        if (_planHash is not null) throw new InvalidOperationException("Journal cannot reserve twice.");
        if (generation == Guid.Empty || !DebianDeploymentPlanning.Hash(planSha256)) throw new InvalidDataException("Invalid journal binding.");
        _generation = generation;
        _planHash = planSha256; // Even a failed reservation cannot be blindly retried.
        _ = await InvokeAsync("reserve", null, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
    }

    public async Task<string> AppendDurablyAsync(Guid generation, ReadOnlyMemory<byte> checkpoint, CancellationToken ct)
    {
        if (generation != _generation || _planHash is null) throw new InvalidOperationException("Journal generation mismatch.");
        var bytes = await InvokeAsync("append", null, checkpoint, ct).ConfigureAwait(false);
        return Encoding.ASCII.GetString(bytes);
    }

    public Task<byte[]> ReopenAsync(string reference, CancellationToken ct) => InvokeAsync("read", reference, ReadOnlyMemory<byte>.Empty, ct);

    private async Task<byte[]> InvokeAsync(string action, string? reference, ReadOnlyMemory<byte> input, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Debian journal requires Linux fsync semantics.");
        if (_planHash is null) throw new InvalidOperationException("Journal is not reserved.");
        var tool = await File.ReadAllBytesAsync(journalToolPath, ct).ConfigureAwait(false);
        if (Convert.ToHexString(SHA256.HashData(tool)) != journalToolSha256)
            throw new InvalidDataException("Journal tool identity changed.");
        if (runtime is not null)
        {
            DebianSessionToolProtection.Verify(runtime.Python);
            if (!runtime.ToolHashes.TryGetValue(runtime.Python, out var pythonHash) ||
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(runtime.Python, ct).ConfigureAwait(false))) != pythonHash)
                throw new InvalidDataException("Journal interpreter differs from session runtime.");
        }
        var start = new ProcessStartInfo(runtime?.Python ?? "/usr/bin/python3") { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-I", journalToolPath, action, "--store", privateStore, "--generation", _generation.ToString("D"), "--plan-hash", _planHash })
            start.ArgumentList.Add(arg);
        if (reference is not null) { start.ArgumentList.Add("--reference"); start.ArgumentList.Add(reference); }
        if (storeWitness is not null)
        {
            if (!HasPersistentStoreWitness) throw new InvalidDataException("Journal witness binding changed.");
            start.ArgumentList.Add("--expected-store");
            start.ArgumentList.Add(JsonSerializer.Serialize(new[] { storeWitness.Device, storeWitness.Inode, storeWitness.MountId }));
        }
        start.Environment.Clear(); start.Environment["PATH"] = "/usr/bin:/bin"; start.Environment["LC_ALL"] = "C";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Journal command not started.");
        try
        {
            var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError.BaseStream, timeout.Token);
            await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false); // Contents are never propagated to logs/receipts.
            if (process.ExitCode != 0) throw new IOException("Journal operation failed; inspect partial durable state.");
            return output;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw; // May have applied. The runner cannot authorize retry from this exception.
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > 32 * 1024 * 1024) throw new IOException("Oversized journal response.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        return output.ToArray();
    }
}
