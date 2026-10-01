using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

public sealed record DebianSessionRuntimeV1(string Python, string Gate, string Entry, string Collector,
    string Bubblewrap, string Observer, ImmutableSortedDictionary<string, string> ToolHashes);

// Persistent supervisor transport. Still intentionally NOT IDebianIsolatedStageHost: there is
// the closed import action is separate from package execution and the 43-stage host.
// Disposal kills an abandoned namespace; it never emits verified unmount/success evidence.
public sealed class DebianNativeMountSession : IAsyncDisposable
{
    private readonly DebianMountSessionAuthority _authority;
    private readonly Process _process = new();
    private Task _discardErrors = Task.CompletedTask;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private bool _disposed;
    private bool _started;
    private bool _startAttempted;

    public DebianNativeMountSession(DebianMountSessionAuthority authority) =>
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));

    public async Task StartAsync(DebianSessionRuntimeV1 runtime, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_startAttempted) throw new InvalidOperationException("Session start is single-use.");
        _startAttempted = true;
        var complete = false;
        try
        {
            ArgumentNullException.ThrowIfNull(runtime);
            await StartCoreAsync(runtime, ct).ConfigureAwait(false);
            complete = true;
        }
        finally { if (!complete) await DisposeAsync().ConfigureAwait(false); }
    }

    private async Task StartCoreAsync(DebianSessionRuntimeV1 runtime, CancellationToken ct)
    {
        await VerifyRuntimeAsync(runtime, ct).ConfigureAwait(false);
        if (_authority.InitramfsPlan is not null && _authority.InitramfsPlan.ExecutionSha256 !=
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(runtime.ToolHashes))))
            throw new InvalidDataException("Initramfs execution revision changed.");
        var parentNamespace = new FileInfo("/proc/self/ns/mnt").LinkTarget ?? throw new IOException("Mount namespace unavailable.");
        if (!parentNamespace.StartsWith("mnt:[", StringComparison.Ordinal) || !parentNamespace.EndsWith(']'))
            throw new IOException("Malformed mount namespace identity.");
        var parentNamespaceId = ulong.Parse(parentNamespace.AsSpan(5, parentNamespace.Length - 6), System.Globalization.CultureInfo.InvariantCulture);
        await _authority.BeginAsync(ct).ConfigureAwait(false); // fsync + independent reopen BEFORE namespace creation
        var start = new ProcessStartInfo(runtime.Gate) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "--supervise", runtime.Python, "-I", "-B", runtime.Entry }) start.ArgumentList.Add(argument);
        start.Environment.Clear(); start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin"; start.Environment["LC_ALL"] = "C";
        var process = _process;
        process.StartInfo = start;
        _started = process.Start();
        if (!_started) throw new IOException("Session supervisor unavailable.");
        _discardErrors = DiscardBoundedAsync(process.StandardError);
        await process.StandardInput.WriteLineAsync(SerializeRequest(_authority, runtime, parentNamespaceId).AsMemory(), ct).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startupTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var ready = JsonDocument.Parse(await ReadBoundedLineAsync(process.StandardOutput, startupTimeout.Token).ConfigureAwait(false));
        await _authority.ObserveSupervisorAsync(ready.RootElement, parentNamespace, startupTimeout.Token).ConfigureAwait(false);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { _authority.SessionId, Accepted = true }).AsMemory(), startupTimeout.Token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(startupTimeout.Token).ConfigureAwait(false);
    }

    internal static string SerializeRequest(DebianMountSessionAuthority authority, DebianSessionRuntimeV1 runtime, ulong parentNamespaceId) =>
        JsonSerializer.Serialize(new
        {
            authority.SessionId, authority.GenerationId, authority.PlanSha256, ImportPlan = (object?)authority.SourcePlan, StorageSmoke = authority.StorageSmoke?.Provenance, LabImport = authority.SourcePlan is DebianLabImportPlanV1 ? authority.LabStorage?.Provenance : null, ConfigurationPlan = authority.ConfigurationPlan, ConfigurationPredecessor = authority.Predecessor, LabConfiguration = authority.ConfigurationPlan is not null ? authority.LabStorage?.Provenance : null, EntryPath = runtime.Entry,
            ParentMountNamespace = parentNamespaceId,
            InitramfsPlan = authority.InitramfsPlan, ConfiguredPredecessor = authority.ConfiguredPredecessor,
            LabInitramfs = authority.InitramfsPlan is not null ? authority.LabStorage?.Provenance : null,
            UserDataPlan = authority.UserDataPlan, InitramfsPredecessor = authority.InitramfsPredecessor, UserDataStore = authority.UserDataStore,
            LabUserData = authority.UserDataPlan is not null ? authority.LabStorage?.Provenance : null,
            ExecutionSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(runtime.ToolHashes))),
            CollectorPath = runtime.Collector, CollectorSha256 = runtime.ToolHashes[runtime.Collector], ToolHashes = runtime.ToolHashes,
            Runtime = RuntimeDeclaration(runtime),
        });

    private static object RuntimeDeclaration(DebianSessionRuntimeV1 runtime) => new
    {
        bubblewrap = runtime.Bubblewrap, bubblewrap_hash = runtime.ToolHashes[runtime.Bubblewrap],
        gate = runtime.Gate, gate_hash = runtime.ToolHashes[runtime.Gate], python = runtime.Python, python_hash = runtime.ToolHashes[runtime.Python],
        observer = runtime.Observer, observer_hash = runtime.ToolHashes[runtime.Observer],
    };

    private static async Task VerifyRuntimeAsync(DebianSessionRuntimeV1 runtime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux mount session required.");
        string[] modules = ["block_session.py", "mount_supervisor.py", "package_broker.py", "isolation_policy.py", "isolation_observer.py", "target_files.py",
            "configured_root.py", "root_transport.py", "configured_root_metadata.py", "target_observer.py", "deployment_journal.py", "session_import.py", "session_configuration.py"];
        var directory = Path.GetDirectoryName(runtime.Entry)!;
        if (runtime.ToolHashes.ContainsKey(Path.Combine(directory, "session_initramfs.py")))
            modules = [.. modules, "configured_successor.py", "initramfs_archive.py", "initramfs_generated.py", "initramfs_candidate.py",
                "initramfs_observer.py", "initramfs_publication.py", "session_initramfs.py"];
        if (runtime.ToolHashes.ContainsKey(Path.Combine(directory, "session_userdata.py")))
            modules = [.. modules, "debian_first_boot.py", "userdata_contract.py", "userdata_producer.py", "userdata_source.py",
                "userdata_admission.py", "userdata_successor.py", "session_userdata.py"];
        var required = new[] { runtime.Python, runtime.Gate, runtime.Entry, runtime.Collector, runtime.Bubblewrap, runtime.Observer, "/usr/bin/dpkg-query", "/usr/bin/openssl", "/usr/bin/localedef", "/usr/lib/x86_64-linux-gnu/libcrypt.so.1.1.0" }
            .Concat(modules.Select(m => Path.Combine(directory, m))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        if (!runtime.ToolHashes.Keys.Order(StringComparer.Ordinal).SequenceEqual(required, StringComparer.Ordinal)) throw new InvalidDataException("Incomplete session tool manifest.");
        foreach (var (path, hash) in runtime.ToolHashes)
        {
            DebianSessionToolProtection.Verify(path);
            if (!Path.IsPathFullyQualified(path) || !DebianDeploymentPlanning.Hash(hash) ||
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false))) != hash)
                throw new InvalidDataException("Session tool identity changed.");
        }
    }

    public static async Task<ImmutableArray<DebianJournalStoreWitnessV1>> ObserveImportJournalsAsync(
        DebianSessionRuntimeV1 runtime, DebianConfiguredRootImportPlanV1 plan, string sessionStore, string importStore,
        Guid expectedRuntimeFileSystemUuid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(runtime);
        return DebianImportJournalStorage.Verify(plan, sessionStore, importStore, expectedRuntimeFileSystemUuid,
            await ObserveJournalPlacementAsync(runtime, sessionStore, importStore, ct).ConfigureAwait(false));
    }

    public static async Task<ImmutableArray<DebianJournalStoreWitnessV1>> ObserveImportJournalsAsync(
        DebianSessionRuntimeV1 runtime, ValidatedInstallationStorage storage, string sessionStore, string importStore,
        Guid expectedRuntimeFileSystemUuid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(runtime);
        return DebianImportJournalStorage.Verify(storage, sessionStore, importStore, expectedRuntimeFileSystemUuid,
            await ObserveJournalPlacementAsync(runtime, sessionStore, importStore, ct, storage.Provenance).ConfigureAwait(false));
    }

    private static async Task<JsonElement> ObserveJournalPlacementAsync(DebianSessionRuntimeV1 runtime,
        string sessionStore, string importStore, CancellationToken ct, InstallationStorageProvenanceV1? provenance = null)
    {
        await VerifyRuntimeAsync(runtime, ct).ConfigureAwait(false);
        var start = new ProcessStartInfo(runtime.Python) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-I", "-B", runtime.Entry }) start.ArgumentList.Add(argument);
        start.Environment.Clear(); start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin"; start.Environment["LC_ALL"] = "C";
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(7));
        if (!process.Start()) throw new IOException("Journal observer unavailable.");
        var errors = DiscardBoundedAsync(process.StandardError);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                ImportJournalPreflight = new[] { sessionStore, importStore }, LabProvenance = provenance, runtime.ToolHashes,
                CollectorPath = runtime.Collector, CollectorSha256 = runtime.ToolHashes[runtime.Collector], Runtime = RuntimeDeclaration(runtime),
            }).AsMemory(), timeout.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            using var observed = JsonDocument.Parse(await ReadBoundedLineAsync(process.StandardOutput, timeout.Token).ConfigureAwait(false));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Journal observer failed.");
            return observed.RootElement.Clone();
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
        }
    }

    public async Task<JsonElement> PerformAsync(DebianMountSessionAction action, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started) throw new InvalidOperationException("Session supervisor has not started.");
        if (!await _serial.WaitAsync(0, ct).ConfigureAwait(false)) throw new InvalidOperationException("Concurrent session operation rejected.");
        try
        {
            _authority.BeginAction(action);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Configuration repeats full-tree semantic/complement observations around
            // the bounded helpers; the mount-only ten-minute budget is insufficient.
            timeout.CancelAfter(action is DebianMountSessionAction.ImportConfiguredRoot or DebianMountSessionAction.ConfigureCore or DebianMountSessionAction.GenerateInitramfs or DebianMountSessionAction.TransferUserData
                ? TimeSpan.FromHours(4) : TimeSpan.FromMinutes(10));
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { _authority.SessionId, Action = action.ToString() }).AsMemory(), timeout.Token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            while (true)
            {
                using var document = JsonDocument.Parse(await ReadBoundedLineAsync(_process.StandardOutput, timeout.Token).ConfigureAwait(false));
                var message = document.RootElement;
                if (message.GetProperty("Kind").GetString() == "Result")
                {
                    _authority.CompleteAction(message);
                    return message.Clone();
                }
                var reply = await _authority.AcceptEventAsync(message, timeout.Token).ConfigureAwait(false);
                await _process.StandardInput.WriteLineAsync(reply.GetRawText().AsMemory(), timeout.Token).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException or OperationCanceledException or NotSupportedException)
        {
            _authority.Invalidate();
            if (_started && !_process.HasExited) _process.Kill(true);
            throw;
        }
        finally { _serial.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_authority.State != DebianMountSessionState.Closed) _authority.Invalidate();
        try
        {
            if (_started)
            {
                if (!_process.HasExited) _process.Kill(true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
                await _discardErrors.ConfigureAwait(false);
            }
        }
        finally { _process.Dispose(); _serial.Dispose(); }
    }

    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken ct)
    {
        var line = new StringBuilder();
        var character = new char[1];
        while (line.Length < 8 * 1024 * 1024)
        {
            if (await reader.ReadAsync(character.AsMemory(), ct).ConfigureAwait(false) == 0) throw new IOException("Supervisor ended with unresolved intent.");
            if (character[0] == '\n') return line.ToString();
            line.Append(character[0]);
        }
        throw new IOException("Oversized supervisor response.");
    }

    private static async Task DiscardBoundedAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        // Drain without retaining any data. Tools cannot fill memory with diagnostics/secrets.
        while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) != 0) { }
    }
}
