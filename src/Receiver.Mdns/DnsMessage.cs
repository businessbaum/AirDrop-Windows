using System.Net;
using System.Text;

namespace Receiver.Mdns;

public sealed class DnsQuestion
{
    public required string Name { get; init; }
    public required DnsType Type { get; init; }
    public DnsClass Class { get; init; } = DnsClass.IN;
    /// <summary>mDNS unicast-response bit (top bit of QCLASS), RFC 6762 section 5.4.</summary>
    public bool UnicastResponse { get; init; }

    public override string ToString() => $"{Name} {Type}{(UnicastResponse ? " (QU)" : "")}";
}

public sealed class DnsRecord
{
    public required string Name { get; init; }
    public required DnsType Type { get; init; }
    public DnsClass Class { get; init; } = DnsClass.IN;
    /// <summary>mDNS cache-flush bit (top bit of CLASS), RFC 6762 section 10.2.</summary>
    public bool CacheFlush { get; init; }
    public uint Ttl { get; init; } = 120;

    // Exactly one of these is set, depending on Type.
    public string? Target { get; init; }            // PTR, SRV target
    public IPAddress? Address { get; init; }        // A, AAAA
    public ushort Priority { get; init; }           // SRV
    public ushort Weight { get; init; }             // SRV
    public ushort Port { get; init; }               // SRV
    public List<string>? TxtStrings { get; init; }  // TXT
    public byte[]? RawRdata { get; init; }          // anything we do not model

    public override string ToString() => Type switch
    {
        DnsType.PTR => $"{Name} PTR {Target}",
        DnsType.SRV => $"{Name} SRV {Priority} {Weight} {Port} {Target}",
        DnsType.A or DnsType.AAAA => $"{Name} {Type} {Address}",
        DnsType.TXT => $"{Name} TXT [{string.Join("; ", TxtStrings ?? [])}]",
        _ => $"{Name} {Type} ({RawRdata?.Length ?? 0} bytes)",
    };
}

public sealed class DnsMessage
{
    public ushort Id { get; set; }
    public bool IsResponse { get; set; }
    public bool AuthoritativeAnswer { get; set; }
    public List<DnsQuestion> Questions { get; } = [];
    public List<DnsRecord> Answers { get; } = [];
    public List<DnsRecord> Authorities { get; } = [];
    public List<DnsRecord> Additionals { get; } = [];

    public static DnsMessage Parse(ReadOnlySpan<byte> data)
    {
        var r = new DnsReader(data);
        var m = new DnsMessage
        {
            Id = r.ReadUInt16(),
        };
        ushort flags = r.ReadUInt16();
        m.IsResponse = (flags & 0x8000) != 0;
        m.AuthoritativeAnswer = (flags & 0x0400) != 0;

        ushort qd = r.ReadUInt16(), an = r.ReadUInt16(), ns = r.ReadUInt16(), ar = r.ReadUInt16();

        for (int i = 0; i < qd; i++) m.Questions.Add(r.ReadQuestion());
        for (int i = 0; i < an; i++) m.Answers.Add(r.ReadRecord());
        for (int i = 0; i < ns; i++) m.Authorities.Add(r.ReadRecord());
        for (int i = 0; i < ar; i++) m.Additionals.Add(r.ReadRecord());
        return m;
    }

    public byte[] ToBytes()
    {
        var w = new DnsWriter();
        w.WriteUInt16(Id);
        ushort flags = 0;
        if (IsResponse) flags |= 0x8000;
        if (AuthoritativeAnswer) flags |= 0x0400;
        w.WriteUInt16(flags);
        w.WriteUInt16((ushort)Questions.Count);
        w.WriteUInt16((ushort)Answers.Count);
        w.WriteUInt16((ushort)Authorities.Count);
        w.WriteUInt16((ushort)Additionals.Count);

        foreach (var q in Questions) w.WriteQuestion(q);
        foreach (var a in Answers) w.WriteRecord(a);
        foreach (var a in Authorities) w.WriteRecord(a);
        foreach (var a in Additionals) w.WriteRecord(a);
        return w.ToArray();
    }

    public string Summarize()
    {
        var sb = new StringBuilder();
        sb.Append(IsResponse ? "RESPONSE" : "QUERY");
        if (Questions.Count > 0)
            sb.Append("  Q: ").Append(string.Join(" | ", Questions));
        if (Answers.Count > 0)
            sb.Append("  A: ").Append(string.Join(" | ", Answers));
        if (Additionals.Count > 0)
            sb.Append("  ADD: ").Append(string.Join(" | ", Additionals));
        return sb.ToString();
    }
}
