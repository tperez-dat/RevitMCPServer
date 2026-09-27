using System.Text;
using RevitMCP.Contracts;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// Framing is the one piece both processes must agree on byte for byte, so it is tested against the
/// cases that actually break wire protocols: split reads, empty payloads, and truncation.
/// </summary>
public class FramingTests
{
    [Fact]
    public async Task RoundTripsAPayload()
    {
        using var stream = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("""{"command":"GET_PROJECT_INFO"}""");

        await Framing.WriteFrameAsync(stream, payload);
        stream.Position = 0;

        var read = await Framing.ReadFrameAsync(stream);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task RoundTripsSeveralFramesInOrder()
    {
        using var stream = new MemoryStream();
        var first = Encoding.UTF8.GetBytes("one");
        var second = Encoding.UTF8.GetBytes("two");

        await Framing.WriteFrameAsync(stream, first);
        await Framing.WriteFrameAsync(stream, second);
        stream.Position = 0;

        Assert.Equal(first, await Framing.ReadFrameAsync(stream));
        Assert.Equal(second, await Framing.ReadFrameAsync(stream));
        Assert.Null(await Framing.ReadFrameAsync(stream));      // clean end of stream
    }

    [Fact]
    public async Task ReturnsNullAtCleanEndOfStream()
    {
        using var stream = new MemoryStream();
        Assert.Null(await Framing.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task HandlesAnEmptyPayload()
    {
        using var stream = new MemoryStream();
        await Framing.WriteFrameAsync(stream, ReadOnlyMemory<byte>.Empty);
        stream.Position = 0;

        Assert.Empty((await Framing.ReadFrameAsync(stream))!);
    }

    [Fact]
    public async Task ThrowsOnATruncatedPayload()
    {
        using var stream = new MemoryStream();
        await Framing.WriteFrameAsync(stream, Encoding.UTF8.GetBytes("abcdefgh"));

        // Keep the 4-byte header but lose half the body, as a dropped connection would.
        var truncated = new MemoryStream(stream.ToArray()[..8]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Framing.ReadFrameAsync(truncated));
    }

    [Fact]
    public async Task ThrowsOnAnAbsurdDeclaredLength()
    {
        // A hostile or desynchronised peer must not be able to make us allocate arbitrarily.
        var header = BitConverter.GetBytes(Protocol.MaxFrameBytes + 1);
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task ReadsAPayloadDeliveredInPieces()
    {
        // A pipe read returns what has arrived, not what was asked for; the reader must loop.
        var payload = Encoding.UTF8.GetBytes(new string('x', 5000));

        using var buffer = new MemoryStream();
        await Framing.WriteFrameAsync(buffer, payload);

        using var dribbling = new DribblingStream(buffer.ToArray(), chunkSize: 7);
        Assert.Equal(payload, await Framing.ReadFrameAsync(dribbling));
    }

    /// <summary>Returns at most a few bytes per read, the way a real socket does.</summary>
    private sealed class DribblingStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = Math.Min(Math.Min(chunkSize, count), data.Length - _position);
            if (available <= 0) return 0;

            Array.Copy(data, _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var temp = new byte[buffer.Length];
            var read = Read(temp, 0, buffer.Length);
            temp.AsMemory(0, read).CopyTo(buffer);
            return ValueTask.FromResult(read);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
