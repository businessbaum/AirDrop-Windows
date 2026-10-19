using System.Buffers.Binary;
using System.Text;

namespace Receiver.Mdns;

/// <summary>
/// Writes DNS wire format with RFC 1035 name compression.
/// Compression is not optional for us: an AirDrop service announcement with PTR + SRV +
/// TXT + AAAA repeats the instance name several times and must stay inside one datagram.
/// </summary>
internal sealed class DnsWriter
{
    private readonly List<byte> _buf = new(512);
    private readonly Dictionary<string, int> _nameOffsets = new(StringComparer.Ordinal);

    public void WriteByte(byte b) => _buf.Add(b);

    public void WriteUInt16(ushort v)
    {
        Span<byte> t = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(t, v);
        _buf.AddRange(t);
    }

    public void WriteUInt32(uint v)
    {
        Span<byte> t = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(t, v);
        _buf.AddRange(t);
    }

    public void WriteBytes(ReadOnlySpan<byte> b) => _buf.AddRange(b);

    public void WriteName(string name)
    {
        if (name is "." or "") { _buf.Add(0); return; }

        string rest = name.TrimEnd('.');
        while (rest.Length > 0)
        {
            if (_nameOffsets.TryGetValue(rest, out int off) && off < 0x3FFF)
            {
                WriteUInt16((ushort)(0xC000 | off));
                return;
            }

            // Only offsets inside the 14-bit pointer range are usable as targets.
            if (_buf.Count < 0x3FFF) _nameOffsets[rest] = _buf.Count;

            int dot = rest.IndexOf('.');
            string label = dot < 0 ? rest : rest[..dot];
            rest = dot < 0 ? "" : rest[(dot + 1)..];

            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63) throw new ArgumentException($"DNS label too long: {label}");
            _buf.Add((byte)bytes.Length);
            _buf.AddRange(bytes);
        }
        _buf.Add(0);
    }

    public void WriteQuestion(DnsQuestion q)
    {
        WriteName(q.Name);
        WriteUInt16((ushort)q.Type);
        WriteUInt16((ushort)((ushort)q.Class | (q.UnicastResponse ? 0x8000 : 0)));
    }

    public void WriteRecord(DnsRecord r)
    {
        WriteName(r.Name);
        WriteUInt16((ushort)r.Type);
        WriteUInt16((ushort)((ushort)r.Class | (r.CacheFlush ? 0x8000 : 0)));
        WriteUInt32(r.Ttl);

        int lenPos = _buf.Count;
        WriteUInt16(0); // RDLENGTH placeholder
        int rdStart = _buf.Count;

        switch (r.Type)
        {
            case DnsType.PTR:
                WriteName(r.Target ?? throw new ArgumentException("PTR needs Target"));
                break;
            case DnsType.SRV:
                WriteUInt16(r.Priority);
                WriteUInt16(r.Weight);
                WriteUInt16(r.Port);
                // RFC 2782 targets are conventionally written uncompressed, and Apple's
                // resolver is fine either way; we compress because it keeps us in one packet.
                WriteName(r.Target ?? throw new ArgumentException("SRV needs Target"));
                break;
            case DnsType.A:
            case DnsType.AAAA:
                WriteBytes((r.Address ?? throw new ArgumentException("A/AAAA needs Address")).GetAddressBytes());
                break;
            case DnsType.TXT:
                var strings = r.TxtStrings ?? [];
                if (strings.Count == 0)
                {
                    // An empty TXT record is a single zero-length string, never zero bytes.
                    _buf.Add(0);
                }
                else
                {
                    foreach (var s in strings)
                    {
                        var b = Encoding.UTF8.GetBytes(s);
                        if (b.Length > 255) throw new ArgumentException("TXT string too long");
                        _buf.Add((byte)b.Length);
                        _buf.AddRange(b);
                    }
                }
                break;
            default:
                if (r.RawRdata is not null) WriteBytes(r.RawRdata);
                break;
        }

        int rdLen = _buf.Count - rdStart;
        _buf[lenPos] = (byte)(rdLen >> 8);
        _buf[lenPos + 1] = (byte)(rdLen & 0xFF);
    }

    public byte[] ToArray() => _buf.ToArray();
}
