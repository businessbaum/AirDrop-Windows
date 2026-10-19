using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Receiver.Mdns;

/// <summary>
/// Reads DNS wire format including RFC 1035 name compression. Deliberately strict about
/// bounds: this parses packets from the local network, so a malformed message must throw
/// rather than loop or read out of range.
/// </summary>
internal ref struct DnsReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public DnsReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    private void Need(int n)
    {
        if (_pos + n > _data.Length) throw new FormatException("DNS message truncated.");
    }

    public byte ReadByte() { Need(1); return _data[_pos++]; }

    public ushort ReadUInt16()
    {
        Need(2);
        var v = BinaryPrimitives.ReadUInt16BigEndian(_data[_pos..]);
        _pos += 2;
        return v;
    }

    public uint ReadUInt32()
    {
        Need(4);
        var v = BinaryPrimitives.ReadUInt32BigEndian(_data[_pos..]);
        _pos += 4;
        return v;
    }

    public byte[] ReadBytes(int n)
    {
        Need(n);
        var b = _data.Slice(_pos, n).ToArray();
        _pos += n;
        return b;
    }

    public string ReadName()
    {
        var sb = new StringBuilder();
        int pos = _pos;
        int jumps = 0;
        bool jumped = false;

        while (true)
        {
            if (pos >= _data.Length) throw new FormatException("DNS name truncated.");
            byte len = _data[pos];

            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= _data.Length) throw new FormatException("DNS pointer truncated.");
                int ptr = ((len & 0x3F) << 8) | _data[pos + 1];
                if (!jumped) { _pos = pos + 2; jumped = true; }
                if (++jumps > 64) throw new FormatException("DNS name compression loop.");
                if (ptr >= _data.Length) throw new FormatException("DNS pointer out of range.");
                pos = ptr;
                continue;
            }

            if (len == 0)
            {
                if (!jumped) _pos = pos + 1;
                break;
            }

            pos++;
            if (pos + len > _data.Length) throw new FormatException("DNS label truncated.");
            // Labels are opaque bytes; mDNS service names are UTF-8 in practice.
            sb.Append(Encoding.UTF8.GetString(_data.Slice(pos, len)));
            sb.Append('.');
            pos += len;
        }

        return sb.Length == 0 ? "." : sb.ToString();
    }

    public DnsQuestion ReadQuestion()
    {
        string name = ReadName();
        var type = (DnsType)ReadUInt16();
        ushort cls = ReadUInt16();
        return new DnsQuestion
        {
            Name = name,
            Type = type,
            Class = (DnsClass)(cls & 0x7FFF),
            UnicastResponse = (cls & 0x8000) != 0,
        };
    }

    public DnsRecord ReadRecord()
    {
        string name = ReadName();
        var type = (DnsType)ReadUInt16();
        ushort cls = ReadUInt16();
        uint ttl = ReadUInt32();
        ushort rdlen = ReadUInt16();

        Need(rdlen);
        int rdStart = _pos;
        int rdEnd = _pos + rdlen;

        string? target = null;
        IPAddress? addr = null;
        ushort prio = 0, weight = 0, port = 0;
        List<string>? txt = null;
        byte[]? raw = null;

        switch (type)
        {
            case DnsType.PTR:
                target = ReadName();
                break;
            case DnsType.SRV:
                prio = ReadUInt16(); weight = ReadUInt16(); port = ReadUInt16();
                target = ReadName();
                break;
            case DnsType.A when rdlen == 4:
                addr = new IPAddress(ReadBytes(4));
                break;
            case DnsType.AAAA when rdlen == 16:
                addr = new IPAddress(ReadBytes(16));
                break;
            case DnsType.TXT:
                txt = [];
                while (_pos < rdEnd)
                {
                    int l = ReadByte();
                    if (_pos + l > rdEnd) break;
                    txt.Add(Encoding.UTF8.GetString(ReadBytes(l)));
                }
                break;
            default:
                raw = ReadBytes(rdlen);
                break;
        }

        // Always resume exactly after RDATA, whatever the branch above consumed.
        _pos = rdEnd;
        _ = rdStart;

        return new DnsRecord
        {
            Name = name,
            Type = type,
            Class = (DnsClass)(cls & 0x7FFF),
            CacheFlush = (cls & 0x8000) != 0,
            Ttl = ttl,
            Target = target,
            Address = addr,
            Priority = prio,
            Weight = weight,
            Port = port,
            TxtStrings = txt,
            RawRdata = raw,
        };
    }
}
