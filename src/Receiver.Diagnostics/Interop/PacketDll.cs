using System.Runtime.InteropServices;

namespace Receiver.Diagnostics.Interop;

/// <summary>
/// Minimal P/Invoke surface for Npcap's Packet.dll.
///
/// Why this exists: `netsh wlan show wirelesscapabilities` prints the same information,
/// but its output is localized (this machine is de-DE) and therefore unsafe to parse.
/// PacketRequest forwards a raw NDIS OID query to the miniport, so the answer comes back
/// as a numeric bitmask that means the same thing in every locale.
/// </summary>
internal static class PacketDll
{
    private const string NpcapDir = @"C:\Windows\System32\Npcap";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string? lpPathName);

    private static bool _initialized;

    /// <summary>
    /// Packet.dll lives in System32\Npcap, which is deliberately not on the default
    /// search path (Npcap keeps it there so it does not shadow WinPcap). Point the
    /// loader at it once before the first P/Invoke resolves.
    /// </summary>
    public static bool TryInitialize(out string? error)
    {
        error = null;
        if (_initialized) return true;

        if (!File.Exists(Path.Combine(NpcapDir, "Packet.dll")))
        {
            error = $"Packet.dll not found in {NpcapDir}. Npcap does not appear to be installed.";
            return false;
        }

        SetDllDirectoryW(NpcapDir);
        _initialized = true;
        return true;
    }

    [DllImport("Packet.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr PacketGetVersion();

    [DllImport("Packet.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr PacketOpenAdapter([MarshalAs(UnmanagedType.LPStr)] string adapterName);

    [DllImport("Packet.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void PacketCloseAdapter(IntPtr adapter);

    [DllImport("Packet.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern bool PacketRequest(IntPtr adapter, bool set, IntPtr oidData);

    public static string Version()
    {
        var p = PacketGetVersion();
        return p == IntPtr.Zero ? "(unknown)" : Marshal.PtrToStringAnsi(p) ?? "(unknown)";
    }

    /// <summary>Npcap device name for a Windows interface GUID, e.g. \Device\NPF_{...}.</summary>
    public static string DeviceName(Guid interfaceGuid) => $@"\Device\NPF_{interfaceGuid:B}";

    public sealed class Adapter : IDisposable
    {
        internal IntPtr Handle;
        internal Adapter(IntPtr h) => Handle = h;
        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { PacketCloseAdapter(Handle); Handle = IntPtr.Zero; }
        }
    }

    public static Adapter? Open(string deviceName)
    {
        var h = PacketOpenAdapter(deviceName);
        // Npcap returns a non-null pointer with a sentinel handle on failure in some builds,
        // so treat only a null pointer as a hard failure and let the first OID query decide.
        return h == IntPtr.Zero ? null : new Adapter(h);
    }

    /// <summary>
    /// Issue an NDIS OID query. Returns the raw payload bytes, or null with a reason.
    /// </summary>
    public static byte[]? QueryOid(Adapter adapter, uint oid, int payloadBytes, out string? error)
    {
        error = null;
        // PACKET_OID_DATA { ULONG Oid; ULONG Length; UCHAR Data[1]; }
        int total = 8 + Math.Max(payloadBytes, 1);
        IntPtr buf = Marshal.AllocHGlobal(total);
        try
        {
            for (int i = 0; i < total; i++) Marshal.WriteByte(buf, i, 0);
            Marshal.WriteInt32(buf, 0, unchecked((int)oid));
            Marshal.WriteInt32(buf, 4, payloadBytes);

            if (!PacketRequest(adapter.Handle, false, buf))
            {
                error = $"PacketRequest(query) failed, Win32 error {Marshal.GetLastWin32Error()}";
                return null;
            }

            int returned = Marshal.ReadInt32(buf, 4);
            if (returned <= 0 || returned > payloadBytes) returned = payloadBytes;
            var outBytes = new byte[returned];
            Marshal.Copy(buf + 8, outBytes, 0, returned);
            return outBytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
