using System.Net;
using System.Net.NetworkInformation;

namespace Receiver.Mdns;

/// <summary>
/// Answers mDNS queries for the services we publish, and announces them unsolicited.
///
/// Windows already runs its own mDNS responder for the machine name. This one is
/// deliberately narrow: it only ever answers for names it was told to publish, so the two
/// do not fight over the same records.
/// </summary>
public sealed class MdnsResponder : IDisposable
{
    private readonly MdnsSocket _socket = new();
    private readonly List<ServiceAdvertisement> _services = [];
    private readonly object _gate = new();

    /// <summary>
    /// Diagnostic only. Suppresses the SRV/TXT/A records normally sent alongside a PTR
    /// answer, which forces an accepting sender to resolve the instance explicitly and
    /// so makes acceptance observable.
    /// </summary>
    public bool SuppressAdditionals { get; set; }
    private Timer? _announceTimer;
    private Timer? _keepAliveTimer;

    /// <summary>
    /// How often the service is re-announced while the responder runs.
    ///
    /// This is robustness and observability, not a fix for a defect. Announcing at
    /// startup and then answering queries is correct: measured against Apple's own
    /// resolver, a startup-only responder with a 4500 s TTL stayed listed and fully
    /// resolvable for hours. It simply sent nothing in between, because a peer holding
    /// the record suppresses it as a known answer (RFC 6762 7.1) while more than half
    /// the TTL remains -- so a short packet capture shows an apparently absent service
    /// that is in fact healthy. That cost us a wrong diagnosis once.
    ///
    /// Re-announcing keeps us visible to a device that starts listening after we did,
    /// and keeps captures interpretable. It is a deliberate divergence from Apple, whose
    /// responder does not do this; set the interval to zero to match Apple exactly.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    public event Action<string>? Log;

    /// <summary>Raised for every query we answered, with the requester and question.</summary>
    public event Action<IPEndPoint, DnsQuestion>? Answered;

    public void Add(ServiceAdvertisement service)
    {
        lock (_gate) _services.Add(service);
    }

    public void Start(IReadOnlyList<NetworkInterface> interfaces)
    {
        _socket.Diagnostic += d => Log?.Invoke(d);
        _socket.PacketReceived += OnPacket;
        _socket.Open(interfaces);
        _socket.StartReceiving();

        // RFC 6762 section 8.3: announce the records a few times, one second apart, so a
        // sender whose browse started before us still learns about the service.
        int sent = 0;
        _announceTimer = new Timer(_ =>
        {
            Announce();
            if (Interlocked.Increment(ref sent) >= 3)
                _announceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1));

        if (KeepAliveInterval > TimeSpan.Zero)
        {
            _keepAliveTimer = new Timer(
                _ => { try { Announce(quiet: true); } catch { } },
                null, KeepAliveInterval, KeepAliveInterval);
        }
    }

    /// <param name="quiet">Keep the periodic refresh out of the log, which it would flood.</param>
    public void Announce(bool quiet = false)
    {
        lock (_gate)
        {
            foreach (var svc in _services)
            {
                var msg = new DnsMessage { IsResponse = true, AuthoritativeAnswer = true };
                msg.Answers.AddRange(svc.AllRecords());
                _socket.SendMulticast(msg.ToBytes());
            }
        }
        if (!quiet) Log?.Invoke("announced services");
    }

    /// <summary>
    /// Sends TTL-0 records so senders drop us immediately instead of waiting out the TTL.
    /// </summary>
    public void Goodbye()
    {
        lock (_gate)
        {
            foreach (var svc in _services)
            {
                var msg = new DnsMessage { IsResponse = true, AuthoritativeAnswer = true };
                msg.Answers.AddRange(svc.AllRecords(ttlOverride: 0));
                _socket.SendMulticast(msg.ToBytes());
            }
        }
        Log?.Invoke("sent goodbye");
    }

    private void OnPacket(MdnsPacket pkt)
    {
        DnsMessage msg;
        try { msg = DnsMessage.Parse(pkt.Data); }
        catch { return; }
        if (msg.IsResponse || msg.Questions.Count == 0) return;

        var answers = new List<DnsRecord>();
        var additionals = new List<DnsRecord>();
        bool unicast = false;
        var matched = new List<DnsQuestion>();

        lock (_gate)
        {
            foreach (var q in msg.Questions)
            {
                foreach (var svc in _services)
                {
                    var a = svc.Answer(q, out var add);
                    if (a.Count == 0) continue;

                    // RFC 6762 section 7.1: suppress an answer the requester already listed
                    // as known, so repeated browses do not generate redundant traffic.
                    a.RemoveAll(rec => msg.Answers.Any(known =>
                        string.Equals(known.Name.TrimEnd('.'), rec.Name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
                        && known.Type == rec.Type
                        && known.Ttl > rec.Ttl / 2));
                    if (a.Count == 0) continue;

                    answers.AddRange(a);
                    additionals.AddRange(add);
                    matched.Add(q);
                    if (q.UnicastResponse) unicast = true;
                }
            }
        }

        if (answers.Count == 0) return;

        var response = new DnsMessage { IsResponse = true, AuthoritativeAnswer = true };
        response.Answers.AddRange(answers);
        if (SuppressAdditionals) additionals.Clear();
        foreach (var add in additionals)
            if (!response.Answers.Any(x => x.Name == add.Name && x.Type == add.Type))
                response.Additionals.Add(add);

        var bytes = response.ToBytes();
        if (unicast)
        {
            // Answer the way the requester asked...
            _socket.SendUnicast(bytes, pkt.Source, pkt.ReceivedOn);
            // ...and also multicast it. RFC 6762 section 5.4 permits this, it lets other
            // devices populate their caches, and it makes our own answers visible to the
            // capture tool -- a unicast reply is otherwise invisible to everyone but the
            // requester, which makes a wrong answer impossible to diagnose.
            _socket.SendMulticast(bytes);
        }
        else
        {
            _socket.SendMulticast(bytes);
        }

        foreach (var q in matched) Answered?.Invoke(pkt.Source, q);
    }

    public void Dispose()
    {
        _keepAliveTimer?.Dispose();
        _announceTimer?.Dispose();
        try { Goodbye(); } catch { }
        Thread.Sleep(150);
        _socket.Dispose();
    }
}
