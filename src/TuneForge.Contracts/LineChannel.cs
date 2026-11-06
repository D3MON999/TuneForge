using System.Text;

namespace TuneForge.Contracts;

/// <summary>Thrown when a peer sends more than the protocol allows in one message.</summary>
public sealed class MessageTooLargeException : Exception
{
    public MessageTooLargeException() : base("Message exceeds the protocol size limit.") { }
}

/// <summary>Reads and writes newline-delimited UTF-8 messages with a hard size limit.</summary>
public sealed class LineChannel(Stream stream, int maxBytes = RpcProtocol.MaxMessageBytes)
{
    private readonly byte[] _buffer = new byte[4096];
    private int _start;
    private int _end;

    /// <summary>Returns the next message, or null when the peer closed the stream.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                if (_end == 0)
                {
                    _end = 0;
                    return null;
                }
            }

            var span = _buffer.AsSpan(_start, _end - _start);
            var newline = span.IndexOf((byte)'\n');
            var take = newline >= 0 ? newline : span.Length;
            if (line.Length + take > maxBytes)
            {
                throw new MessageTooLargeException();
            }

            line.Write(span[..take]);
            _start += newline >= 0 ? take + 1 : take;
            if (newline >= 0)
            {
                return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
            }
        }
    }

    public async Task WriteLineAsync(string message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
