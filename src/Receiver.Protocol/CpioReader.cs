using System.Text;

namespace Receiver.Protocol;

public enum CpioFormat
{
    /// <summary>POSIX.1 portable, magic "070707", octal fields, no padding.</summary>
    Odc,
    /// <summary>SVR4 "new ASCII", magic "070701", hex fields, 4-byte padding.</summary>
    Newc,
}

public sealed class CpioEntry
{
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required int Mode { get; init; }
    public required CpioFormat Format { get; init; }

    /// <summary>Unix file-type bits (S_IFMT).</summary>
    public int FileType => Mode & 0xF000;

    public bool IsRegularFile => FileType == 0x8000;
    public bool IsDirectory => FileType == 0x4000;
    public bool IsSymlink => FileType == 0xA000;

    /// <summary>
    /// True for the "." member iOS puts at the head of its archives. It names the
    /// archive root, not a directory to create, and a traversal guard that has not been
    /// told about it will reject the whole transfer.
    /// </summary>
    public bool IsArchiveRoot => Name is "." or "./";

    public override string ToString() =>
        $"{Name} ({Size} bytes, mode 0{Convert.ToString(Mode, 8)}, {Format})";
}

/// <summary>
/// Forward-only CPIO reader accepting both formats AirDrop traffic has been seen to use.
///
/// Which one arrives is not ours to choose. Apple writes newc; opendrop writes odc,
/// because libarchive's format name "cpio" selects odc rather than newc. A reader that
/// insists on one of them is stricter than the protocol, so this one dispatches on the
/// magic of each header rather than being configured up front.
///
///   odc : 76-byte header, all fields octal ASCII, nothing padded.
///   newc: 110-byte header, all fields hexadecimal ASCII, name and data each padded
///         with NULs to a 4-byte boundary.
///
/// Both end with an entry named "TRAILER!!!".
/// </summary>
public sealed class CpioReader
{
    public const string TrailerName = "TRAILER!!!";
    private const int OdcHeaderLength = 76;
    private const int NewcHeaderLength = 110;

    /// <summary>Refuses absurd names long before any filesystem call happens.</summary>
    public const int MaxNameLength = 4096;

    private readonly Stream _stream;
    private BoundedStream? _current;

    public CpioReader(Stream stream) => _stream = stream;

    /// <summary>
    /// Reads the next header. Returns null at the trailer or at end of stream.
    /// Any bytes of the previous entry left unread are skipped automatically, so the
    /// caller cannot desynchronise the archive by ignoring an entry's content.
    /// </summary>
    public CpioEntry? ReadNext(out Stream content)
    {
        content = Stream.Null;

        // Finish the previous entry, including its padding, before looking for a header.
        if (_current is not null)
        {
            _current.DrainRemaining();
            SkipPadding(_current.PaddingAfter);
            _current = null;
        }

        var magic = new byte[6];
        int got = ReadExact(magic);
        if (got == 0) return null;
        if (got < 6) throw new InvalidDataException("CPIO: truncated header.");

        string magicText = Encoding.ASCII.GetString(magic);
        var format = magicText switch
        {
            "070707" => CpioFormat.Odc,
            "070701" => CpioFormat.Newc,
            // "070702" is newc with a CRC field; the layout is identical and the CRC is
            // advisory, so it is read as newc rather than refused.
            "070702" => CpioFormat.Newc,
            _ => throw new InvalidDataException(
                $"CPIO: expected magic 070707 or 070701 but found '{Sanitize(magicText)}'."),
        };

        return format == CpioFormat.Odc
            ? ReadOdc(out content)
            : ReadNewc(out content);
    }

    private CpioEntry? ReadOdc(out Stream content)
    {
        // Remaining 70 bytes after the magic we already consumed.
        var rest = new byte[OdcHeaderLength - 6];
        if (ReadExact(rest) != rest.Length)
            throw new InvalidDataException("CPIO odc: truncated header.");

        // Offsets are relative to the full header, so subtract the 6 magic bytes.
        int mode = (int)Octal(rest, 18 - 6, 6);
        long nameSize = Octal(rest, 59 - 6, 6);
        long fileSize = Octal(rest, 65 - 6, 11);

        return Finish(nameSize, fileSize, mode, CpioFormat.Odc,
            namePadding: 0, dataPadding: 0, out content);
    }

    private CpioEntry? ReadNewc(out Stream content)
    {
        var rest = new byte[NewcHeaderLength - 6];
        if (ReadExact(rest) != rest.Length)
            throw new InvalidDataException("CPIO newc: truncated header.");

        // Field order after the magic: ino, mode, uid, gid, nlink, mtime, filesize,
        // devmajor, devminor, rdevmajor, rdevminor, namesize, check -- 8 hex chars each.
        int mode = (int)Hex(rest, 1 * 8, 8);
        long fileSize = Hex(rest, 6 * 8, 8);
        long nameSize = Hex(rest, 11 * 8, 8);

        // Header plus name is padded to a 4-byte boundary, and so is the data.
        int namePadding = (int)((4 - (NewcHeaderLength + nameSize) % 4) % 4);
        int dataPadding = (int)((4 - fileSize % 4) % 4);

        return Finish(nameSize, fileSize, mode, CpioFormat.Newc,
            namePadding, dataPadding, out content);
    }

    private CpioEntry? Finish(
        long nameSize, long fileSize, int mode, CpioFormat format,
        int namePadding, int dataPadding, out Stream content)
    {
        content = Stream.Null;

        if (nameSize is <= 0 or > MaxNameLength)
            throw new InvalidDataException($"CPIO: implausible name length {nameSize}.");
        if (fileSize < 0)
            throw new InvalidDataException($"CPIO: negative file size {fileSize}.");

        var nameBytes = new byte[nameSize];
        if (ReadExact(nameBytes) != nameBytes.Length)
            throw new InvalidDataException("CPIO: truncated name.");
        SkipPadding(namePadding);

        // The stored length includes the terminating NUL.
        int end = Array.IndexOf(nameBytes, (byte)0);
        string name = Encoding.UTF8.GetString(nameBytes, 0, end >= 0 ? end : nameBytes.Length);

        if (name == TrailerName) return null;

        var stream = new BoundedStream(_stream, fileSize, dataPadding);
        _current = stream;
        content = stream;

        return new CpioEntry { Name = name, Size = fileSize, Mode = mode, Format = format };
    }

    private void SkipPadding(int count)
    {
        if (count <= 0) return;
        Span<byte> pad = stackalloc byte[4];
        ReadExact(pad[..count]);
    }

    private static long Octal(byte[] buffer, int offset, int length)
    {
        long value = 0;
        for (int i = offset; i < offset + length; i++)
        {
            byte c = buffer[i];
            // Some writers pad with spaces or NUL instead of leading zeros.
            if (c is (byte)' ' or 0) continue;
            if (c < '0' || c > '7')
                throw new InvalidDataException($"CPIO: non-octal digit 0x{c:X2} in a numeric field.");
            value = value * 8 + (c - '0');
        }
        return value;
    }

    private static long Hex(byte[] buffer, int offset, int length)
    {
        long value = 0;
        for (int i = offset; i < offset + length; i++)
        {
            byte c = buffer[i];
            if (c is (byte)' ' or 0) continue;
            int digit = c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - '0',
                >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                _ => throw new InvalidDataException(
                    $"CPIO newc: non-hex digit 0x{c:X2} in a numeric field."),
            };
            value = value * 16 + digit;
        }
        return value;
    }

    private int ReadExact(Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = _stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static string Sanitize(string s) =>
        new(s.Select(c => char.IsControl(c) ? '.' : c).ToArray());

    /// <summary>Consumes any unread bytes of the current entry.</summary>
    public static void Drain(Stream content)
    {
        var scratch = new byte[8192];
        while (content.Read(scratch, 0, scratch.Length) > 0) { }
    }

    /// <summary>
    /// Exposes exactly the entry's bytes and then reports end of stream, so one entry
    /// can never read into the next one's header.
    /// </summary>
    private sealed class BoundedStream(Stream inner, long length, int paddingAfter) : Stream
    {
        private readonly long _total = length;
        private long _remaining = length;

        public int PaddingAfter { get; } = paddingAfter;

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0) return 0;
            int want = (int)Math.Min(buffer.Length, _remaining);
            int n = inner.Read(buffer[..want]);
            _remaining -= n;
            return n;
        }

        public void DrainRemaining()
        {
            Span<byte> scratch = stackalloc byte[4096];
            while (_remaining > 0 && Read(scratch) > 0) { }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _total;
        public override long Position
        {
            get => _total - _remaining;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
