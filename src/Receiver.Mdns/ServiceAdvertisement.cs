using System.Net;

namespace Receiver.Mdns;

/// <summary>
/// One DNS-SD service instance we publish, plus the host records its SRV target needs.
/// </summary>
public sealed class ServiceAdvertisement
{
    /// <summary>e.g. "_airdrop._tcp.local."</summary>
    public required string ServiceType { get; init; }

    /// <summary>The instance label only, e.g. "9a3f1c2b7d04" — not the full name.</summary>
    public required string InstanceLabel { get; init; }

    /// <summary>e.g. "windows-pc.local."</summary>
    public required string HostName { get; init; }

    public required ushort Port { get; init; }

    /// <summary>TXT strings in "key=value" form. AirDrop uses exactly one: "flags=...".</summary>
    public List<string> TxtStrings { get; init; } = [];

    public List<IPAddress> Addresses { get; init; } = [];

    public uint Ttl { get; init; } = 120;

    public string InstanceName => $"{InstanceLabel}.{ServiceType}";

    /// <summary>
    /// The records that answer a query for <paramref name="question"/>, or an empty list.
    /// </summary>
    public List<DnsRecord> Answer(DnsQuestion question, out List<DnsRecord> additionals)
    {
        var answers = new List<DnsRecord>();
        additionals = [];

        string qname = Normalize(question.Name);
        string type = Normalize(ServiceType);
        string instance = Normalize(InstanceName);
        string host = Normalize(HostName);

        bool wantsPtr = question.Type is DnsType.PTR or DnsType.ANY;
        bool wantsSrv = question.Type is DnsType.SRV or DnsType.ANY;
        bool wantsTxt = question.Type is DnsType.TXT or DnsType.ANY;
        bool wantsAddr = question.Type is DnsType.A or DnsType.AAAA or DnsType.ANY;

        // Browse: "_airdrop._tcp.local. PTR"
        if (wantsPtr && qname == type)
        {
            answers.Add(PtrRecord());
            additionals.Add(SrvRecord());
            additionals.Add(TxtRecord());
            additionals.AddRange(AddressRecords());
        }
        // Service type enumeration: "_services._dns-sd._udp.local. PTR"
        else if (wantsPtr && qname == "_services._dns-sd._udp.local")
        {
            answers.Add(new DnsRecord
            {
                Name = "_services._dns-sd._udp.local.",
                Type = DnsType.PTR,
                Ttl = Ttl,
                Target = ServiceType,
            });
        }
        // Resolve: "<instance>._airdrop._tcp.local. SRV / TXT"
        else if (qname == instance)
        {
            if (wantsSrv)
            {
                answers.Add(SrvRecord());
                additionals.AddRange(AddressRecords());
            }
            if (wantsTxt) answers.Add(TxtRecord());
        }
        // Host address lookup.
        else if (wantsAddr && qname == host)
        {
            foreach (var r in AddressRecords())
            {
                bool matches = question.Type == DnsType.ANY
                    || (question.Type == DnsType.A && r.Type == DnsType.A)
                    || (question.Type == DnsType.AAAA && r.Type == DnsType.AAAA);
                if (matches) answers.Add(r);
            }

            // Asked for an address family we do not have: say so, rather than leaving
            // the asker to retry forever.
            if (answers.Count == 0 && AddressTypes().Length > 0)
                answers.Add(Nsec(HostName, Ttl, AddressTypes()));
        }

        // Nothing may appear in both sections.
        additionals.RemoveAll(a => answers.Any(x => x.Name == a.Name && x.Type == a.Type));
        return answers;
    }

    /// <summary>
    /// Builds an mDNS NSEC record asserting that <paramref name="name"/> has exactly
    /// <paramref name="presentTypes"/> and nothing else (RFC 6762 6.1).
    ///
    /// Staying silent when asked for a type we do not have is not harmless: the asker
    /// cannot tell "absent" from "lost", so it keeps retrying. That was observed here as
    /// the router re-asking for AAAA every two minutes, because we publish only an A
    /// record and answered its question with nothing at all.
    /// </summary>
    private static DnsRecord Nsec(string name, uint ttl, params DnsType[] presentTypes)
    {
        // Type bit map, window block 0 only: types 1..255 all live there. Bit index for
        // type T is byte T/8, bit 7-(T%8), most significant bit first.
        int highest = presentTypes.Max(t => (int)t);
        int bitmapLength = highest / 8 + 1;
        var bitmap = new byte[bitmapLength];
        foreach (var t in presentTypes)
        {
            int type = (int)t;
            bitmap[type / 8] |= (byte)(0x80 >> (type % 8));
        }

        // RDATA = next domain name + window block + bitmap length + bitmap. In mDNS the
        // "next" name is the record's own name.
        var w = new DnsWriter();
        w.WriteName(name);
        w.WriteByte(0);                      // window block 0
        w.WriteByte((byte)bitmapLength);
        w.WriteBytes(bitmap);

        return new DnsRecord
        {
            Name = name,
            Type = DnsType.NSEC,
            Ttl = ttl,
            CacheFlush = true,
            RawRdata = w.ToArray(),
        };
    }

    /// <summary>All records, for unsolicited announcements and for goodbye packets.</summary>
    public List<DnsRecord> AllRecords(uint? ttlOverride = null)
    {
        var list = new List<DnsRecord> { PtrRecord(ttlOverride), SrvRecord(ttlOverride), TxtRecord(ttlOverride) };
        list.AddRange(AddressRecords(ttlOverride));

        // A goodbye packet withdraws records; adding NSEC there would assert their
        // continued existence, so it only rides along on a normal announcement.
        // Real AirDrop announcements were measured to carry NSEC for both names.
        if (ttlOverride is not 0)
        {
            var addressTypes = AddressTypes();
            if (addressTypes.Length > 0) list.Add(Nsec(HostName, Ttl, addressTypes));
            list.Add(Nsec(InstanceName, Ttl, DnsType.TXT, DnsType.SRV));
        }
        return list;
    }

    private DnsRecord PtrRecord(uint? ttl = null) => new()
    {
        Name = ServiceType,
        Type = DnsType.PTR,
        Ttl = ttl ?? Ttl,
        Target = InstanceName,
        // RFC 6762: shared records like PTR never set the cache-flush bit.
        CacheFlush = false,
    };

    private DnsRecord SrvRecord(uint? ttl = null) => new()
    {
        Name = InstanceName,
        Type = DnsType.SRV,
        Ttl = ttl ?? Ttl,
        Priority = 0,
        Weight = 0,
        Port = Port,
        Target = HostName,
        CacheFlush = true,
    };

    private DnsRecord TxtRecord(uint? ttl = null) => new()
    {
        Name = InstanceName,
        Type = DnsType.TXT,
        Ttl = ttl ?? Ttl,
        TxtStrings = TxtStrings,
        CacheFlush = true,
    };

    /// <summary>Which address record types this advertisement actually publishes.</summary>
    private DnsType[] AddressTypes() =>
        Addresses
            .Select(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? DnsType.AAAA : DnsType.A)
            .Distinct()
            .ToArray();

    private List<DnsRecord> AddressRecords(uint? ttl = null) =>
        Addresses.Select(a => new DnsRecord
        {
            Name = HostName,
            Type = a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? DnsType.AAAA : DnsType.A,
            Ttl = ttl ?? Ttl,
            Address = a,
            CacheFlush = true,
        }).ToList();

    private static string Normalize(string name) => name.TrimEnd('.').ToLowerInvariant();
}
