using System.Buffers.Binary;

namespace RevitMCP.Contracts;

/// <summary>
/// Length-prefixed framing over the pipe: 4-byte little-endian payload length, then UTF-8 JSON.
/// Byte-stream framing (rather than PipeTransmissionMode.Message) keeps the client side portable —
/// a message-mode pipe needs the client to opt in, and partial reads are easy to get wrong.
/// </summary>
public static class Framing
{
    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length > Protocol.MaxFrameBytes)
            throw new InvalidOperationException($"Frame of {payload.Length} bytes exceeds the {Protocol.MaxFrameBytes}-byte limit.");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame. Returns null on a clean end-of-stream (peer closed between frames).</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEofAsync(stream, header, ct).ConfigureAwait(false))
            return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > Protocol.MaxFrameBytes)
            throw new InvalidDataException($"Declared frame length {length} is out of range.");
        if (length == 0) return [];

        var payload = new byte[length];
        if (!await ReadExactlyOrEofAsync(stream, payload, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Peer closed the pipe midway through a frame.");
        return payload;
    }

    /// <summary>Fills the buffer. False only if EOF arrived before any byte was read.</summary>
    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return read != 0 ? throw new EndOfStreamException("Truncated frame.") : false;
            read += n;
        }
        return true;
    }
}
