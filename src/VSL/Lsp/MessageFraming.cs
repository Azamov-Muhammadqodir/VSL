using System.Text;

namespace VSL.Lsp;

/// <summary>
/// "Content-Length: N\r\n\r\n{json}" framing shared by the Language Server Protocol and the Debug Adapter Protocol.
/// </summary>
public sealed class MessageFraming(Stream input, Stream output)
{
    readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Reads one message body, or returns null at end of stream.</summary>
    public async Task<byte[]?> ReadAsync(CancellationToken ct)
    {
        var length = await ReadHeadersAsync(ct).ConfigureAwait(false);
        if (length < 0)
            return null;
        var buffer = new byte[length];
        await input.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }

    public async Task WriteAsync(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await output.WriteAsync(header).ConfigureAwait(false);
            await output.WriteAsync(body).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Reads header lines; returns Content-Length, or -1 at end of stream.</summary>
    async Task<int> ReadHeadersAsync(CancellationToken ct)
    {
        var length = -1;
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await input.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0)
                return -1;
            if (one[0] == '\n')
            {
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0)
                {
                    if (length >= 0)
                        return length;
                    continue;
                }
                var colon = text.IndexOf(':');
                if (colon > 0 && text[..colon].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(text[(colon + 1)..].Trim());
            }
            else
            {
                line.Append((char)one[0]);
            }
        }
    }
}
