namespace Receiver.Diagnostics.Interop;

/// <summary>
/// Native 802.11 OID constants, taken verbatim from the installed Windows WDK header
/// C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\shared\windot11.h.
/// Nothing here is guessed.
/// </summary>
internal static class Dot11Oids
{
    public const uint OID_DOT11_NDIS_START = 0x0D010300;

    // windot11.h: (OID_DOT11_NDIS_START + 7) / (+ 8)
    public const uint OID_DOT11_OPERATION_MODE_CAPABILITY = OID_DOT11_NDIS_START + 7;
    public const uint OID_DOT11_CURRENT_OPERATION_MODE    = OID_DOT11_NDIS_START + 8;
    public const uint OID_DOT11_NIC_POWER_STATE           = OID_DOT11_NDIS_START + 17;

    // windot11.h DOT11_OPERATION_MODE_* bit values.
    public const uint MODE_STATION            = 0x00000001;
    public const uint MODE_AP                 = 0x00000002;
    public const uint MODE_EXTENSIBLE_STATION = 0x00000004;
    public const uint MODE_EXTENSIBLE_AP      = 0x00000008;
    public const uint MODE_WFD_DEVICE         = 0x00000010;
    public const uint MODE_WFD_GROUP_OWNER    = 0x00000020;
    public const uint MODE_WFD_CLIENT         = 0x00000040;
    public const uint MODE_MANUFACTURING      = 0x40000000;
    public const uint MODE_NETWORK_MONITOR    = 0x80000000;

    public static string Describe(uint mask)
    {
        if (mask == 0) return "none";
        var parts = new List<string>();
        if ((mask & MODE_STATION) != 0) parts.Add("station");
        if ((mask & MODE_AP) != 0) parts.Add("ap");
        if ((mask & MODE_EXTENSIBLE_STATION) != 0) parts.Add("ext_station(managed)");
        if ((mask & MODE_EXTENSIBLE_AP) != 0) parts.Add("ext_ap(master)");
        if ((mask & MODE_WFD_DEVICE) != 0) parts.Add("wfd_device");
        if ((mask & MODE_WFD_GROUP_OWNER) != 0) parts.Add("wfd_owner");
        if ((mask & MODE_WFD_CLIENT) != 0) parts.Add("wfd_client");
        if ((mask & MODE_MANUFACTURING) != 0) parts.Add("manufacturing");
        if ((mask & MODE_NETWORK_MONITOR) != 0) parts.Add("network_monitor");

        uint known = MODE_STATION | MODE_AP | MODE_EXTENSIBLE_STATION | MODE_EXTENSIBLE_AP
                   | MODE_WFD_DEVICE | MODE_WFD_GROUP_OWNER | MODE_WFD_CLIENT
                   | MODE_MANUFACTURING | MODE_NETWORK_MONITOR;
        uint rest = mask & ~known;
        if (rest != 0) parts.Add($"unknown(0x{rest:X8})");
        return string.Join(",", parts);
    }
}
