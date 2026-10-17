/**
 * File: Drives the local WSL test server (status, start/stop, log, deploy, rollback, config, admin routes)
 * by running bash scripts through WslShell. Never touches the VPS.
 */
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Solforge.Mcp.LocalServer;

/** A slice of the server log: lines, the next read offset, whether the log restarted, and its size. */
public sealed record LogChunk(IReadOnlyList<string> Lines, long Next, bool Reset, long Size);
/** Outcome of a server action with its output. */
public sealed record ActionResult(bool Ok, string Message, string Output = "", JsonObject? Report = null)
{
    /** JSON form with output clipped to 6000 characters; deploys add a structured report. */
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["ok"] = Ok, ["message"] = Message, ["output"] = LocalServerParse.Clip(Output, 6000) };
        if (Report is not null) json["report"] = Report.DeepClone();
        return json;
    }
}

/**
 * Start, stop, status, log, deploy, rollback, config, selftest and admin calls for the local WSL sol-server
 * (master.md §10 runbook). Every operation is one bash script run by IWslShell; the admin API is reached with curl on
 * 127.0.0.1 inside WSL with the loopback operator header, the same way the bundled panel works. The VPS is never touched.
 */
public sealed partial class LocalServerManager(IWslShell shell, LocalServerOptions options)
{
    public LocalServerOptions Options { get; } = options;
    private readonly SemaphoreSlim gate = new(1, 1);

    /** Manager configured from environment variables. */
    public static LocalServerManager CreateDefault()
    {
        var options = LocalServerOptions.FromEnvironment();
        return new LocalServerManager(new WslShell(options.Distro), options);
    }

    private string Run => Shell.Quote(Options.RunDirectory);
    private string Exe => Shell.Quote(Options.Executable);

    private string? Guard() => Options.Problem();

    /** One pgrep for the configured executable; prints its pid or nothing. */
    private string PidLine => $"pid=$(pgrep -o -x {Exe} || true)";

    /** Bash script whose sectioned output LocalServerParse.Status turns into a status. */
    public string StatusScript() => $$"""
        RUN={{Run}}
        {{PidLine}}
        echo "PID=$pid"
        if [ -n "$pid" ]; then echo "ETIME=$(ps -o etimes= -p "$pid" | tr -d ' ')"; echo "CWD=$(readlink /proc/$pid/cwd)"; fi
        echo '{{LocalServerParse.SocketsMark}}'
        if [ -n "$pid" ]; then ss -H -tulpn 2>/dev/null | grep "pid=$pid," || true; fi
        echo '{{LocalServerParse.AdminMark}}'
        if [ -n "$pid" ]; then curl -s -m 3 -H 'X-Sol-Panel: 1' http://127.0.0.1:{{Options.AdminPort}}/status || true; echo; fi
        echo '{{LocalServerParse.PlayersMark}}'
        if [ -n "$pid" ]; then curl -s -m 3 -H 'X-Sol-Panel: 1' http://127.0.0.1:{{Options.AdminPort}}/players || true; echo; fi
        echo '{{LocalServerParse.CensusMark}}'
        tail -n 20000 "$RUN/stdout.log" 2>/dev/null | grep -a 'stage=census' | tail -n 400 || true
        echo '{{LocalServerParse.EndMark}}'
        """.ReplaceLineEndings("\n") + "\n";

    /** Reads the server's process, sockets, census and ghost-member state. */
    public async Task<LocalServerStatus> StatusAsync(CancellationToken cancel = default)
    {
        if (Guard() is { } problem) return Failed(problem);
        var result = await shell.RunAsync(StatusScript(), Options.CommandTimeout, cancel).ConfigureAwait(false);
        if (!result.Output.Contains(LocalServerParse.EndMark, StringComparison.Ordinal))
            return Failed(result.TimedOut ? "WSL did not answer in time." : "WSL status failed: " + LocalServerParse.Clip((result.Error + result.Output).Trim(), 300));
        return LocalServerParse.Status(result.Output, Options);
    }

    private static LocalServerStatus Failed(string error) =>
        new(false, null, null, null, false, [], false, false, false, false, null, [], CensusSummary.Empty, error);

    /**
     * Race-free start (master.md §10.2): refuse when already running, wait up to 15 s for the BAP port to be released,
     * then setsid nohup ./sol-server config, and wait for the process and the UDP socket.
     */
    public string StartScript() => $$"""
        cd {{Run}}
        {{PidLine}}
        if [ -n "$pid" ]; then echo "ALREADY_RUNNING=$pid"; exit 0; fi
        for i in $(seq 1 30); do ss -H -tuln 2>/dev/null | grep -q ':{{Options.BapPort}} ' || break; sleep 0.5; done
        if ss -H -tuln 2>/dev/null | grep -q ':{{Options.BapPort}} '; then echo "PORT_BUSY={{Options.BapPort}}"; exit 5; fi
        echo "=== solforge start $(date -u +%FT%TZ) ===" >> stdout.log
        setsid nohup ./{{Options.Executable}} config >> stdout.log 2>&1 < /dev/null &
        for i in $(seq 1 40); do
          {{PidLine}}
          if [ -n "$pid" ] && ss -H -uln 2>/dev/null | grep -q ':{{Options.BapPort}} '; then echo "STARTED=$pid"; exit 0; fi
          sleep 0.5
        done
        {{PidLine}}
        if [ -n "$pid" ]; then echo "STARTED_NO_PORT=$pid"; exit 0; fi
        echo "EXITED"; tail -n 20 stdout.log; exit 6
        """.ReplaceLineEndings("\n") + "\n";

    /** SIGTERM, up to 10 s grace, then SIGKILL, then wait for the kernel to release the BAP port. */
    public string StopScript() => $$"""
        {{PidLine}}
        if [ -z "$pid" ]; then echo "NOT_RUNNING"; exit 0; fi
        pkill -TERM -x {{Exe}} || true
        for i in $(seq 1 20); do pgrep -x {{Exe}} >/dev/null || break; sleep 0.5; done
        if pgrep -x {{Exe}} >/dev/null; then pkill -9 -x {{Exe}} || true; echo "KILLED"; sleep 1; fi
        for i in $(seq 1 20); do ss -H -tuln 2>/dev/null | grep -q ':{{Options.BapPort}} ' || break; sleep 0.5; done
        echo "STOPPED=$pid"
        """.ReplaceLineEndings("\n") + "\n";

    /** Starts the server. */
    public Task<ActionResult> StartAsync(CancellationToken cancel = default) => Exclusive(StartScript(), "Started", cancel);
    /** Stops the server. */
    public Task<ActionResult> StopAsync(CancellationToken cancel = default) => Exclusive(StopScript(), "Stopped", cancel);

    /** Stops, then starts the server. */
    public async Task<ActionResult> RestartAsync(CancellationToken cancel = default)
    {
        var stop = await StopAsync(cancel).ConfigureAwait(false);
        if (!stop.Ok) return stop;
        var start = await StartAsync(cancel).ConfigureAwait(false);
        return start with { Message = "Restarted: " + start.Message, Output = stop.Output + start.Output };
    }

    private async Task<ActionResult> Exclusive(string script, string done, CancellationToken cancel, TimeSpan? timeout = null)
    {
        if (Guard() is { } problem) return new ActionResult(false, problem);
        await gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var result = await shell.RunAsync(script, timeout ?? Options.CommandTimeout, cancel).ConfigureAwait(false);
            string output = (result.Output + (result.Error.Length > 0 ? "\n" + result.Error : "")).Trim();
            string last = output.Split('\n').LastOrDefault(line => line.Length > 0) ?? "";
            return result.Ok ? new ActionResult(true, $"{done}: {last}", output)
                : new ActionResult(false, result.TimedOut ? "Timed out." : $"Failed (exit {result.ExitCode}): {LocalServerParse.Clip(last, 200)}", output);
        }
        finally { gate.Release(); }
    }

    /**
     * New log bytes since offset (negative: the last `lines` lines). A shrunk file (rotated or truncated) restarts from its
     * tail. Reads at most 256 KB per call.
     */
    public string LogScript(long offset, int lines) => $$"""
        f={{Shell.Quote(Options.LogPath)}}
        [ -f "$f" ] || { echo "SIZE=0"; echo "NEXT=0"; echo "---LOG"; exit 0; }
        s=$(stat -c %s "$f")
        echo "SIZE=$s"
        o={{offset.ToString(CultureInfo.InvariantCulture)}}
        if [ "$o" -lt 0 ] || [ "$o" -gt "$s" ]; then
          [ "$o" -ge 0 ] && echo "RESET=1"
          echo "NEXT=$s"; echo "---LOG"; tail -n {{Math.Clamp(lines, 1, 5000).ToString(CultureInfo.InvariantCulture)}} "$f"
        else
          n=$((s-o)); [ "$n" -gt 262144 ] && n=262144
          echo "NEXT=$((o+n))"; echo "---LOG"; tail -c +$((o+1)) "$f" | head -c "$n"
        fi
        """.ReplaceLineEndings("\n") + "\n";

    /** Reads log lines from offset; the chunk says when the log restarted. */
    public async Task<LogChunk> ReadLogAsync(long offset, int lines = 200, CancellationToken cancel = default)
    {
        if (Guard() is { } problem) throw new InvalidOperationException(problem);
        var result = await shell.RunAsync(LogScript(offset, lines), Options.CommandTimeout, cancel).ConfigureAwait(false);
        int mark = result.Output.IndexOf("---LOG\n", StringComparison.Ordinal);
        if (mark < 0) throw new IOException("Log read failed: " + LocalServerParse.Clip((result.Error + result.Output).Trim(), 300));
        var head = result.Output[..mark].Split('\n');
        long Number(string key) => long.TryParse(head.FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..],
            NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;
        string body = result.Output[(mark + 7)..];
        bool reset = head.Contains("RESET=1");
        string[] linesOut = offset < 0 || reset ? body.TrimEnd('\n').Split('\n') : new[] { body };
        return new LogChunk(linesOut, Number("NEXT"), reset, Number("SIZE"));
    }

    /** The last `lines` matching lines (the filter is applied in Solforge over the last `scan` lines). */
    public async Task<IReadOnlyList<string>> TailAsync(string? filter, int lines, int scan = 4000, CancellationToken cancel = default)
    {
        var chunk = await ReadLogAsync(-1, scan, cancel).ConfigureAwait(false);
        var match = LocalServerParse.Filter(filter);
        return chunk.Lines.Where(match).TakeLast(Math.Clamp(lines, 1, 2000)).ToArray();
    }

    // --- deploy -----------------------------------------------------------------------------------

    /** Loads (compiles) every deployed .lua file in a Lua interpreter in WSL; the server is not started or touched. */
    public async Task<ScriptCheck> CheckScriptsAsync(DeployPlan plan, CancellationToken cancel = default)
    {
        if (Guard() is { } problem) return ScriptCheck.Unavailable(problem);
        var files = plan.Files.Select(file => file.Target).Where(t => t.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length == 0) return new ScriptCheck(true, "none", 0, [], []);
        var result = await shell.RunAsync(DeployPlanner.CheckScriptsScript(files, Options), Options.CommandTimeout, cancel).ConfigureAwait(false);
        return DeployPlanner.ParseScriptCheck(result.Output, result.TimedOut ? "script check timed out" : (result.Error + result.Output).Trim());
    }

    /** Running, BAP UDP, web and admin all answering. */
    public static bool IsHealthy(LocalServerStatus status) => status.Running && status.BapUdp && status.Web && status.Admin;

    /** Waits up to HealthWait for the server to be healthy, returning the last status. */
    private async Task<LocalServerStatus> WaitHealthyAsync(CancellationToken cancel)
    {
        var until = DateTime.UtcNow + Options.HealthWait;
        while (true)
        {
            var status = await StatusAsync(cancel).ConfigureAwait(false);
            if (IsHealthy(status) || DateTime.UtcNow >= until) return status;
            await Task.Delay((int)Math.Clamp(Options.HealthWait.TotalMilliseconds / 5, 1, 1000), cancel).ConfigureAwait(false);
        }
    }

    /**
     * Copies the plan's files into the server under a new backup id, loads every deployed Lua script to find errors now,
     * and optionally restarts the server and checks it came back healthy. The report says what happened at each step.
     */
    public async Task<ActionResult> DeployAsync(DeployPlan plan, bool restart, CancellationToken cancel = default)
    {
        if (plan.Files.Count == 0) return new ActionResult(false, "Nothing to deploy: " + string.Join("; ", plan.Notes));
        string id = DeployPlanner.NewBackupId(DateTime.UtcNow) + "-" + Guid.NewGuid().ToString("N")[..4];
        var copy = await Exclusive(DeployPlanner.DeployScript(plan, Options, id), $"Deployed {plan.Files.Count} file(s), backup {id}", cancel,
            TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        var report = new JsonObject { ["copied"] = copy.Ok ? plan.Files.Count : 0, ["backup"] = id, ["copy_ok"] = copy.Ok };
        if (!copy.Ok) return copy with { Report = report };

        var scripts = await CheckScriptsAsync(plan, cancel).ConfigureAwait(false);
        report["scripts"] = scripts.ToJson();
        var parts = new List<string> { copy.Message, scripts.Summary };
        string output = copy.Output + "\n" + string.Join("\n", scripts.Errors.Concat(scripts.Warnings.Select(w => "warning: " + w)));
        bool ok = !scripts.HasErrors;

        if (restart)
        {
            var again = await RestartAsync(cancel).ConfigureAwait(false);
            var status = again.Ok ? await WaitHealthyAsync(cancel).ConfigureAwait(false) : await StatusAsync(cancel).ConfigureAwait(false);
            bool healthy = again.Ok && IsHealthy(status);
            var ports = status.Sockets.Select(s => $"{s.Protocol} {s.Port}").Distinct().ToArray();
            report["restart"] = new JsonObject
            {
                ["requested"] = true, ["ok"] = again.Ok, ["healthy"] = healthy, ["pid"] = status.Pid,
                ["bap_udp"] = status.BapUdp, ["web"] = status.Web, ["admin"] = status.Admin,
                ["ports"] = new JsonArray(ports.Select(p => (JsonNode)p).ToArray()),
            };
            string state = $"BAP UDP {(status.BapUdp ? "up" : "down")}, web {(status.Web ? "up" : "down")}, admin {(status.Admin ? "up" : "down")}";
            parts.Add(healthy ? $"restart OK: server healthy, pid {status.Pid}, ports {string.Join(", ", ports)}"
                : "restart FAILED: " + (again.Ok ? $"server not healthy ({(status.Running ? "pid " + status.Pid : "not running")}, {state})" : again.Message));
            output += "\n" + again.Output;
            ok &= healthy;
        }
        else parts.Add("server not restarted");
        report["ok"] = ok;
        return new ActionResult(ok, string.Join("; ", parts), output.Trim(), report);
    }

    /**
     * Restores a backup (the newest not yet rolled back when none is named), optionally restarting the
     * server.
     */
    public async Task<ActionResult> RollbackAsync(string? backupId, bool restart, CancellationToken cancel = default)
    {
        if (backupId is null or "" or "latest")
            backupId = (await BackupsAsync(cancel).ConfigureAwait(false)).FirstOrDefault(entry => !entry.RolledBack)?.Id;
        if (!DeployPlanner.IsBackupId(backupId)) return new ActionResult(false, "No deploy backup to roll back.");
        var result = await Exclusive(DeployPlanner.RollbackScript(Options, backupId!), $"Rolled back {backupId}", cancel, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        if (!result.Ok || !restart) return result;
        var again = await RestartAsync(cancel).ConfigureAwait(false);
        return new ActionResult(again.Ok, result.Message + "; " + again.Message, result.Output + "\n" + again.Output);
    }

    /** Lists the deploy backups on the server. */
    public async Task<IReadOnlyList<BackupEntry>> BackupsAsync(CancellationToken cancel = default)
    {
        if (Guard() is { } problem) throw new InvalidOperationException(problem);
        var result = await shell.RunAsync(DeployPlanner.ListBackupsScript(Options), Options.CommandTimeout, cancel).ConfigureAwait(false);
        var list = new List<BackupEntry>();
        foreach (string line in result.Output.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 3 || !DeployPlanner.IsBackupId(parts[0])) continue;
            list.Add(new BackupEntry(parts[0], int.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out int n) ? n : 0,
                parts[2] == "rolled-back", parts.Length > 3 ? parts[3] : ""));
        }
        return list;
    }

    // --- config -----------------------------------------------------------------------------------

    [GeneratedRegex(@"^(config/[A-Za-z0-9_.-]+\.(json|cfg|txt)|Sunrise/crucible\.cfg)$", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigName();

    /** config/<name>.json|cfg|txt or Sunrise/crucible.cfg; a bare name means config/<name>. */
    public static string? ConfigPath(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim().Replace('\\', '/');
        if (!name.Contains('/', StringComparison.Ordinal)) name = "config/" + name;
        return ConfigName().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal) ? name : null;
    }

    /** Names of the config files this manager may read and write. */
    public async Task<IReadOnlyList<string>> ConfigFilesAsync(CancellationToken cancel = default)
    {
        if (Guard() is { } problem) throw new InvalidOperationException(problem);
        var result = await shell.RunAsync($"cd {Run} && ls -1 config 2>/dev/null; [ -f Sunrise/crucible.cfg ] && echo Sunrise/crucible.cfg\n",
            Options.CommandTimeout, cancel).ConfigureAwait(false);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Contains('/', StringComparison.Ordinal) ? line : "config/" + line)
            .Where(path => ConfigPath(path) is not null).ToArray();
    }

    /** Reads one allowed config file; other names are rejected. */
    public async Task<string> ReadConfigAsync(string name, CancellationToken cancel = default)
    {
        string path = ConfigPath(name) ?? throw new ArgumentException($"'{name}' is not a server config file (config/*.json|cfg|txt or Sunrise/crucible.cfg).");
        if (Guard() is { } problem) throw new InvalidOperationException(problem);
        var result = await shell.RunAsync($"cd {Run} && base64 -w0 {Shell.Quote(path)}\n", Options.CommandTimeout, cancel).ConfigureAwait(false);
        if (!result.Ok) throw new IOException($"{path} could not be read: {result.Error.Trim()}");
        return Encoding.UTF8.GetString(Convert.FromBase64String(result.Output.Trim()));
    }

    /** JSON files must parse. The old file is copied to <backups>/config/<stamp>-<name> first; a restart applies it. */
    public string WriteConfigScript(string path, string content, string stamp)
    {
        string backups = Options.BackupDirectory + "/config";
        string base64 = Convert.ToBase64String(new UTF8Encoding(false).GetBytes(content.Replace("\r\n", "\n")));
        return $$"""
            set -eu
            cd {{Run}}
            mkdir -p {{Shell.Quote(backups)}}
            if [ -f {{Shell.Quote(path)}} ]; then cp -p {{Shell.Quote(path)}} {{Shell.Quote(backups + "/" + stamp + "-" + path.Replace('/', '_'))}}; fi
            printf '%s' '{{base64}}' | base64 -d > {{Shell.Quote(path + ".solforge-tmp")}}
            mv -f {{Shell.Quote(path + ".solforge-tmp")}} {{Shell.Quote(path)}}
            echo "WROTE={{path}}"
            """.ReplaceLineEndings("\n") + "\n";
    }

    /** Validation message for a JSON config that does not parse; null for other formats or valid JSON. */
    public static string? ConfigProblem(string path, string content)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;
        try { JsonNode.Parse(content, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); return null; }
        catch (JsonException error) { return $"{path} is not valid JSON: {error.Message}"; }
    }

    /** Validates and writes one allowed config file; the server needs a restart to apply it. */
    public Task<ActionResult> WriteConfigAsync(string name, string content, CancellationToken cancel = default)
    {
        string? path = ConfigPath(name);
        if (path is null) return Task.FromResult(new ActionResult(false, $"'{name}' is not a server config file."));
        if (ConfigProblem(path, content) is { } problem) return Task.FromResult(new ActionResult(false, problem));
        return Exclusive(WriteConfigScript(path, content, DeployPlanner.NewBackupId(DateTime.UtcNow)), $"Saved {path} (restart to apply)", cancel);
    }

    /** Hides values on lines that name a key, secret, token or password (for the MCP read). */
    public static string Redact(string text) => string.Join('\n', text.Split('\n').Select(line =>
        SecretLine().IsMatch(line) ? SecretValue().Replace(line, "$1\"<redacted>\"") : line));

    [GeneratedRegex(@"(key|secret|token|password|passwd)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretLine();
    [GeneratedRegex(@"([:=]\s*)(""[^""]*""|[^,\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretValue();

    // --- selftest and admin ------------------------------------------------------------------------

    /** sol-server --selftest (headless round-trips, no server boot); exit 0 = pass. */
    public string SelftestScript() => $$"""
        cd {{Run}}
        timeout {{(int)Options.SelftestTimeout.TotalSeconds}} ./{{Options.Executable}} --selftest 2>&1 | tail -n 200
        echo "SELFTEST_EXIT=${PIPESTATUS[0]}"
        """.ReplaceLineEndings("\n") + "\n";

    /** Runs the server's self-test and reports its result. */
    public async Task<ActionResult> SelftestAsync(CancellationToken cancel = default)
    {
        var result = await Exclusive(SelftestScript(), "Selftest finished", cancel, Options.SelftestTimeout + TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        var exit = Regex.Match(result.Output, @"SELFTEST_EXIT=(\d+)");
        bool pass = exit.Success && exit.Groups[1].Value == "0";
        return result with { Ok = pass, Message = pass ? "Selftest passed" : exit.Success ? $"Selftest FAILED (exit {exit.Groups[1].Value})" : result.Message };
    }

    /** Read routes of the admin API; writes are limited to commands and inventory edits (never /stop or /reboot). */
    public static readonly string[] AdminReads =
        ["status", "players", "clients", "sessions", "fireteam", "lobbies", "coop", "vendors", "bounties", "quests",
         "sol/accounts", "sol/inventory", "sol/items/names", "sol/changes", "sol/moderation"];
    public static readonly string[] AdminWrites = ["sol/command", "sol/inventory/set"];

    /** Null when path is an allowed admin route for the verb; otherwise the reason it is refused. */
    public static string? AdminProblem(string path, bool post) =>
        (post ? AdminWrites : AdminReads).Contains(path) ? null
            : $"'{path}' is not an allowed {(post ? "POST" : "GET")} route here (allowed: {string.Join(", ", post ? AdminWrites : AdminReads)}).";

    /** URL-encodes values as a form body. */
    public static string Form(IReadOnlyDictionary<string, string>? values) => values is null ? ""
        : string.Join('&', values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

    /** Script that calls the local admin port for an allowed route. */
    public string AdminScript(string path, IReadOnlyDictionary<string, string>? values, bool post)
    {
        string url = $"http://127.0.0.1:{Options.AdminPort}/{path}";
        string form = Form(values);
        return post
            ? $"curl -s -m 20 -H 'X-Sol-Panel: 1' -H 'Content-Type: application/x-www-form-urlencoded' --data-binary {Shell.Quote(form)} {Shell.Quote(url)}\n"
            : $"curl -s -m 20 -H 'X-Sol-Panel: 1' {Shell.Quote(form.Length > 0 ? url + "?" + form : url)}\n";
    }

    /** Calls an allowed local admin route and returns its reply. */
    public async Task<JsonNode?> AdminAsync(string path, IReadOnlyDictionary<string, string>? values, bool post, CancellationToken cancel = default)
    {
        path = path.Trim().TrimStart('/');
        if (AdminProblem(path, post) is { } problem) throw new ArgumentException(problem);
        if (Guard() is { } guard) throw new InvalidOperationException(guard);
        var result = await shell.RunAsync(AdminScript(path, values, post), Options.CommandTimeout, cancel).ConfigureAwait(false);
        string text = result.Output.Trim();
        if (text.Length == 0) throw new IOException("The local server's admin API did not answer (is it running?).");
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return JsonValue.Create(LocalServerParse.Clip(text, 2000)); }
    }

    /** A server command as the operator console (/sol/command): give, clear, activity, goto, who... */
    public async Task<ActionResult> CommandAsync(string line, CancellationToken cancel = default)
    {
        line = line.Trim().TrimStart('/');
        if (line.Length == 0 || line.Contains('\n')) return new ActionResult(false, "Give one command line.");
        var reply = await AdminAsync("sol/command", new Dictionary<string, string> { ["line"] = line }, true, cancel).ConfigureAwait(false);
        var answer = reply as JsonObject;
        bool ok = answer?["ok"] is JsonValue value && value.TryGetValue(out bool b) && b;
        string lines = answer?["lines"] is JsonArray rows ? string.Join("\n", rows.Select(row => row?.ToString())) : reply?.ToJsonString() ?? "";
        return new ActionResult(ok, ok ? $"/{line}" : $"/{line} failed", lines);
    }
}
