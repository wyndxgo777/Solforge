using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** One MCP tool: a JSON schema for its arguments and a handler that runs off the UI thread. */
public sealed record McpTool(string Name, string Description, JsonObject InputSchema, bool RequiresApproval,
    Func<JsonObject, CancellationToken, Task<McpToolResult>> Handler);

/** A tool result: text blocks, optional PNG images, and the error flag MCP clients show to the model. */
public sealed class McpToolResult
{
    public List<string> Texts { get; } = [];
    public List<byte[]> Images { get; } = [];
    public bool IsError { get; init; }

    public static McpToolResult Text(string text) => new() { Texts = { text } };
    public static McpToolResult Json(JsonNode? node) => Text(node?.ToJsonString(McpJson.Indented) ?? "null");
    public static McpToolResult Error(string text) => new() { IsError = true, Texts = { text } };

    public JsonObject ToJson()
    {
        var content = new JsonArray();
        foreach (string text in Texts) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (byte[] png in Images)
            content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" });
        return new JsonObject { ["content"] = content, ["isError"] = IsError };
    }
}

public static class McpJson
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    public static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, description, isRequired) in properties)
        {
            props[name] = new JsonObject { ["type"] = type, ["description"] = description };
            if (isRequired) required.Add(name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    public static string? String(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue(out string? text) ? text : args[name]?.ToString();

    public static int Int(JsonObject args, string name, int fallback)
    {
        if (args[name] is not JsonValue value) return fallback;
        if (value.TryGetValue(out int number)) return number;
        if (value.TryGetValue(out double real)) return (int)real;
        return value.TryGetValue(out string? text) && int.TryParse(text, out number) ? number : fallback;
    }

    public static bool Bool(JsonObject args, string name, bool fallback) =>
        args[name] is JsonValue value && value.TryGetValue(out bool flag) ? flag : fallback;
}

/**
 * JSON-RPC 2.0 dispatch for MCP (initialize, ping, tools/list, tools/call). Transport-free: the HTTP
 * server and the checks feed it one message at a time and get the reply (null for a notification).
 */
public sealed class McpDispatcher(IReadOnlyList<McpTool> tools, string version, string instructions)
{
    public static readonly string[] ProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];
    public IReadOnlyList<McpTool> Tools => tools;

    public async Task<string?> HandleAsync(string message, CancellationToken cancel)
    {
        JsonNode? request;
        try { request = JsonNode.Parse(message); }
        catch (JsonException) { return Failure(null, -32700, "Parse error").ToJsonString(McpJson.Compact); }
        if (request is not JsonObject call || call["jsonrpc"]?.ToString() != "2.0")
            return Failure(null, -32600, "Invalid request: send one JSON-RPC 2.0 object").ToJsonString(McpJson.Compact);
        var id = call["id"]?.DeepClone();
        string? method = call["method"]?.ToString();
        if (method is null) return null; // A response from the client; Solforge sends no requests.
        var parameters = call["params"] as JsonObject ?? new JsonObject();
        JsonObject reply;
        try { reply = await Dispatch(id, method, parameters, cancel).ConfigureAwait(false); }
        catch (OperationCanceledException) { reply = Failure(id, -32800, "Request cancelled"); }
        catch (Exception exception) { reply = Failure(id, -32603, exception.Message); }
        return id is null ? null : reply.ToJsonString(McpJson.Compact);
    }

    private async Task<JsonObject> Dispatch(JsonNode? id, string method, JsonObject parameters, CancellationToken cancel)
    {
        switch (method)
        {
            case "initialize":
                string asked = parameters["protocolVersion"]?.ToString() ?? ProtocolVersions[0];
                return Success(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "solforge", ["title"] = "Solforge", ["version"] = version },
                    ["instructions"] = instructions
                });
            case "ping": return Success(id, new JsonObject());
            case "tools/list":
                var list = new JsonArray();
                foreach (var tool in tools)
                    list.Add(new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.RequiresApproval ? tool.Description + " Needs an approve click in Solforge." : tool.Description,
                        ["inputSchema"] = tool.InputSchema.DeepClone(),
                        ["annotations"] = new JsonObject { ["readOnlyHint"] = !tool.RequiresApproval }
                    });
                return Success(id, new JsonObject { ["tools"] = list });
            case "tools/call":
                string? name = parameters["name"]?.ToString();
                if (tools.FirstOrDefault(tool => tool.Name == name) is not { } target)
                    return Failure(id, -32602, $"Unknown tool: {name}");
                var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
                McpToolResult result;
                try { result = await target.Handler(arguments, cancel).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { result = McpToolResult.Error($"{target.Name} failed: {exception.Message}"); }
                return Success(id, result.ToJson());
            default:
                if (method.StartsWith("notifications/", StringComparison.Ordinal)) return Success(id, new JsonObject());
                return Failure(id, -32601, $"Method not found: {method}");
        }
    }

    private static JsonObject Success(JsonNode? id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    private static JsonObject Failure(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
