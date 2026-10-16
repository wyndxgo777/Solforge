// Automatic mode checks: the AutomaticWatchdog state machine on a fake clock and fake screen observations (no engine), the
// /automatic command parser, the saved goal, and the automatic_* / goal_* MCP tools with no approve click.
using System.Text.Json.Nodes;
using Solforge.Mcp;

static class AutomaticChecks
{
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("automatic: " + message);
    }

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static async Task Run(string scratch, FixtureHost host)
    {
        Directory.CreateDirectory(scratch);
        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var options = new AutomaticOptions(MaxNudges: 4, MaxMinutes: 60, IdleSeconds: 30, StuckRounds: 2);
        string save = Path.Combine(scratch, "automatic-goal.json");
        var watchdog = new AutomaticWatchdog(() => now, () => options, save);
        long revision = 1;
        void Look(string tail = "screen", bool prompt = false, bool running = true) =>
            watchdog.Tick(new AutomaticObservation(running, revision, tail, prompt));
        void Wait(double seconds, string tail = "screen", bool prompt = false)
        {
            // One look per 5 s, like a slow UI timer, so idle is measured across several ticks.
            for (double t = 0; t < seconds; t += 5) { now = now.AddSeconds(5); Look(tail, prompt); }
        }

        // Parser.
        Expect(AutomaticWatchdog.ParseCommand("/automatic build the strike") == ("start", "build the strike"), "parse start");
        Expect(AutomaticWatchdog.ParseCommand("  /Automatic stop ") == ("stop", ""), "parse stop");
        Expect(AutomaticWatchdog.ParseCommand("/automatic") == ("status", "") && AutomaticWatchdog.ParseCommand("/automatic status") == ("status", ""), "parse status");
        Expect(AutomaticWatchdog.ParseCommand("/automaticity") is null && AutomaticWatchdog.ParseCommand("/agents") is null
            && AutomaticWatchdog.ParseCommand("fix /automatic later") is null, "other text is not a command");

        // Start: typed goals queue the prompt; nothing else happens until idle.
        Expect(watchdog.State == AutomaticState.Off && watchdog.TakeOutgoing() is null, "starts off");
        Expect(watchdog.Start("", "typed", true) is not null, "empty goal refused");
        Expect(watchdog.RunCommand("start", "make the EDZ event spawn") == "Automatic mode started: make the EDZ event spawn", "typed start");
        string? first = watchdog.TakeOutgoing();
        Expect(first is not null && first.Contains("make the EDZ event spawn") && first.Contains("goal_complete") && first.Contains("goal_blocked")
            && first.Contains("goal_progress"), "start prompt: " + first);
        Expect(File.Exists(save) && AutomaticWatchdog.LoadSaved(save)?.Goal == "make the EDZ event spawn", "running goal saved");

        // A busy engine (screen changing) or an allow prompt on screen is never nudged.
        for (int i = 0; i < 20; i++) { now = now.AddSeconds(5); revision++; Look(); }
        Expect(watchdog.TakeOutgoing() is null && watchdog.Nudges == 0, "busy engine not nudged");
        Wait(60, prompt: true);
        Expect(watchdog.TakeOutgoing() is null, "allow prompt waits for auto-allow");
        Look(running: false);
        now = now.AddSeconds(120);
        Look(running: false);
        Expect(watchdog.TakeOutgoing() is null && watchdog.ToJson()["waitingForEngine"]?.GetValue<bool>() == true, "stopped engine waits");

        // Idle with progress: a continue nudge quoting the goal, progress and remaining steps.
        revision++;
        Look("step 1 output");
        watchdog.NoteToolCall("get_editor_state");
        Expect(watchdog.Progress("found the event script", "edit spawn table; reload; verify in game"), "progress noted");
        Wait(30, "step 1 output");
        string? nudge = watchdog.TakeOutgoing();
        Expect(nudge is not null && nudge.Contains("nudge 1") && nudge.Contains("make the EDZ event spawn") && nudge.Contains("found the event script")
            && nudge.Contains("edit spawn table") && nudge.Contains("Continue") && !nudge.Contains('\n'), "idle nudge: " + nudge);
        Expect(watchdog.Nudges == 1 && (string?)watchdog.ToJson()["lastNudge"] == "continue", "nudge counted");
        Wait(25, "step 1 output");
        Expect(watchdog.TakeOutgoing() is null, "no second nudge inside the idle window");

        // Same screen again: a repeat nudge; a second no-progress round asks for a new tactic.
        Wait(10, "step 1 output");
        string? repeat = watchdog.TakeOutgoing();
        Expect(repeat is not null && repeat.Contains("same as at the last nudge"), "repeat nudge: " + repeat);
        revision++;
        Look("different text, no tools");
        Wait(30, "different text, no tools");
        string? tactic = watchdog.TakeOutgoing();
        Expect(tactic is not null && tactic.Contains("Change tactic") && watchdog.ToJson()["tacticChanges"]?.GetValue<int>() == 1, "stuck nudge: " + tactic);

        // Nesting guard: another start is refused with a clear message; replace swaps it.
        string? refused = watchdog.Start("something else", "mcp", false);
        Expect(refused is not null && refused.Contains("already running") && watchdog.Goal == "make the EDZ event spawn", "nesting refused: " + refused);

        // goal_complete ends it, clears the saved goal and stops nudging.
        Expect(watchdog.Complete("event spawns", "server log: event_spawned x3"), "complete");
        Expect(watchdog.State == AutomaticState.Complete && !File.Exists(save), "complete clears the save");
        Wait(120);
        Expect(watchdog.TakeOutgoing() is null && !watchdog.Complete("again", "x"), "nothing after complete");
        Expect(watchdog.StatusLine().StartsWith("AUTOMATIC COMPLETE", StringComparison.Ordinal), "status line: " + watchdog.StatusLine());

        // goal_blocked and stop.
        Expect(watchdog.Start("goal two", "mcp", sendPrompt: false) is null && watchdog.TakeOutgoing() is null, "engine-started goal sends no prompt");
        Expect(watchdog.Block("needs Seth to sign in") && watchdog.State == AutomaticState.Blocked
            && (string?)watchdog.ToJson()["result"] == "needs Seth to sign in", "blocked");
        Expect(watchdog.Start("goal three", "typed", true) is null, "restart after blocked");
        watchdog.TakeOutgoing();
        Expect(watchdog.RunCommand("stop", "") == "Automatic mode stopped." && watchdog.State == AutomaticState.Stopped, "typed stop");
        Expect(watchdog.RunCommand("stop", "") == "Automatic mode was not running.", "stop twice");

        // Nudge limit.
        Expect(watchdog.Start("goal four", "mcp", false) is null, "start four");
        for (int i = 0; i < 4; i++)
        {
            revision++;
            Look("round " + i);
            Wait(30, "round " + i);
            Expect(watchdog.TakeOutgoing() is not null, "limit round " + i);
        }
        revision++;
        Look("last");
        Wait(30, "last");
        Expect(watchdog.State == AutomaticState.Limit && ((string?)watchdog.ToJson()["result"])?.Contains("Nudge limit") == true, "nudge limit");

        // Time limit.
        Expect(watchdog.Start("goal five", "mcp", false) is null, "start five");
        now = now.AddMinutes(61);
        Look();
        Expect(watchdog.State == AutomaticState.Limit && ((string?)watchdog.ToJson()["result"])?.Contains("Time limit") == true, "time limit");

        // Resume a saved goal after a restart: a resume nudge goes first, counters carry on.
        var restored = new AutomaticWatchdog(() => now, () => options, save);
        Expect(restored.Start("goal six", "typed", true) is null, "start six");
        restored.TakeOutgoing();
        restored.Progress("half done", "the rest");
        var saved = AutomaticWatchdog.LoadSaved(save);
        Expect(saved is { Goal: "goal six", Progress: "half done", Remaining: "the rest" }, "saved progress");
        var next = new AutomaticWatchdog(() => now, () => options, save);
        Expect(next.Start(saved!.Goal, "resume", true, resume: saved) is null, "resume");
        string? resumed = next.TakeOutgoing();
        Expect(resumed is not null && resumed.Contains("restarted") && resumed.Contains("half done"), "resume nudge: " + resumed);
        next.Stop();
        Expect(AutomaticWatchdog.LoadSaved(save) is null, "stop clears the save");

        // The MCP tools: no approval, work while paused, guard nesting, report status.
        var tools = SolforgeTools.Create(host).Where(tool => SolforgeTools.AutomaticTools.Contains(tool.Name)).ToDictionary(tool => tool.Name);
        Expect(tools.Count == SolforgeTools.AutomaticTools.Length && tools.Values.All(tool => !tool.RequiresApproval), "tools need no approval");
        var none = await tools["automatic_status"].Handler(new JsonObject(), CancellationToken.None);
        Expect(none.IsError, "no watchdog is an error");
        host.Automatic = new AutomaticWatchdog(() => now, () => options);
        host.Paused = true;
        async Task<McpToolResult> Call(string name, JsonObject args) => await tools[name].Handler(args, CancellationToken.None);
        var started = await Call("automatic_start", new JsonObject { ["goal"] = "ship the raid" });
        Expect(!started.IsError && started.Texts[0].Contains("goal_complete") && host.Automatic.TakeOutgoing() is null, "automatic_start: " + started.Texts[0]);
        var nested = await Call("automatic_start", new JsonObject { ["goal"] = "other" });
        Expect(nested.IsError && nested.Texts[0].Contains("already running"), "automatic_start nesting");
        var replaced = await Call("automatic_start", new JsonObject { ["goal"] = "other", ["replace"] = true, ["send_prompt"] = true });
        Expect(!replaced.IsError && host.Automatic.Goal == "other" && host.Automatic.TakeOutgoing() is not null, "automatic_start replace + send_prompt");
        Expect(!(await Call("goal_progress", new JsonObject { ["done"] = "a", ["remaining"] = "b" })).IsError, "goal_progress");
        var status = JsonNode.Parse((await Call("automatic_status", new JsonObject())).Texts[0])!;
        Expect((string?)status["state"] == "running" && (string?)status["progress"] == "a" && (string?)status["remaining"] == "b", "automatic_status: " + status.ToJsonString());
        Expect((await Call("goal_complete", new JsonObject { ["summary"] = "done" })).IsError, "goal_complete needs evidence");
        Expect(!(await Call("goal_complete", new JsonObject { ["summary"] = "done", ["evidence"] = "checks pass" })).IsError
            && host.Automatic.State == AutomaticState.Complete, "goal_complete");
        Expect((await Call("goal_blocked", new JsonObject { ["reason"] = "x" })).IsError, "goal_blocked when not running");
        await Call("automatic_start", new JsonObject { ["goal"] = "again" });
        Expect(!(await Call("goal_blocked", new JsonObject { ["reason"] = "need a login" })).IsError && host.Automatic.State == AutomaticState.Blocked, "goal_blocked");
        await Call("automatic_start", new JsonObject { ["goal"] = "again" });
        Expect(!(await Call("automatic_stop", new JsonObject())).IsError && host.Automatic.State == AutomaticState.Stopped, "automatic_stop");
        host.Paused = false;
        host.Automatic = null;
    }
}
