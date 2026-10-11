using System.Diagnostics;
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
    public CliTransportError Code { get; }
}

/// <summary>One typed request per CLI process. No endpoint implementation is linked here.</summary>
public sealed class CliCommandClient
{
    private readonly string _executablePath;
    private readonly string? _distrosDirectory;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _cancellationGrace;

    public CliCommandClient(string executablePath, TimeSpan? timeout = null,
        TimeSpan? cancellationGrace = null, string? distrosDirectory = null)
    {
        _executablePath = CliExecutableLocator.Validate(executablePath);
        _distrosDirectory = distrosDirectory is null ? null : Path.GetFullPath(distrosDirectory);
        _timeout = timeout ?? TimeSpan.FromMinutes(30);
        _cancellationGrace = cancellationGrace ?? TimeSpan.FromSeconds(5);
        if (_timeout <= TimeSpan.Zero || _cancellationGrace <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public static CliCommandClient ForApplication(string applicationDirectory,
        TimeSpan? timeout = null, string? distrosDirectory = null) =>
        new(CliExecutableLocator.Resolve(applicationDirectory), timeout,
            distrosDirectory: distrosDirectory);

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
        var start = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
        };
        start.ArgumentList.Add("invoke");
        start.ArgumentList.Add("--machine");
        if (_distrosDirectory is not null)
            start.Environment["IGLOO_DISTROS_DIRECTORY"] = _distrosDirectory;
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new CliTransportException(CliTransportError.ProcessFailed,
                "The iGloo CLI process could not start.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new CliTransportException(CliTransportError.ProcessFailed,
                "The iGloo CLI process could not start.");
        }

        try
        {
            var stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null,
                CancellationToken.None);
            var cancellationSignal = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var timeoutSignal = Task.Delay(_timeout, CancellationToken.None);
            var helloTask = ReadHelloAsync(process.StandardOutput);
            var first = await Task.WhenAny(helloTask, cancellationSignal, timeoutSignal).ConfigureAwait(false);
            if (first != helloTask)
                throw new CliTransportException(first == timeoutSignal
                    ? CliTransportError.TimedOut : CliTransportError.Cancelled,
                    "CLI transport ended before protocol negotiation.");
            await helloTask.ConfigureAwait(false); // Never send the request to an incompatible CLI.

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request,
                CommandProtocol.JsonOptions)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            var streamTask = ReadStreamAsync(process.StandardOutput, request, progress,
                helloAlreadyRead: true);
            first = await Task.WhenAny(streamTask, cancellationSignal, timeoutSignal).ConfigureAwait(false);
            if (first != streamTask)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        type = "cancel", requestId = request.RequestId,
                    }, CommandProtocol.JsonOptions)).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (IOException) { }
                if (await Task.WhenAny(streamTask, Task.Delay(_cancellationGrace, CancellationToken.None))
                        .ConfigureAwait(false) != streamTask)
                {
                    TerminateIfRunning(process);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    throw new CliTransportException(first == timeoutSignal
                        ? CliTransportError.TimedOut : CliTransportError.Cancelled,
                        "CLI transport ended without a confirmed operation outcome.");
                }
            }
            var result = await streamTask.ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAny(process.WaitForExitAsync(CancellationToken.None),
                Task.Delay(_cancellationGrace, CancellationToken.None)).ConfigureAwait(false);
            if (!process.HasExited)
            {
                TerminateIfRunning(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw new CliTransportException(CliTransportError.ProcessFailed,
                    "CLI did not exit after its terminal result.");
            }
            await stderrDrain.ConfigureAwait(false); // Diagnostics never affect semantic results.
            if (process.ExitCode == 0 && !result.Succeeded || process.ExitCode != 0 && result.Succeeded)
                throw new CliTransportException(CliTransportError.ProcessFailed,
                    "CLI exit status disagrees with its structured result.");
            return result;
        }
        finally
        {
            TerminateIfRunning(process);
        }
    }

    private static void TerminateIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { } // Process exited between the check and Kill.
    }

    internal static async Task ReadHelloAsync(TextReader reader)
    {
        var line = await reader.ReadLineAsync().ConfigureAwait(false);
        ValidateHello(ParseFrame(line));
    }

    internal static async Task<CommandResult> ReadStreamAsync(TextReader reader,
        CommandRequest request, IProgress<CommandProgress>? progress, bool helloAlreadyRead = false)
    {
        var phase = helloAlreadyRead ? 1 : 0;
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
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
                progress?.Report(update);
                continue;
            }
            if (phase == 2 && frame.Type == "result" && frame.Result is { } result &&
                frame.Product is null && frame.ProductVersion is null && frame.Progress is null &&
                result.ProtocolVersion == CommandProtocol.Version &&
                result.CapabilityId == request.CapabilityId &&
                result.CapabilityVersion == request.CapabilityVersion &&
                result.RequestId == request.RequestId && result.CorrelationId == request.CorrelationId)
            {
                if (await reader.ReadLineAsync().ConfigureAwait(false) is not null) throw BadStream();
                return result;
            }
            throw BadStream();
        }
        throw BadStream();
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
