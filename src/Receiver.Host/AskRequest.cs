using Claunia.PropertyList;

namespace Receiver.Host;

/// <summary>
/// The parts of an /Ask body we actually use. Field names follow what opendrop-rs
/// observed; anything missing stays null rather than being invented.
/// </summary>
public sealed class AskRequest
{
    public string? TransferId { get; private init; }
    public string? SenderId { get; private init; }
    public string? SenderComputerName { get; private init; }
    public string? SenderModelName { get; private init; }
    public string? BundleId { get; private init; }
    public List<string> FileNames { get; } = [];
    public List<string> FileTypes { get; } = [];

    /// <summary>The whole decoded plist, so the UI can show fields we do not model yet.</summary>
    public NSDictionary? Raw { get; private init; }

    public static AskRequest Parse(byte[] body)
    {
        if (body.Length == 0) throw new FormatException("Empty /Ask body.");
        if (PropertyListParser.Parse(body) is not NSDictionary root)
            throw new FormatException("/Ask body is not a dictionary.");

        var req = new AskRequest
        {
            TransferId = Str(root, "TransferID"),
            SenderId = Str(root, "SenderID"),
            SenderComputerName = Str(root, "SenderComputerName"),
            SenderModelName = Str(root, "SenderModelName"),
            BundleId = Str(root, "BundleID"),
            Raw = root,
        };

        // "Files" is the list we care about; each entry is a dictionary describing one item.
        if (root.TryGetValue("Files", out var filesObj) && filesObj is NSArray files)
        {
            for (int i = 0; i < files.Count; i++)
            {
                if (files[i] is not NSDictionary f) continue;
                var name = Str(f, "FileName") ?? Str(f, "FileBundleName");
                var type = Str(f, "FileType");
                if (name is not null) req.FileNames.Add(name);
                if (type is not null) req.FileTypes.Add(type);
            }
        }

        return req;
    }

    private static string? Str(NSDictionary d, string key) =>
        d.TryGetValue(key, out var v) && v is NSString s ? s.Content : null;

    public string Describe()
    {
        var who = SenderComputerName ?? "Unbekannter Absender";
        var what = FileNames.Count switch
        {
            0 => "keine benannten Dateien",
            1 => FileNames[0],
            _ => $"{FileNames.Count} Dateien: {string.Join(", ", FileNames.Take(5))}"
                 + (FileNames.Count > 5 ? " ..." : ""),
        };
        return $"{who} ({SenderModelName ?? "unbekanntes Modell"}) bietet an: {what}";
    }
}
