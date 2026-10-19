using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Receiver.Host;
using Receiver.Mdns;
using Receiver.Protocol;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string computerName = Arg("--name") ?? "Windows-PC";
string modelName = Arg("--model") ?? "Windows";
// 8770 is what real peers advertise, measured on awdl0 on 2026-09-18. 8771 is
// opendrop's choice and was our earlier default. The port travels in the SRV record,
// so it should not matter -- but matching removes one more difference.
int port = int.TryParse(Arg("--port"), out var parsedPort) ? parsedPort : 8770;
string? ifaceFilter = Arg("--interface");
// 0x0A = SUPPORTS_DVZIP | SUPPORTS_MIXED_TYPES. Chosen over macOS's much larger value
// for a measured reason: a real Mac advertises flags=13311 (0x33FF) and the payload then
// runs over QUIC on UDP, which we do not implement. An independent Windows project
// (WinDrop) reports that iOS 26.6 falls back to HTTP over TCP against a receiver
// advertising 0x0A. Advertising less is not modesty here -- it selects the transport we
// can actually serve.
int flags = ParseFlags(Arg("--flags")) ?? 0x0A;
bool autoAccept = args.Contains("--auto-accept");
// IPv4 only by default. The AirPlay receiver in this workspace learned this the hard
// way on this very machine: when several addresses are advertised, the Apple sender
// picks one itself and can pick a dead one. This host also carries two global IPv6
// addresses, one of them a temporary privacy address, and a sender that tries those
// first may give up before ever reaching the IPv4 address that works.
bool withIPv6 = args.Contains("--with-ipv6");
// Diagnostic: see MdnsResponder.SuppressAdditionals.
bool bareAnswers = args.Contains("--bare-answers");
string destination = Arg("--out") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AirDrop");

string repoRoot = FindRepoRoot();
string runDir = Path.Combine(repoRoot, "evidence", "runs",
    DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-receiver");
Directory.CreateDirectory(runDir);
string stateDir = Path.Combine(repoRoot, ".state");

string logPath = Path.Combine(runDir, "receiver.log");
object logGate = new();

void Log(string line)
{
    string stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
    lock (logGate)
    {
        Console.WriteLine(stamped);
        File.AppendAllText(logPath, stamped + Environment.NewLine);
    }
}

Log($"AirDrop receiver starting. Evidence: {runDir}");
Log($"received files go to: {destination}");

var identity = ReceiverIdentity.CreateOrLoad(stateDir, computerName);
Log($"service id  : {identity.ServiceId}");
Log($"certificate : CN={computerName}, thumbprint {identity.Certificate.Thumbprint}");

var nics = NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == OperationalStatus.Up)
    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
    .Where(n => n.SupportsMulticast)
    .Where(n => n.GetIPProperties().UnicastAddresses.Count > 0)
    .Where(n => ifaceFilter is null ||
                n.Name.Contains(ifaceFilter, StringComparison.OrdinalIgnoreCase))
    .ToList();

if (nics.Count == 0)
{
    Console.Error.WriteLine("No usable interface is up. Use --interface to pick one.");
    return 3;
}

// Only routable addresses are advertised. A 169.254.x address means the adapter never
// got a lease, and publishing it would point the sender at nowhere. IPv6 link-local is
// excluded too: it needs a scope id that DNS-SD cannot carry.
var addresses = nics
    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
    .Select(a => a.Address)
    .Where(a => !IPAddress.IsLoopback(a))
    .Where(a => a.AddressFamily != AddressFamily.InterNetwork
                || !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
    .Where(a => a.AddressFamily != AddressFamily.InterNetworkV6 || !a.IsIPv6LinkLocal)
    .Where(a => withIPv6 || a.AddressFamily == AddressFamily.InterNetwork)
    .Distinct()
    .ToList();

// One interface only, for the same reason. If several still qualify, prefer the one
// the machine itself would route through: it must have a default gateway (which drops
// the VMware adapters without matching on their names), wired before wireless.
if (nics.Count > 1)
{
    var preferred = nics
        .Where(n => n.GetIPProperties().GatewayAddresses
            .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                   && !g.Address.Equals(IPAddress.Any)))
        .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
        .ThenByDescending(n => n.Speed)
        .FirstOrDefault();

    if (preferred is not null)
    {
        nics = [preferred];
        var keep = preferred.GetIPProperties().UnicastAddresses.Select(a => a.Address).ToHashSet();
        addresses = addresses.Where(keep.Contains).ToList();
    }
}

Log($"interfaces  : {string.Join(", ", nics.Select(n => n.Name))}");
Log($"advertising : {string.Join(", ", addresses)}");

if (addresses.Count == 0)
{
    Console.Error.WriteLine("No routable address to advertise.");
    return 3;
}

// Real peers use a bare UUID as the SRV target, not a descriptive name.
string hostName = identity.HostUuid + ".local.";

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = null; // /Upload is streamed; limits live in the endpoint.
    k.ListenAnyIP(port, listen =>
    {
        listen.Protocols = HttpProtocols.Http1;
        listen.UseHttps(identity.Certificate, https =>
        {
            // AirDrop senders present a client certificate from Apple's PKI. We ask for
            // it and record it, but we cannot validate it against Apple's roots, so it
            // must not gate the connection -- and it is therefore not an identity claim
            // we are allowed to trust.
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.ClientCertificateValidation = (cert, _, _) =>
            {
                Log($"TLS: client certificate subject='{cert.Subject}' issuer='{cert.Issuer}' " +
                    "(recorded, NOT validated, NOT an identity)");
                return true;
            };
        });
    });
});

var app = builder.Build();

var store = new TransferStore(destination, new TransferLimits(), Log);
var accepted = new AcceptedTransfers(TimeSpan.FromMinutes(5), Log);
var endpoints = new AirDropEndpoints(computerName, modelName, runDir, store, accepted, Log);
endpoints.AcceptHandler = autoAccept
    ? (req, _) =>
    {
        Log($"AUTO-ACCEPT: {req.Describe()}");
        return Task.FromResult(true);
    }
    : PromptOnConsole;

app.Use(async (ctx, next) =>
{
    Log($"--> {ctx.Request.Method} {ctx.Request.Path} from {ctx.Connection.RemoteIpAddress}");
    await next();
    Log($"<-- {ctx.Response.StatusCode} for {ctx.Request.Path}");
});

app.MapPost("/Discover", (HttpContext ctx) => endpoints.Discover(ctx));
app.MapPost("/Ask", (HttpContext ctx) => endpoints.Ask(ctx));
app.MapPost("/Upload", (HttpContext ctx) => endpoints.Upload(ctx));
app.MapFallback((HttpContext ctx) =>
{
    Log($"UNHANDLED {ctx.Request.Method} {ctx.Request.Path}");
    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    return Task.CompletedTask;
});

await app.StartAsync();
Log($"HTTPS listening on port {port}");

using var responder = new MdnsResponder();
responder.Log += m => Log($"mdns: {m}");
responder.Answered += (src, q) => Log($"mdns: answered {src} -> {q}");
responder.SuppressAdditionals = bareAnswers;
responder.KeepAliveInterval = TimeSpan.FromSeconds(
    int.TryParse(Arg("--announce-seconds"), out var ka) ? ka : 30);
if (bareAnswers)
    Log("DIAGNOSTIC: answering PTR without additionals; an accepting sender must now " +
        "ask for SRV/TXT, which makes acceptance visible in this log");
responder.Add(new ServiceAdvertisement
{
    ServiceType = "_airdrop._tcp.local.",
    InstanceLabel = identity.ServiceId,
    HostName = hostName,
    Port = (ushort)port,
    TxtStrings = [$"flags={flags}"],
    Addresses = addresses,
    // Real peers publish 1h15m, not the two minutes we had. A short TTL makes a sender
    // re-resolve constantly and is one more way our record stands out.
    Ttl = 4500,
});
responder.Start(nics);

Log($"advertising {identity.ServiceId}._airdrop._tcp.local. -> {hostName}:{port} (flags=0x{flags:X})");
Log($"re-announcing every {responder.KeepAliveInterval.TotalSeconds:0}s");
Log("Open the AirDrop sheet on the iPhone now. Ctrl+C to stop.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
await stop.Task;

Log("stopping: withdrawing advertisement");
responder.Dispose();
await app.StopAsync();
Log("stopped");
return 0;

async Task<bool> PromptOnConsole(AskRequest req, CancellationToken ct)
{
    Console.WriteLine();
    Console.WriteLine("==========================================================");
    Console.WriteLine("  AirDrop-Angebot");
    Console.WriteLine("  " + req.Describe());
    Console.WriteLine("  Der Absendername ist NICHT verifiziert.");
    Console.WriteLine("  [j] annehmen     [n] ablehnen");
    Console.WriteLine("==========================================================");

    // The prompt must not occupy a request thread, and an aborted request has to
    // release it: the sender gives up long before a human necessarily answers.
    return await Task.Run(() =>
    {
        while (!ct.IsCancellationRequested)
        {
            if (Console.IsInputRedirected || !Console.KeyAvailable)
            {
                Thread.Sleep(100);
                continue;
            }
            var key = Console.ReadKey(true).Key;
            if (key is ConsoleKey.J or ConsoleKey.Y) return true;
            if (key is ConsoleKey.N or ConsoleKey.Escape) return false;
        }
        return false;
    }, CancellationToken.None);
}

static int? ParseFlags(string? text)
{
    if (string.IsNullOrWhiteSpace(text)) return null;
    text = text.Trim();
    return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToInt32(text[2..], 16)
        : int.Parse(text);
}

static string FindRepoRoot()
{
    string? dir = AppContext.BaseDirectory;
    for (int i = 0; i < 8 && dir is not null; i++)
    {
        if (File.Exists(Path.Combine(dir, "ARCHITEKTUR.md"))) return dir;
        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
    }
    return Directory.GetCurrentDirectory();
}
