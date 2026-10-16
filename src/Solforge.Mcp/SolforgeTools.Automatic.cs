/**
 * File: MCP tools for automatic mode (AutomaticWatchdog.cs): automatic_start, automatic_status, automatic_stop for any
 * client, and goal_progress, goal_complete, goal_blocked for the engine working on the goal. None needs an approve click
 * and all work while context publishing is paused; they only steer the watchdog, never the project.
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** Partial: the automatic-mode tools (automatic_start, goal_progress, goal_complete, goal_blocked). */
public static partial class SolforgeTools
{
    /** Shared by the server instructions and the session file: the AI may put itself into automatic mode. */
    public const string AutomaticHint =
        "Automatic mode: for a long multi-step goal you can put yourself into automatic mode with automatic_start (goal); Solforge then " +
        "nudges you to continue whenever you go idle until you call goal_complete (summary, evidence) or goal_blocked (reason). Report " +
        "each step with goal_progress (done, remaining). automatic_status shows it and automatic_stop ends it. Only one goal runs at a " +
        "time. The user can also type /automatic <goal>, /automatic status or /automatic stop in Solforge's AI terminal; an engine " +
        "cannot type that line itself, so use the tools.";

    /** The automatic-mode tool names; they skip the approve click and the pause gate. */
    public static readonly string[] AutomaticTools =
        ["automatic_start", "automatic_status", "automatic_stop", "goal_progress", "goal_complete", "goal_blocked"];

    /** Yields the automatic-mode tools; each reports an error when this Solforge has no watchdog. */
    private static IEnumerable<McpTool> AutomaticModeTools(ISolforgeHost host)
    {
        McpTool Tool(string name, string description, JsonObject schema, Func<AutomaticWatchdog, JsonObject, McpToolResult> run) =>
            new(name, description, schema, false, (args, _) => Task.FromResult(host.Automatic is { } watchdog
                ? run(watchdog, args) : McpToolResult.Error("This Solforge has no automatic mode.")));
        static JsonObject Schema(params (string, string, string, bool)[] properties) => McpJson.Schema(properties);
        static string Arg(JsonObject args, string name) => McpJson.String(args, name)?.Trim() ?? "";
        static McpToolResult Status(AutomaticWatchdog watchdog, string? note = null)
        {
            var json = watchdog.ToJson();
            if (note is not null) json["note"] = note;
            return McpToolResult.Json(json);
        }

        yield return Tool("automatic_start", "Put the AI into automatic mode for a long multi-step goal: Solforge keeps nudging the engine " +
            "in its AI terminal to continue whenever it goes idle, until goal_complete or goal_blocked is called or the limits in " +
            "Settings > AI (nudges, hours) run out. Use it on yourself when a goal needs many steps. No approve click. Refuses while " +
            "another goal runs unless replace is true. The result holds the working instructions.",
            Schema(("goal", "string", "What must be true when done, specific enough to verify", true),
                ("replace", "boolean", "Replace a goal that is already running (default false)", false),
                ("send_prompt", "boolean", "Also type the goal into the AI terminal now (default false: you already have it; " +
                    "true when an external client starts the terminal's engine on a goal)", false)),
            (watchdog, args) =>
            {
                string goal = Arg(args, "goal");
                if (goal.Length == 0) return McpToolResult.Error("Give a goal.");
                if (watchdog.Start(goal, "mcp", McpJson.Bool(args, "send_prompt", false), McpJson.Bool(args, "replace", false)) is { } problem)
                    return McpToolResult.Error(problem);
                return Status(watchdog, AutomaticWatchdog.StartPrompt(goal));
            });
        yield return Tool("automatic_status", "Automatic mode now: state (off, running, complete, blocked, stopped, limit), goal, nudges " +
            "and the limit, elapsed seconds, the last nudge, reported progress and remaining steps, and how it ended. No approve click.",
            Schema(), (watchdog, _) => Status(watchdog));
        yield return Tool("automatic_stop", "End automatic mode now (no more nudges). No approve click.",
            Schema(("reason", "string", "Why (optional)", false)),
            (watchdog, args) => watchdog.Stop(Arg(args, "reason") is { Length: > 0 } reason ? reason : "Stopped over MCP.")
                ? Status(watchdog, "Stopped.") : Status(watchdog, "Automatic mode was not running."));
        yield return Tool("goal_progress", "Automatic mode: report a finished step and what is left; the next nudge quotes it back. " +
            "No approve click.",
            Schema(("done", "string", "What is done and verified so far", true), ("remaining", "string", "The steps still left", false)),
            (watchdog, args) => Arg(args, "done") is { Length: > 0 } done
                ? watchdog.Progress(done, Arg(args, "remaining")) ? Status(watchdog, "Noted.") : McpToolResult.Error("Automatic mode is not running.")
                : McpToolResult.Error("Say what is done."));
        yield return Tool("goal_complete", "Automatic mode: the goal is achieved AND verified. Ends the nudges. Give a summary and the " +
            "evidence (checks run, output, log lines, screenshots looked at). No approve click.",
            Schema(("summary", "string", "What was done", true), ("evidence", "string", "How you verified it", true)),
            (watchdog, args) =>
            {
                string summary = Arg(args, "summary"), evidence = Arg(args, "evidence");
                if (summary.Length == 0 || evidence.Length == 0) return McpToolResult.Error("Give both a summary and the evidence that it works.");
                return watchdog.Complete(summary, evidence) ? Status(watchdog, "Goal complete.") : McpToolResult.Error("Automatic mode is not running.");
            });
        yield return Tool("goal_blocked", "Automatic mode: you cannot go on without the user (a decision, login, hardware or access). Ends " +
            "the nudges and shows the reason in Solforge. No approve click.",
            Schema(("reason", "string", "What you need from the user", true)),
            (watchdog, args) => Arg(args, "reason") is { Length: > 0 } reason
                ? watchdog.Block(reason) ? Status(watchdog, "Blocked; the user will see the reason.") : McpToolResult.Error("Automatic mode is not running.")
                : McpToolResult.Error("Say what you need."));
    }
}
