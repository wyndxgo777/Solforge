/**
 * File: Local server page: status, log, deploy, config, actions and subsystems for the local game server, backed by
 * Solforge.Mcp LocalServer (the same manager the local_server_* MCP tools use).
 */
using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using Solforge.Mcp.LocalServer;

namespace SunriseStudio.Editor;

/**
 * View > Local Server: the local WSL sol-server for testing (never the VPS). Status with ports, players and the ghost-member
 * check; start, stop and restart; a filtered live log; one-click deploy of the open project with backup and rollback;
 * config editing; selftest; quick actions through the admin API; and what each server subsystem does.
 * All WSL work runs off the UI thread; Draw only reads the latest results.
 */
public sealed class LocalServerPage
{
    /** The local server manager (status, log, backups, deploy). */
    private readonly LocalServerManager server = LocalServerManager.CreateDefault();
    /** Guards the status and log state shared with worker tasks. */
    private readonly object sync = new();
    private readonly List<string> log = [];
    /** Follows the server log file. */
    private readonly LogFollower follower = new();
    /** Client build provenance and testkit health (the Client tab). */
    private readonly ClientTestkitSection clientTab = new();
    private const int MaxLogLines = 5000;

    private LocalServerStatus? status;
    private IReadOnlyList<BackupEntry> backups = [];
    private IReadOnlyList<string> configFiles = [];
    private DeployPlan? plan;
    private string planProject = "";
    private string busy = "", lastMessage = "", lastOutput = "";
    private bool lastOk = true;
    /** Timers for status and log refreshes. */
    private readonly Stopwatch statusClock = new(), logClock = new();
    private bool statusPending, logPending;

    private string logFilter = "";
    private bool followLog = true, pauseLog;
    private bool restartAfterDeploy = true;
    private int selectedConfig = -1;
    private string configText = "", configLoaded = "";
    private string commandLine = "", player = "", item = "", target = "", character = "";
    private int amount = 1;

    /**
     * The status dot for the workbench tab: grey while unknown, green running, red stopped or unreachable, amber when a
     * ghost member or a sol-server from another folder needs a restart. Polls status even while another tab is shown.
     */
    public (Vector4 Color, string Tip) Dot()
    {
        if (!statusClock.IsRunning || (statusClock.Elapsed > TimeSpan.FromSeconds(10) && !statusPending)) { statusClock.Restart(); RefreshStatus(); }
        var current = status;
        if (current is null) return (new(.6f, .6f, .6f, 1), "Local server: checking");
        if (current.Error is not null) return (Bad, "Local server: " + current.Error);
        if (current.Running && (current.Census.Ghosts.Count > 0 || current.ForeignInstance)) return (Warn, "Local server: " + current.Summary);
        return (current.Running ? Good : Bad, "Local server: " + current.Summary);
    }

    /** The tab body: header with start/stop/restart/selftest, then the section tabs. */
    public void DrawBody(string? projectRoot, string? lastBuild)
    {
        Poll();
        DrawHeader();
        var (testkitColor, testkitTip) = clientTab.Dot();
        ImGui.TextColored(testkitColor, "(o) testkit");
        if (ImGui.IsItemHovered()) Tip(testkitTip);
        if (ImGui.BeginTabBar("local-server-tabs"))
        {
            if (ImGui.BeginTabItem("Status")) { DrawStatus(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Log")) { DrawLog(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Deploy")) { DrawDeploy(projectRoot, lastBuild); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Config")) { DrawConfig(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Actions")) { DrawActions(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Subsystems")) { DrawSubsystems(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Client")) { clientTab.Draw(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    // --- background work ----------------------------------------------------------------------------

    /** Refreshes status and log on their timers. */
    private void Poll()
    {
        if (!statusClock.IsRunning || (statusClock.Elapsed > TimeSpan.FromSeconds(4) && !statusPending)) { statusClock.Restart(); RefreshStatus(); }
        if (followLog && !pauseLog && !logPending && (!logClock.IsRunning || logClock.Elapsed > TimeSpan.FromSeconds(1)))
        {
            logClock.Restart();
            logPending = true;
            long offset = follower.Offset;
            _ = Task.Run(async () =>
            {
                try
                {
                    var chunk = await server.ReadLogAsync(offset, 400).ConfigureAwait(false);
                    var lines = offset < 0 || chunk.Reset ? chunk.Lines : follower.Accept(chunk.Lines.Count > 0 ? chunk.Lines[0] : "", chunk.Next, false);
                    if (offset < 0 || chunk.Reset) follower.Accept("", chunk.Next, true);
                    lock (sync)
                    {
                        if (chunk.Reset) log.Clear();
                        log.AddRange(lines);
                        if (log.Count > MaxLogLines) log.RemoveRange(0, log.Count - MaxLogLines);
                    }
                }
                catch (Exception error) when (error is IOException or InvalidOperationException) { followLog = false; Report(false, "Log: " + error.Message, ""); }
                finally { logPending = false; }
            });
        }
    }

    /** Refreshes the server status in the background. */
    private void RefreshStatus()
    {
        statusPending = true;
        _ = Task.Run(async () =>
        {
            try { status = await server.StatusAsync().ConfigureAwait(false); }
            finally { statusPending = false; }
        });
    }

    /** Runs a server action in the background and reports its result. */
    private void Start(string label, Func<Task<ActionResult>> work)
    {
        if (busy.Length > 0) return;
        busy = label;
        _ = Task.Run(async () =>
        {
            try { var result = await work().ConfigureAwait(false); Report(result.Ok, result.Message, result.Output); }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or InvalidDataException)
            { Report(false, error.Message, ""); }
            finally { busy = ""; RefreshStatus(); }
        });
    }

    /** Records the last action result for display. */
    private void Report(bool ok, string message, string output) { lastOk = ok; lastMessage = message; lastOutput = output; }

    private void LoadBackups() => _ = Task.Run(async () =>
    {
        try { backups = await server.BackupsAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { Report(false, error.Message, ""); }
    });

    // --- header and status ------------------------------------------------------------------------

    /** Colours for good, bad and warning text. */
    private static readonly Vector4 Good = new(.45f, .85f, .45f, 1), Bad = new(.95f, .4f, .35f, 1), Warn = new(.95f, .8f, .3f, 1);

    /** Draws the page header and controls. */
    private void DrawHeader()
    {
        var current = status;
        bool running = current?.Running == true;
        Colored(current is null ? Warn : running ? Good : Bad, current is null ? "checking..." : current.Error ?? current.Summary);
        ImGui.SameLine();
        ImGui.TextDisabled($"(local WSL: {server.Options.RunDirectory})");
        ImGui.BeginDisabled(busy.Length > 0);
        if (ImGui.Button(running ? "Running" : "Start")) { if (!running) Start("Starting", () => server.StartAsync()); }
        ImGui.SameLine();
        if (ImGui.Button("Stop")) Start("Stopping", () => server.StopAsync());
        ImGui.SameLine();
        if (ImGui.Button("Restart")) Start("Restarting", () => server.RestartAsync());
        ImGui.SameLine();
        if (ImGui.Button("Selftest")) Start("Selftest running", () => server.SelftestAsync());
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Refresh")) RefreshStatus();
        if (busy.Length > 0) { ImGui.SameLine(); Colored(Warn, busy + "..."); }
        if (lastMessage.Length > 0)
        {
            Colored(lastOk ? Good : Bad, lastMessage);
            if (lastOutput.Length > 0 && ImGui.IsItemHovered()) Tip(LocalServerParse.Clip(lastOutput, 3000));
        }
        ImGui.Separator();
    }

    /** Draws the status section. */
    private void DrawStatus()
    {
        if (status is not { } current) { ImGui.TextDisabled("Reading status from WSL..."); return; }
        if (current.Error is not null) Colored(Bad, current.Error);
        Row("Process", current.Running ? $"pid {current.Pid}, up {LocalServerParse.Duration(current.UptimeSeconds ?? 0)}" : "not running", current.Running);
        if (current.ForeignInstance) Colored(Warn, $"A sol-server from {current.WorkingDirectory} is running, not {server.Options.RunDirectory}.");
        Row($"BAP UDP {server.Options.BapPort}", current.BapUdp ? "listening" : "closed", current.BapUdp);
        Row($"BAP TCP {server.Options.BapPort}", current.BapTcp ? "listening" : "closed", current.BapTcp);
        Row($"Web HTTP {server.Options.WebPort}", current.Web ? "listening" : "closed", current.Web);
        Row($"Admin HTTP {server.Options.AdminPort}", current.Admin ? "listening (loopback)" : "closed", current.Admin);
        Row("Players", current.Players?.ToString() ?? "?", true);
        foreach (string name in current.PlayerNames) Bullet(name);
        var census = current.Census;
        Row("Census", $"{census.Sessions} session(s), {census.Members} member(s)", true);
        if (census.Ghosts.Count == 0) Row("Ghost check", "ok: no member twice in a session", true);
        else
        {
            Row("Ghost check", $"{census.Ghosts.Count} ghost member(s): events will not spawn", false);
            foreach (var ghost in census.Ghosts) Bullet($"{ghost.Member} x{ghost.Count} in session {ghost.Session}");
            if (ImGui.Button("Restart to clear ghosts")) Start("Restarting", () => server.RestartAsync());
        }
        if (census.SplitMembers.Count > 0) ImGui.TextDisabled("In several sessions: " + string.Join(", ", census.SplitMembers));
        if (current.Sockets.Count > 0 && ImGui.TreeNode("All sockets"))
        {
            foreach (var socket in current.Sockets) Bullet($"{socket.Protocol} {socket.Address}:{socket.Port}");
            ImGui.TreePop();
        }
    }

    // Server text can hold '%', so nothing here goes through ImGui's printf-style calls.
    private static void Colored(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /** Draws wrapped text. */
    private static void Wrapped(string text) { ImGui.PushTextWrapPos(); ImGui.TextUnformatted(text); ImGui.PopTextWrapPos(); }
    /** Draws a bullet line. */
    private static void Bullet(string text) { ImGui.Bullet(); ImGui.SameLine(); ImGui.TextUnformatted(text); }
    /** Draws a tooltip with wrapped text. */
    private static void Tip(string text) { ImGui.BeginTooltip(); ImGui.PushTextWrapPos(600); ImGui.TextUnformatted(text); ImGui.PopTextWrapPos(); ImGui.EndTooltip(); }

    /** Draws a label/value row coloured by ok. */
    private static void Row(string label, string value, bool ok)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(180);
        Colored(ok ? Good : Bad, value);
    }

    // --- log ------------------------------------------------------------------------------------------

    /** Draws the server log with filter. */
    private void DrawLog()
    {
        ImGui.SetNextItemWidth(320);
        ImGui.InputTextWithHint("##log-filter", "filter: terms, -exclude, re:regex", ref logFilter, 256);
        ImGui.SameLine();
        ImGui.Checkbox("Follow", ref followLog);
        ImGui.SameLine();
        ImGui.Checkbox("Pause", ref pauseLog);
        ImGui.SameLine();
        if (ImGui.Button("Clear")) lock (sync) log.Clear();
        ImGui.SameLine();
        if (ImGui.Button("Errors")) logFilter = "re:error|fail|warn|crash";
        ImGui.SameLine();
        if (ImGui.Button("Census")) logFilter = "stage=census";
        string[] rows;
        var match = LocalServerParse.Filter(logFilter);
        lock (sync) rows = log.Where(match).ToArray();
        if (ImGui.BeginChild("local-server-log", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            foreach (string row in rows)
            {
                bool error = row.Contains("level=error", StringComparison.Ordinal) || row.Contains("FAIL", StringComparison.Ordinal);
                bool warn = row.Contains("level=warn", StringComparison.Ordinal);
                if (error || warn) Colored(error ? Bad : Warn, row); else ImGui.TextUnformatted(row);
            }
            if (!pauseLog && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20) ImGui.SetScrollHereY(1);
        }
        ImGui.EndChild();
    }

    // --- deploy -------------------------------------------------------------------------------------

    /** Draws the deploy section. */
    private void DrawDeploy(string? projectRoot, string? lastBuild)
    {
        if (projectRoot is null) { ImGui.TextDisabled("Open a project to deploy its scripts, sdk and build to the local server."); return; }
        Wrapped("Copies the project's scripts/ and sdk/, plus activity_sdk.pack and build_data.bin from the newest build " +
            $"(or the project root), into {server.Options.SunriseDirectory}. Overwritten files are backed up to {server.Options.BackupDirectory} first.");
        bool rescan = planProject != projectRoot;
        if (ImGui.Button("Re-scan project")) rescan = true;
        if (rescan)
        {
            planProject = projectRoot;
            try { plan = DeployPlanner.Plan(projectRoot, lastBuild is not null && Directory.Exists(lastBuild) ? lastBuild : null); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { plan = null; Report(false, error.Message, ""); return; }
        }
        if (plan is null) return;
        ImGui.TextUnformatted($"{plan.Files.Count} file(s) from {plan.ProjectRoot}" + (plan.BuildDirectory is null ? "" : $" (build {Path.GetFileName(plan.BuildDirectory)})"));
        foreach (var group in plan.Files.GroupBy(file => file.Target.Split('/')[0])) Bullet($"{group.Key}: {group.Count()}");
        foreach (string note in plan.Notes) ImGui.TextDisabled(note);
        ImGui.Checkbox("Restart the server afterwards", ref restartAfterDeploy);
        ImGui.BeginDisabled(busy.Length > 0 || plan.Files.Count == 0);
        var current = plan;
        if (ImGui.Button("Deploy to local server")) Start("Deploying", async () =>
        {
            var result = await server.DeployAsync(current, restartAfterDeploy).ConfigureAwait(false);
            LoadBackups();
            return result;
        });
        ImGui.EndDisabled();
        ImGui.Separator();
        ImGui.TextUnformatted("Backups");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh##backups")) LoadBackups();
        foreach (var entry in backups.Take(20))
        {
            ImGui.TextUnformatted($"{entry.Id}  {entry.Files} file(s)  {(entry.RolledBack ? "rolled back" : "active")}");
            if (ImGui.IsItemHovered() && entry.Project.Length > 0) Tip(entry.Project);
            if (!entry.RolledBack)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(busy.Length > 0);
                string id = entry.Id;
                if (ImGui.SmallButton("Roll back##" + id)) Start("Rolling back", async () =>
                {
                    var result = await server.RollbackAsync(id, restartAfterDeploy).ConfigureAwait(false);
                    LoadBackups();
                    return result;
                });
                ImGui.EndDisabled();
            }
        }
    }

    // --- config ---------------------------------------------------------------------------------------

    /** Draws the config section. */
    private void DrawConfig()
    {
        if (ImGui.Button("List files")) _ = Task.Run(async () =>
        {
            try { configFiles = await server.ConfigFilesAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or InvalidOperationException) { Report(false, error.Message, ""); }
        });
        var files = configFiles;
        for (int i = 0; i < files.Count; i++)
        {
            ImGui.SameLine();
            string name = files[i];
            if (ImGui.RadioButton(name.Replace("config/", "", StringComparison.Ordinal), selectedConfig == i))
            {
                selectedConfig = i;
                _ = Task.Run(async () =>
                {
                    try { configText = await server.ReadConfigAsync(name).ConfigureAwait(false); configLoaded = name; }
                    catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or FormatException) { Report(false, error.Message, ""); }
                });
            }
        }
        if (configLoaded.Length == 0) { ImGui.TextDisabled("Pick a file. Changes apply after a restart."); return; }
        string? problem = LocalServerManager.ConfigProblem(configLoaded, configText);
        ImGui.TextUnformatted(configLoaded);
        ImGui.SameLine();
        ImGui.BeginDisabled(busy.Length > 0 || problem is not null);
        string path = configLoaded, text = configText;
        if (ImGui.Button("Save (backs up the old file)")) Start("Saving " + path, () => server.WriteConfigAsync(path, text));
        ImGui.EndDisabled();
        if (problem is not null) Colored(Bad, problem);
        ImGui.InputTextMultiline("##config-text", ref configText, 1 << 20, new Vector2(-1, -1));
    }

    // --- actions ----------------------------------------------------------------------------------------

    /** Draws the action buttons (start, stop, restart, selftest...). */
    private void DrawActions()
    {
        Wrapped("Runs server commands as the operator console through the local admin API (127.0.0.1:" +
            $"{server.Options.AdminPort}). 'help' lists every command.");
        ImGui.SetNextItemWidth(420);
        bool enter = ImGui.InputTextWithHint("##command", "who | give <player> glimmer 50000 | activity find <text>", ref commandLine, 512,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        ImGui.BeginDisabled(busy.Length > 0 || commandLine.Trim().Length == 0);
        string line = commandLine;
        if (ImGui.Button("Run") || enter) Start("/" + line, () => server.CommandAsync(line));
        ImGui.EndDisabled();
        ImGui.Separator();
        ImGui.SetNextItemWidth(200); ImGui.InputTextWithHint("##qa-player", "player", ref player, 128);
        ImGui.SameLine(); ImGui.SetNextItemWidth(220); ImGui.InputTextWithHint("##qa-item", "item name or hash", ref item, 256);
        ImGui.SameLine(); ImGui.SetNextItemWidth(110); ImGui.InputInt("amount", ref amount);
        ImGui.SetNextItemWidth(420); ImGui.InputTextWithHint("##qa-target", "activity (name, index, find <text>) or place", ref target, 256);
        ImGui.BeginDisabled(busy.Length > 0);
        if (ImGui.Button("Give item")) Quick("give");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150); ImGui.InputTextWithHint("##qa-character", "class or slot 1-3", ref character, 16);
        ImGui.SameLine();
        if (ImGui.Button("Reset character (all gear, level, progress)")) Quick("reset_character");
        ImGui.SameLine();
        if (ImGui.Button("Set next activity")) Quick("set_activity");
        ImGui.SameLine();
        if (ImGui.Button("Go to place")) Quick("goto");
        ImGui.EndDisabled();
        if (lastOutput.Length > 0)
        {
            ImGui.Separator();
            Wrapped(LocalServerParse.Clip(lastOutput, 4000));
        }
    }

    /** Runs a quick action by name. */
    private void Quick(string action)
    {
        var args = new System.Text.Json.Nodes.JsonObject
        {
            ["action"] = action, ["player"] = player, ["item"] = item, ["amount"] = amount, ["target"] = target, ["character"] = character,
        };
        var (line, problem) = LocalServerTools.QuickLine(args);
        if (problem is not null) { Report(false, problem, ""); return; }
        Start("/" + line, () => server.CommandAsync(line!));
    }

    // --- subsystems -----------------------------------------------------------------------------------

    /** Draws the subsystems section. */
    private void DrawSubsystems()
    {
        Wrapped("Demonware (Activision) did sign-in, lobbies, NAT relay and title storage and no gameplay; Bungie's Activity " +
            "Host and Physics Host ran the game. The sol-server does both in one process.");
        if (!ImGui.BeginTable("local-server-subsystems", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable)) return;
        ImGui.TableSetupColumn("Subsystem");
        ImGui.TableSetupColumn("Up");
        ImGui.TableSetupColumn("Replaces / endpoint");
        ImGui.TableSetupColumn("What it does");
        ImGui.TableHeadersRow();
        var current = status;
        foreach (var subsystem in ServerSubsystems.All)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(subsystem.Name);
            ImGui.TableNextColumn();
            bool? up = current is null ? null : ServerSubsystems.IsUp(subsystem, current);
            Colored(up is null ? Warn : up.Value ? Good : Bad, up is null ? "?" : up.Value ? "yes" : "no");
            ImGui.TableNextColumn(); Wrapped(subsystem.Replaces + "\n" + subsystem.Endpoint);
            ImGui.TableNextColumn(); Wrapped(subsystem.Role);
            if (ImGui.IsItemHovered()) Tip("Source: " + subsystem.Source);
        }
        ImGui.EndTable();
    }
}
