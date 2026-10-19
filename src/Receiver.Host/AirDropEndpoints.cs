using System.Text;
using Claunia.PropertyList;
using Microsoft.AspNetCore.Http;
using Receiver.Protocol;

namespace Receiver.Host;

/// <summary>
/// The three AirDrop HTTP endpoints. This first version is a research receiver: it
/// answers enough to appear and to be asked, and it records exactly what the sender sent
/// so the protocol profile is built from real traffic rather than from assumptions.
/// </summary>
public sealed class AirDropEndpoints
{
    private readonly string _computerName;
    private readonly string _modelName;
    private readonly string _evidenceDir;
    private readonly Action<string> _log;
    private readonly TransferStore _store;
    private readonly AcceptedTransfers _accepted;
    private int _requestCounter;

    public AirDropEndpoints(
        string computerName, string modelName, string evidenceDir,
        TransferStore store, AcceptedTransfers accepted, Action<string> log)
    {
        _computerName = computerName;
        _modelName = modelName;
        _evidenceDir = evidenceDir;
        _store = store;
        _accepted = accepted;
        _log = log;
        Directory.CreateDirectory(evidenceDir);
    }

    /// <summary>
    /// Asked before answering /Ask. Returning false rejects the transfer.
    /// AirDrop has no separate accept message: HTTP 200 on /Ask *is* the acceptance,
    /// so the decision must be made before the response is written.
    /// </summary>
    public Func<AskRequest, CancellationToken, Task<bool>> AcceptHandler { get; set; }
        = (_, _) => Task.FromResult(false);

    public async Task Discover(HttpContext ctx)
    {
        var body = await ReadAndRecord(ctx, "Discover", maxRecord: 1 << 20);

        var response = new NSDictionary
        {
            ["ReceiverComputerName"] = new NSString(_computerName),
            ["ReceiverModelName"] = new NSString(_modelName),
        };

        _log($"/Discover from {ctx.Connection.RemoteIpAddress} -> announcing as '{_computerName}'");
        await WritePlist(ctx, response);
        _ = body;
    }

    public async Task Ask(HttpContext ctx)
    {
        var body = await ReadAndRecord(ctx, "Ask", maxRecord: 4 << 20);

        AskRequest request;
        try
        {
            request = AskRequest.Parse(body);
        }
        catch (Exception ex)
        {
            _log($"/Ask from {ctx.Connection.RemoteIpAddress}: unreadable plist ({ex.Message}) -> 400");
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        _log($"/Ask from {request.SenderComputerName ?? "(unnamed)"} " +
             $"[{request.SenderModelName ?? "?"}] transfer={request.TransferId ?? "?"} " +
             $"files={request.FileNames.Count}");
        foreach (var f in request.FileNames) _log($"    offered: {f}");

        bool accepted;
        try
        {
            accepted = await AcceptHandler(request, ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            _log("/Ask: sender went away while the prompt was open");
            return;
        }

        if (!accepted)
        {
            _log("/Ask -> declined (HTTP 401)");
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        _accepted.Accept(request, ctx.Connection.RemoteIpAddress?.ToString());
        _log($"/Ask -> accepted (HTTP 200), transfer {request.TransferId ?? "(ohne ID)"} vorgemerkt");
        var response = new NSDictionary
        {
            ["ReceiverComputerName"] = new NSString(_computerName),
            ["ReceiverModelName"] = new NSString(_modelName),
        };
        await WritePlist(ctx, response);
    }

    public async Task Upload(HttpContext ctx)
    {
        int n = Interlocked.Increment(ref _requestCounter);
        var contentType = ctx.Request.ContentType ?? "(none)";
        var remote = ctx.Connection.RemoteIpAddress?.ToString();

        _log($"/Upload from {remote}, Content-Type: {contentType}");

        // An upload is only valid against an acceptance the user actually gave, that has
        // not been consumed, and that came from the same peer. Replays, uploads before
        // the prompt, uploads after a rejection and concurrent reuse all land here.
        var ticket = _accepted.Claim(remote);
        if (ticket is null)
        {
            _log("/Upload REJECTED: no accepted, unconsumed transfer for this peer");
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // The raw body is kept as evidence while the protocol profile is still being
        // built from real traffic. This is a research setting, not a permanent one.
        var rawPath = Path.Combine(_evidenceDir, $"{n:D3}-Upload.bin");
        long received = 0;
        try
        {
            await using (var raw = File.Create(rawPath))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                {
                    await raw.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
                    received += read;
                }
            }
            _log($"/Upload body: {received} bytes -> {Path.GetFileName(rawPath)}");
            await RecordHeaders(ctx, n, "Upload", $"body {received} bytes -> {Path.GetFileName(rawPath)}");

            await using var body = File.OpenRead(rawPath);
            var result = await _store.StoreAsync(
                body, ticket.Request.SenderComputerName ?? "AirDrop", ctx.RequestAborted);

            foreach (var rejected in result.Rejected) _log($"/Upload abgewiesen: {rejected}");

            _accepted.MarkCompleted(ticket, result.FinalDirectory);
            _log($"/Upload OK: {result.Files.Count} Dateien, {result.TotalBytes} Bytes -> {result.FinalDirectory}");
            ctx.Response.StatusCode = StatusCodes.Status200OK;
        }
        catch (OperationCanceledException)
        {
            _log("/Upload abgebrochen: Sender hat die Verbindung beendet");
            _accepted.Release(ticket);
        }
        catch (Exception ex)
        {
            // Nothing partial is kept, and no success is reported for data we could not
            // fully decode and write.
            _log($"/Upload FEHLGESCHLAGEN: {ex.Message}");
            _accepted.Release(ticket);
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }

    private async Task<byte[]> ReadAndRecord(HttpContext ctx, string label, int maxRecord)
    {
        int n = Interlocked.Increment(ref _requestCounter);

        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
        {
            if (ms.Length + read > maxRecord)
            {
                _log($"{label}: body exceeds {maxRecord} bytes, truncating the record");
                break;
            }
            ms.Write(buffer, 0, read);
        }
        var bytes = ms.ToArray();

        var binPath = Path.Combine(_evidenceDir, $"{n:D3}-{label}.plist");
        await File.WriteAllBytesAsync(binPath, bytes);
        await RecordHeaders(ctx, n, label, $"body: {bytes.Length} bytes -> {Path.GetFileName(binPath)}");

        // A readable dump next to the raw bytes, so the profile can be checked by eye.
        try
        {
            if (bytes.Length > 0)
            {
                var obj = PropertyListParser.Parse(bytes);
                await File.WriteAllTextAsync(
                    Path.Combine(_evidenceDir, $"{n:D3}-{label}.xml"), obj.ToXmlPropertyList());
            }
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_evidenceDir, $"{n:D3}-{label}.parse-error.txt"), ex.ToString());
        }

        return bytes;
    }

    private async Task RecordHeaders(HttpContext ctx, int n, string label, string bodyNote)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {DateTimeOffset.Now:O}");
        sb.AppendLine($"{ctx.Request.Method} {ctx.Request.Path} {ctx.Request.Protocol}");
        sb.AppendLine($"# remote: {ctx.Connection.RemoteIpAddress}:{ctx.Connection.RemotePort}");
        foreach (var (k, v) in ctx.Request.Headers) sb.AppendLine($"{k}: {v}");
        sb.AppendLine();
        sb.AppendLine($"# {bodyNote}");
        await File.WriteAllTextAsync(
            Path.Combine(_evidenceDir, $"{n:D3}-{label}.http.txt"), sb.ToString());
    }

    private static async Task WritePlist(HttpContext ctx, NSDictionary dict)
    {
        var bytes = BinaryPropertyListWriter.WriteToArray(dict);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes);
    }
}
