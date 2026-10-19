using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using Receiver.Mdns;

namespace Receiver.Diagnostics.Probes;

/// <summary>
/// Passive mDNS observation.
///
/// Open question this answers: when the user opens the AirDrop sheet on an unmodified
/// iPhone or Mac, does that device browse for _airdrop._tcp.local over the ordinary LAN
/// at all? If it never asks on this link, no responder we write on this link can ever be
/// found, and the LAN variant of P1 is settled without implementing any of AirDrop.
///
/// This only listens. It sends nothing and changes nothing.
/// </summary>
internal static class MdnsWatch
{
    private static readonly string[] InterestingNames =
    [
        "_airdrop._tcp",
        "_airplay._tcp",
        "_raop._tcp",
        "_companion-link._tcp",
        "_rdlink._tcp",     // Apple "Continuity"/remote desktop link
        "_sleep-proxy._udp",
        "_apple-mobdev2._tcp",
        "_touch-able._tcp",
        "_homekit._tcp",
    ];

    public static int Run(string[] args)
    {
        int seconds = int.TryParse(GetOption(args, "--seconds"), out var s) ? s : 120;
        string? ifaceFilter = GetOption(args, "--interface");
        bool all = args.Contains("--all");
        bool query = args.Contains("--query");
        // Ask for one specific name instead of the service enumeration. Needed to test
        // whether a responder defends its own hostname, which a service browse never
        // exercises: the host records ride along as additionals and are never requested.
        string? queryName = GetOption(args, "--query-name");
        string queryTypeText = GetOption(args, "--query-type") ?? "ANY";
        string outDir = GetOption(args, "--out")
            ?? Path.Combine(RepoRoot(), "evidence", "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-mdns-watch");
        Directory.CreateDirectory(outDir);

        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Where(n => n.SupportsMulticast)
            // Windows exposes one pseudo-interface per bound NDIS lightweight filter
            // (Npcap, WFP, QoS, ...). They carry no addresses and cannot be joined.
            .Where(n => n.GetIPProperties().UnicastAddresses.Count > 0)
            .Where(n => ifaceFilter is null ||
                        n.Name.Contains(ifaceFilter, StringComparison.OrdinalIgnoreCase) ||
                        n.Description.Contains(ifaceFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (nics.Count == 0)
        {
            Console.Error.WriteLine("No matching interfaces are up.");
            return 3;
        }

        var log = new StringBuilder();
        var logGate = new object();
        void Log(string line)
        {
            lock (logGate)
            {
                Console.WriteLine(line);
                log.AppendLine(line);
            }
        }

        Log($"# mdns-watch  started {DateTimeOffset.Now:O}");
        Log($"# listening {seconds}s on: {string.Join(", ", nics.Select(n => n.Name))}");
        Log($"# highlighting: {string.Join(" ", InterestingNames)}");
        Log($"# showing {(all ? "ALL traffic" : "only Apple-relevant names (use --all for everything)")}");
        Log("");

        using var sock = new MdnsSocket();
        sock.Diagnostic += d => Log($"# {d}");

        var airdropHits = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var talkers = new Dictionary<string, int>();
        var gate = new object();

        // Every address this machine owns. Our own query and Windows' own mDNS responder
        // would otherwise show up as an "AirDrop sighting", which is exactly the kind of
        // self-echo the architecture warns against counting as a result.
        // Compared without the IPv6 scope id on purpose: the same link-local address is
        // reported with different scope ids depending on which interface a socket used,
        // so a scope-sensitive comparison lets our own packets pass as foreign sightings.
        var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address.IsIPv6LinkLocal || a.Address.IsIPv6SiteLocal
                ? new IPAddress(a.Address.GetAddressBytes())
                : a.Address)
            .ToHashSet();
        bool includeSelf = args.Contains("--include-self");
        if (queryName is not null) query = true;

        sock.PacketReceived += pkt =>
        {
          lock (gate)
          {
            var bare = pkt.Source.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? new IPAddress(pkt.Source.Address.GetAddressBytes())
                : pkt.Source.Address;
            bool fromSelf = localAddresses.Contains(bare);

            DnsMessage msg;
            try { msg = DnsMessage.Parse(pkt.Data); }
            catch (Exception ex)
            {
                if (all) Log($"{Ts()} {pkt.Source} UNPARSEABLE ({ex.Message}, {pkt.Data.Length} bytes)");
                return;
            }

            var names = msg.Questions.Select(q => q.Name)
                .Concat(msg.Answers.Select(a => a.Name))
                .Concat(msg.Additionals.Select(a => a.Name))
                .ToList();
            foreach (var n in names) seenNames.Add(n.TrimEnd('.'));

            bool interesting = names.Any(n =>
                InterestingNames.Any(i => n.Contains(i, StringComparison.OrdinalIgnoreCase)));
            bool isAirdrop = names.Any(n => n.Contains("_airdrop._tcp", StringComparison.OrdinalIgnoreCase));

            if (isAirdrop && !fromSelf)
            {
                var key = pkt.Source.Address.ToString();
                talkers[key] = talkers.GetValueOrDefault(key) + 1;
                var line = $"{Ts()} *** AIRDROP *** from {pkt.Source}  {msg.Summarize()}";
                airdropHits.Add(line);
                Log(line);
                return;
            }

            if (isAirdrop && fromSelf && !includeSelf)
            {
                Log($"{Ts()} [self] {pkt.Source,-40} {msg.Summarize()}");
                return;
            }

            if (interesting || all)
                Log($"{Ts()} {(fromSelf ? "[self]" : "      ")} {pkt.Source,-40} {msg.Summarize()}");
          }
        };

        sock.Open(nics);
        sock.StartReceiving();

        if (query)
        {
            // Two purposes. First, it proves the receive path works at all: if we do not
            // even see the answers to our own question, a silent log means "we are deaf",
            // not "the network is quiet". Second, the DNS-SD meta-query enumerates every
            // service type on the link, which tells us what Apple devices advertise here.
            var q = new DnsMessage();
            if (queryName is not null)
            {
                var type = Enum.TryParse<DnsType>(queryTypeText, ignoreCase: true, out var t) ? t : DnsType.ANY;
                var name = queryName.EndsWith('.') ? queryName : queryName + ".";
                q.Questions.Add(new DnsQuestion { Name = name, Type = type });
                Thread.Sleep(300);
                Log($"{Ts()} >>> sending mDNS {type} query for {name}");
            }
            else
            {
                q.Questions.Add(new DnsQuestion { Name = "_services._dns-sd._udp.local.", Type = DnsType.PTR });
                q.Questions.Add(new DnsQuestion { Name = "_airdrop._tcp.local.", Type = DnsType.PTR });
                Thread.Sleep(300);
                Log($"{Ts()} >>> sending mDNS PTR query for _services._dns-sd._udp.local. and _airdrop._tcp.local.");
            }
            sock.SendMulticast(q.ToBytes());
        }

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
            // KeyAvailable throws when stdin is redirected (e.g. run from a script).
            if (!Console.IsInputRedirected && Console.KeyAvailable
                && Console.ReadKey(true).Key == ConsoleKey.Q) break;
        }

        Thread.Sleep(200);
        Log("");
        Log($"# stopped {DateTimeOffset.Now:O}");
        Log($"# _airdrop._tcp packets observed: {airdropHits.Count}");
        foreach (var (addr, count) in talkers.OrderByDescending(k => k.Value))
            Log($"#   {addr}: {count}");
        Log($"# distinct mDNS names seen: {seenNames.Count}");
        foreach (var n in seenNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            Log($"#   {n}");

        var path = Path.Combine(outDir, "mdns-watch.txt");
        File.WriteAllText(path, log.ToString(), Encoding.UTF8);
        Console.WriteLine();
        Console.WriteLine($"Log written to: {path}");

        // Exit code carries the finding so scripts can branch on it.
        return airdropHits.Count > 0 ? 0 : 1;
    }

    private static string Ts() => DateTime.Now.ToString("HH:mm:ss.fff");

    private static string? GetOption(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "ARCHITEKTUR.md"))) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return Directory.GetCurrentDirectory();
    }
}
