namespace OpenIndustrialComm.Transports;

public static class ByteReader
{
    public static async Task<byte[]> ReadExactAsync(ITransport transport, int length, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await transport.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Remote endpoint closed the connection.");
            offset += read;
        }
        return buffer;
    }
}
