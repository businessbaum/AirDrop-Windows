using System.IO.Compression;

namespace Receiver.Protocol;

public enum UploadEncoding
{
    /// <summary>A bare CPIO archive: the body starts with an ASCII cpio magic.</summary>
    Cpio,
    /// <summary>gzip, magic 1f 8b.</summary>
    Gzip,
    /// <summary>Apple's DVZip framing: a length word, then a block.</summary>
    DvZip,
}

/// <summary>
/// Decides how an /Upload body is encoded by looking at the bytes.
///
/// Guessing from our own advertised capability flags is the mistake to avoid here, and
/// it is not hypothetical: an independent Windows project reasoned "we advertised DVZip,
/// so this is DVZip", and opendrop sent gzip with only `Content-Type: application/x-cpio`
/// and no `Content-Encoding` at all. The DVZip reader then read gzip's magic `1f 8b 08 00`
/// as a big-endian length of 529.205.248 bytes and failed.
///
/// All three encodings identify themselves, so the bytes get the deciding vote. A
/// `Content-Encoding` header is still honoured as a hint where a peer sends one, but it
/// cannot override what the body actually is.
/// </summary>
public static class UploadEncodingDetector
{
    /// <summary>How many leading bytes <see cref="Detect"/> needs.</summary>
    public const int ProbeBytes = 6;

    public static UploadEncoding Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B)
            return UploadEncoding.Gzip;

        // cpio magics are ASCII digits: "070707" (odc), "070701"/"070702" (newc).
        if (head.Length >= 6
            && head[0] == '0' && head[1] == '7' && head[2] == '0'
            && head[3] == '7' && head[4] == '0'
            && head[5] is (byte)'7' or (byte)'1' or (byte)'2')
            return UploadEncoding.Cpio;

        // Anything else is DVZip, whose frame opens with a length rather than a constant.
        return UploadEncoding.DvZip;
    }

    /// <summary>
    /// Wraps <paramref name="body"/> so the result yields plain CPIO bytes, whatever the
    /// body was encoded as. <paramref name="detected"/> reports what it turned out to be.
    /// The DVZip instance, when one is used, is handed back so its per-block record can
    /// be logged as evidence.
    /// </summary>
    public static Stream OpenArchive(Stream body, out UploadEncoding detected, out DvZipStream? dvzip)
    {
        var head = new byte[ProbeBytes];
        int got = 0;
        while (got < head.Length)
        {
            int n = body.Read(head, got, head.Length - got);
            if (n == 0) break;
            got += n;
        }

        detected = Detect(head.AsSpan(0, got));

        // The probed bytes are part of the archive, so put them back in front.
        Stream full = got == 0
            ? body
            : new ConcatStream(new MemoryStream(head, 0, got, writable: false), body);

        switch (detected)
        {
            case UploadEncoding.Gzip:
                dvzip = null;
                return new GZipStream(full, CompressionMode.Decompress);
            case UploadEncoding.Cpio:
                dvzip = null;
                return full;
            default:
                var dv = new DvZipStream(full, leaveOpen: true);
                dvzip = dv;
                return dv;
        }
    }

    /// <summary>Reads one stream to its end, then continues with the next.</summary>
    private sealed class ConcatStream(Stream first, Stream second) : Stream
    {
        private bool _firstDone;

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (!_firstDone)
            {
                int n = first.Read(buffer);
                if (n > 0) return n;
                _firstDone = true;
            }
            return second.Read(buffer);
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
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { first.Dispose(); second.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
