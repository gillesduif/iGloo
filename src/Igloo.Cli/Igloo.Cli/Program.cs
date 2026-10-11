using System.Text.Json;
using System.Text;
using Igloo.Command.Contracts;
using Igloo.Core.Host;

namespace Igloo.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["invoke", "--machine"])
            Console.InputEncoding = new UTF8Encoding(false, true);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        try
        {
            var backend = await IglooHost.CreateAsync(FindDistrosDirectory(),
                cancellation.Token).ConfigureAwait(false);
            await using var backendDisposal = backend.ConfigureAwait(false);
            return await CliApplication.RunAsync(args, backend.Commands, Console.In, Console.Out,
                Console.Error, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
#pragma warning disable CA1031 // A CLI process must not print unexpected private exception text.
        catch (Exception)
        {
            // Startup failures have no trusted command result. Never write raw exception text.
            await Console.Error.WriteLineAsync("LocalBackendInitializationFailed").ConfigureAwait(false);
            return 4;
        }
#pragma warning restore CA1031
    }

    private static string FindDistrosDirectory() =>
        FindDistrosDirectory(AppContext.BaseDirectory);

    internal static string FindDistrosDirectory(string cliBaseDirectory)
    {
        var executableDirectory = new DirectoryInfo(Path.GetFullPath(cliBaseDirectory));
        if (string.Equals(executableDirectory.Name, "cli", StringComparison.OrdinalIgnoreCase))
        {
            var packageRoot = executableDirectory.Parent ?? throw new InvalidDataException();
            RejectRedirectedAncestors(executableDirectory);
            var packaged = Path.Join(packageRoot.FullName, "distros");
            VerifyPackagedProviders(packaged);
            return packaged;
        }

        // Source execution is deliberately limited to the exact repository
        // build output; a packaged CLI cannot fall back to ancestor providers.
        var current = executableDirectory;
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "Igloo.sln")))
            {
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    var build = Path.Join(current.FullName, "src", "Igloo.Cli", "Igloo.Cli",
                        "bin", configuration, "net10.0-windows10.0.19041.0");
                    if (string.Equals(Path.TrimEndingDirectorySeparator(build),
                            Path.TrimEndingDirectorySeparator(executableDirectory.FullName),
                            StringComparison.OrdinalIgnoreCase))
                        return Path.Join(current.FullName, "distros");
                }
                break;
            }
            current = current.Parent;
        }
        throw new InvalidDataException("The CLI provider package is unavailable.");
    }

    private static void VerifyPackagedProviders(string directory)
    {
        if (!Directory.Exists(directory)) throw new InvalidDataException();
        RejectRedirectedAncestors(new DirectoryInfo(directory));
        foreach (var (id, assembly) in new[]
                 {
                     ("fedora-kde", "Igloo.Distro.FedoraKde.dll"),
                     ("debian", "Igloo.Distro.Debian.dll"),
                     ("ubuntu", "Igloo.Distro.Ubuntu.dll"),
                     ("linuxmint-cinnamon", "Igloo.Distro.LinuxmintCinnamon.dll"),
                 })
        {
            var provider = new DirectoryInfo(Path.Join(directory, id));
            if (!provider.Exists || (provider.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException();
            var manifest = new FileInfo(Path.Join(provider.FullName, "distro.json"));
            var plugin = new FileInfo(Path.Join(provider.FullName, assembly));
            if (!manifest.Exists || !plugin.Exists ||
                (manifest.Attributes & FileAttributes.ReparsePoint) != 0 ||
                (plugin.Attributes & FileAttributes.ReparsePoint) != 0 ||
                manifest.Length is <= 0 or > 1024 * 1024 || plugin.Length <= 0)
                throw new InvalidDataException();
            using var stream = manifest.OpenRead();
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException();
            var identities = document.RootElement.EnumerateObject().Where(property =>
                property.NameEquals("id")).ToArray();
            if (identities.Length != 1 || identities[0].Value.ValueKind != JsonValueKind.String ||
                identities[0].Value.GetString() != id)
                throw new InvalidDataException();
        }
    }

    private static void RejectRedirectedAncestors(DirectoryInfo directory)
    {
        for (var current = directory; current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException();
    }
}

internal static class CliApplication
{
    private static readonly JsonSerializerOptions HumanJson = new(CommandProtocol.JsonOptions) { WriteIndented = true };

    public static Task<int> RunAsync(string[] args, ICommandDispatcher dispatcher,
        TextReader input, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default) =>
        RunAsync(args, dispatcher, input, output, error, null, cancellationToken);

    internal static async Task<int> RunAsync(string[] args, ICommandDispatcher dispatcher,
        TextReader input, TextWriter output, TextWriter error,
        TimeSpan? machineOutputTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args is ["invoke", "--machine"])
            return await RunMachineAsync(dispatcher, input, output, machineOutputTimeout,
                cancellationToken).ConfigureAwait(false);
        var json = args.Contains("--json", StringComparer.Ordinal);
        var positional = args.Where(a => a != "--json").ToArray();
        CommandRequest request;
        try
        {
            if (positional is ["invoke"])
            {
                json = true;
                request = JsonSerializer.Deserialize<CommandRequest>(
                    await MachineInputLines.ReadBoundedAllAsync(input, cancellationToken)
                        .ConfigureAwait(false),
                    CommandProtocol.JsonOptions) ?? throw new JsonException();
            }
            else
            {
                var capability = ResolveCapability(positional);
                object value = positional is ["distros", "inspect" or "compatibility", _]
                    or ["iso", "acquire", _]
                    or ["artifacts", "acquire", _]
                    ? new DistroSelection(positional[2])
                    : positional is ["migration", "plan", _, ..]
                        ? new MigrationPlanInput(positional[2], positional.Length == 4
                            ? long.Parse(positional[3], System.Globalization.CultureInfo.InvariantCulture) : 0)
                    : positional is ["migration", "plans", "read", _]
                        or ["migration", "target", "bind", _]
                        ? new PlanSelection(Guid.Parse(positional[3]))
                    : positional is ["migration", "target", "allocate", _, _, _]
                        ? new MigrationAllocationInput(Guid.Parse(positional[3]),
                            Guid.Parse(positional[4]), long.Parse(positional[5],
                                System.Globalization.CultureInfo.InvariantCulture))
                    : positional is ["operations", "status", _] or ["migration", "status", _]
                        ? new OperationSelection(Guid.Parse(positional[2]))
                    : positional is ["artifacts", "status", _]
                        ? new ArtifactSelection(positional[2])
                    : positional is ["capabilities", "describe", _]
                        ? new CapabilitySelection(positional[2]) : new { };
                request = new(CommandProtocol.Version, capability,
                    capability == "migration.plan" ? 2 : 1, Guid.NewGuid(), Guid.NewGuid(),
                    JsonSerializer.SerializeToElement(value, CommandProtocol.JsonOptions));
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException
            or OverflowException or InvalidDataException or EncoderFallbackException)
        {
            var bad = new CommandResult(CommandProtocol.Version, "", 0, Guid.NewGuid(), Guid.NewGuid(),
                null, false, null, new(CommandErrorCode.InvalidRequest, "Invalid command or JSON request."), [], []);
            await RenderAsync(bad, json, output, error).ConfigureAwait(false);
            return 2;
        }
        IProgress<CommandProgress>? progress = dispatcher.Capabilities.Any(c =>
            c.Id == request.CapabilityId && c.Kind == CommandKind.Mutation)
            ? new Progress<CommandProgress>(p =>
            {
                if (!json) error.WriteLine(p.Phase);
            })
            : null;
        var result = await dispatcher.ExecuteAsync(request, progress, cancellationToken).ConfigureAwait(false);
        await RenderAsync(result, json, output, error).ConfigureAwait(false);
        return ExitCode(result);
    }

    private static async Task<int> RunMachineAsync(ICommandDispatcher dispatcher, TextReader input,
        TextWriter output, TimeSpan? machineOutputTimeout, CancellationToken cancellationToken)
    {
        var lines = new MachineInputLines(input);
        var frames = new MachineFrameWriter(output, machineOutputTimeout);
        await using var frameDisposal = frames.ConfigureAwait(false);
        var version = typeof(CliApplication).Assembly.GetName().Version?.ToString() ?? "unknown";
        await frames.WriteRequiredAsync(JsonSerializer.Serialize(new CommandStreamEvent("hello",
            CommandProtocol.Version, Guid.Empty, Guid.Empty,
            Product: "Igloo.Cli", ProductVersion: version), CommandProtocol.JsonOptions),
            cancellationToken).ConfigureAwait(false);

        CommandRequest request;
        try
        {
            var line = await lines.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            request = JsonSerializer.Deserialize<CommandRequest>(line ?? string.Empty,
                CommandProtocol.JsonOptions) ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException
            or DecoderFallbackException)
        {
            var invalid = new CommandResult(CommandProtocol.Version, string.Empty, 0,
                Guid.NewGuid(), Guid.NewGuid(), null, false, null,
                new(CommandErrorCode.InvalidRequest, "Invalid machine request."), [], []);
            await frames.WriteRequiredAsync(JsonSerializer.Serialize(new CommandStreamEvent("result",
                CommandProtocol.Version, invalid.RequestId, invalid.CorrelationId,
                Result: invalid), CommandProtocol.JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            await frames.CompleteAsync().ConfigureAwait(false);
            return ExitCode(invalid);
        }

        await frames.WriteRequiredAsync(JsonSerializer.Serialize(new CommandStreamEvent("accepted",
            CommandProtocol.Version, request.RequestId, request.CorrelationId),
            CommandProtocol.JsonOptions), cancellationToken).ConfigureAwait(false);

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!requestCancellation.IsCancellationRequested)
                {
                    var control = await lines.ReadLineAsync(requestCancellation.Token).ConfigureAwait(false);
                    if (control is null) return;
                    if (IsMatchingCancellation(control, request.RequestId))
                    {
                        await requestCancellation.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException
                or InvalidOperationException or FormatException or DecoderFallbackException) { }
        }, CancellationToken.None);

        var stream = new MachineProgress(frames, request);
        CommandResult result;
        try
        {
            result = await dispatcher.ExecuteAsync(request, stream,
                requestCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new(CommandProtocol.Version, request.CapabilityId, request.CapabilityVersion,
                request.RequestId, request.CorrelationId, request.OperationId, false, null,
                new(CommandErrorCode.Cancelled, "Operation cancelled."), [], []);
        }
#pragma warning disable CA1031 // Keep exactly one sanitized terminal outcome on machine stdout.
        catch (Exception)
        {
            result = new(CommandProtocol.Version, request.CapabilityId, request.CapabilityVersion,
                request.RequestId, request.CorrelationId, request.OperationId, false, null,
                new(CommandErrorCode.InternalFailure, "The local operation failed. Consult protected local diagnostics."),
                [], []);
        }
#pragma warning restore CA1031
        await stream.CompleteAsync(result).ConfigureAwait(false);
        await frames.CompleteAsync().ConfigureAwait(false);
        return ExitCode(result);
    }

    internal static bool IsMatchingCancellation(string control, Guid requestId)
    {
        using var document = JsonDocument.Parse(control, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;
        var count = 0;
        var isCancel = false;
        var matchesRequest = false;
        foreach (var property in root.EnumerateObject())
        {
            if (++count > 2) return false;
            if (property.NameEquals("type") && property.Value.ValueKind == JsonValueKind.String)
                isCancel = property.Value.GetString() == "cancel";
            else if (property.NameEquals("requestId") &&
                     property.Value.ValueKind == JsonValueKind.String &&
                     property.Value.TryGetGuid(out var actual))
                matchesRequest = actual == requestId;
            else return false;
        }
        return count == 2 && isCancel && matchesRequest;
    }

    private sealed class MachineProgress(MachineFrameWriter writer, CommandRequest request) : IProgress<CommandProgress>
    {
        private readonly object _gate = new();
        private bool _complete;
        private bool _overflow;

        public void Report(CommandProgress value)
        {
            lock (_gate)
            {
                if (_complete) return;
                if (!writer.TryWriteProgress(JsonSerializer.Serialize(new CommandStreamEvent("progress",
                    CommandProtocol.Version, request.RequestId, request.CorrelationId,
                    Progress: value), CommandProtocol.JsonOptions)))
                    _overflow = true;
            }
        }

        public async Task CompleteAsync(CommandResult result)
        {
            lock (_gate)
            {
                _complete = true;
                if (_overflow)
                    throw new IOException("The bounded machine progress stream overflowed.");
            }
            await writer.WriteRequiredAsync(JsonSerializer.Serialize(new CommandStreamEvent("result",
                CommandProtocol.Version, request.RequestId, request.CorrelationId,
                Result: result), CommandProtocol.JsonOptions)).ConfigureAwait(false);
        }
    }

    private static int ExitCode(CommandResult result) => result.Error?.Code switch
        {
            null => 0,
            CommandErrorCode.InvalidRequest or CommandErrorCode.UnsupportedCapability
                or CommandErrorCode.UnsupportedVersion => 2,
            CommandErrorCode.Cancelled => 130,
            CommandErrorCode.InternalFailure => 4,
            _ => 3,
        };

    private static string ResolveCapability(string[] args)
    {
        return args switch
        {
            ["version"] => "system.version",
            ["inspect"] => "system.inspect",
            ["inspect", "detail"] => "system.inspect.detail",
            ["capabilities", "list"] => "system.capabilities",
            ["capabilities", "describe", _] => "system.capability.describe",
            ["distros", "list"] => "distro.list",
            ["distros", "inspect", _] => "distro.inspect",
            ["distros", "compatibility", _] => "distro.compatibility",
            ["iso", "acquire", _] => "iso.acquire",
            ["artifacts", "acquire", _] => "artifact.acquire",
            ["artifacts", "status", _] => "artifact.status",
            ["migration", "inspect"] => "migration.inspect",
            ["migration", "plan", _] or ["migration", "plan", _, _] => "migration.plan",
            ["migration", "plans", "read", _] => "migration.plan.read",
            ["migration", "target", "bind", _] => "migration.target.bind",
            ["migration", "target", "allocate", _, _, _] => "migration.target.allocate",
            ["migration", "status", _] => "migration.status",
            ["operations", "status", _] => "operation.status",
            ["recovery", "inspect"] => "recovery.inspect",
            _ => throw new ArgumentException("Unknown command."),
        };
    }

    private static async Task RenderAsync(CommandResult result, bool json,
        TextWriter output, TextWriter error)
    {
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(result, CommandProtocol.JsonOptions))
                .ConfigureAwait(false);
            return;
        }
        if (result.Error is not null)
        {
            await error.WriteLineAsync($"{result.Error.Code}: {result.Error.Message}").ConfigureAwait(false);
            return;
        }
        switch (result.CapabilityId)
        {
            case "system.inspect":
                var machine = result.Data!.Value.Deserialize<SystemInspection>(CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"UEFI: {machine.IsUefi}; Secure Boot: {machine.SecureBootEnabled}; TPM: {machine.TpmPresent}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync($"BitLocker: {machine.BitLockerState}; RAM: {machine.TotalRamBytes} bytes; disks: {machine.DiskCount}")
                    .ConfigureAwait(false);
                foreach (var finding in machine.Findings)
                    await output.WriteLineAsync($"{finding.Severity}: {finding.Code}").ConfigureAwait(false);
                break;
            case "system.inspect.detail":
                var detail = result.Data!.Value.Deserialize<DetailedSystemInspection>(CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"UEFI: {detail.Summary.IsUefi}; Secure Boot: {detail.Summary.SecureBootEnabled}; " +
                    $"BitLocker: {detail.Summary.BitLockerState}").ConfigureAwait(false);
                foreach (var disk in detail.Disks)
                {
                    await output.WriteLineAsync($"Disk {disk.Ordinal}: {disk.TotalBytes} bytes, " +
                        $"{disk.FreeBytes} observed free, {disk.PartitionStyle}; " +
                        $"boot: {disk.HasBootPartition}; system: {disk.HasSystemPartition}")
                        .ConfigureAwait(false);
                    foreach (var partition in disk.Partitions)
                        await output.WriteLineAsync($"  Partition {partition.Index}: " +
                            $"{partition.SizeBytes} bytes, {partition.FileSystemCategory}; " +
                            $"shrinkable: {partition.ShrinkableBytes} bytes")
                            .ConfigureAwait(false);
                }
                await output.WriteLineAsync($"Linux installations: {detail.LinuxInstallationCount}; " +
                    $"installer leftovers: {detail.InstallerLeftoverCount}").ConfigureAwait(false);
                foreach (var finding in detail.Summary.Findings)
                    await output.WriteLineAsync($"{finding.Severity}: {finding.Code}").ConfigureAwait(false);
                break;
            case "distro.list":
                var distros = result.Data!.Value.Deserialize<DistroList>(CommandProtocol.JsonOptions)!;
                foreach (var distro in distros.Distros)
                    await output.WriteLineAsync($"{distro.Id} - {distro.DisplayName} ({distro.Status ?? "available"})")
                        .ConfigureAwait(false);
                break;
            case "distro.inspect":
                var inspected = result.Data!.Value.Deserialize<DistroSummary>(CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"{inspected.DisplayName} [{inspected.Id}] ({inspected.Status ?? "available"})")
                    .ConfigureAwait(false);
                break;
            case "distro.compatibility":
                var compatibility = result.Data!.Value.Deserialize<DistroCompatibility>(CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"{compatibility.DistroId}: {(compatibility.HasBlockers ? "blocked" : "no blocker found")}")
                    .ConfigureAwait(false);
                foreach (var finding in compatibility.Findings)
                    await output.WriteLineAsync($"{finding.Severity}: {finding.Code}").ConfigureAwait(false);
                break;
            case "iso.acquire":
                var iso = result.Data!.Value.Deserialize<IsoAcquired>(CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"{iso.DistroId}: acquired {iso.SizeBytes} bytes; SHA-256 verified: {iso.Sha256Verified}; GPG verified: {iso.GpgVerified}")
                    .ConfigureAwait(false);
                break;
            case "artifact.acquire":
                var acquired = result.Data!.Value.Deserialize<ArtifactAcquired>(CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"{acquired.DistroId}: verified artifact {acquired.ArtifactId}; " +
                    $"SHA-256 {acquired.Sha256}; GPG verified: {acquired.GpgVerified}")
                    .ConfigureAwait(false);
                break;
            case "artifact.status":
                var artifact = result.Data!.Value.Deserialize<ArtifactStatus>(CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"{artifact.ArtifactId}: " +
                    $"{(artifact.VerifiedNow ? "verified now" : artifact.Present ? "verification failed" : "missing")}")
                    .ConfigureAwait(false);
                break;
            case "migration.inspect":
                var migration = result.Data!.Value.Deserialize<MigrationInspection>(CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"Windows boot disks: {migration.WindowsBootDiskCount}; observed capacity: {migration.ObservedAvailableBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} bytes")
                    .ConfigureAwait(false);
                foreach (var blocker in migration.BlockerCodes)
                    await output.WriteLineAsync($"Review: {blocker}").ConfigureAwait(false);
                break;
            case "migration.plan":
                if (result.CapabilityVersion == 2)
                {
                    var saved = result.Data!.Value.Deserialize<MigrationPlanResultV2>(CommandProtocol.JsonOptions)!;
                    await output.WriteLineAsync($"Plan {saved.PlanId}: {saved.DistroId}; " +
                        $"{saved.Disposition}; capacity {saved.CapacityState}; " +
                        $"required {saved.RequiredBytes} bytes; observed " +
                        $"{saved.ObservedAvailableBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} bytes")
                        .ConfigureAwait(false);
                    foreach (var blocker in saved.BlockerCodes)
                        await output.WriteLineAsync($"Blocker: {blocker}").ConfigureAwait(false);
                }
                else
                {
                    var plan = result.Data!.Value.Deserialize<MigrationPlanResult>(CommandProtocol.JsonOptions)!;
                    await output.WriteLineAsync($"{plan.DistroId}: {plan.Disposition}; required {plan.RequiredBytes} bytes; observed {plan.ObservedAvailableBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} bytes")
                        .ConfigureAwait(false);
                    foreach (var blocker in plan.BlockerCodes)
                        await output.WriteLineAsync($"Blocker: {blocker}").ConfigureAwait(false);
                }
                break;
            case "migration.plan.read":
                var previous = result.Data!.Value.Deserialize<MigrationPlanResultV2>(CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"Plan {previous.PlanId}: {previous.DistroId}; " +
                    $"{previous.Disposition}; capacity {previous.CapacityState}; " +
                    $"required {previous.RequiredBytes} bytes")
                    .ConfigureAwait(false);
                break;
            case "migration.target.bind":
                var binding = result.Data!.Value.Deserialize<MigrationTargetBinding>(
                    CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"Target {binding.TargetId} bound to advisory plan " +
                    $"{binding.PlanId}; executable: {binding.Executable}").ConfigureAwait(false);
                break;
            case "migration.target.allocate":
                var allocation = result.Data!.Value.Deserialize<MigrationAllocationResult>(
                    CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"Allocation {allocation.AllocationId}: " +
                    $"{allocation.Scope}, shrink {allocation.PlannedShrinkBytes} bytes; " +
                    $"installer layout unbound; executable: {allocation.Executable}")
                    .ConfigureAwait(false);
                break;
            case "system.capabilities":
                var catalogue = result.Data!.Value.Deserialize<CapabilityDescriptor[]>(CommandProtocol.JsonOptions)!;
                foreach (var capability in catalogue)
                    await output.WriteLineAsync($"{capability.Id} v{capability.Version} ({capability.Kind}, {capability.Risk})")
                        .ConfigureAwait(false);
                break;
            default:
                await output.WriteLineAsync(JsonSerializer.Serialize(result.Data, HumanJson)).ConfigureAwait(false);
                break;
        }
    }
}
