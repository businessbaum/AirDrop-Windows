namespace Receiver.Protocol;

/// <summary>
/// Limits on what one transfer may cost us. These are product decisions, not properties
/// of the AirDrop protocol, and are meant to be configurable.
/// </summary>
public sealed record TransferLimits
{
    public int MaxFiles { get; init; } = 100;
    public long MaxTotalBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public long MaxSingleFileBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// Cap on how far the archive may expand relative to the bytes the sender pushed.
    /// Without it, a small upload of highly compressible data could fill the disk.
    /// </summary>
    public int MaxExpansionRatio { get; init; } = 200;
}

public sealed record StoredFile(string RelativePath, long Bytes, bool NameWasRewritten, string? RewriteReason);

public sealed record StoreResult(
    string FinalDirectory,
    IReadOnlyList<StoredFile> Files,
    long TotalBytes,
    IReadOnlyList<string> Rejected);

/// <summary>
/// Turns an /Upload body into files on disk.
///
/// The guarantees this type is built around:
/// - Memory use does not grow with the size of the transfer; everything streams.
/// - Nothing is written outside the destination, whatever the archive claims.
/// - A failed or aborted transfer leaves no directory that looks complete. The files are
///   assembled in a private staging directory on the same volume and only moved into
///   place once every byte has been received and checked.
/// </summary>
public sealed class TransferStore
{
    private readonly string _destinationRoot;
    private readonly TransferLimits _limits;
    private readonly Action<string> _log;

    public TransferStore(string destinationRoot, TransferLimits? limits = null, Action<string>? log = null)
    {
        _destinationRoot = destinationRoot;
        _limits = limits ?? new TransferLimits();
        _log = log ?? (_ => { });
        Directory.CreateDirectory(_destinationRoot);
    }

    /// <summary>Staging lives under the destination so the final move never crosses volumes.</summary>
    private string StagingRoot => Path.Combine(_destinationRoot, ".incoming");

    public async Task<StoreResult> StoreAsync(
        Stream uploadBody, string transferLabel, CancellationToken ct = default)
    {
        Directory.CreateDirectory(StagingRoot);
        string staging = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        var stored = new List<StoredFile>();
        var rejected = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        try
        {
            var counting = new CountingStream(uploadBody);
            // What the body is encoded as is decided by its bytes, not by our own
            // advertised capabilities and not by a header the sender may omit.
            var archive = UploadEncodingDetector.OpenArchive(counting, out var encoding, out var dvzip);
            _log($"Upload-Kodierung erkannt: {encoding}");
            var cpio = new CpioReader(archive);

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var entry = cpio.ReadNext(out var content);
                if (entry is null) break;

                if (entry.IsArchiveRoot)
                {
                    // iOS puts a "." member at the head of its archives. It names the
                    // archive root, not a directory to create; treating it as a path
                    // would trip the traversal guard and fail an otherwise fine transfer.
                    CpioReader.Drain(content);
                    continue;
                }

                if (entry.IsSymlink)
                {
                    // A symlink in the archive could point anywhere on this machine.
                    rejected.Add($"{entry.Name}: Symlinks werden nicht angenommen");
                    CpioReader.Drain(content);
                    continue;
                }

                var safe = SafePath.Sanitize(entry.Name);
                if (safe is null)
                {
                    rejected.Add($"{entry.Name}: kein verwertbarer Dateiname");
                    CpioReader.Drain(content);
                    continue;
                }

                string target = Path.Combine(staging, safe.RelativePath);

                // Belt and braces: the string rules above should already guarantee this,
                // but the final check happens against the resolved path.
                if (!SafePath.IsInside(staging, target))
                {
                    rejected.Add($"{entry.Name}: Pfad liegt ausserhalb des Zielordners");
                    CpioReader.Drain(content);
                    continue;
                }

                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                if (stored.Count >= _limits.MaxFiles)
                    throw new InvalidDataException(
                        $"Transfer enthaelt mehr als {_limits.MaxFiles} Dateien.");

                var dir = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(dir);

                string fileName = SafePath.ResolveCollision(dir, Path.GetFileName(target), usedNames);
                target = Path.Combine(dir, fileName);

                long written = await WriteEntryAsync(content, target, totalBytes, ct);
                totalBytes += written;

                // An expansion check needs a meaningful denominator, so it only applies
                // once the sender has actually pushed a reasonable number of bytes.
                if (counting.BytesRead > 4096 && totalBytes / counting.BytesRead > _limits.MaxExpansionRatio)
                    throw new InvalidDataException(
                        $"Archiv expandiert staerker als das {_limits.MaxExpansionRatio}-fache der gesendeten Daten.");

                string relative = Path.GetRelativePath(staging, target);
                stored.Add(new StoredFile(relative, written, safe.WasRewritten, safe.Reason));
                _log($"gespeichert: {relative} ({written} Bytes)" +
                     (safe.WasRewritten ? $" [umbenannt: {safe.Reason}]" : ""));
            }

            if (dvzip is not null && dvzip.Blocks.Count > 0)
            {
                int zlib = dvzip.Blocks.Count(b => !b.Stored);
                int raw = dvzip.Blocks.Count(b => b.Stored);
                int flagged = dvzip.Blocks.Count(b => b.StoredFlagSet);
                _log($"DVZip: {dvzip.Blocks.Count} Bloecke ({zlib} zlib, {raw} stored, " +
                     $"davon {flagged} mit gesetztem Bit 31)");
            }

            if (stored.Count == 0)
                throw new InvalidDataException("Der Transfer enthielt keine speicherbare Datei.");

            string final = PublishAtomically(staging, transferLabel);
            _log($"Transfer abgeschlossen: {final} ({stored.Count} Dateien, {totalBytes} Bytes)");
            return new StoreResult(final, stored, totalBytes, rejected);
        }
        catch
        {
            // Nothing half-written may survive and look like a finished transfer.
            TryDelete(staging);
            throw;
        }
    }

    private async Task<long> WriteEntryAsync(Stream content, string target, long alreadyStored, CancellationToken ct)
    {
        long written = 0;
        var buffer = new byte[64 * 1024];

        await using var file = new FileStream(
            target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true);

        while (true)
        {
            int n = await content.ReadAsync(buffer, ct);
            if (n == 0) break;

            written += n;
            if (written > _limits.MaxSingleFileBytes)
                throw new InvalidDataException(
                    $"Einzeldatei ueberschreitet {_limits.MaxSingleFileBytes} Bytes.");
            if (alreadyStored + written > _limits.MaxTotalBytes)
                throw new InvalidDataException(
                    $"Transfer ueberschreitet insgesamt {_limits.MaxTotalBytes} Bytes.");

            await file.WriteAsync(buffer.AsMemory(0, n), ct);
        }

        await file.FlushAsync(ct);
        return written;
    }

    /// <summary>
    /// Moves the staging directory to its final name. A directory move on the same volume
    /// is atomic, so an observer never sees a partially filled transfer folder.
    /// </summary>
    private string PublishAtomically(string staging, string transferLabel)
    {
        var labelPath = SafePath.Sanitize(transferLabel)?.RelativePath ?? "Transfer";
        string baseName = $"{DateTime.Now:yyyy-MM-dd HH-mm-ss} {Path.GetFileName(labelPath)}".Trim();

        string final = Path.Combine(_destinationRoot, baseName);
        int n = 1;
        while (Directory.Exists(final))
        {
            final = Path.Combine(_destinationRoot, $"{baseName} ({n})");
            n++;
        }

        Directory.Move(staging, final);
        return final;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort: a leftover staging directory is not worth masking the real error */ }
    }

    /// <summary>Counts how many bytes the sender actually pushed, for the expansion check.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            BytesRead += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
