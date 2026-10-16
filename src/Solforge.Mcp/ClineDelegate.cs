/**
 * File: Stdio MCP server exposing ask_clinepass; sibling of AgyDelegate.cs (see its header for how the shim starts
 * these).
 */
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * The "clinepass" stdio MCP server (solforge-mcp --clinepass) with one tool, ask_clinepass, that hands a task to the
 * Cline CLI in headless JSON mode on its ClinePass provider (cline --json -P cline-pass) under the user's own Cline
 * sign-in or CLINE_API_KEY. It mirrors ask_gemini: Claude Code stays on its own Claude sign-in, Cline on its own.
 * Terms: nothing here reads, copies or forwards a sign-in token or key; cline authenticates itself as in a terminal.
 * Linked into the shim, so it uses only System.Text.Json's trim-safe node API and EngineCommands.
 */
public static class ClineDelegate
{
    public const string ServerName = "clinepass", ToolName = "ask_clinepass", Provider = "cline-pass";
    public const int MaxPrompt = AgyDelegate.MaxPrompt, DefaultTimeout = AgyDelegate.DefaultTimeout, MaxTimeout = AgyDelegate.MaxTimeout;
    public const string Instructions = "ask_clinepass delegates a task to a ClinePass model via the user's own Cline CLI sign-in.";

    /** The MCP tool definition (name, description, input schema) for ask_clinepass. */
    public static JsonObject Tool() => new()
    {
        ["name"] = ToolName,
        ["description"] = "Hand a self-contained task to an open-weight ClinePass model (Qwen, Kimi, GLM, DeepSeek, MiniMax...) through the " +
            "user's own Cline CLI login (cline --json -P cline-pass). It runs headless in the given folder and returns the model's reply. " +
            "Use it when the user asks for ClinePass, Cline or one of those models, or for a cheap second opinion; the prompt must stand " +
            "alone (the model does not see this conversation). For long inputs write them to a file and name it.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["prompt"] = AgyDelegate.Field("string", "The whole task for the ClinePass model."),
                ["folder"] = AgyDelegate.Field("string", "Working folder (default: this server's folder, normally the project)."),
                ["mode"] = new JsonObject
                {
                    ["type"] = "string", ["enum"] = new JsonArray("plan", "act"),
                    ["description"] = "plan (default): read and answer without editing; act: the model may edit files and run commands."
                },
                ["model"] = AgyDelegate.Field("string", "ClinePass model id, e.g. qwen3.8-max, kimi-k3, glm-5.3, deepseek-v4-pro " +
                    "(the cline-pass/ prefix is optional); default is the one cline auth picked."),
                ["timeout_seconds"] = AgyDelegate.Field("integer", $"Time limit, default {DefaultTimeout}, at most {MaxTimeout}.")
            },
            ["required"] = new JsonArray("prompt")
        }
    };

    /** cline's arguments for one call (each one argv entry, the prompt last after --), or an error. */
    public static (IReadOnlyList<string>? Arguments, string? Error) Arguments(JsonObject? args)
    {
        string prompt = AgyDelegate.Text(args, "prompt")?.Trim() ?? "";
        if (prompt.Length == 0) return (null, "prompt is required.");
        if (prompt.Length > MaxPrompt) return (null, $"prompt is over {MaxPrompt} characters; write it to a file and ask the model to read that file.");
        string mode = AgyDelegate.Text(args, "mode") is { Length: > 0 } given ? given : "plan";
        if (mode is not ("plan" or "act")) return (null, "mode must be plan or act.");
        int timeout = args?["timeout_seconds"] is JsonValue value && value.TryGetValue(out int seconds) ? seconds : DefaultTimeout;
        var list = new List<string> { "--json", "-P", Provider };
        if (AgyDelegate.Text(args, "model")?.Trim() is { Length: > 0 } model)
            list.AddRange(["-m", model.StartsWith(Provider + "/", StringComparison.OrdinalIgnoreCase) ? model : Provider + "/" + model]);
        if (mode == "plan") list.Add("-p");
        list.AddRange(["-t", Math.Clamp(timeout, 30, MaxTimeout).ToString(System.Globalization.CultureInfo.InvariantCulture), "--", prompt]);
        return (list, null);
    }

    /**
     * cline --json prints one JSON event per line and ends with {"type":"run_result","finishReason":...,"text":...,"model":{"id":...}};
     * failures print {"type":"error","message":...}. Tool text: the reply, then the model and finish reason; Error unless completed.
     */
    public static (string Text, bool Error) Result(string stdout, string stderr, int exitCode)
    {
        JsonObject? result = null, done = null;
        var errors = new List<string>();
        foreach (string line in stdout.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith('{')) continue;
            JsonObject? node;
            try { node = JsonNode.Parse(trimmed) as JsonObject; }
            catch (JsonException) { continue; }
            switch (AgyDelegate.Text(node, "type"))
            {
                case "run_result": result = node; break;
                case "error": if (AgyDelegate.Text(node, "message") is { Length: > 0 } message) errors.Add(message); break;
                case "agent_event" when AgyDelegate.Text(node?["event"] as JsonObject, "type") == "done": done = node?["event"] as JsonObject; break;
            }
        }
        if (result is null && done is null)
        {
            string raw = errors.Count > 0 ? string.Join("\n", errors) : (stdout + "\n" + stderr).Trim();
            return ($"cline exited {exitCode} without a result: {(raw.Length > 4000 ? raw[^4000..] : raw)}", true);
        }
        string reason = AgyDelegate.Text(result, "finishReason") ?? AgyDelegate.Text(done, "reason") ?? "";
        string reply = (AgyDelegate.Text(result, "text") ?? AgyDelegate.Text(done, "text") ?? "").TrimEnd();
        var text = new StringBuilder(reply.Length > 0 ? reply : "(no response text)");
        text.Append("\n\n[cline");
        if (AgyDelegate.Text(result?["model"] as JsonObject, "id") is { Length: > 0 } model) text.Append(' ').Append(model);
        text.Append(' ').Append(reason.Length > 0 ? reason : "done");
        if (errors.Count > 0) text.Append("; ").Append(string.Join("; ", errors));
        text.Append(']');
        return (text.ToString(), !string.Equals(reason, "completed", StringComparison.OrdinalIgnoreCase) || exitCode != 0);
    }

    /**
     * The program and leading arguments that run the Cline CLI without cmd.exe: a native cline.exe on the PATH, else
     * node.exe with npm's bin/cline script (the global install puts cline.cmd beside node_modules\cline). Null: not installed.
     */
    public static (string Program, IReadOnlyList<string> Prefix)? FindCline(string? path = null)
    {
        path ??= EngineCommands.FreshPath();
        string? pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        if (EngineCommands.Resolve("cline", path, pathExt) is not { } found) return null;
        if (found.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return (found, Array.Empty<string>());
        string script = Path.Combine(Path.GetDirectoryName(found) ?? "", "node_modules", "cline", "bin", "cline");
        if (!File.Exists(script) || EngineCommands.Resolve("node", path, pathExt) is not { } node
            || !node.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return (node, new[] { script });
    }

    /** Runs one ask_clinepass call. */
    public static async Task<(string Text, bool Error)> Call(JsonObject? args, CancellationToken cancel = default)
    {
        var (arguments, error) = Arguments(args);
        if (arguments is null) return (error!, true);
        string folder = AgyDelegate.Text(args, "folder") is { Length: > 0 } given ? given : Environment.CurrentDirectory;
        if (!Directory.Exists(folder)) return ($"folder not found: {folder}", true);
        if (FindCline() is not { } cline)
            return ("The Cline CLI is not installed. Install it (npm install -g cline), run cline auth and pick ClinePass (or set CLINE_API_KEY), then retry.", true);
        var info = new ProcessStartInfo(cline.Program)
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in cline.Prefix.Concat(arguments)) info.ArgumentList.Add(argument);
        info.Environment["PATH"] = EngineCommands.FreshPath();
        try
        {
            using var process = Process.Start(info);
            if (process is null) return ("cline did not start.", true);
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(cancel);
            var stderr = process.StandardError.ReadToEndAsync(cancel);
            // cline enforces -t itself; this only catches a CLI that hangs past it.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromSeconds(MaxTimeout + 60));
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } return ("cline did not finish in time and was stopped.", true); }
            return Result(await stdout, await stderr, process.ExitCode);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { return ("cline failed to run: " + exception.Message, true); }
    }

    /** The ask_clinepass stdio server; @param call replaces the cline run (tests). */
    public static Task Serve(TextReader input, TextWriter output, Func<JsonObject?, CancellationToken, Task<(string Text, bool Error)>>? call = null) =>
        AgyDelegate.Serve(input, output, [new AgyDelegate.StdioTool(Tool(), call ?? Call)], "solforge-clinepass", Instructions);
}
