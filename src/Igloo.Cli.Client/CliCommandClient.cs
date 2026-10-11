using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Igloo.Command.Contracts;

namespace Igloo.Cli.Client;

public enum CliTransportError
{
    ExecutableUnavailable, IdentityMismatch, IncompatibleProtocol,
    MalformedStream, ProcessFailed, TimedOut, Cancelled,
}

/// <summary>A transport failure does not establish whether an operation was cancelled.</summary>
public sealed class CliTransportException : IOException
{
    public CliTransportException() => Code = CliTransportError.ProcessFailed;
    public CliTransportException(string message) : base(message) => Code = CliTransportError.ProcessFailed;
    public CliTransportException(string message, Exception innerException) : base(message, innerException) =>
        Code = CliTransportError.ProcessFailed;
    public CliTransportException(CliTransportError code, string message) : base(message) => Code = code;
    public CliTransportException(CliTransportError code, string message,
        Guid requestId, Guid correlationId, Guid? operationId = null) : this(code, message)
    {
        RequestId = requestId;
        CorrelationId = correlationId;
        OperationId = operationId;
    }
    public CliTransportError Code { get; }
    public Guid? RequestId { get; }
    public Guid? CorrelationId { get; }
    public Guid? OperationId { get; }
}

/// <summary>One typed request per CLI process. No endpoint implementation is linked here.</summary>
public sealed class CliCommandClient
{
    private readonly string _executablePath;
    private readonly byte[] _packageSha256;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _cancellationGrace;

    public CliCommandClient(string executablePath, TimeSpan? timeout = null,
        TimeSpan? cancellationGrace = null)
    {
        _executablePath = CliExecutableLocator.Validate(executablePath);
        _packageSha256 = HashPackage(_executablePath);
        _timeout = timeout ?? TimeSpan.FromMinutes(30);
        _cancellationGrace = cancellationGrace ?? TimeSpan.FromSeconds(5);
        if (_timeout <= TimeSpan.Zero || _cancellationGrace <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public static CliCommandClient ForApplication(string applicationDirectory,
        TimeSpan? timeout = null, string? distrosDirectory = null)
    {
        var applicationRoot = Path.GetFullPath(applicationDirectory);
        var adjacent = Path.Join(applicationRoot, "distros");
        if (distrosDirectory is not null && !string.Equals(Path.GetFullPath(distrosDirectory),
                adjacent, StringComparison.OrdinalIgnoreCase))
            throw new CliTransportException(CliTransportError.IdentityMismatch,
                "The provider directory is not in the packaged application slot.");
        // First-party frontends never resolve a repository build or a root-level
        // executable if their declared package slot is missing.
        return new CliCommandClient(CliExecutableLocator.Validate(Path.Join(applicationRoot,
            "cli", CliExecutableLocator.ExecutableName)), timeout);
    }

    public async Task<IReadOnlyList<CapabilityDescriptor>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(Request("system.capabilities", new { }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new CliTransportException(CliTransportError.ProcessFailed,
                "CLI capability discovery failed.");
        return result.Data?.Deserialize<CapabilityDescriptor[]>(CommandProtocol.JsonOptions)
            ?? throw new CliTransportException(CliTransportError.MalformedStream,
                "CLI capability discovery returned no catalogue.");
    }

    public static CommandRequest Request(string capabilityId, object input, bool dryRun = false,
        Guid? correlationId = null, int capabilityVersion = 1) => new(CommandProtocol.Version,
        capabilityId, capabilityVersion,
        Guid.NewGuid(), correlationId ?? Guid.NewGuid(),
        JsonSerializer.SerializeToElement(input, CommandProtocol.JsonOptions), dryRun);

    public async Task<CommandResult> ExecuteAsync(CommandRequest request,
        IProgress<CommandProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        // A client can outlive its packaged CLI closure. Recheck the exact files
        // seen at resolution before sending a request to the next process.
        CliExecutableLocator.Validate(_executablePath);
        if (!CryptographicOperations.FixedTimeEquals(_packageSha256,
                HashPackage(_executablePath)))
            throw new CliTransportException(CliTransportError.IdentityMismatch,
                "The packaged CLI files changed after resolution.");
        var requestLine = JsonSerializer.Serialize(request, CommandProtocol.JsonOptions);
        if (Encoding.UTF8.GetByteCount(requestLine) > BoundedMachineLineReader.MaxLineBytes)
            throw new CliTransportException(CliTransportError.MalformedStream,
                "The CLI machine request exceeds its size limit.");
        var start = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
        };
        start.ArgumentList.Add("invoke");
        start.ArgumentList.Add("--machine");
        RestrictChildEnvironment(start.Environment);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new CliTransportException(CliTransportError.ProcessFailed,
                "The iGloo CLI process could not start.", request.RequestId,
                request.CorrelationId, request.OperationId);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new CliTransportException(CliTransportError.ProcessFailed,
                "The iGloo CLI process could not start.", request.RequestId,
                request.CorrelationId, request.OperationId);
        }

        using var lifetime = new CancellationTokenSource();
        var operation = new OperationCorrelation(request.OperationId);
        try
        {
            var stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null,
                lifetime.Token);
            var cancellationSignal = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var timeoutSignal = Task.Delay(_timeout, CancellationToken.None);
            var frames = new BoundedMachineLineReader(process.StandardOutput);
            await BeforeDeadlineAsync(ReadHelloAsync(frames, lifetime.Token),
                cancellationSignal, timeoutSignal, cancellationToken).ConfigureAwait(false);
            // Negotiation completed before any request bytes are written.
            await BeforeDeadlineAsync(WriteLineAndFlushAsync(process.StandardInput,
                    requestLine, lifetime.Token), cancellationSignal, timeoutSignal,
                cancellationToken).ConfigureAwait(false);
            var streamTask = ReadStreamAsync(frames, request, progress,
                helloAlreadyRead: true, _cancellationGrace, operation, lifetime.Token);
            var first = await Task.WhenAny(streamTask, cancellationSignal, timeoutSignal)
                .ConfigureAwait(false);
            if (first != streamTask)
            {
                var interrupted = cancellationToken.IsCancellationRequested
                    ? CliTransportError.Cancelled : CliTransportError.TimedOut;
                using var grace = new CancellationTokenSource(_cancellationGrace);
                try
                {
                    await WriteLineAndFlushAsync(process.StandardInput,
                        JsonSerializer.Serialize(new
                    {
                        type = "cancel", requestId = request.RequestId,
                    }, CommandProtocol.JsonOptions), grace.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                if (await Task.WhenAny(streamTask,
                        Task.Delay(Timeout.InfiniteTimeSpan, grace.Token))
                        .ConfigureAwait(false) != streamTask)
                    throw new CliTransportException(interrupted,
                        "CLI transport ended without a confirmed operation outcome.");
            }
            var result = await streamTask.ConfigureAwait(false);
            await Task.WhenAny(process.WaitForExitAsync(CancellationToken.None),
                Task.Delay(_cancellationGrace, CancellationToken.None)).ConfigureAwait(false);
            if (!process.HasExited)
            {
                throw new CliTransportException(CliTransportError.ProcessFailed,
                    "CLI did not exit after its terminal result.");
            }
            if (await Task.WhenAny(stderrDrain, Task.Delay(_cancellationGrace,
                    CancellationToken.None)).ConfigureAwait(false) != stderrDrain)
                throw new CliTransportException(CliTransportError.ProcessFailed,
                    "CLI diagnostics did not close after process exit.");
            await stderrDrain.ConfigureAwait(false); // Diagnostics never affect semantic results.
            if (process.ExitCode == 0 && !result.Succeeded || process.ExitCode != 0 && result.Succeeded)
                throw new CliTransportException(CliTransportError.ProcessFailed,
                    "CLI exit status disagrees with its structured result.");
            return result;
        }
        catch (DecoderFallbackException)
        {
            throw new CliTransportException(CliTransportError.MalformedStream,
                "The CLI machine stream has invalid UTF-8.",
                request.RequestId, request.CorrelationId, operation.KnownOperationId);
        }
        catch (CliTransportException ex)
        {
            throw BindTransportFailure(ex, request, operation.KnownOperationId);
        }
        catch (IOException ex) when (ex is not CliTransportException)
        {
            throw BindTransportFailure(ex, request, operation.KnownOperationId);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            TerminateIfRunning(process);
        }
    }

    internal static CliTransportException BindTransportFailure(IOException failure,
        CommandRequest request, Guid? observedOperationId = null)
    {
        var knownOperationId = observedOperationId ?? request.OperationId;
        if (failure is not CliTransportException transport)
            return new(CliTransportError.ProcessFailed,
                "CLI transport I/O failed without a confirmed operation outcome.",
                request.RequestId, request.CorrelationId, knownOperationId);
        if (knownOperationId is null && transport.RequestId == request.RequestId &&
            transport.CorrelationId == request.CorrelationId)
            knownOperationId = transport.OperationId;
        return new(transport.Code, transport.Message, request.RequestId,
            request.CorrelationId, knownOperationId);
    }

    internal static async Task BeforeDeadlineAsync(Task operation, Task cancellationSignal,
        Task timeoutSignal, CancellationToken cancellationToken)
    {
        var first = await Task.WhenAny(operation, cancellationSignal, timeoutSignal)
            .ConfigureAwait(false);
        if (first != operation)
            throw new CliTransportException(cancellationToken.IsCancellationRequested
                ? CliTransportError.Cancelled : CliTransportError.TimedOut,
                "CLI transport ended without a confirmed operation outcome.");
        await operation.ConfigureAwait(false);
    }

    internal static async Task WriteLineAndFlushAsync(StreamWriter writer, string line,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void TerminateIfRunning(Process process)
    {
        try
        {
            // A transport timeout is not authority to terminate a durable Core
            // worker that the gateway may have launched separately.
            if (!process.HasExited) process.Kill(entireProcessTree: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            System.ComponentModel.Win32Exception) { } // Exit or access raced the targeted request.
    }

    private static byte[] HashPackage(string executablePath)
    {
        try
        {
            var root = new DirectoryInfo(Path.GetDirectoryName(executablePath)!);
            var packageDistros = string.Equals(root.Name, "cli", StringComparison.OrdinalIgnoreCase)
                ? new DirectoryInfo(Path.Join(root.Parent!.FullName, "distros")) : null;
            if (packageDistros is not null && !packageDistros.Exists)
                throw new CliTransportException(CliTransportError.IdentityMismatch,
                    "The packaged provider directory is unavailable.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var count = 0;
            long bytes = 0;
            foreach (var packageRoot in packageDistros is null
                         ? new[] { root } : new[] { root, packageDistros })
            {
                var pending = new Stack<DirectoryInfo>();
                pending.Push(packageRoot);
                while (pending.Count != 0)
                {
                    var directory = pending.Pop();
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new CliTransportException(CliTransportError.IdentityMismatch,
                            "The packaged CLI contains a redirected path.");
                    foreach (var entry in directory.EnumerateFileSystemInfos()
                                 .OrderBy(item => item.FullName, StringComparer.Ordinal))
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            throw new CliTransportException(CliTransportError.IdentityMismatch,
                                "The packaged CLI contains a redirected path.");
                        if (entry is DirectoryInfo child)
                        {
                            pending.Push(child);
                            continue;
                        }
                        if (entry is not FileInfo file || ++count > 1024 ||
                            file.Length < 0 || file.Length > 1L << 30 ||
                            bytes > (1L << 30) - file.Length)
                            throw new CliTransportException(CliTransportError.IdentityMismatch,
                                "The packaged CLI file set exceeds its bound.");
                        bytes += file.Length;
                        var relative = packageRoot.Name + "/" +
                            Path.GetRelativePath(packageRoot.FullName, file.FullName)
                                .Replace('\\', '/');
                        hash.AppendData(Encoding.UTF8.GetBytes(relative));
                        hash.AppendData([0]);
                        using var stream = new FileStream(file.FullName, FileMode.Open,
                            FileAccess.Read, FileShare.Read);
                        hash.AppendData(SHA256.HashData(stream));
                    }
                }
            }
            return hash.GetHashAndReset();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliTransportException(CliTransportError.IdentityMismatch,
                "The packaged CLI files could not be verified.");
        }
    }

    internal static void RestrictChildEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var key in environment.Keys.ToArray())
            if (key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("COREHOST_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("IGLOO_", StringComparison.OrdinalIgnoreCase))
                environment.Remove(key);
    }

    internal static Task ReadHelloAsync(TextReader reader,
        CancellationToken cancellationToken = default) =>
        ReadHelloAsync(new BoundedMachineLineReader(reader), cancellationToken);

    private static async Task ReadHelloAsync(BoundedMachineLineReader reader,
        CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        ValidateHello(ParseFrame(line));
    }

    internal static Task<CommandResult> ReadStreamAsync(TextReader reader,
        CommandRequest request, IProgress<CommandProgress>? progress,
        bool helloAlreadyRead = false, TimeSpan? terminalGrace = null,
        OperationCorrelation? correlation = null,
        CancellationToken cancellationToken = default) =>
        ReadStreamAsync(new BoundedMachineLineReader(reader), request, progress,
            helloAlreadyRead, terminalGrace ?? TimeSpan.FromSeconds(5),
            correlation ?? new OperationCorrelation(request.OperationId), cancellationToken);

    private static async Task<CommandResult> ReadStreamAsync(BoundedMachineLineReader reader,
        CommandRequest request, IProgress<CommandProgress>? progress, bool helloAlreadyRead,
        TimeSpan terminalGrace, OperationCorrelation correlation, CancellationToken cancellationToken)
    {
        var phase = helloAlreadyRead ? 1 : 0;
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            var frame = ParseFrame(line);
            if (phase == 0)
            {
                ValidateHello(frame);
                phase = 1;
                continue;
            }
            if (frame.ProtocolVersion != CommandProtocol.Version)
                throw new CliTransportException(CliTransportError.IncompatibleProtocol,
                    "The CLI machine protocol version is incompatible.");
            if (frame.RequestId != request.RequestId || frame.CorrelationId != request.CorrelationId)
                throw BadStream();
            if (phase == 1 && frame.Type == "accepted" && frame.Product is null &&
                frame.ProductVersion is null && frame.Progress is null && frame.Result is null)
            { phase = 2; continue; }
            if (phase == 2 && frame.Type == "progress" && frame.Progress is { } update &&
                update.RequestId == request.RequestId && frame.Product is null &&
                frame.ProductVersion is null && frame.Result is null)
            {
                correlation.ObserveProgress(update.OperationId);
                progress?.Report(update);
                continue;
            }
            if (phase == 2 && frame.Type == "result" && frame.Result is { } result &&
                frame.Product is null && frame.ProductVersion is null && frame.Progress is null &&
                result.ProtocolVersion == CommandProtocol.Version &&
                result.CapabilityId == request.CapabilityId &&
                result.CapabilityVersion == request.CapabilityVersion &&
                result.RequestId == request.RequestId && result.CorrelationId == request.CorrelationId &&
                result.Succeeded == (result.Error is null))
            {
                correlation.ObserveTerminal(result.OperationId);
                using var terminal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                terminal.CancelAfter(terminalGrace);
                try
                {
                    if (await reader.ReadLineAsync(terminal.Token).ConfigureAwait(false) is not null)
                        throw BadStream();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new CliTransportException(CliTransportError.ProcessFailed,
                        "CLI did not close its machine stream after the terminal result.");
                }
                return result;
            }
            throw BadStream();
        }
        throw BadStream();
    }

    internal sealed class OperationCorrelation(Guid? nominatedOperationId)
    {
        private readonly object _gate = new();
        private Guid? _knownOperationId = nominatedOperationId;

        internal Guid? KnownOperationId
        {
            get { lock (_gate) return _knownOperationId; }
        }

        internal void ObserveProgress(Guid? operationId)
        {
            lock (_gate)
            {
                if (_knownOperationId is { } known && operationId != known)
                    throw BadStream();
                if (operationId is not null) _knownOperationId = operationId;
            }
        }

        internal void ObserveTerminal(Guid? operationId)
        {
            lock (_gate)
            {
                if (_knownOperationId is { } known && operationId != known)
                    throw BadStream();
                _knownOperationId = operationId;
            }
        }
    }

    private static CommandStreamEvent ParseFrame(string? line)
    {
        if (line is null || line.Length > 4 * 1024 * 1024) throw BadStream();
        try
        {
            return JsonSerializer.Deserialize<CommandStreamEvent>(line,
                CommandProtocol.JsonOptions) ?? throw BadStream();
        }
        catch (JsonException) { throw BadStream(); }
    }

    private static void ValidateHello(CommandStreamEvent frame)
    {
        if (frame.ProtocolVersion != CommandProtocol.Version)
            throw new CliTransportException(CliTransportError.IncompatibleProtocol,
                "The CLI machine protocol version is incompatible.");
        if (frame.Type != "hello" || frame.RequestId != Guid.Empty ||
            frame.CorrelationId != Guid.Empty || frame.Product != "Igloo.Cli" ||
            string.IsNullOrWhiteSpace(frame.ProductVersion) || frame.Progress is not null ||
            frame.Result is not null)
            throw BadStream();
    }

    private static CliTransportException BadStream() => new(CliTransportError.MalformedStream,
        "The CLI machine stream is malformed or incomplete.");
}
