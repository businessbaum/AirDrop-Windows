using System.ServiceProcess;
using Microsoft.Win32;

namespace Receiver.Diagnostics.Probes;

internal static class NpcapProbe
{
    private const string ParamsKey = @"SYSTEM\CurrentControlSet\Services\npcap\Parameters";

    public static void Collect(CapabilityReport r)
    {
        r.Npcap["installDir"] = @"C:\Windows\System32\Npcap";
        var wpcap = @"C:\Windows\System32\Npcap\wpcap.dll";
        r.Npcap["wpcapPresent"] = File.Exists(wpcap);
        if (File.Exists(wpcap))
            r.Npcap["wpcapVersion"] = System.Diagnostics.FileVersionInfo.GetVersionInfo(wpcap).ProductVersion;

        try
        {
            using var sc = new ServiceController("npcap");
            r.Npcap["serviceStatus"] = sc.Status.ToString();
        }
        catch (Exception ex)
        {
            r.Npcap["serviceStatus"] = "query failed: " + ex.Message;
        }

        using var key = Registry.LocalMachine.OpenSubKey(ParamsKey);
        if (key is null)
        {
            r.Npcap["parameters"] = null;
        }
        else
        {
            var p = new Dictionary<string, object?>();
            foreach (var name in key.GetValueNames()) p[name] = key.GetValue(name);
            r.Npcap["parameters"] = p;
        }
    }

    /// <summary>
    /// Npcap only attempts native 802.11 / monitor-mode handling when it was installed with
    /// the "Support raw 802.11 traffic" option, which persists as Dot11Support=1.
    /// This reads that flag; it never writes it.
    /// </summary>
    public static int? Dot11Support()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ParamsKey);
        return key?.GetValue("Dot11Support") as int?;
    }
}
