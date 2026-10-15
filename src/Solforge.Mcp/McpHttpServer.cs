using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Solforge.Mcp;

/**
 * MCP streamable HTTP on 127.0.0.1 only: POST /mcp carries one JSON-RPC message, GET /mcp opens an SSE
 * stream (keep-alive only; Solforge sends no server requests), DELETE /mcp ends the session. Every request
 * needs "Authorization: Bearer <token>". A plain TcpListener keeps http.sys URL ACLs and ASP.NET Core out.
 */
public sealed class McpHttpServer : IAsyncDisposable
{
    private const int MaximumBody = 16 << 20;
    private readonly McpDispatcher dispatcher;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly HashSet<string> sessions = [];
    private Task? acceptLoop;

    public McpHttpServer(McpDispatcher dispatcher, int port = 0)
    {
        this.dispatcher = dispatcher;
        listener = new TcpListener(IPAddress.Loopback, port);
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    }

    public string Token { get; }
    public int Port { get; private set; }
    public string Url => $"http://127.0.0.1:{Port}/mcp";
    public int RequestCount;
    public event Action<string>? Log;

    public void Start()
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false); }
            catch (Exception) when (stop.IsCancellationRequested) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var request = await HttpRequest.Read(stream, MaximumBody, stop.Token).ConfigureAwait(false);
            if (request is null) return;
            Interlocked.Increment(ref RequestCount);
            await Respond(stream, request).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception exception) { Log?.Invoke("MCP request failed: " + exception.Message); }
    }

    private async Task Respond(NetworkStream stream, HttpRequest request)
    {
        // DNS rebinding: a browser page may only reach this server from a loopback origin.
        if (request.Header("origin") is { } origin && !IsLoopbackOrigin(origin))
        { await Write(stream, 403, "text/plain", "Origin not allowed").ConfigureAwait(false); return; }
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Header("authorization") ?? ""),
                Encoding.UTF8.GetBytes("Bearer " + Token)))
        { await Write(stream, 401, "text/plain", "Missing or wrong Solforge MCP token").ConfigureAwait(false); return; }
        if (request.Path != "/mcp") { await Write(stream, 404, "text/plain", "Not found").ConfigureAwait(false); return; }
        string? session = request.Header("mcp-session-id");
        switch (request.Method)
        {
            case "DELETE":
                if (session is not null) lock (sessions) sessions.Remove(session);
                await Write(stream, 200, "text/plain", "").ConfigureAwait(false);
                return;
            case "GET":
                await Stream(stream).ConfigureAwait(false);
                return;
            case "POST":
                break;
            default:
                await Write(stream, 405, "text/plain", "Method not allowed").ConfigureAwait(false);
                return;
        }
        string body = Encoding.UTF8.GetString(request.Body);
        bool initialize = body.Contains("\"initialize\"", StringComparison.Ordinal);
        string? reply = await dispatcher.HandleAsync(body, stop.Token).ConfigureAwait(false);
        var headers = new List<(string, string)>();
        if (initialize && reply is not null && reply.Contains("\"result\"", StringComparison.Ordinal))
        {
            session = Guid.NewGuid().ToString("N");
            lock (sessions) sessions.Add(session);
            headers.Add(("Mcp-Session-Id", session));
        }
        if (reply is null) { await Write(stream, 202, "text/plain", "", headers).ConfigureAwait(false); return; }
        // A client that accepts only SSE gets the reply as one event; everyone else gets plain JSON.
        string accept = request.Header("accept") ?? "application/json";
        if (accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
            && !accept.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            await Write(stream, 200, "text/event-stream", $"event: message\ndata: {reply}\n\n", headers).ConfigureAwait(false);
        else
            await Write(stream, 200, "application/json", reply, headers).ConfigureAwait(false);
    }

    private async Task Stream(NetworkStream stream)
    {
        var head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), stop.Token).ConfigureAwait(false);
        while (!stop.IsCancellationRequested)
        {
            await stream.WriteAsync(": keep-alive\n\n"u8.ToArray(), stop.Token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(15), stop.Token).ConfigureAwait(false);
        }
    }

    private static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host == "localhost");

    private static async Task Write(NetworkStream stream, int status, string type, string body, List<(string, string)>? headers = null)
    {
        byte[] content = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder($"HTTP/1.1 {status} {Reason(status)}\r\n");
        head.Append($"Content-Type: {type}; charset=utf-8\r\nContent-Length: {content.Length}\r\nConnection: close\r\n");
        if (headers is not null) foreach (var (name, value) in headers) head.Append($"{name}: {value}\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
        await stream.WriteAsync(content).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 202 => "Accepted", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
        405 => "Method Not Allowed", 413 => "Payload Too Large", _ => "Error"
    };

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        if (acceptLoop is not null) try { await acceptLoop.ConfigureAwait(false); } catch (Exception) { }
        stop.Dispose();
    }
}

/** The request line, headers and Content-Length body of one HTTP/1.1 request. */
internal sealed class HttpRequest
{
    public string Method { get; private init; } = "";
    public string Path { get; private init; } = "";
    public byte[] Body { get; private set; } = [];
    private readonly Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

    public string? Header(string name) => headers.GetValueOrDefault(name);

    public static async Task<HttpRequest?> Read(Stream stream, int maximumBody, CancellationToken cancel)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false);
            if (read == 0) return null;
            buffer.Write(chunk, 0, read);
            headerEnd = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
            if (headerEnd < 0 && buffer.Length > 64 * 1024) return null;
        }
        string head = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
        string[] lines = head.Split("\r\n");
        string[] start = lines[0].Split(' ');
        if (start.Length < 2) return null;
        string target = start[1];
        int query = target.IndexOf('?');
        var request = new HttpRequest { Method = start[0].ToUpperInvariant(), Path = query < 0 ? target : target[..query] };
        foreach (string line in lines.Skip(1))
            if (line.IndexOf(':') is var colon and > 0) request.headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        int length = int.TryParse(request.Header("content-length"), out int parsed) ? parsed : 0;
        if (length < 0 || length > maximumBody) return null;
        var body = new byte[length];
        int have = (int)buffer.Length - (headerEnd + 4);
        Array.Copy(buffer.GetBuffer(), headerEnd + 4, body, 0, Math.Min(have, length));
        while (have < length)
        {
            int read = await stream.ReadAsync(body.AsMemory(have, length - have), cancel).ConfigureAwait(false);
            if (read == 0) return null;
            have += read;
        }
        request.Body = body;
        return request;
    }

    private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern) =>
        data.AsSpan(0, length).IndexOf(pattern);
}
