using System.Collections.Concurrent;

namespace Receiver.Host;

public sealed class TransferTicket
{
    public required AskRequest Request { get; init; }
    public required string? PeerAddress { get; init; }
    public required DateTimeOffset AcceptedAt { get; init; }

    /// <summary>Set once an upload has claimed this ticket, so it cannot be claimed twice.</summary>
    public int Claimed;

    /// <summary>Where the files ended up, once the transfer finished.</summary>
    public string? CompletedDirectory { get; set; }
}

/// <summary>
/// Binds an /Upload to an acceptance the user actually gave.
///
/// The hard part of AirDrop's shape: acceptance is signalled by HTTP 200 on /Ask, and the
/// upload then arrives on a *separate* connection. There is no token the unmodified Apple
/// sender would hand back, so the binding has to be made from what the protocol really
/// carries -- here the peer address plus a short validity window.
///
/// This is deliberately conservative and deliberately not clever: it never falls back to
/// matching on the sender's display name, which is not a verified identity. Where the
/// binding cannot be established, the upload is refused. See ARCHITEKTUR.md 7.3, which
/// treats a missing binding as an open protocol question rather than something to paper
/// over.
/// </summary>
public sealed class AcceptedTransfers
{
    private readonly ConcurrentDictionary<TransferTicket, byte> _open = new();
    private readonly TimeSpan _validity;
    private readonly Action<string> _log;

    public AcceptedTransfers(TimeSpan? validity = null, Action<string>? log = null)
    {
        _validity = validity ?? TimeSpan.FromMinutes(5);
        _log = log ?? (_ => { });
    }

    public TransferTicket Accept(AskRequest request, string? peerAddress)
    {
        Expire();
        var ticket = new TransferTicket
        {
            Request = request,
            PeerAddress = Normalize(peerAddress),
            AcceptedAt = DateTimeOffset.UtcNow,
        };
        _open[ticket] = 0;
        return ticket;
    }

    /// <summary>
    /// Takes the one acceptance this peer may use, or null. Claiming is atomic, so two
    /// uploads racing for the same acceptance cannot both win.
    /// </summary>
    public TransferTicket? Claim(string? peerAddress)
    {
        Expire();
        var peer = Normalize(peerAddress);

        foreach (var ticket in _open.Keys.OrderBy(t => t.AcceptedAt))
        {
            if (ticket.PeerAddress is not null && peer is not null && ticket.PeerAddress != peer)
                continue;
            if (Interlocked.CompareExchange(ref ticket.Claimed, 1, 0) != 0)
                continue;
            return ticket;
        }
        return null;
    }

    /// <summary>Returns a claimed ticket to the pool after a failed upload, so a retry can use it.</summary>
    public void Release(TransferTicket ticket)
    {
        if (ticket.CompletedDirectory is not null) return; // already finished; do not reopen
        Interlocked.Exchange(ref ticket.Claimed, 0);
    }

    /// <summary>
    /// Marks the transfer finished. The ticket is kept, not deleted: if the sender lost
    /// our response and retries, we must recognise the same completed transfer instead of
    /// writing the files a second time.
    /// </summary>
    public void MarkCompleted(TransferTicket ticket, string directory)
    {
        ticket.CompletedDirectory = directory;
        _open.TryRemove(ticket, out _);
    }

    private void Expire()
    {
        var cutoff = DateTimeOffset.UtcNow - _validity;
        foreach (var ticket in _open.Keys)
        {
            if (ticket.AcceptedAt >= cutoff) continue;
            if (_open.TryRemove(ticket, out _))
                _log($"Annahme abgelaufen: {ticket.Request.TransferId ?? "(ohne ID)"}");
        }
    }

    /// <summary>
    /// Kestrel reports an IPv4 peer over a dual-stack socket as ::ffff:a.b.c.d, while the
    /// same peer can appear plain elsewhere. Comparing the raw strings would silently
    /// never match.
    /// </summary>
    private static string? Normalize(string? address)
    {
        if (string.IsNullOrEmpty(address)) return null;
        const string v4MappedPrefix = "::ffff:";
        if (address.StartsWith(v4MappedPrefix, StringComparison.OrdinalIgnoreCase))
            address = address[v4MappedPrefix.Length..];
        int zone = address.IndexOf('%');
        if (zone >= 0) address = address[..zone];
        return address.ToLowerInvariant();
    }
}
