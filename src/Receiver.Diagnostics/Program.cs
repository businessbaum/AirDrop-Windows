using System.Text.Json;
using Receiver.Diagnostics;
using Receiver.Diagnostics.Probes;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "probe";

switch (command)
{
    case "probe":
        return RunProbe(args);
    case "mdns-watch":
        return MdnsWatch.Run(args);
    case "-h":
    case "--help":
    case "help":
        PrintHelp();
        return 0;
    default:
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintHelp();
        return 2;
}

static void PrintHelp()
{
    Console.WriteLine("""
        airdrop-diag - capability and transport diagnostics for the Windows AirDrop receiver

        Usage:
          airdrop-diag probe [--out <dir>]
          airdrop-diag mdns-watch [--seconds N] [--interface <name>] [--all] [--query] [--out <dir>]

        probe        Read-only capability survey of this machine's WLAN path. Writes
                     capabilities.json plus verbatim evidence files. Changes no settings.

        mdns-watch   Passively logs mDNS traffic and reports whether any device on this
                     link browses for _airdrop._tcp.local. Sends nothing unless
                     --query is given, which emits one DNS-SD enumeration query.
                     Exit code 0 = AirDrop queries seen, 1 = none seen.

        Capability states: unknown | unsupported | reported | verified
          reported  = the driver claims it; we did not exercise it
          verified  = we exercised it and observed the real effect
        """);
}

static int RunProbe(string[] args)
{
    string outDir = GetOption(args, "--out")
        ?? Path.Combine(FindRepoRoot(), "evidence", "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-probe");
    Directory.CreateDirectory(outDir);

    var report = new CapabilityReport
    {
        MachineName = Environment.MachineName,
        ElevatedProcess = HostProbe.IsElevated(),
    };

    HostProbe.Collect(report);
    AdapterProbe.Collect(report);
    NpcapProbe.Collect(report);

    var wlan = AdapterProbe.FindPhysicalWlan();
    if (wlan is null)
    {
        report.WlanDriver["found"] = false;
        report.Cap("rawRx").Set(CapState.Unknown, "No physical 802.11 adapter found.");
    }
    else
    {
        report.WlanDriver["found"] = true;
        report.WlanDriver["description"] = wlan.Description;
        report.WlanDriver["interfaceGuid"] = wlan.Guid.ToString("B");
        report.WlanDriver["mac"] = wlan.Mac;
        report.WlanDriver["operationalStatus"] = wlan.Status.ToString();

        Dot11Probe.Collect(report, wlan, outDir);
    }

    // Verbatim command captures. Localized, so they are evidence for humans, not parse input.
    foreach (var (exe, cmdArgs, id) in new[]
    {
        ("netsh", "wlan show drivers", "netsh-wlan-show-drivers"),
        ("netsh", "wlan show wirelesscapabilities", "netsh-wlan-show-wirelesscapabilities"),
        ("netsh", "wlan show interfaces", "netsh-wlan-show-interfaces"),
    })
    {
        var file = HostProbe.CaptureCommand(exe, cmdArgs, outDir, id);
        if (file is not null) report.EvidenceFiles.Add(file);
    }

    if (wlan is not null)
    {
        var wh = @"C:\Windows\System32\Npcap\WlanHelper.exe";
        if (File.Exists(wh))
        {
            var f1 = HostProbe.CaptureCommand(wh, $"{wlan.Guid:D} modes", outDir, "wlanhelper-modes");
            var f2 = HostProbe.CaptureCommand(wh, $"{wlan.Guid:D} mode", outDir, "wlanhelper-mode");
            if (f1 is not null) report.EvidenceFiles.Add(f1);
            if (f2 is not null) report.EvidenceFiles.Add(f2);
        }
    }

    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
    var jsonPath = Path.Combine(outDir, "capabilities.json");
    File.WriteAllText(jsonPath, json);

    Console.WriteLine($"Probe written to: {outDir}");
    Console.WriteLine();
    Console.WriteLine($"  WLAN adapter : {report.WlanDriver.GetValueOrDefault("description") ?? "(none)"}");
    Console.WriteLine($"  op-mode mask : {report.WlanDriver.GetValueOrDefault("dot11OpModeCapabilityMask") ?? "(not read)"}");
    Console.WriteLine($"  decoded      : {report.WlanDriver.GetValueOrDefault("dot11OpModeCapabilityDecoded") ?? "(not read)"}");
    Console.WriteLine($"  elevated     : {report.ElevatedProcess}");
    Console.WriteLine();
    Console.WriteLine("Capabilities:");
    foreach (var (name, cap) in report.Capabilities.OrderBy(k => k.Key))
        Console.WriteLine($"  {name,-16} {cap.State,-12} {cap.Rationale}");

    return 0;
}

static string? GetOption(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    for (int i = 0; i < 8 && dir is not null; i++)
    {
        if (File.Exists(Path.Combine(dir, "ARCHITEKTUR.md"))) return dir;
        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
    }
    return Directory.GetCurrentDirectory();
}
