using Receiver.Diagnostics.Interop;

namespace Receiver.Diagnostics.Probes;

/// <summary>
/// Asks the WLAN miniport itself which 802.11 operation modes it can enter.
///
/// This is the decisive question for the whole radio path: monitor mode (NetMon) is the
/// only documented Windows route to raw 802.11 receive, and AWDL needs raw receive of
/// action frames. If the miniport does not offer NETWORK_MONITOR, no user-mode library
/// and no NDIS filter can add it (see ARCHITEKTUR.md 5.2).
/// </summary>
internal static class Dot11Probe
{
    public static void Collect(CapabilityReport r, WlanAdapter wlan, string evidenceDir)
    {
        var rawRx = r.Cap("rawRx");
        var rawTxAction = r.Cap("rawTxAction");
        var rawTxData = r.Cap("rawTxData");
        var channelControl = r.Cap("channelControl");
        var hardwareAck = r.Cap("hardwareAck");
        var timestampSource = r.Cap("timestampSource");
        var macAddressing = r.Cap("macAddressing");

        if (!PacketDll.TryInitialize(out var initError))
        {
            rawRx.Set(CapState.Unknown, initError ?? "Npcap not available");
            return;
        }

        r.Npcap["packetDllVersion"] = SafeVersion();

        var device = PacketDll.DeviceName(wlan.Guid);
        r.WlanDriver["npcapDeviceName"] = device;

        using var adapter = PacketDll.Open(device);
        if (adapter is null)
        {
            rawRx.Set(CapState.Unknown, $"PacketOpenAdapter failed for {device}");
            return;
        }

        // --- OID_DOT11_OPERATION_MODE_CAPABILITY -------------------------------------
        // DOT11_OPERATION_MODE_CAPABILITY { ULONG uReserved; ULONG uMajorVersion;
        //   ULONG uMinorVersion; ULONG uNumOfTXBuffers; ULONG uNumOfRXBuffers;
        //   ULONG uOpModeCapability; }  -- 24 bytes, windot11.h
        var capBytes = PacketDll.QueryOid(adapter, Dot11Oids.OID_DOT11_OPERATION_MODE_CAPABILITY, 24, out var capErr);
        var log = new List<string>
        {
            $"# Dot11 OID probe, {DateTimeOffset.UtcNow:O}",
            $"# adapter: {wlan.Description}  guid={wlan.Guid:B}  mac={wlan.Mac}",
            $"# npcap device: {device}",
            "",
        };

        if (capBytes is null || capBytes.Length < 24)
        {
            log.Add($"OID_DOT11_OPERATION_MODE_CAPABILITY (0x{Dot11Oids.OID_DOT11_OPERATION_MODE_CAPABILITY:X8}): FAILED -> {capErr ?? "short response"}");
            rawRx.Set(CapState.Unknown, $"OID query failed: {capErr ?? "short response"}");
        }
        else
        {
            uint opModeCap = BitConverter.ToUInt32(capBytes, 20);
            uint txBuffers = BitConverter.ToUInt32(capBytes, 12);
            uint rxBuffers = BitConverter.ToUInt32(capBytes, 16);

            r.WlanDriver["dot11OpModeCapabilityMask"] = $"0x{opModeCap:X8}";
            r.WlanDriver["dot11OpModeCapabilityDecoded"] = Dot11Oids.Describe(opModeCap);
            r.WlanDriver["dot11NumTxBuffers"] = txBuffers;
            r.WlanDriver["dot11NumRxBuffers"] = rxBuffers;

            log.Add($"OID_DOT11_OPERATION_MODE_CAPABILITY (0x{Dot11Oids.OID_DOT11_OPERATION_MODE_CAPABILITY:X8}):");
            log.Add($"  raw bytes        : {Convert.ToHexString(capBytes)}");
            log.Add($"  uNumOfTXBuffers  : {txBuffers}");
            log.Add($"  uNumOfRXBuffers  : {rxBuffers}");
            log.Add($"  uOpModeCapability: 0x{opModeCap:X8} = {Dot11Oids.Describe(opModeCap)}");

            bool monitor = (opModeCap & Dot11Oids.MODE_NETWORK_MONITOR) != 0;
            if (monitor)
            {
                rawRx.Set(CapState.Reported,
                    "Miniport advertises DOT11_OPERATION_MODE_NETWORK_MONITOR. Not yet exercised.");
                rawRx.Limitations.Add("Advertised mode bit is not proof that frames actually arrive.");
            }
            else
            {
                rawRx.Set(CapState.Unsupported,
                    $"Miniport operation-mode capability 0x{opModeCap:X8} does not include " +
                    $"DOT11_OPERATION_MODE_NETWORK_MONITOR (0x80000000). Monitor mode cannot be entered.");
            }

            // Raw TX and channel control are only reachable through monitor mode on Windows.
            // Without it there is no documented user-mode path at all, so they inherit the verdict.
            if (!monitor)
            {
                rawTxAction.Set(CapState.Unsupported,
                    "No monitor mode on this miniport; Windows exposes no other documented user-mode raw 802.11 TX path.");
                rawTxData.Set(CapState.Unsupported, "Same as rawTxAction.");
                channelControl.Set(CapState.Unsupported,
                    "WlanHelper/Npcap channel control requires monitor mode (OID_DOT11_CURRENT_CHANNEL in NetMon).");
            }
        }

        // --- OID_DOT11_CURRENT_OPERATION_MODE ----------------------------------------
        // DOT11_CURRENT_OPERATION_MODE { ULONG uReserved; ULONG uCurrentOpMode; } -- 8 bytes
        var curBytes = PacketDll.QueryOid(adapter, Dot11Oids.OID_DOT11_CURRENT_OPERATION_MODE, 8, out var curErr);
        if (curBytes is not null && curBytes.Length >= 8)
        {
            uint cur = BitConverter.ToUInt32(curBytes, 4);
            r.WlanDriver["dot11CurrentOperationMode"] = $"0x{cur:X8} = {Dot11Oids.Describe(cur)}";
            log.Add("");
            log.Add($"OID_DOT11_CURRENT_OPERATION_MODE (0x{Dot11Oids.OID_DOT11_CURRENT_OPERATION_MODE:X8}):");
            log.Add($"  raw bytes      : {Convert.ToHexString(curBytes)}");
            log.Add($"  uCurrentOpMode : 0x{cur:X8} = {Dot11Oids.Describe(cur)}");
        }
        else
        {
            log.Add("");
            log.Add($"OID_DOT11_CURRENT_OPERATION_MODE: FAILED -> {curErr ?? "short response"}");
        }

        // Capabilities we have not built a probe for yet stay honest about that.
        if (hardwareAck.State == CapState.Unknown)
            hardwareAck.Set(CapState.Unknown, "No probe implemented; meaningless until raw TX exists.");
        if (timestampSource.State == CapState.Unknown)
            timestampSource.Set(CapState.Unknown, "No probe implemented; requires monitor-mode RX metadata.");
        if (macAddressing.State == CapState.Unknown)
            macAddressing.Set(CapState.Unknown, "No probe implemented; requires raw TX.");

        var id = "dot11-oid-probe";
        File.WriteAllText(Path.Combine(evidenceDir, id + ".txt"), string.Join(Environment.NewLine, log));
        rawRx.EvidenceIds.Add(id + ".txt");
        r.EvidenceFiles.Add(id + ".txt");
    }

    private static string SafeVersion()
    {
        try { return PacketDll.Version(); } catch { return "(unavailable)"; }
    }
}
