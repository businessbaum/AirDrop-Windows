namespace Receiver.Mdns;

public enum DnsType : ushort
{
    A = 1,
    PTR = 12,
    TXT = 16,
    AAAA = 28,
    SRV = 33,
    OPT = 41,
    NSEC = 47,
    ANY = 255,
}

public enum DnsClass : ushort
{
    IN = 1,
    ANY = 255,
}
