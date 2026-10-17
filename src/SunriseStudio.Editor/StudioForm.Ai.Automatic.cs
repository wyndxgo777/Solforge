/**
 * File: the AI workspace side of /automatic (Solforge.Mcp/AutomaticWatchdog.cs). Catches "/automatic ..." typed in the AI
 * terminal (TerminalView.LineCommand) or sent from the remote page (StudioForm.Ai.Remote.cs) before the engine sees it, feeds
 * the watchdog screen observations from the auto-allow timer, types its prompts and nudges, draws the status strip under the
 * AI header, offers to resume a goal saved by the last run, and adds Settings > AI rows. The MCP tools live in
 * Solforge.Mcp/SolforgeTools.Automatic.cs; get_editor_state and the remote AI state carry ToJson().
 */
using System.Text.Json.Nodes;
using ImGuiNET;
using Solforge.Mcp;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** StudioForm part: /automatic goal mode glue for the AI terminal (see file header). */
public sealed partial class StudioForm
{
    private AutomaticWatchdog? aiAutomatic;
    /** A goal the last Solforge left running, offered once (Resume / Discard); checked on the first strip draw. */
    private AutomaticSaved? aiAutomaticSaved;
    private bool aiAutomaticChecked, aiAutomaticHidden;
    /** The last /automatic command's answer, shown in the strip. */
    private string? aiAutomaticNote;
    /** Clock seconds when the pasted message gets its Enter (0 = none pending), the last tick, and the screen's quiet start. */
    private double aiAutomaticEnterAt, aiAutomaticLastTick, aiAutomaticQuietSince;
    private long aiAutomaticRevision = -1;

    /** The watchdog; made on first use from the UI or an MCP thread. */
    private AutomaticWatchdog AiAutomatic
    {
        get
        {
            if (aiAutomatic is { } existing) return existing;
            var made = new AutomaticWatchdog(() => DateTimeOffset.Now, AiAutomaticOptions, AiAutomaticSavePath);
            made.Changed += () => aiAutomaticHidden = false;
            return Interlocked.CompareExchange(ref aiAutomatic, made, null) ?? made;
        }
    }

    /** automatic-goal.json beside the settings file. */
    private string AiAutomaticSavePath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "automatic-goal.json");

    private AutomaticOptions AiAutomaticOptions() =>
        new(settings.AiAutomaticMaxNudges, settings.AiAutomaticMaxMinutes, settings.AiAutomaticIdleSeconds);

    /** TerminalView.LineCommand: true when the typed line was an /automatic command (it never reaches the engine). */
    private bool AiAutomaticLine(string line)
    {
        if (AutomaticWatchdog.ParseCommand(line) is not { } command) return false;
        AiAutomaticCommand(command.Verb, command.Goal);
        return true;
    }

    /** UI thread: runs an /automatic command from the terminal or the remote page; returns the note it shows. */
    private string AiAutomaticCommand(string verb, string goal)
    {
        aiAutomaticSaved = null;
        string note = AiAutomatic.RunCommand(verb, goal);
        aiAutomaticNote = note;
        aiAutomaticHidden = false;
        aiAttention.Record(AttentionKind.Open, "automatic", "/automatic " + (verb == "start" ? goal : verb));
        return note;
    }

    /**
     * From the 100 ms auto-allow timer, so it runs while Solforge is minimized: sends the pending Enter, looks at the screen
     * about once a second for the watchdog, and pastes its next message once the screen has been still for 1.5 s (so a
     * resume prompt waits for the engine to finish starting).
     */
    private void AutomaticTickAi()
    {
        if (aiAutomatic is not { } watchdog) return;
        double now = clock.Elapsed.TotalSeconds;
        var terminal = aiTerminal.Terminal;
        bool running = terminal is { HasExited: false } && !aiTerminal.Stopped && aiInstalling is null;
        if (aiAutomaticEnterAt > 0 && now >= aiAutomaticEnterAt)
        {
            aiAutomaticEnterAt = 0;
            if (running) aiTerminal.Submit();
        }
        if (!watchdog.Active && !watchdog.HasOutgoing) return;
        if (!running || terminal is null)
        {
            if (now - aiAutomaticLastTick >= 1) { aiAutomaticLastTick = now; watchdog.Tick(new AutomaticObservation(false, 0, "", false)); }
            return;
        }
        long revision = terminal.Screen.Revision;
        if (revision != aiAutomaticRevision) { aiAutomaticRevision = revision; aiAutomaticQuietSince = now; }
        if (now - aiAutomaticLastTick >= 1)
        {
            aiAutomaticLastTick = now;
            watchdog.Tick(new AutomaticObservation(true, revision, string.Join("\n", terminal.Screen.Tail(30)), aiAllowPrompt is not null));
        }
        if (aiAutomaticEnterAt == 0 && now - aiAutomaticQuietSince >= 1.5 && watchdog.TakeOutgoing() is { } message)
        {
            aiTerminal.Paste(message);
            // Claude Code drops keys that arrive with a paste, so Enter follows a little later.
            aiAutomaticEnterAt = now + .4;
        }
    }

    /** The strip under the AI header: goal, nudge count, elapsed, last nudge and state; Resume/Discard for a saved goal. */
    private void DrawAiAutomaticStrip()
    {
        if (!aiAutomaticChecked)
        {
            aiAutomaticChecked = true;
            aiAutomaticSaved = AutomaticWatchdog.LoadSaved(AiAutomaticSavePath);
        }
        if (aiAutomaticSaved is { } saved && !AiAutomatic.Active)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(SolTheme.Sun, "Resume automatic goal?");
            ImGui.SameLine();
            ImGui.TextWrapped($"\"{saved.Goal}\" ({saved.Nudges} nudges so far)");
            if (UiAuto.SmallButton("Resume##ai-auto-resume"))
            {
                AiAutomatic.Start(saved.Goal, "resume", sendPrompt: true, replace: true, resume: saved);
                aiAutomaticSaved = null;
            }
            ImGui.SameLine();
            if (UiAuto.SmallButton("Discard##ai-auto-discard"))
            {
                AutomaticWatchdog.DeleteSaved(AiAutomaticSavePath);
                aiAutomaticSaved = null;
            }
            ImGui.Separator();
            return;
        }
        if (aiAutomatic is not { } watchdog || aiAutomaticHidden) return;
        var state = watchdog.State;
        if (state == AutomaticState.Off && aiAutomaticNote is null) return;
        var color = state switch
        {
            AutomaticState.Running => SolTheme.Sun,
            AutomaticState.Complete => SolTheme.Ok,
            AutomaticState.Blocked or AutomaticState.Limit => SolTheme.Bad,
            _ => SolTheme.Dim
        };
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(state == AutomaticState.Off ? aiAutomaticNote! : watchdog.StatusLine());
        ImGui.PopStyleColor();
        if (state != AutomaticState.Off && watchdog.ToJson() is { } json)
        {
            string? detail = (string?)json["progress"] is { Length: > 0 } progress ? "Progress: " + progress
                + ((string?)json["remaining"] is { Length: > 0 } left ? "  |  Left: " + left : "") : null;
            if ((string?)json["evidence"] is { Length: > 0 } evidence) detail = "Evidence: " + evidence;
            if (detail is not null) ImGui.TextDisabled(detail);
        }
        StudioUi.Tip("/automatic <goal> in the terminal starts this; /automatic stop ends it; the AI can also start it with automatic_start. "
            + "Limits are in Settings > AI.");
        if (state == AutomaticState.Running)
        {
            if (UiAuto.SmallButton("Stop automatic##ai-auto-stop")) AiAutomaticCommand("stop", "");
        }
        else if (UiAuto.SmallButton("Hide##ai-auto-hide")) { aiAutomaticHidden = true; aiAutomaticNote = null; }
        ImGui.Separator();
    }

    /** Settings > AI rows for automatic mode. */
    private void DrawAiAutomaticSettings()
    {
        ImGui.TextDisabled("Automatic mode (/automatic <goal>)");
        int nudges = settings.AiAutomaticMaxNudges, minutes = settings.AiAutomaticMaxMinutes, idle = settings.AiAutomaticIdleSeconds;
        ImGui.SetNextItemWidth(160 * scale);
        if (UiAuto.SliderInt("Max nudges", ref nudges, 1, 500)) settings.AiAutomaticMaxNudges = nudges;
        if (ImGui.IsItemDeactivatedAfterEdit()) SaveSettings();
        ImGui.SetNextItemWidth(160 * scale);
        if (UiAuto.SliderInt("Max duration (minutes)", ref minutes, 5, 1440)) settings.AiAutomaticMaxMinutes = minutes;
        if (ImGui.IsItemDeactivatedAfterEdit()) SaveSettings();
        ImGui.SetNextItemWidth(160 * scale);
        if (UiAuto.SliderInt("Idle seconds before a nudge", ref idle, 5, 600)) settings.AiAutomaticIdleSeconds = idle;
        if (ImGui.IsItemDeactivatedAfterEdit()) SaveSettings();
        SettingNote("Type /automatic <goal> in the AI terminal (or the remote page's AI chat): Solforge sends the goal, answers one-time "
            + "allow prompts, and nudges the engine to continue whenever its screen stays still this long, until it calls goal_complete "
            + "or goal_blocked or a limit is hit. /automatic status and /automatic stop. The AI can start it itself with automatic_start.");
    }

    /** get_editor_state and the remote AI state: automatic mode as JSON (state off when it never ran). */
    private JsonObject AiAutomaticJson() => aiAutomatic?.ToJson() ?? new JsonObject { ["state"] = "off" };

    private sealed partial class AiHost
    {
        /** ISolforgeHost: the /automatic watchdog for the automatic_* and goal_* tools. */
        public AutomaticWatchdog? Automatic => Owner.AiAutomatic;
    }
}
