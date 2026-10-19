using Receiver.Protocol;
using Xunit;

namespace Receiver.Protocol.Tests;

/// <summary>
/// The whole path from an /Upload body to files on disk, driven by the same fixtures the
/// decoder tests use. These assert the properties the architecture demands: nothing lands
/// outside the destination, a failure leaves no directory that looks finished, and the
/// published transfer appears only once every byte has been written.
/// </summary>
public class TransferStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "airdrop-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* a leftover temp directory is not worth failing a test over */ }
    }

    private static string FixtureDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new DirectoryNotFoundException("tests/fixtures not found.");
    }

    private static Stream Fixture(string name) =>
        File.OpenRead(Path.Combine(FixtureDir(), name));

    private static IEnumerable<string> FilesUnder(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                 .Select(f => Path.GetRelativePath(dir, f));

    [Theory]
    [InlineData("multi-file.dvzip")]
    [InlineData("multi-file-newc.dvzip")]
    public async Task Stores_every_member_whatever_the_cpio_format(string fixture)
    {
        var store = new TransferStore(_root);
        await using var body = Fixture(fixture);

        var result = await store.StoreAsync(body, "Testgeraet");

        Assert.Equal(3, result.Files.Count);   // the directory member is not a file
        var files = FilesUnder(result.FinalDirectory).ToList();
        Assert.Contains("notes.txt", files);
        Assert.Contains(Path.Combine("photos", "img1.bin"), files);
        Assert.Contains("empty.txt", files);
        Assert.Equal(50_000, new FileInfo(
            Path.Combine(result.FinalDirectory, "photos", "img1.bin")).Length);
    }

    [Fact]
    public async Task Accepts_an_archive_that_begins_with_the_root_member()
    {
        // Without the archive-root rule the "." member trips the traversal guard and the
        // whole transfer fails -- which is how a real iPhone archive would be lost.
        var store = new TransferStore(_root);
        await using var body = Fixture("archive-root.dvzip");

        var result = await store.StoreAsync(body, "iPhone");

        var file = Assert.Single(result.Files);
        Assert.Equal("IMG_0001.HEIC", file.RelativePath);
        Assert.Equal(1024, file.Bytes);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public async Task Gzip_body_without_a_content_encoding_header_still_works()
    {
        var store = new TransferStore(_root);
        await using var body = Fixture("single-file.cpio.gz");

        var result = await store.StoreAsync(body, "opendrop");

        var file = Assert.Single(result.Files);
        Assert.Equal("hello.txt", file.RelativePath);
    }

    [Theory]
    [InlineData("hostile-names.dvzip")]
    [InlineData("hostile-names-newc.dvzip")]
    public async Task Hostile_names_never_escape_the_destination(string fixture)
    {
        var store = new TransferStore(_root);
        await using var body = Fixture(fixture);

        var result = await store.StoreAsync(body, "Angreifer");

        // Everything written must sit under the published directory.
        foreach (var relative in FilesUnder(result.FinalDirectory))
        {
            var full = Path.GetFullPath(Path.Combine(result.FinalDirectory, relative));
            Assert.True(SafePath.IsInside(result.FinalDirectory, full),
                $"'{relative}' landed outside the destination");
        }

        // And nothing may have appeared beside it, which is where "../" would aim.
        var strays = Directory.EnumerateFileSystemEntries(_root)
            .Where(p => !string.Equals(p, result.FinalDirectory, StringComparison.OrdinalIgnoreCase))
            .Where(p => !Path.GetFileName(p).StartsWith('.'))
            .ToList();
        Assert.Empty(strays);

        Assert.Contains(result.Files, f => f.RelativePath == "ok.txt");
    }

    [Fact]
    public async Task A_corrupt_body_leaves_no_finished_looking_directory()
    {
        var store = new TransferStore(_root);
        await using var body = Fixture("corrupt-truncated.dvzip");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.StoreAsync(body, "Kaputt"));

        // The destination may hold an (empty) staging root, but no published transfer.
        var published = Directory.EnumerateDirectories(_root)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .ToList();
        Assert.Empty(published);

        var staging = Path.Combine(_root, ".incoming");
        if (Directory.Exists(staging))
            Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }

    [Fact]
    public async Task Too_many_files_is_refused_and_nothing_is_published()
    {
        var store = new TransferStore(_root, new TransferLimits { MaxFiles = 2 });
        await using var body = Fixture("multi-file.dvzip");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.StoreAsync(body, "ZuViele"));

        Assert.DoesNotContain(Directory.EnumerateDirectories(_root),
            d => !Path.GetFileName(d).StartsWith('.'));
    }

    [Fact]
    public async Task Two_transfers_from_the_same_sender_get_separate_directories()
    {
        var store = new TransferStore(_root);

        await using (var first = Fixture("single-file.dvzip"))
            await store.StoreAsync(first, "Alice");
        await using (var second = Fixture("single-file.dvzip"))
            await store.StoreAsync(second, "Alice");

        var dirs = Directory.EnumerateDirectories(_root)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .ToList();
        Assert.Equal(2, dirs.Count);
        Assert.All(dirs, d => Assert.Single(FilesUnder(d)));
    }
}
