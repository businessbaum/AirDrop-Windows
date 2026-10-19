using System.Net.NetworkInformation;

namespace Receiver.Diagnostics.Probes;

internal sealed record WlanAdapter(string Id, Guid Guid, string Description, string Mac, OperationalStatus Status);

internal static class AdapterProbe
{
    public static void Collect(CapabilityReport r)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            r.Adapters.Add(new Dictionary<string, object?>
            {
                ["id"] = nic.Id,
                ["name"] = nic.Name,
                ["description"] = nic.Description,
                ["type"] = nic.NetworkInterfaceType.ToString(),
                ["status"] = nic.OperationalStatus.ToString(),
                ["mac"] = FormatMac(nic.GetPhysicalAddress().GetAddressBytes()),
                ["speedBps"] = nic.Speed,
            });
        }
    }

    /// <summary>
    /// Picks the physical 802.11 adapter. Windows also exposes "Microsoft Wi-Fi Direct
    /// Virtual Adapter" instances of type Wireless80211; those are virtual miniports on
    /// top of the same radio and must not be mistaken for the hardware interface.
    /// </summary>
    public static WlanAdapter? FindPhysicalWlan(string? descriptionHint = null)
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .Where(n => !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            .Where(n => !n.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (descriptionHint is not null)
        {
            var hinted = candidates.FirstOrDefault(
                n => n.Description.Contains(descriptionHint, StringComparison.OrdinalIgnoreCase));
            if (hinted is not null) candidates = [hinted];
        }

        var nic = candidates.FirstOrDefault();
        if (nic is null) return null;

        // NetworkInterface.Id is the interface GUID in registry string form, e.g. "{326b...}".
        if (!Guid.TryParse(nic.Id, out var guid)) return null;

        return new WlanAdapter(nic.Id, guid, nic.Description,
            FormatMac(nic.GetPhysicalAddress().GetAddressBytes()), nic.OperationalStatus);
    }

    public static string FormatMac(byte[] b) =>
        b.Length == 0 ? "" : string.Join(":", b.Select(x => x.ToString("x2")));
}
