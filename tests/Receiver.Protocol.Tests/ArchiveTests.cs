using System.Security.Cryptography;
using System.Text;
using Receiver.Protocol;
using Xunit;

namespace Receiver.Protocol.Tests;

/// <summary>
/// Decoder tests against fixtures written by tools/make-fixtures.py, a separate
/// implementation built from the format description. Decoding what our own encoder
/// produced would only prove self-consistency.
/// </summary>
public class ArchiveTests
{
    private static string FixtureDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new DirectoryNotFoundException(
            "tests/fixtures not found. Run: python tools/make-fixtures.py tests/fixtures");
    }

    private static Stream Fixture(string name) =>
        File.OpenRead(Path.Combine(FixtureDir(), name));

    private static List<(CpioEntry Entry, byte[] Data)> ReadAll(Stream archive)
    {
        var reader = new CpioReader(archive);
        var result = new List<(CpioEntry, byte[])>();
        while (true)
        {
            var entry = reader.ReadNext(out var content);
            if (entry is null) break;
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            result.Add((entry, ms.ToArray()));
        }
        return result;
    }

    // --- CPIO, both formats ---------------------------------------------------------

    [Theory]
    [InlineData("single-file.cpio", CpioFormat.Odc)]
    [InlineData("single-file-newc.cpio", CpioFormat.Newc)]
    public void Both_cpio_formats_read_the_same_file(string fixture, CpioFormat expected)
    {
        // Which format arrives is not ours to choose: Apple writes newc, opendrop odc.
        using var s = Fixture(fixture);
        var entries = ReadAll(s);

        var (entry, data) = Assert.Single(entries);
        Assert.Equal("hello.txt", entry.Name);
        Assert.Equal(expected, entry.Format);
        Assert.True(entry.IsRegularFile);
        Assert.Equal("Hallo Welt\n", Encoding.UTF8.GetString(data));
    }

    [Fact]
    public void Newc_padding_does_not_desynchronise_a_multi_member_archive()
    {
        // newc pads both the name and the data to a 4-byte boundary. Getting that wrong
        // shows up as a corrupt magic on the *second* member, not the first.
        using var s = Fixture("multi-file-newc.dvzip");
        using var dv = new DvZipStream(s);
        var entries = ReadAll(dv);

        Assert.Equal(4, entries.Count);
        Assert.All(entries, e => Assert.Equal(CpioFormat.Newc, e.Entry.Format));
        Assert.Equal("notes.txt", entries[0].Entry.Name);
        Assert.Equal("photos/img1.bin", entries[2].Entry.Name);
        Assert.Equal(50_000, entries[2].Data.Length);
        Assert.Empty(entries[3].Data);
    }

    [Fact]
    public void Archive_root_member_is_recognised_rather_than_treated_as_a_path()
    {
        // iOS puts a "." member at the head of its archives. A traversal guard that has
        // not been told about it rejects the whole transfer.
        using var s = Fixture("archive-root.dvzip");
        using var dv = new DvZipStream(s);
        var entries = ReadAll(dv);

        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].Entry.IsArchiveRoot);
        Assert.Equal("IMG_0001.HEIC", entries[1].Entry.Name);
        Assert.Equal(1024, entries[1].Data.Length);
        Assert.False(entries[1].Entry.IsArchiveRoot);
    }

    [Fact]
    public void Wrong_cpio_magic_is_reported_clearly()
    {
        using var s = Fixture("corrupt-badmagic.cpio");
        var reader = new CpioReader(s);
        var ex = Assert.Throws<InvalidDataException>(() => reader.ReadNext(out _));
        Assert.Contains("070707", ex.Message);
    }

    // --- DVZip -----------------------------------------------------------------------

    [Fact]
    public void DvZip_unwraps_to_the_same_bytes_as_the_plain_archive()
    {
        using var plain = Fixture("multi-file.cpio");
        using var plainMs = new MemoryStream();
        plain.CopyTo(plainMs);

        using var zipped = Fixture("multi-file.dvzip");
        using var dv = new DvZipStream(zipped);
        using var decoded = new MemoryStream();
        dv.CopyTo(decoded);

        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(plainMs.ToArray())),
            Convert.ToHexString(SHA256.HashData(decoded.ToArray())));
        Assert.True(dv.Blocks.Count > 1, "the 50 KB fixture must span several blocks");
        Assert.All(dv.Blocks, b => Assert.False(b.Stored));
        Assert.All(dv.Blocks, b => Assert.False(b.StoredFlagSet));
    }

    [Fact]
    public void Stored_blocks_are_recognised_from_bit_31()
    {
        // The encoding a real iPhone transfer was observed to use: bit 31 of the length
        // word marks a stored block and the length is the low 31 bits.
        using var stored = Fixture("single-file-stored.dvzip");
        using var dv = new DvZipStream(stored);
        using var decoded = new MemoryStream();
        dv.CopyTo(decoded);

        var entries = ReadAll(new MemoryStream(decoded.ToArray()));
        var (entry, data) = Assert.Single(entries);
        Assert.Equal("hello.txt", entry.Name);
        Assert.Equal("Hallo Welt\n", Encoding.UTF8.GetString(data));

        Assert.All(dv.Blocks, b => Assert.True(b.StoredFlagSet));
        Assert.All(dv.Blocks, b => Assert.True(b.Stored));
        // The length must be masked, or the block would be read far too long.
        Assert.All(dv.Blocks, b => Assert.True(b.RawLength < 0x1000));
    }

    [Fact]
    public void Stored_blocks_without_the_flag_still_decode()
    {
        // Fallback path: a block with no zlib header is treated as stored even when
        // bit 31 is clear. Trusting one signal alone turns a surprise into a failure.
        using var stored = Fixture("single-file-stored-noflag.dvzip");
        using var dv = new DvZipStream(stored);
        var entries = ReadAll(dv);

        var (entry, data) = Assert.Single(entries);
        Assert.Equal("hello.txt", entry.Name);
        Assert.Equal("Hallo Welt\n", Encoding.UTF8.GetString(data));
        Assert.All(dv.Blocks, b => Assert.False(b.StoredFlagSet));
        Assert.All(dv.Blocks, b => Assert.True(b.Stored));
    }

    [Fact]
    public void Truncated_container_is_rejected_not_silently_accepted()
    {
        using var s = Fixture("corrupt-truncated.dvzip");
        using var dv = new DvZipStream(s);
        using var sink = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => dv.CopyTo(sink));
    }

    [Fact]
    public void Block_length_beyond_the_limit_is_rejected()
    {
        using var s = Fixture("corrupt-hugeblock.dvzip");
        using var dv = new DvZipStream(s);
        using var sink = new MemoryStream();
        var ex = Assert.Throws<InvalidDataException>(() => dv.CopyTo(sink));
        Assert.Contains("exceeds", ex.Message);
    }

    [Fact]
    public void Zlib_header_with_garbage_body_does_not_throw_out_of_the_decoder()
    {
        using var s = Fixture("corrupt-badzlib.dvzip");
        using var dv = new DvZipStream(s);
        using var sink = new MemoryStream();
        dv.CopyTo(sink);
        Assert.Equal(10, sink.Length);
        Assert.All(dv.Blocks, b => Assert.True(b.Stored));
    }

    // --- encoding detection ------------------------------------------------------------

    [Theory]
    [InlineData("single-file.cpio", UploadEncoding.Cpio)]
    [InlineData("single-file-newc.cpio", UploadEncoding.Cpio)]
    [InlineData("single-file.cpio.gz", UploadEncoding.Gzip)]
    [InlineData("single-file.dvzip", UploadEncoding.DvZip)]
    [InlineData("single-file-stored.dvzip", UploadEncoding.DvZip)]
    public void Upload_encoding_is_detected_from_the_bytes(string fixture, UploadEncoding expected)
    {
        // The sender does not reliably signal this. opendrop sends gzip while declaring
        // only Content-Type: application/x-cpio, so the bytes have to decide.
        using var s = Fixture(fixture);
        var head = new byte[UploadEncodingDetector.ProbeBytes];
        int got = s.Read(head, 0, head.Length);
        Assert.Equal(expected, UploadEncodingDetector.Detect(head.AsSpan(0, got)));
    }

    [Theory]
    [InlineData("single-file.cpio")]
    [InlineData("single-file-newc.cpio")]
    [InlineData("single-file.cpio.gz")]
    [InlineData("single-file.dvzip")]
    [InlineData("single-file-newc.dvzip")]
    [InlineData("single-file-stored.dvzip")]
    public void Every_encoding_yields_the_same_file(string fixture)
    {
        // Whatever the wrapping, the content must come out identical. This is the
        // property that matters: the receiver cannot know in advance what it will get.
        using var s = Fixture(fixture);
        using var archive = UploadEncodingDetector.OpenArchive(s, out _, out _);
        var entries = ReadAll(archive);

        var (entry, data) = Assert.Single(entries);
        Assert.Equal("hello.txt", entry.Name);
        Assert.Equal("Hallo Welt\n", Encoding.UTF8.GetString(data));
    }

    [Fact]
    public void Gzip_magic_is_not_mistaken_for_a_dvzip_block_length()
    {
        // The concrete failure this guards against: 1f 8b 08 00 read as a big-endian
        // length is 529,205,248, which a DVZip reader reports as an oversized block.
        Span<byte> gzipHead = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00];
        Assert.Equal(UploadEncoding.Gzip, UploadEncodingDetector.Detect(gzipHead));
    }
}
