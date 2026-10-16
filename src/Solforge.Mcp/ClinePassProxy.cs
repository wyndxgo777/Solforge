using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * "Claude Code on ClinePass": a localhost-only translator from the Anthropic Messages API (what Claude Code speaks when
 * ANTHROPIC_BASE_URL points at it) to Cline's OpenAI-compatible Chat Completions API (https://api.cline.bot/api/v1).
 * Cline documents this API for "your own scripts, apps, or automation" with a ClinePass API key (CLINE_API_KEY), and its
 * ClinePass FAQ allows using ClinePass "with other coding agents by using your ClinePass API key".
 *
 * Credentials: upstream calls carry only CLINE_API_KEY, read from the user's own environment at each request and never
 * stored, logged or written anywhere. Claude Code is started with ANTHROPIC_AUTH_TOKEN set to this proxy's random
 * per-run LocalToken (and every Anthropic/Claude login variable removed), so it never sends a Claude or Google sign-in
 * here; incoming Authorization headers are only compared with LocalToken and never forwarded.
 */
public sealed class ClinePassProxy : IAsyncDisposable
{
    public const string ApiKeyVariable = "CLINE_API_KEY", DefaultUpstream = "https://api.cline.bot/api/v1";
    public const string DefaultModel = "cline-pass/kimi-k3", DefaultFastModel = "cline-pass/deepseek-v4.1-flash";
    private const int MaximumBody = 32 << 20;

    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly HttpClient http;
    private readonly Func<string?> apiKey;
    private Task? acceptLoop;

    /** @param apiKey Reads the ClinePass key per request (default: the CLINE_API_KEY environment variable). */
    public ClinePassProxy(string? upstream = null, Func<string?>? apiKey = null, HttpMessageHandler? handler = null, int port = 0)
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        Upstream = (upstream ?? DefaultUpstream).TrimEnd('/');
        this.apiKey = apiKey ?? new Func<string?>(ReadApiKey);
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromMinutes(30);
        LocalToken = "solforge-local-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    }

    public string Upstream { get; }
    /** The only credential Claude Code gets (ANTHROPIC_AUTH_TOKEN); it unlocks nothing but this localhost port. */
    public string LocalToken { get; }
    public int Port { get; private set; }
    /** ANTHROPIC_BASE_URL for Claude Code. */
    public string Url => $"http://127.0.0.1:{Port}";
    /** Model for claude-*, sonnet and opus names; ids without a provider prefix get cline-pass/. */
    public string Model { get; set; } = DefaultModel;
    /** Model for haiku names (Claude Code's background calls). */
    public string FastModel { get; set; } = DefaultFastModel;
    public bool Running => acceptLoop is { IsCompleted: false };
    public event Action<string>? Log;

    public static bool HasApiKey() => !string.IsNullOrWhiteSpace(ReadApiKey());

    /** The user's own CLINE_API_KEY: this process's environment, else the saved user environment (set after Solforge started). */
    private static string? ReadApiKey() => Environment.GetEnvironmentVariable(ApiKeyVariable) is { Length: > 0 } key ? key.Trim()
        : OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(ApiKeyVariable, EnvironmentVariableTarget.User)?.Trim() : null;

    public void Start()
    {
        if (acceptLoop is not null) return;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = Task.Run(AcceptLoop);
    }

    /**
     * Claude Code's environment for this proxy: base URL and local token, every other Anthropic or Claude login removed
     * (an empty value removes the variable), and the /model aliases pointed at ClinePass models.
     */
    public IReadOnlyDictionary<string, string> ClaudeEnvironment() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ANTHROPIC_BASE_URL"] = Url, ["ANTHROPIC_AUTH_TOKEN"] = LocalToken, ["ANTHROPIC_API_KEY"] = "",
        ["CLAUDE_CODE_OAUTH_TOKEN"] = "", ["CLAUDE_CODE_USE_BEDROCK"] = "", ["CLAUDE_CODE_USE_VERTEX"] = "",
        ["ANTHROPIC_MODEL"] = Model, ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = Model, ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = Model,
        ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = FastModel, ["ANTHROPIC_SMALL_FAST_MODEL"] = FastModel,
        ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1"
    };

    /** The ClinePass model id for a model name Claude Code sends. */
    public string MapModel(string? requested)
    {
        string name = requested?.Trim() ?? "";
        if (name.StartsWith(ClineDelegate.Provider + "/", StringComparison.OrdinalIgnoreCase)) return name;
        if (name.Contains("haiku", StringComparison.OrdinalIgnoreCase)) return FastModel;
        if (name.Length == 0 || name.StartsWith("claude", StringComparison.OrdinalIgnoreCase)
            || name.Contains("sonnet", StringComparison.OrdinalIgnoreCase) || name.Contains("opus", StringComparison.OrdinalIgnoreCase)) return Model;
        return name.Contains('/') ? name : ClineDelegate.Provider + "/" + name;
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
            if (await HttpRequest.Read(stream, MaximumBody, stop.Token).ConfigureAwait(false) is not { } request) return;
            await Respond(stream, request).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception exception) { Log?.Invoke("ClinePass proxy request failed: " + exception.GetType().Name); }
    }

    private async Task Respond(NetworkStream stream, HttpRequest request)
    {
        if (request.Method is "GET" or "HEAD" && request.Path == "/") { await Write(stream, 200, "text/plain", "ok"); return; }
        if (!Authorized(request)) { await WriteError(stream, 401, "authentication_error", "Not the Solforge local token."); return; }
        if (request.Method != "POST") { await WriteError(stream, 405, "invalid_request_error", "POST only."); return; }
        JsonObject? body;
        try { body = JsonNode.Parse(request.Body) as JsonObject; }
        catch (JsonException) { body = null; }
        if (body is null) { await WriteError(stream, 400, "invalid_request_error", "Body is not a JSON object."); return; }
        switch (request.Path.TrimEnd('/'))
        {
            case "/v1/messages/count_tokens":
                // ClinePass has no token counter; an estimate keeps Claude Code's context meter moving.
                await Write(stream, 200, "application/json", new JsonObject { ["input_tokens"] = Math.Max(1, request.Body.Length / 4) }.ToJsonString());
                return;
            case "/v1/messages":
                await Messages(stream, body);
                return;
            default:
                await WriteError(stream, 404, "not_found_error", "Only /v1/messages is served.");
                return;
        }
    }

    private bool Authorized(HttpRequest request)
    {
        string given = request.Header("x-api-key") ?? "";
        if (request.Header("authorization") is { } authorization && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            given = authorization[7..].Trim();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(LocalToken));
    }

    private async Task Messages(NetworkStream stream, JsonObject body)
    {
        if (apiKey() is not { Length: > 0 } key)
        {
            await WriteError(stream, 401, "authentication_error",
                $"{ApiKeyVariable} is not set. Create a key at app.cline.bot (Settings > API Keys), set it in your user environment.");
            return;
        }
        bool streaming = body["stream"] is JsonValue flag && flag.TryGetValue(out bool on) && on;
        string model = MapModel(AnthropicTranslator.Text(body, "model"));
        var openAi = AnthropicTranslator.Request(body, model);
        using var upstream = new HttpRequestMessage(HttpMethod.Post, Upstream + "/chat/completions")
        { Content = new StringContent(openAi.ToJsonString(), Encoding.UTF8, "application/json") };
        upstream.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        HttpResponseMessage response;
        try { response = await http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, stop.Token); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        { await WriteError(stream, 502, "api_error", "Could not reach ClinePass: " + exception.Message); return; }
        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            string detail = (await response.Content.ReadAsStringAsync(stop.Token)).Replace(key, "***");
            int status = (int)response.StatusCode;
            await WriteError(stream, status, AnthropicTranslator.ErrorType(status),
                $"ClinePass answered {status}: {(detail.Length > 2000 ? detail[..2000] : detail)}");
            return;
        }
        if (!streaming)
        {
            JsonObject? reply;
            try { reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(stop.Token)) as JsonObject; }
            catch (JsonException) { reply = null; }
            if (reply is null) { await WriteError(stream, 502, "api_error", "ClinePass sent a reply that is not JSON."); return; }
            await Write(stream, 200, "application/json", AnthropicTranslator.Response(reply, model).ToJsonString());
            return;
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n"), stop.Token);
        var translator = new AnthropicStream(model);
        await WriteEvents(stream, translator.Start());
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(stop.Token), Encoding.UTF8);
        try
        {
            while (await reader.ReadLineAsync(stop.Token) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                string data = line[5..].Trim();
                if (data == "[DONE]") break;
                JsonObject? chunk;
                try { chunk = JsonNode.Parse(data) as JsonObject; }
                catch (JsonException) { continue; }
                if (chunk is not null) await WriteEvents(stream, translator.Feed(chunk));
            }
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            await WriteEvents(stream, [("error", AnthropicTranslator.Error("api_error", "ClinePass stream broke: " + exception.Message))]);
            return;
        }
        await WriteEvents(stream, translator.Finish());
    }

    private async Task WriteEvents(NetworkStream stream, IEnumerable<(string Event, JsonObject Data)> events)
    {
        var text = new StringBuilder();
        foreach (var (name, data) in events) text.Append("event: ").Append(name).Append("\ndata: ").Append(data.ToJsonString()).Append("\n\n");
        if (text.Length == 0) return;
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()), stop.Token);
        await stream.FlushAsync(stop.Token);
    }

    private static Task WriteError(NetworkStream stream, int status, string type, string message) =>
        Write(stream, status, "application/json", AnthropicTranslator.Error(type, message).ToJsonString());

    private static async Task Write(NetworkStream stream, int status, string type, string body)
    {
        byte[] content = Encoding.UTF8.GetBytes(body);
        string head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(content);
        await stream.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        if (acceptLoop is not null) try { await acceptLoop.ConfigureAwait(false); } catch (Exception) { }
        http.Dispose();
        stop.Dispose();
    }
}

/** Anthropic Messages <-> OpenAI Chat Completions, request and whole-reply shapes. */
public static class AnthropicTranslator
{
    /** An Anthropic Messages request as an OpenAI Chat Completions request for @param model. */
    public static JsonObject Request(JsonObject body, string model)
    {
        var messages = new JsonArray();
        string system = Joined(body["system"]);
        if (system.Length > 0) messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        foreach (var message in (body["messages"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            string role = Text(message, "role") == "assistant" ? "assistant" : "user";
            if (message["content"] is JsonValue plain && plain.TryGetValue(out string? only))
            { messages.Add(new JsonObject { ["role"] = role, ["content"] = only }); continue; }
            var blocks = (message["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            if (role == "assistant") { messages.Add(Assistant(blocks)); continue; }
            // Tool results answer the previous assistant turn, so they go first, one tool message each.
            foreach (var result in blocks.Where(block => Text(block, "type") == "tool_result"))
            {
                string content = Joined(result["content"]);
                if (result["is_error"] is JsonValue error && error.TryGetValue(out bool failed) && failed) content = "Error: " + content;
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = Text(result, "tool_use_id") ?? "", ["content"] = content });
            }
            var parts = new JsonArray();
            foreach (var block in blocks)
                switch (Text(block, "type"))
                {
                    case "text" when Text(block, "text") is { Length: > 0 } text:
                        parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                        break;
                    case "image" when ImageUrl(block) is { } url:
                        parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
                        break;
                    case "document":
                        parts.Add(new JsonObject { ["type"] = "text", ["text"] = Joined((block["source"] as JsonObject)?["data"] ?? (block["source"] as JsonObject)?["content"]) });
                        break;
                }
            if (parts.Count == 0) continue;
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = parts.All(part => Text(part as JsonObject, "type") == "text")
                    ? string.Join("\n\n", parts.Select(part => Text(part as JsonObject, "text"))) : parts
            });
        }
        var request = new JsonObject { ["model"] = model, ["messages"] = messages };
        if (body["max_tokens"] is JsonValue max) request["max_tokens"] = max.DeepClone();
        foreach (string name in new[] { "temperature", "top_p" })
            if (body[name] is JsonValue value) request[name] = value.DeepClone();
        if (body["stop_sequences"] is JsonArray { Count: > 0 } stops) request["stop"] = stops.DeepClone();
        var tools = new JsonArray();
        foreach (var tool in (body["tools"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            if (Text(tool, "name") is { Length: > 0 } name && tool["input_schema"] is JsonObject schema)
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = name, ["description"] = Text(tool, "description") ?? "", ["parameters"] = schema.DeepClone() }
                });
        if (tools.Count > 0)
        {
            request["tools"] = tools;
            if (body["tool_choice"] is JsonObject choice)
                request["tool_choice"] = Text(choice, "type") switch
                {
                    "any" => "required",
                    "none" => "none",
                    "tool" => new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = Text(choice, "name") ?? "" } },
                    _ => "auto"
                };
        }
        if (body["stream"] is JsonValue stream && stream.TryGetValue(out bool on) && on)
        {
            request["stream"] = true;
            request["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
        return request;
    }

    private static JsonObject Assistant(List<JsonObject> blocks)
    {
        string text = string.Join("\n\n", blocks.Where(block => Text(block, "type") == "text").Select(block => Text(block, "text")).Where(t => t is { Length: > 0 }));
        var calls = new JsonArray();
        foreach (var use in blocks.Where(block => Text(block, "type") == "tool_use"))
            calls.Add(new JsonObject
            {
                ["id"] = Text(use, "id") ?? "", ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = Text(use, "name") ?? "", ["arguments"] = (use["input"] ?? new JsonObject()).ToJsonString() }
            });
        var message = new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 ? text : null };
        if (calls.Count > 0) message["tool_calls"] = calls;
        return message;
    }

    private static string? ImageUrl(JsonObject block) => block["source"] as JsonObject is { } source
        ? Text(source, "type") switch
        {
            "base64" => $"data:{Text(source, "media_type") ?? "image/png"};base64,{Text(source, "data")}",
            "url" => Text(source, "url"),
            _ => null
        }
        : null;

    /** A string, or the text blocks of a content array, as one string (images become a placeholder). */
    public static string Joined(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue(out string? text) => text,
        JsonArray array => string.Join("\n\n", array.OfType<JsonObject>().Select(block => Text(block, "type") switch
        {
            "text" => Text(block, "text") ?? "",
            "image" => "[image]",
            _ => ""
        }).Where(text => text.Length > 0)),
        _ => ""
    };

    /** An OpenAI chat completion (optionally in Cline's {success,data} envelope) as an Anthropic message. */
    public static JsonObject Response(JsonObject reply, string model)
    {
        if (reply["data"] is JsonObject data && reply.ContainsKey("success")) reply = data;
        var choice = (reply["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var message = choice?["message"] as JsonObject;
        var content = new JsonArray();
        if (Text(message, "content") is { Length: > 0 } text) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var call in (message?["tool_calls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            content.Add(new JsonObject
            {
                ["type"] = "tool_use", ["id"] = Text(call, "id") is { Length: > 0 } id ? id : NewId("toolu_"),
                ["name"] = Text(call["function"] as JsonObject, "name") ?? "", ["input"] = Arguments(Text(call["function"] as JsonObject, "arguments"))
            });
        var usage = reply["usage"] as JsonObject;
        return new JsonObject
        {
            ["id"] = Text(reply, "id") is { Length: > 0 } replyId ? "msg_" + replyId : NewId("msg_"),
            ["type"] = "message", ["role"] = "assistant", ["model"] = model, ["content"] = content,
            ["stop_reason"] = StopReason(Text(choice, "finish_reason")), ["stop_sequence"] = null,
            ["usage"] = new JsonObject { ["input_tokens"] = Number(usage, "prompt_tokens"), ["output_tokens"] = Number(usage, "completion_tokens") }
        };
    }

    /** Tool-call argument text as an object; a model's broken JSON becomes {} rather than failing the turn. */
    public static JsonObject Arguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    public static string StopReason(string? finish) => finish switch
    {
        "length" => "max_tokens",
        "tool_calls" or "function_call" => "tool_use",
        _ => "end_turn"
    };

    public static string ErrorType(int status) => status switch
    {
        400 or 422 => "invalid_request_error",
        401 => "authentication_error",
        402 or 403 => "permission_error",
        404 => "not_found_error",
        429 => "rate_limit_error",
        529 or 503 => "overloaded_error",
        _ => "api_error"
    };

    public static JsonObject Error(string type, string message) =>
        new() { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = message } };

    internal static string NewId(string prefix) => prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    internal static int Number(JsonObject? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue(out int number) ? number : 0;

    public static string? Text(JsonObject? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}

/** OpenAI chat.completion.chunk events in, Anthropic Messages stream events out. */
public sealed class AnthropicStream(string model)
{
    private readonly Dictionary<int, int> toolBlocks = [];
    private int nextBlock, inputTokens, outputTokens;
    private int? openBlock;
    private bool openIsText;
    private string stopReason = "end_turn";

    public IEnumerable<(string Event, JsonObject Data)> Start() =>
    [
        ("message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = AnthropicTranslator.NewId("msg_"), ["type"] = "message", ["role"] = "assistant", ["model"] = model,
                ["content"] = new JsonArray(), ["stop_reason"] = null, ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 }
            }
        })
    ];

    public List<(string Event, JsonObject Data)> Feed(JsonObject chunk)
    {
        var events = new List<(string, JsonObject)>();
        if (chunk["data"] is JsonObject data && chunk.ContainsKey("success")) chunk = data;
        if (chunk["usage"] is JsonObject usage)
        {
            inputTokens = Math.Max(inputTokens, AnthropicTranslator.Number(usage, "prompt_tokens"));
            outputTokens = Math.Max(outputTokens, AnthropicTranslator.Number(usage, "completion_tokens"));
        }
        foreach (var choice in (chunk["choices"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var delta = choice["delta"] as JsonObject;
            if (AnthropicTranslator.Text(delta, "content") is { Length: > 0 } text)
            {
                if (openBlock is null || !openIsText)
                {
                    Close(events);
                    openBlock = nextBlock++;
                    openIsText = true;
                    events.Add(("content_block_start", new JsonObject
                    {
                        ["type"] = "content_block_start", ["index"] = openBlock,
                        ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" }
                    }));
                }
                events.Add(("content_block_delta", Delta(openBlock.Value, new JsonObject { ["type"] = "text_delta", ["text"] = text })));
            }
            foreach (var call in (delta?["tool_calls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                int index = AnthropicTranslator.Number(call, "index");
                var function = call["function"] as JsonObject;
                if (!toolBlocks.TryGetValue(index, out int block))
                {
                    Close(events);
                    block = toolBlocks[index] = nextBlock++;
                    openBlock = block;
                    openIsText = false;
                    events.Add(("content_block_start", new JsonObject
                    {
                        ["type"] = "content_block_start", ["index"] = block,
                        ["content_block"] = new JsonObject
                        {
                            ["type"] = "tool_use", ["id"] = AnthropicTranslator.Text(call, "id") is { Length: > 0 } id ? id : AnthropicTranslator.NewId("toolu_"),
                            ["name"] = AnthropicTranslator.Text(function, "name") ?? "", ["input"] = new JsonObject()
                        }
                    }));
                }
                if (AnthropicTranslator.Text(function, "arguments") is { Length: > 0 } partial)
                    events.Add(("content_block_delta", Delta(block, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = partial })));
            }
            if (AnthropicTranslator.Text(choice, "finish_reason") is { Length: > 0 } finish) stopReason = AnthropicTranslator.StopReason(finish);
        }
        return events;
    }

    public List<(string Event, JsonObject Data)> Finish()
    {
        var events = new List<(string, JsonObject)>();
        Close(events);
        events.Add(("message_delta", new JsonObject
        {
            ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = stopReason, ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["input_tokens"] = inputTokens, ["output_tokens"] = outputTokens }
        }));
        events.Add(("message_stop", new JsonObject { ["type"] = "message_stop" }));
        return events;
    }

    private void Close(List<(string, JsonObject)> events)
    {
        if (openBlock is not { } block) return;
        events.Add(("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = block }));
        openBlock = null;
    }

    private static JsonObject Delta(int index, JsonObject delta) =>
        new() { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta };
}
