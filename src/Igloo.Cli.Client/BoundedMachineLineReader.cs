using System.Text;

namespace Igloo.Cli.Client;

/// <summary>Preserves CR/LF framing across arbitrary read chunks without allocating an unbounded line.</summary>
internal sealed class BoundedMachineLineReader(TextReader reader)
{
    internal const int MaxLineBytes = 4 * 1024 * 1024;
    private readonly char[] _buffer = new char[4096];
    private int _position;
    private int _available;
    private bool _skipLf;

    internal async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var bytes = 0;
        var pendingHighSurrogate = false;
        while (true)
        {
            if (_position == _available)
            {
                _available = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                _position = 0;
                if (_available == 0)
                {
                    if (pendingHighSurrogate) throw BadEncoding();
                    return line.Length == 0 ? null : line.ToString();
                }
            }
            var next = _buffer[_position++];
            if (_skipLf)
            {
                _skipLf = false;
                if (next == '\n') continue;
            }
            if (next == '\r')
            {
                if (pendingHighSurrogate) throw BadEncoding();
                _skipLf = true;
                return line.ToString();
            }
            if (next == '\n')
            {
                if (pendingHighSurrogate) throw BadEncoding();
                return line.ToString();
            }
            if (char.IsHighSurrogate(next))
            {
                if (pendingHighSurrogate) throw BadEncoding();
                pendingHighSurrogate = true;
            }
            else if (char.IsLowSurrogate(next))
            {
                if (!pendingHighSurrogate) throw BadEncoding();
                pendingHighSurrogate = false;
                bytes += 4;
            }
            else
            {
                if (pendingHighSurrogate) throw BadEncoding();
                bytes += next <= 0x7F ? 1 : next <= 0x7FF ? 2 : 3;
            }
            if (bytes > MaxLineBytes || line.Length == MaxLineBytes)
                throw new CliTransportException(CliTransportError.MalformedStream,
                    "The CLI machine frame exceeds its size limit.");
            line.Append(next);
        }
    }

    private static CliTransportException BadEncoding() =>
        new(CliTransportError.MalformedStream, "The CLI machine frame has invalid text encoding.");
}
