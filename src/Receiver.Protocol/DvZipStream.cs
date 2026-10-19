using System.Buffers.Binary;
using System.IO.Compression;

namespace Receiver.Protocol;

/// <summary>
/// Decodes Apple's DVZip container as a forward-only stream.
///
/// Layout, per the opendrop-rs protocol notes: a sequence of blocks, each a 4-byte
/// big-endian length followed by that many bytes of zlib data. Concatenating the
/// decompressed blocks yields one CPIO archive.
///
/// Not every block is compressed. **Bit 31 of the length word marks a stored block**, and
/// the length is then the low 31 bits. That encoding is not guessed here: an independent
/// Windows project (WinDrop) derived it from a real iPhone transfer, where a photo
/// arrived with the length word 0x80020000 -- bit 31 set over a length of exactly
/// 128 KiB, carrying raw JPEG bytes that deflate could not usefully shrink.
///
/// The zlib-header probe is kept as a second line of defence, because a decoder that
/// trusts one signal alone turns a single surprise into a failed transfer.
/// </summary>
public sealed class DvZipStream : Stream
{
    /// <summary>Guards against a hostile or corrupt length field.</summary>
    public const int MaxBlockLength = 64 * 1024 * 1024;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly List<BlockInfo> _blocks = [];

    private MemoryStream? _current;
    private bool _finished;

    public DvZipStream(Stream inner, bool leaveOpen = false)
    {
        _inner = inner;
        _leaveOpen = leaveOpen;
    }

    /// <param name="StoredFlagSet">Whether bit 31 of the length word was set.</param>
    public readonly record struct BlockInfo(
        int RawLength, bool Stored, int DecodedLength, bool StoredFlagSet);

    /// <summary>What each block turned out to be. Written as evidence, not guessed.</summary>
    public IReadOnlyList<BlockInfo> Blocks => _blocks;

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            if (_current is not null)
            {
                int n = _current.Read(buffer);
                if (n > 0) return n;
                _current.Dispose();
                _current = null;
            }

            if (_finished) return 0;
            if (!ReadNextBlock()) { _finished = true; return 0; }
        }
    }

    private bool ReadNextBlock()
    {
        Span<byte> lenBuf = stackalloc byte[4];
        int got = ReadExact(_inner, lenBuf);
        if (got == 0) return false;                       // clean end of container
        if (got < 4) throw new InvalidDataException("DVZip: truncated block length.");

        uint raw = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
        if (raw == 0) return false;                       // explicit terminator

        bool storedFlag = (raw & 0x8000_0000u) != 0;
        uint length = raw & 0x7FFF_FFFFu;

        if (length > MaxBlockLength)
            throw new InvalidDataException(
                $"DVZip: block length {length} exceeds the {MaxBlockLength} byte limit.");

        var payload = new byte[(int)length];
        if (ReadExact(_inner, payload) != payload.Length)
            throw new InvalidDataException("DVZip: truncated block payload.");

        // zlib streams start with CMF/FLG where CMF's low nibble is 8 (deflate) and the
        // two bytes together are a multiple of 31.
        bool looksZlib = !storedFlag
                         && payload.Length >= 2
                         && (payload[0] & 0x0F) == 8
                         && ((payload[0] << 8) | payload[1]) % 31 == 0;

        byte[] decoded;
        if (looksZlib)
        {
            try
            {
                using var src = new MemoryStream(payload, writable: false);
                using var inflate = new ZLibStream(src, CompressionMode.Decompress);
                using var dst = new MemoryStream();
                inflate.CopyTo(dst);
                decoded = dst.ToArray();
            }
            catch (InvalidDataException)
            {
                // Header looked right but the body was not deflate after all: treat the
                // block as stored rather than failing the whole transfer.
                decoded = payload;
                looksZlib = false;
            }
        }
        else
        {
            decoded = payload;
        }

        _blocks.Add(new BlockInfo((int)length, Stored: !looksZlib, decoded.Length, storedFlag));
        _current = new MemoryStream(decoded, writable: false);
        return true;
    }

    private static int ReadExact(Stream s, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _current?.Dispose();
            if (!_leaveOpen) _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
