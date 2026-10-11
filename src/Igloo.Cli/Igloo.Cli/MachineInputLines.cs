using System.Text;

namespace Igloo.Cli;

/// <summary>Bounds a machine-input line while it is read, including its UTF-8 wire size.</summary>
internal sealed class MachineInputLines(TextReader input)
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
                _available = await input.ReadAsync(_buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                _position = 0;
                if (_available == 0)
                {
                    if (pendingHighSurrogate) throw new InvalidDataException();
                    return line.Length == 0 ? null : line.ToString();
                }
            }
            var next = _buffer[_position++];
            if (_skipLf)
            {
                _skipLf = false;
                if (next == '\n') continue;
            }
            if (next is '\r' or '\n')
            {
                if (pendingHighSurrogate) throw new InvalidDataException();
                if (next == '\r') _skipLf = true;
                return line.ToString();
            }
            if (char.IsHighSurrogate(next))
            {
                if (pendingHighSurrogate) throw new InvalidDataException();
                pendingHighSurrogate = true;
            }
            else if (char.IsLowSurrogate(next))
            {
                if (!pendingHighSurrogate) throw new InvalidDataException();
                pendingHighSurrogate = false;
                bytes += 4;
            }
            else
            {
                if (pendingHighSurrogate) throw new InvalidDataException();
                bytes += next <= 0x7F ? 1 : next <= 0x7FF ? 2 : 3;
            }
            if (bytes > MaxLineBytes || line.Length == MaxLineBytes)
                throw new InvalidDataException();
            line.Append(next);
        }
    }

    internal static async Task<string> ReadBoundedAllAsync(TextReader input,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var text = new StringBuilder();
        var encoder = new UTF8Encoding(false, true).GetEncoder();
        var bytes = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0) break;
            var nextBytes = encoder.GetByteCount(buffer.AsSpan(0, count), flush: false);
            if (nextBytes > MaxLineBytes - bytes || count > MaxLineBytes - text.Length)
                throw new InvalidDataException();
            bytes += nextBytes;
            text.Append(buffer, 0, count);
        }
        if (encoder.GetByteCount(ReadOnlySpan<char>.Empty, flush: true) > MaxLineBytes - bytes)
            throw new InvalidDataException();
        return text.ToString();
    }
}
