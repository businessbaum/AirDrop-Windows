using System.Text;

namespace Receiver.Protocol;

/// <summary>
/// Turns an archive entry name supplied by a remote device into a path that is safe to
/// create under a chosen destination directory.
///
/// Everything here treats the incoming name as hostile. The archive comes from a device
/// whose only claim to identity is a display name we cannot verify, so a name like
/// "../../Startup/evil.exe" or "CON" or "notes.txt:hidden" has to be neutralised before
/// it ever reaches a filesystem call.
/// </summary>
public static class SafePath
{
    /// <summary>Windows device names, which are reserved with or without an extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public sealed record Result(string RelativePath, bool WasRewritten, string? Reason);

    /// <summary>
    /// Maps an archive name to a relative path under the destination, or rejects it.
    /// Returns null when nothing safe can be salvaged.
    /// </summary>
    public static Result? Sanitize(string archiveName)
    {
        if (string.IsNullOrWhiteSpace(archiveName)) return null;

        bool rewritten = false;
        var reasons = new List<string>();

        // Normalise separators first so the checks below see one form.
        string work = archiveName.Replace('\\', '/');

        // An absolute path, a drive letter or a UNC path must never survive.
        if (work.StartsWith('/') || work.StartsWith("//"))
        {
            work = work.TrimStart('/');
            rewritten = true;
            reasons.Add("absoluter Pfad entfernt");
        }
        if (work.Length >= 2 && char.IsLetter(work[0]) && work[1] == ':')
        {
            work = work[2..].TrimStart('/');
            rewritten = true;
            reasons.Add("Laufwerksangabe entfernt");
        }

        var safeSegments = new List<string>();
        foreach (var rawSegment in work.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawSegment == ".") { rewritten = true; continue; }
            if (rawSegment == "..")
            {
                // Never resolved against the parent: dropped outright, so no sequence of
                // segments can climb out of the destination directory.
                rewritten = true;
                reasons.Add("Pfadwechsel nach oben entfernt");
                continue;
            }

            var cleaned = SanitizeSegment(rawSegment, ref rewritten, reasons);
            if (cleaned is null) continue;
            safeSegments.Add(cleaned);
        }

        if (safeSegments.Count == 0) return null;

        return new Result(
            Path.Combine([.. safeSegments]),
            rewritten,
            reasons.Count > 0 ? string.Join("; ", reasons.Distinct()) : null);
    }

    private static string? SanitizeSegment(string segment, ref bool rewritten, List<string> reasons)
    {
        var sb = new StringBuilder(segment.Length);
        foreach (char c in segment)
        {
            // ':' would open an NTFS alternate data stream; the rest are simply invalid
            // in a Windows path, and control characters have no business in a filename.
            if (c == ':' || c is '<' or '>' or '"' or '|' or '?' or '*' || char.IsControl(c))
            {
                sb.Append('_');
                rewritten = true;
                if (c == ':') reasons.Add("NTFS-Datenstrom-Trenner ersetzt");
                else reasons.Add("unzulaessiges Zeichen ersetzt");
                continue;
            }
            sb.Append(c);
        }

        // Windows silently strips trailing dots and spaces, which would let
        // "evil.exe." and "evil.exe" collide. Strip them ourselves, visibly.
        var cleaned = sb.ToString().TrimEnd(' ', '.');
        if (cleaned.Length != sb.Length)
        {
            rewritten = true;
            reasons.Add("Punkte/Leerzeichen am Ende entfernt");
        }
        if (cleaned.Length == 0) return null;

        // A reserved device name is reserved with any extension too ("CON.txt").
        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (ReservedNames.Contains(stem))
        {
            cleaned = "_" + cleaned;
            rewritten = true;
            reasons.Add("reservierter Windows-Name entschaerft");
        }

        return cleaned;
    }

    /// <summary>
    /// Final guard before writing: confirms the resolved path really is inside
    /// <paramref name="destinationRoot"/>. Catches anything the string rules missed,
    /// including symlink and junction tricks in already-created directories.
    /// </summary>
    public static bool IsInside(string destinationRoot, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        var full = Path.GetFullPath(candidatePath);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picks a name that does not collide, comparing case-insensitively because NTFS
    /// treats "Foto.JPG" and "foto.jpg" as the same file while the archive may not.
    /// </summary>
    public static string ResolveCollision(string directory, string fileName, ISet<string> alreadyUsed)
    {
        string candidate = fileName;
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        int n = 1;

        while (alreadyUsed.Contains(candidate.ToLowerInvariant())
               || File.Exists(Path.Combine(directory, candidate))
               || Directory.Exists(Path.Combine(directory, candidate)))
        {
            candidate = $"{stem} ({n}){ext}";
            n++;
            if (n > 10_000) throw new IOException($"Kein freier Name fuer '{fileName}'.");
        }

        alreadyUsed.Add(candidate.ToLowerInvariant());
        return candidate;
    }
}
