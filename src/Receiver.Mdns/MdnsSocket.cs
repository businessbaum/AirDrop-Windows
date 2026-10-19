using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Receiver.Mdns;

public sealed record MdnsPacket(
    IPEndPoint Source, byte[] Data, int InterfaceIndex, bool IsIPv6, Socket ReceivedOn);

/// <summary>
/// One UDP socket per address family on port 5353, joined to the mDNS groups on the
/// interfaces we care about.
///
/// Port 5353 is already in use on Windows 11 (the OS ships its own mDNS responder in
/// dnscache), so the socket must be opened with address reuse and non-exclusive use or
/// Bind fails with WSAEADDRINUSE. Reuse means we see the same multicast traffic the OS
/// responder sees, which is exactly what this diagnostic needs.
/// </summary>
public sealed class MdnsSocket : IDisposable
{
    public static readonly IPAddress MulticastV4 = IPAddress.Parse("224.0.0.251");
    public static readonly IPAddress MulticastV6 = IPAddress.Parse("ff02::fb");
    public const int Port = 5353;

    private readonly List<Socket> _sockets = [];
    private readonly List<(Socket Socket, int IfIndex, bool V6)> _bound = [];
    private CancellationTokenSource? _cts;

    public event Action<MdnsPacket>? PacketReceived;
    public event Action<string>? Diagnostic;

    /// <summary>
    /// Joins the mDNS group on the given interfaces. Passing an empty list joins on every
    /// interface that is up and has a usable address.
    /// </summary>
    public void Open(IReadOnlyList<NetworkInterface> interfaces, bool enableIPv4 = true, bool enableIPv6 = true)
    {
        var nics = interfaces.Count > 0
            ? interfaces
            : NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(n => n.SupportsMulticast)
                // Skip NDIS lightweight-filter pseudo-interfaces, which have no addresses.
                .Where(n => n.GetIPProperties().UnicastAddresses.Count > 0)
                .ToList();

        if (enableIPv4) OpenFamily(nics, AddressFamily.InterNetwork);
        if (enableIPv6) OpenFamily(nics, AddressFamily.InterNetworkV6);
    }

    private void OpenFamily(IReadOnlyList<NetworkInterface> nics, AddressFamily family)
    {
        // One socket per interface, bound to that interface's own address rather than to
        // the wildcard. This machine has Ethernet and Wi-Fi on the same subnet, and a
        // wildcard-bound socket lets Windows pick the source address by route: replies
        // then leave through the wrong adapter and no longer match the address we
        // advertise, which an Apple responder treats as inconsistent and ignores.
        foreach (var nic in nics)
        {
            var props = nic.GetIPProperties();
            IPAddress? local;
            int ifIndex;

            if (family == AddressFamily.InterNetwork)
            {
                var v4 = props.GetIPv4Properties();
                if (v4 is null) continue;
                local = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                if (local is null) continue;
                ifIndex = v4.Index;
            }
            else
            {
                var v6 = props.GetIPv6Properties();
                if (v6 is null) continue;
                // Link-local is the right source for link-scoped multicast.
                local = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6
                                      && a.Address.IsIPv6LinkLocal)?.Address
                    ?? props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6)?.Address;
                if (local is null) continue;
                ifIndex = v6.Index;
            }

            var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.ExclusiveAddressUse = false;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(local, Port));

                if (family == AddressFamily.InterNetwork)
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(MulticastV4, local));
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        IPAddress.HostToNetworkOrder(ifIndex));
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                }
                else
                {
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
                        new IPv6MulticastOption(MulticastV6, ifIndex));
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, ifIndex);
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);
                }

                // Our own multicast has to come back to us so self-conflict checks work.
                socket.MulticastLoopback = true;

                _sockets.Add(socket);
                _bound.Add((socket, ifIndex, family == AddressFamily.InterNetworkV6));
                Diagnostic?.Invoke(
                    $"bound {local} on '{nic.Name}' (ifIndex {ifIndex}), joined " +
                    (family == AddressFamily.InterNetwork ? "224.0.0.251" : "ff02::fb"));
            }
            catch (Exception ex)
            {
                Diagnostic?.Invoke($"{family} setup on '{nic.Name}' failed: {ex.Message}");
                socket.Dispose();
            }
        }
    }

    public void StartReceiving()
    {
        _cts = new CancellationTokenSource();
        foreach (var s in _sockets)
        {
            var sock = s;
            bool v6 = sock.AddressFamily == AddressFamily.InterNetworkV6;
            _ = Task.Run(() => ReceiveLoop(sock, v6, _cts.Token));
        }
    }

    private async Task ReceiveLoop(Socket socket, bool v6, CancellationToken ct)
    {
        var buffer = new byte[9000];
        EndPoint any = v6 ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
                var data = buffer[..res.ReceivedBytes];
                PacketReceived?.Invoke(new MdnsPacket((IPEndPoint)res.RemoteEndPoint, data, 0, v6, socket));
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                Diagnostic?.Invoke($"receive error: {ex.Message}");
                await Task.Delay(100, CancellationToken.None);
            }
        }
    }

    /// <summary>Sends a datagram to the mDNS group out of every joined interface.</summary>
    public void SendMulticast(byte[] payload)
    {
        foreach (var (socket, ifIndex, v6) in _bound)
        {
            try
            {
                socket.SendTo(payload, new IPEndPoint(v6 ? MulticastV6 : MulticastV4, Port));
            }
            catch (Exception ex)
            {
                Diagnostic?.Invoke($"multicast on ifIndex {ifIndex} (v6={v6}) failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Replies through the very socket the query arrived on, so the source address of
    /// the answer is the address we advertise on that link.
    /// </summary>
    public void SendUnicast(byte[] payload, IPEndPoint destination, Socket? via = null)
    {
        var socket = via ?? _sockets.FirstOrDefault(s => s.AddressFamily == destination.AddressFamily);
        if (socket is null || socket.AddressFamily != destination.AddressFamily) return;
        try { socket.SendTo(payload, destination); }
        catch (Exception ex) { Diagnostic?.Invoke($"unicast to {destination} failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        foreach (var s in _sockets) { try { s.Dispose(); } catch { } }
        _sockets.Clear();
        _bound.Clear();
        _cts?.Dispose();
    }
}
