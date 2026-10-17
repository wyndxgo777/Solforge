/**
 * File: AI workspace UI (engine picker, terminal, context dropdown). Related: StudioForm.Ai.Host.cs (ISolforgeHost
 * implementation, UI-thread marshalling), .Ai.Query.cs (project reads), .Ai.Assets.cs (shader/animation/audio reads),
 * .Ai.Media.cs (havok/audio/video), .Ai.Stream.cs (viewport frames), StudioForm.PhoneStream.cs (stream to phone);
 * tools themselves are in Solforge.Mcp; terminal in Ai/.
 */
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using Solforge.Mcp;
using SunriseStudio.Editor.Ai;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * Solforge Phase 47B: the AI workspace (Studio.md §3.8). An engine picker and an embedded ConPTY terminal
 * rooted at the project folder; the Context dropdown shows exactly what the AI can see, and Folders lists
 * what else the engine may read. The MCP server and tools live in StudioForm.Ai.Host.cs.
 */
public sealed partial class StudioForm
{
    /** A built-in engine and its install line; an empty Command is the custom command line. */
    private sealed record AiEngine(string Name, string Command, string Install = "", string Note = "", bool PowerShell = false);

    private static readonly AiEngine[] AiEngines =
    [
        new("Claude Code", "claude", "npm install -g @anthropic-ai/claude-code", "Needs Node.js 18 or later."),
        new("Cline", "cline", "npm install -g cline", "Needs Node.js 22 or later."),
        new("Antigravity", "agy", "irm https://antigravity.google/cli/install.ps1 | iex",
            "Runs in PowerShell and installs to %LOCALAPPDATA%\\agy\\bin.", true),
        new("Custom command", "")
    ];
    private const string AiContextPopup = "ai-context-popup", AiFoldersPopup = "ai-folders-popup";
    /** The embedded ConPTY terminal view. */
    private readonly TerminalView aiTerminal = new();
    /** Log of what the AI looked at, shown in the context dropdown. */
    private readonly AttentionLog aiAttention = new();
    private readonly List<(string Id, string Label)> aiPinned = [];
    /** Cache of resolved engine command paths (null when not found). */
    private readonly Dictionary<string, string?> aiResolved = new(StringComparer.Ordinal);
    private int aiEngine;
    private string aiCustomCommand = "", aiNewFolder = "", aiFreshPath = "";
    private string? aiInstalling;
    /** Result of the last engine install attempt: message and success flag. */
    private (string Text, bool Ok)? aiInstallNote;
    private double aiNextResolve;
    private bool aiKeyHooked;
    private volatile bool aiPaused;
    private long aiShortcutFrame = -1;
    private object? aiHoverCandidate, aiLastHover, aiLastSelected;
    private double aiHoverSince;
    private Vector3 aiLastPivot;
    /** Auto-allow: the engine command the terminal runs, the last match, and when Enter went for it (clock seconds). */
    private readonly System.Windows.Forms.Timer aiAllowTimer = new() { Interval = 100 };
    private string aiRunEngine = "";
    private ConPtyTerminal? aiAllowTerminal;
    private ApprovalPrompt? aiAllowPrompt;
    private string? aiAllowSeen;
    private long aiAllowRevision = -1, aiAllowSentRevision = -1;
    private double aiAllowSince, aiAllowLast = -10;
    private int aiAllowSent, aiAllowCount;

    /** Shows the AI workspace (Ctrl+Shift+A), or its own window when it is popped out. */
    public void OpenAiWorkspace()
    {
        if (ShowPopout(StudioPanelKind.AI, true)) return;
        layout.Show(StudioPanelKind.AI);
        maximizedPanel = null;
        layoutDirty = true;
    }

    /** Handles the AI workspace keyboard shortcuts. */
    private void HandleAiShortcuts()
    {
        TrackAiAttention();
        PublishAiContext();
        if (!ShortcutInput.Pressed(ShortcutActions.OpenAiWorkspace) || aiShortcutFrame == ShortcutInput.Frame) return;
        aiShortcutFrame = ShortcutInput.Frame;
        OpenAiWorkspace();
    }

    /** Draws the whole AI workspace panel: toolbar, terminal, context and request cards. */
    private void DrawAiWorkspace(Vector2 position, Vector2 size)
    {
        BeginPanel("AI workspace", position, size);
        // The MCP never lists or drives the AI workspace (approvals, auto-allow, engine controls).
        UiAuto.PushBlocked();
        TrackAreaFocus();
        if (!aiKeyHooked)
        {
            KeyPress += (_, e) => aiTerminal.OnChar(e.KeyChar);
            aiTerminal.LineCommand = AiAutomaticLine;
            FormClosed += (_, _) => { aiTerminal.Dispose(); StopAiService(); };
            // Files dragged from Explorer onto the terminal type their quoted paths (TerminalView.DropFiles).
            AllowDrop = true;
            DragEnter += AiTerminalDragOver;
            DragOver += AiTerminalDragOver;
            DragDrop += AiTerminalDrop;
            aiKeyHooked = true;
        }
        EnsureAiService();
        LoadAiEngineChoice();
        if (aiResolved.Count == 0) ResolveAiEngines(true);
        if (aiInstalling is not null && aiTerminal.Terminal is not { HasExited: false }) FinishAiInstall();
        var engine = AiEngines[aiEngine];
        bool missing = engine.Command.Length > 0 && AiResolved(engine.Command) is null;
        DrawAiHeader(engine, missing);
        DrawAiAutomaticStrip();
        DrawAiPresence();
        DrawAiRequests();
        ImGui.Separator();
        // A missing engine shows how to install it instead of launching; a session still running stays in view.
        bool card = missing && aiInstalling != engine.Command;
        // The terminal handles its own wheel; the install card alone may scroll in a short panel.
        ImGui.BeginChild("ai-terminal-area", Vector2.Zero, ImGuiChildFlags.None,
            card && aiTerminal.Terminal is null ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (aiInstallNote is { } note) AiNote(note.Ok ? SolTheme.Ok : SolTheme.Sun, note.Text);
        DrawAiMcpWarning();
        if (card && aiTerminal.Terminal is { HasExited: false })
            AiNote(SolTheme.Sun, $"{engine.Name} is not installed. Stop the running session to install it.");
        else if (card) DrawAiInstallCard(engine);
        if (gui is not null && (!card || aiTerminal.Terminal is not null)) aiTerminal.Draw(gui.MonospaceFont, ImGui.GetContentRegionAvail());
        if (aiTerminal.TakeRestart()) RestartAiEngine();
        ImGui.EndChild();
        UiAuto.PopBlocked();
        ImGui.End();
    }

    /**
     * One compact toolbar row: engine picker, Start/Restart, Stop, the context chip (what the AI sees; click for details,
     * right-click to ask about it), the MCP status dot and a "..." menu holding the secondary controls. Items flow onto a
     * second row only when the panel is very narrow.
     */
    private void DrawAiHeader(AiEngine engine, bool missing)
    {
        var style = ImGui.GetStyle();
        float room = ImGui.GetContentRegionAvail().X, spacing = style.ItemSpacing.X, x = 0;
        float comboWidth = Math.Clamp(room * .3f, Math.Min(room, 90 * scale), 150 * scale);
        AiFlow(ref x, comboWidth, room);
        ImGui.SetNextItemWidth(comboWidth);
        if (UiAuto.BeginCombo("##ai-engine", AiEngineLabel(engine)))
        {
            if (ImGui.IsWindowAppearing()) ResolveAiEngines(true);
            for (int i = 0; i < AiEngines.Length; i++)
                if (UiAuto.Selectable(AiEngineLabel(AiEngines[i]), i == aiEngine) && i != aiEngine) SelectAiEngine(i);
            UiAuto.EndCombo();
        }
        StudioUi.Tip("Runs your own install and login; Solforge never stores API keys.");
        bool custom = engine.Command.Length == 0;
        if (custom)
        {
            float inputWidth = Math.Clamp(room - x - spacing, Math.Min(room, 120 * scale), 220 * scale);
            AiFlow(ref x, inputWidth, room);
            ImGui.SetNextItemWidth(inputWidth);
            if (UiAuto.InputTextWithHint("##ai-custom", "command line", ref aiCustomCommand, 512)) settings.AiCustomCommand = aiCustomCommand;
            if (ImGui.IsItemDeactivatedAfterEdit()) SaveSettings();
        }
        string command = custom ? aiCustomCommand.Trim() : engine.Command;
        string start = aiTerminal.Terminal is { HasExited: false } && aiInstalling is null ? "Restart##ai-start" : "Start##ai-start";
        AiFlow(ref x, SolTheme.PrimaryButtonWidth(start) + spacing + ButtonWidth("Stop"), room);
        ImGui.BeginDisabled(command.Length == 0 || missing);
        if (SolTheme.PrimaryButton(start)) StartAiEngine(engine, command, settings.AiResumeSessions);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            StudioUi.Tip(missing ? $"{engine.Name} is not installed; the panel below shows how."
                : $"Start/Restart {(settings.AiResumeSessions ? "resumes" : "does not resume")} the engine's last session for this folder.");
        ImGui.SameLine();
        ImGui.BeginDisabled(aiTerminal.Terminal is not { HasExited: false });
        if (UiAuto.Button("Stop")) aiTerminal.Stop();
        ImGui.EndDisabled();

        // The chip takes what is left beside the status dot and the "..." button, with a sensible minimum.
        float more = ButtonWidth("..."), dot = ImGui.GetFrameHeight(), badge = settings.AiAutoAllow ? ButtonWidth("AUTO") : 0;
        float chipRoom = room - x - more - dot - badge - 4 * spacing;
        if (chipRoom < 120 * scale) chipRoom = Math.Max(120 * scale, room - more - dot - badge - 3 * spacing);
        chipRoom = Math.Min(chipRoom, 420 * scale);
        string chip = StudioUi.Fit(AiContextChip(), chipRoom - style.FramePadding.X * 2);
        AiFlow(ref x, ButtonWidth(chip), room);
        ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]);
        bool openContext = UiAuto.Button(chip + "###ai-context");
        ImGui.PopStyleColor();
        if (openContext) ImGui.OpenPopup(AiContextPopup);
        StudioUi.Tip("What the AI sees now. Click for details, pins and screenshots; right-click to ask the AI about it.");
        if (ImGui.BeginPopupContextItem("ai-context-ask"))
        {
            ImGui.BeginDisabled(!CanAskAi);
            if (UiAuto.MenuItem("Ask about this")) AskAiAboutContext();
            ImGui.EndDisabled();
            ImGui.EndPopup();
        }
        DrawAiContextPopup();
        DrawAiStatusDot();
        if (settings.AiAutoAllow)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(SolTheme.Sun, "AUTO");
            StudioUi.Tip($"Auto-allow is on ({aiAllowCount} allowed this session). Turn it off in the ... menu.");
        }
        ImGui.SameLine();
        if (UiAuto.Button("...###ai-more")) ImGui.OpenPopup("ai-more");
        StudioUi.Tip("New session, auto-allow, folders, screenshots and AI settings");
        bool openFolders = false;
        if (ImGui.BeginPopup("ai-more"))
        {
            ImGui.BeginDisabled(command.Length == 0 || missing);
            if (UiAuto.MenuItem("New session")) StartAiEngine(engine, command, resume: false);
            ImGui.EndDisabled();
            // Handoff keeps the project, MCP and skills; each engine still runs on its own login (Claude / Google AI Pro).
            string from = aiRunEngine.Length > 0 ? aiRunEngine : engine.Command;
            for (int i = 0; i < AiEngines.Length; i++)
            {
                var target = AiEngines[i];
                if (target.Command is not ("claude" or "agy") || target.Command == from) continue;
                if (UiAuto.MenuItem($"Hand off to {target.Name}", "", false, workspace.Project is not null && AiResolved(target.Command) is not null))
                    HandOffAiEngine(from, i);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip($"Stops this engine, writes .solforge/HANDOFF.md from its last session and starts {target.Name} on it.");
            }
            bool resume = settings.AiResumeSessions;
            if (UiAuto.MenuItem("Resume last session on Start/Restart", "", ref resume)) { settings.AiResumeSessions = resume; SaveSettings(); }
            bool allow = settings.AiAutoAllow;
            if (UiAuto.MenuItem("Auto-allow one-time prompts", "", ref allow)) { settings.AiAutoAllow = allow; aiAllowSeen = null; SaveSettings(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(AutoAllowTip);
            bool clinePass = settings.AiAskClinePass;
            if (UiAuto.MenuItem("Give Claude Code ask_clinepass", "", ref clinePass)) { settings.AiAskClinePass = clinePass; SaveSettings(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Claude Code gets an ask_clinepass tool that runs your own Cline CLI on ClinePass (cline -P cline-pass) "
                    + "for a task. Needs Cline installed and signed in to ClinePass. Takes effect on the next Start.");
            ImGui.Separator();
            ImGui.BeginDisabled(!CanAskAi);
            if (UiAuto.MenuItem("Ask about this")) AskAiAboutContext();
            ImGui.EndDisabled();
            if (UiAuto.MenuItem("Send screenshot", "", false, workspace.Project is not null && viewport is { IsRendering: true })) SendAiScreenshot();
            bool publish = !aiPaused;
            if (UiAuto.MenuItem("Publish context", "", ref publish)) aiPaused = !publish;
            if (UiAuto.MenuItem("Folders...")) openFolders = true;
            ImGui.Separator();
            if (AiProvider(engine.Command).Preset is { } preset)
            {
                ImGui.TextDisabled("Provider: " + preset.Name);
                ImGui.BeginDisabled(preset.Login.Length == 0 || missing || AiResolved(preset.Login.Split(' ')[0]) is null);
                if (UiAuto.MenuItem("Sign in (" + (preset.Login.Length > 0 ? preset.Login : "none needed") + ")")) StartAiLogin(preset);
                ImGui.EndDisabled();
            }
            if (UiAuto.MenuItem("AI settings...")) preferencesOpen = true;
            ImGui.EndPopup();
        }
        if (openFolders) ImGui.OpenPopup(AiFoldersPopup);
        DrawAiFoldersPopup();
    }

    private const string AutoAllowTip = "When on, Solforge presses Enter when the engine asks to allow something and the highlighted "
        + "choice is a one-time Yes/Allow (never \"always allow\", No or trust prompts). Claude Code, Cline and Antigravity only.";

    /** Switches the active engine and restarts its terminal session. */
    private void SelectAiEngine(int index)
    {
        aiEngine = Math.Clamp(index, 0, AiEngines.Length - 1);
        aiInstallNote = null;
        settings.AiEngine = AiEngines[aiEngine].Name;
        SaveSettings();
    }

    /** The engine's provider preset (null for agy and custom) and its saved choice. */
    private (EngineProviders.Preset? Preset, EngineProviders.Choice Choice) AiProvider(string engine)
    {
        var choice = settings.AiProviders.GetValueOrDefault(engine) ?? new();
        return (EngineProviders.Resolve(engine, choice.Provider), choice);
    }

    /** The saved provider choice for an engine (see EngineProviders). */
    private EngineProviders.Choice AiProviderChoice(string engine)
    {
        if (!settings.AiProviders.TryGetValue(engine, out var choice)) settings.AiProviders[engine] = choice = new();
        return choice;
    }

    /** Runs the provider's own sign-in command in the terminal; the CLI keeps the credentials, Solforge sees none. */
    private void StartAiLogin(EngineProviders.Preset preset)
    {
        string[] parts = preset.Login.Split(' ', 2);
        if (AiResolved(parts[0]) is not { } executable) return;
        string folder = workspace.Project?.DirectoryPath ?? Environment.CurrentDirectory;
        aiRunEngine = "";
        aiTerminal.Start(EngineLaunch.CmdLine(executable, parts.Length > 1 ? parts[1] : ""), folder, 120, 32,
            new Dictionary<string, string> { ["PATH"] = aiFreshPath });
    }

    /** Settings > AI: the provider combo, plus model and base URL when the preset uses them. */
    private void DrawAiProviderRows(string engine, string label)
    {
        var (preset, choice) = AiProvider(engine);
        if (preset is null) return;
        ImGui.SetNextItemWidth(260 * scale);
        if (UiAuto.BeginCombo(label + " provider", preset.Name))
        {
            foreach (var item in EngineProviders.For(engine))
                if (UiAuto.Selectable(item.Name, item.Id == preset.Id)) { AiProviderChoice(engine).Provider = item.Id; SaveSettings(); }
            UiAuto.EndCombo();
        }
        SettingNote(preset.Note + (preset.Login.Length > 0 ? $" Sign in: {preset.Login} (... menu > Sign in)." : ""));
        if (preset.UsesModel)
        {
            string model = choice.Model;
            ImGui.SetNextItemWidth(260 * scale);
            if (UiAuto.InputTextWithHint(label + " model", preset.DefaultModel.Length > 0 ? preset.DefaultModel : "provider default", ref model, 128))
                AiProviderChoice(engine).Model = model;
            if (ImGui.IsItemDeactivatedAfterEdit()) SaveSettings();
        }
        if (preset.UsesBaseUrl)
        {
            string url = choice.BaseUrl;
            ImGui.SetNextItemWidth(260 * scale);
            if (UiAuto.InputTextWithHint(label + " base URL", "https://gateway.example/anthropic", ref url, 256))
                AiProviderChoice(engine).BaseUrl = url;
            if (ImGui.IsItemDeactivatedAfterEdit()) SaveSettings();
        }
    }

    /** The saved engine and custom command, read once when the AI workspace first draws. */
    private void LoadAiEngineChoice()
    {
        if (aiEngineLoaded) return;
        aiEngineLoaded = true;
        int saved = Array.FindIndex(AiEngines, item => item.Name == settings.AiEngine);
        if (saved >= 0) aiEngine = saved;
        aiCustomCommand = settings.AiCustomCommand;
    }

    private bool aiEngineLoaded;

    /** The chip: the hovered or selected object, else the open script, asset or map. */
    private string AiContextChip()
    {
        if (aiPaused) return "Context paused";
        if ((viewport?.HoveredObject ?? viewport?.SelectedObject) is not null) return "Selected: " + AiContextShort();
        if (missionGraph.ScriptPath is { } script) return "Script: " + Path.GetFileName(script);
        if (workspace.Document is { } document) return "Asset: " + document.Name;
        if (workspace.World?.Map.Name is { } map) return "Map: " + map;
        return "Context: nothing selected";
    }

    private bool CanAskAi => !aiPaused && aiTerminal.Terminal is { HasExited: false }
        && (viewport?.SelectedObject is not null || missionGraph.ScriptPath is not null || workspace.Document is not null || workspace.World is not null);

    /** Types a question about the current focus into the engine's prompt; the user presses Enter to send it. */
    private void AskAiAboutContext()
    {
        string subject;
        if (viewport?.SelectedObject is { } selected)
            subject = $"the object I selected in Solforge, {AiLabel(selected)} (id {AiId(selected)}). Use get_context and get_object";
        else if (missionGraph.ScriptPath is { } script)
            subject = $"the mission script I have open, {AiShownPath(script)}. Use get_open_documents and read_panel mission_graph";
        else if (workspace.Document is { } document)
            subject = $"the asset I have open, {document.Name} (tag 0x{document.Entry.TagHash:X8}). Use get_open_documents and read_panel inspector";
        else subject = $"the map I have open, {workspace.World?.Map.Name}. Use get_context and read_panel report";
        aiTerminal.Paste($"Tell me about {subject}, then say what I could change about it. ");
        aiAttention.Record(AttentionKind.Open, "ask", "Asked the AI about " + subject);
    }

    /**
     * Auto-allow, from its own 100 ms UI-thread timer so it keeps working while Solforge is minimized or busy. Once
     * the same allow prompt has been on screen for 250 ms (550 ms for Claude Code, whose dialogs drop keys that come
     * too early) it sends Enter the way typed keys go, once. Claude Code only: if that prompt is still up 1.5 s later
     * and the engine has redrawn since (a dropped key remounts the dialog), it sends one more. A new or vanished
     * prompt re-arms. The screen is read under its own lock, only when it changed.
     */
    private void AutoAllowAi()
    {
        // Automatic mode answers one-time prompts too, so a long goal never sits at an allow dialog.
        if (!(settings.AiAutoAllow || AiAutomatic.Active) || aiInstalling is not null || aiTerminal.Terminal is not { HasExited: false } terminal)
        {
            aiAllowSeen = null;
            aiAllowRevision = -1;
            return;
        }
        double now = clock.Elapsed.TotalSeconds;
        var screen = terminal.Screen;
        long revision = screen.Revision;
        if (revision != aiAllowRevision || !ReferenceEquals(terminal, aiAllowTerminal))
        {
            aiAllowTerminal = terminal;
            aiAllowRevision = revision;
            var (rows, shade) = screen.Snapshot(30);
            aiAllowPrompt = ApprovalPrompts.Find(aiRunEngine, rows, shade);
        }
        string? signature = aiAllowPrompt?.Signature;
        if (signature != aiAllowSeen) { aiAllowSeen = signature; aiAllowSince = now; aiAllowSent = 0; return; }
        bool claude = aiRunEngine == "claude";
        if (signature is null || now - aiAllowSince < (claude ? .55 : .25) || now - aiAllowLast < .5) return;
        if (aiAllowSent > 0 && (!claude || aiAllowSent >= 2 || now - aiAllowLast < 1.5 || revision == aiAllowSentRevision)) return;
        terminal.Send("\r");
        if (aiAllowSent++ == 0) aiAllowCount++;
        aiAllowLast = now;
        aiAllowSentRevision = revision;
    }

    /** The MCP status as a coloured dot; the text is its tooltip. */
    private void DrawAiStatusDot()
    {
        string status = "MCP off";
        AiServiceStatus(ref status);
        var color = status.StartsWith("MCP :", StringComparison.Ordinal) ? SolTheme.Ok
            : status.StartsWith("MCP error", StringComparison.Ordinal) ? SolTheme.Bad : SolTheme.Dim;
        float dot = ImGui.GetFrameHeight();
        ImGui.SameLine();
        ImGui.Dummy(new(dot));
        var center = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
        ImGui.GetWindowDrawList().AddCircleFilled(center, dot * .22f, SolTheme.U32(color));
        StudioUi.Tip(status);
    }

    /** Puts the next header item on this row when it fits, else starts a new row; x is the row's used width. */
    private static void AiFlow(ref float x, float width, float room)
    {
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        if (x > 0 && x + spacing + width <= room) { ImGui.SameLine(); x += spacing + width; }
        else x = width;
    }

    /** Draws a coloured one-line note. */
    private static void AiNote(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /** Pixel width of a button with this label. */
    private static float ButtonWidth(string label) => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;

    /** Display name of an engine. */
    private string AiEngineLabel(AiEngine engine) =>
        engine.Command.Length > 0 && aiResolved.Count > 0 && AiResolved(engine.Command) is null ? engine.Name + " (not installed)" : engine.Name;

    /** The resolved path of a command if it was found earlier, else null. */
    private string? AiResolved(string command) => aiResolved.GetValueOrDefault(command);

    /** Re-reads PATH from the registry and finds each engine (and npm); unforced, at most every 10 s. */
    private void ResolveAiEngines(bool force = false)
    {
        double now = ImGui.GetTime();
        if (!force && now < aiNextResolve) return;
        aiNextResolve = now + 10;
        aiFreshPath = EngineCommands.FreshPath();
        string pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? "";
        foreach (string command in AiEngines.Select(engine => engine.Command).Where(command => command.Length > 0).Append("npm"))
            aiResolved[command] = EngineCommands.Resolve(command, aiFreshPath, pathExt);
    }

    /** Writes the handoff note from the engine that ran here and starts the target fresh on it (same project, MCP, skills). */
    private void HandOffAiEngine(string fromCommand, int target)
    {
        if (workspace.Project?.DirectoryPath is not { } folder) return;
        aiTerminal.Stop();
        string fromName = AiEngines.FirstOrDefault(item => item.Command == fromCommand)?.Name ?? "the previous engine";
        string? prompt = EngineLaunch.HandoffPrompt;
        try { EngineLaunch.WriteHandoff(folder, fromCommand, fromName, AiEngines[target].Name); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { prompt = null; }
        SelectAiEngine(target);
        StartAiEngine(AiEngines[target], AiEngines[target].Command, resume: false, prompt);
    }

    /** The terminal's "engine stopped" Restart (after Ctrl+C twice): starts the picked engine as the toolbar's Start does. */
    private void RestartAiEngine()
    {
        var engine = AiEngines[aiEngine];
        string command = engine.Command.Length == 0 ? aiCustomCommand.Trim() : engine.Command;
        if (command.Length > 0 && (engine.Command.Length == 0 || AiResolved(engine.Command) is not null))
            StartAiEngine(engine, command, settings.AiResumeSessions);
    }

    /** Engines run by full path with the refreshed PATH, so an install made after Solforge started works at once. */
    private void StartAiEngine(AiEngine engine, string command, bool resume, string? prompt = null)
    {
        string folder = workspace.Project?.DirectoryPath ?? Environment.CurrentDirectory;
        PrepareAiEngine(folder);
        ResolveAiEngines(true);
        aiInstalling = null;
        aiInstallNote = null;
        if (workspace.Project is not null) AiWriteSession(folder);
        aiRunEngine = engine.Command;
        var environment = new Dictionary<string, string> { ["PATH"] = aiFreshPath };
        // cmd /k keeps a prompt after the engine exits.
        if (engine.Command.Length == 0) { aiTerminal.Start($"cmd.exe /d /k {command}", folder, 120, 32, environment); return; }
        if (AiResolved(engine.Command) is not { } executable) return;
        var (preset, choice) = AiProvider(engine.Command);
        foreach (var (name, value) in EngineProviders.Environment(preset, choice)) environment[name] = value;
        if (preset?.Id == EngineProviders.ClaudeClinePass)
        {
            if (AiClinePassEnvironment(EngineProviders.Model(preset, choice)) is not { } proxyEnvironment) return;
            foreach (var (name, value) in proxyEnvironment) environment[name] = value;
        }
        string arguments = workspace.Project is null ? "" : engine.Command switch
        {
            "claude" => AiClaudeArguments(folder),
            "agy" => AiAgyArguments(folder),
            _ => ""
        };
        arguments = EngineLaunch.JoinArguments(EngineProviders.Arguments(preset, choice), arguments);
        if (settings.AiSkipPermissions) arguments = EngineLaunch.JoinArguments(arguments, EngineLaunch.SkipPermissionsArguments(engine.Command));
        if (resume) arguments = EngineLaunch.JoinArguments(arguments, EngineLaunch.ResumeArguments(engine.Command, AiLastSession(engine.Command, executable, folder)));
        arguments = EngineLaunch.WithPrompt(engine.Command, arguments, prompt);
        aiTerminal.Start(EngineLaunch.CmdLine(executable, arguments), folder, 120, 32, environment);
    }

    /** The engine's newest session for the folder, or null (first run, or the lookup failed: start fresh). */
    private string? AiLastSession(string command, string executable, string folder) => command switch
    {
        "claude" => EngineLaunch.ClaudeSession(folder),
        "agy" => EngineLaunch.AgySession(folder),
        "cline" => AiClineSession(executable, folder),
        _ => null
    };

    /** Runs `cline history --json` (read-only) with a 3 s cap, so a hung CLI only delays the start. */
    private string? AiClineSession(string executable, string folder)
    {
        try
        {
            string line = EngineLaunch.CmdLine(executable, "history --json --limit 30", keep: false);
            var info = new ProcessStartInfo("cmd.exe", line["cmd.exe ".Length..])
            {
                WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.Environment["PATH"] = aiFreshPath;
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000)) { try { process.Kill(true); } catch (InvalidOperationException) { } return null; }
            return output.Wait(500) ? EngineLaunch.ClineSession(folder, output.Result) : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    /** Draws the install card for an engine that is not installed yet. */
    private void DrawAiInstallCard(AiEngine engine)
    {
        ResolveAiEngines();
        ImGui.BeginChild("ai-install", Vector2.Zero, ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY);
        SolTheme.SectionHeader(engine.Name.ToUpperInvariant() + " IS NOT INSTALLED");
        ImGui.TextWrapped($"'{engine.Command}' was not found on PATH (read fresh from Windows), in %LOCALAPPDATA%\\agy\\bin or in %APPDATA%\\npm.");
        bool npm = engine.Install.StartsWith("npm ", StringComparison.Ordinal);
        bool ready = !npm || AiResolved("npm") is not null;
        if (!ready)
        {
            ImGui.TextColored(SolTheme.Sun, "npm was not found. Install Node.js (LTS) from https://nodejs.org first.");
            if (UiAuto.Button("Open nodejs.org")) Process.Start(new ProcessStartInfo("https://nodejs.org") { UseShellExecute = true })?.Dispose();
        }
        ImGui.Spacing();
        ImGui.TextDisabled(engine.PowerShell ? "Run in PowerShell:" : "Run:");
        string line = engine.Install;
        ImGui.SetNextItemWidth(-1);
        UiAuto.InputText("##ai-install-line", ref line, 512, ImGuiInputTextFlags.ReadOnly);
        if (engine.Note.Length > 0) ImGui.TextWrapped(engine.Note);
        ImGui.BeginDisabled(!ready);
        if (SolTheme.PrimaryButton("Install in terminal")) RunAiInstall(engine);
        ImGui.EndDisabled();
        StudioUi.Tip("Runs the command above in this terminal. Solforge checks again when it finishes.");
        ImGui.SameLine();
        if (UiAuto.Button("Check again")) ResolveAiEngines(true);
        ImGui.EndChild();
    }

    /** Runs the engine's install line in the terminal, only on the button click; FinishAiInstall checks again after. */
    private void RunAiInstall(AiEngine engine)
    {
        ResolveAiEngines(true);
        string folder = workspace.Project?.DirectoryPath ?? Environment.CurrentDirectory;
        string line = engine.PowerShell ? $"cmd.exe /d /c powershell.exe -NoProfile -Command \"{engine.Install}\"" : $"cmd.exe /d /c {engine.Install}";
        aiInstalling = engine.Command;
        aiInstallNote = null;
        aiTerminal.Start(line, folder, 120, 32, new Dictionary<string, string> { ["PATH"] = aiFreshPath });
    }

    /** Finishes an engine install: refreshes command resolution and shows the outcome. */
    private void FinishAiInstall()
    {
        string command = aiInstalling!;
        aiInstalling = null;
        ResolveAiEngines(true);
        string name = AiEngines.FirstOrDefault(engine => engine.Command == command)?.Name ?? command;
        aiInstallNote = AiResolved(command) is null
            ? ($"The install finished but {name} is still not found; see the output below.", false)
            : ($"{name} is installed. Press Start.", true);
    }

    /** The Context button's text: the hovered or selected object's short name, else the map. */
    private string AiContextShort()
    {
        if (aiPaused) return "paused";
        if ((viewport?.HoveredObject ?? viewport?.SelectedObject) is { } focus)
        {
            string label = AiLabel(focus);
            int slash = label.LastIndexOf(" / ", StringComparison.Ordinal);
            if (slash >= 0) label = label[(slash + 3)..];
            int dash = label.IndexOf(" - ", StringComparison.Ordinal);
            return dash > 0 ? label[..dash] : label;
        }
        return workspace.World?.Map.Name ?? workspace.Document?.Name ?? "nothing selected";
    }

    /** A width capped to the window (and the screen of a popped-out panel), so the popup never clips; values wrap. */
    private void DrawAiContextPopup()
    {
        if (!ImGui.IsPopupOpen(AiContextPopup)) return;
        var display = ImGui.GetIO().DisplaySize;
        float width = Math.Clamp(display.X - 16 * scale, 120 * scale, 400 * scale);
        ImGui.SetNextWindowSize(new(width, 0));
        ImGui.SetNextWindowSizeConstraints(new(width, 0), new(width, Math.Max(160 * scale, display.Y * .75f)));
        if (!ImGui.BeginPopup(AiContextPopup)) return;
        DrawAiContext();
        ImGui.EndPopup();
    }

    /** Draws the Context dropdown: what the AI currently sees. */
    private void DrawAiContext()
    {
        bool publish = !aiPaused;
        if (UiAuto.Checkbox("Publish context", ref publish)) aiPaused = !publish;
        StudioUi.Tip("Off pauses everything the AI can see about the scene (privacy).");
        SolTheme.SectionHeader("CONTEXT (LIVE)");
        if (aiPaused) ImGui.TextColored(SolTheme.Sun, "Paused: the AI sees nothing.");
        var view = viewport;
        AiRow("Hovered", view?.HoveredObject is { } hovered ? AiLabel(hovered) : null);
        AiRow("Selected", view?.SelectedObject is { } selected ? AiLabel(selected) : null);
        if (view is not null)
        {
            var eye = view.Camera.Position;
            AiRow("Camera", $"{eye.X:0.#}, {eye.Y:0.#}, {eye.Z:0.#}" + (workspace.World?.Map.Name is { } map ? $" - {map}" : ""));
        }
        AiRow("Open file", missionGraph.ScriptPath is { } script ? Path.GetFileName(script) : null);
        if (SolTheme.PrimaryButton("Pin")) PinAiObject();
        StudioUi.Tip("Keep the hovered (or selected) object in the AI's context.");
        ImGui.SameLine();
        if (UiAuto.Button("Send screenshot")) SendAiScreenshot();
        StudioUi.Tip("Save the viewport as a PNG in the project and paste its path into the terminal.");
        if (aiPinned.Count > 0)
        {
            SolTheme.SectionHeader("PINNED");
            for (int i = 0; i < aiPinned.Count; i++)
            {
                ImGui.PushID(i);
                if (UiAuto.SmallButton("x")) { aiPinned.RemoveAt(i--); ImGui.PopID(); continue; }
                ImGui.SameLine();
                ImGui.TextWrapped(aiPinned[i].Label);
                ImGui.PopID();
            }
        }
        DrawAiEdits();
        SolTheme.SectionHeader("RECENT (ATTENTION LOG)");
        foreach (var item in aiAttention.Recent(10))
        {
            ImGui.TextColored(SolTheme.Faint, $"{(DateTimeOffset.UtcNow - item.Time).TotalSeconds,4:0}s {item.Kind.ToString().ToLowerInvariant()}");
            ImGui.SameLine();
            ImGui.TextWrapped(item.Label);
        }
    }

    /** Draws one name/value row in the context dropdown. */
    private void AiRow(string name, string? value)
    {
        ImGui.TextColored(SolTheme.Dim, name);
        ImGui.SameLine(90 * scale);
        ImGui.TextWrapped(value ?? "-");
    }

    /** Found folders with a switch each, folders added by hand, and a path box; kept in the settings. */
    private void DrawAiFoldersPopup()
    {
        if (!ImGui.IsPopupOpen(AiFoldersPopup)) return;
        var display = ImGui.GetIO().DisplaySize;
        float width = Math.Clamp(display.X - 16 * scale, 120 * scale, 460 * scale);
        ImGui.SetNextWindowSize(new(width, 0));
        ImGui.SetNextWindowSizeConstraints(new(width, 0), new(width, Math.Max(160 * scale, display.Y * .75f)));
        if (!ImGui.BeginPopup(AiFoldersPopup)) return;
        DrawAiFolders();
        ImGui.EndPopup();
    }

    /** The Folders list, shared by the AI workspace's popup and Settings > AI. */
    private void DrawAiFolders()
    {
        ImGui.TextWrapped("Claude Code may also read these folders (--add-dir). Changes apply the next time an engine starts.");
        SolTheme.SectionHeader("FOUND");
        if (workspace.Project?.DirectoryPath is not { } project) ImGui.TextDisabled("Open a project first.");
        else
        {
            var found = AiFoundFolders(project);
            if (found.Count == 0) ImGui.TextDisabled("Nothing open outside the project.");
            foreach (string folder in found)
            {
                ImGui.PushID(folder);
                bool on = !settings.AiHiddenFolders.Contains(folder, StringComparer.OrdinalIgnoreCase);
                if (UiAuto.Checkbox("##on", ref on))
                {
                    settings.AiHiddenFolders.RemoveAll(item => string.Equals(item, folder, StringComparison.OrdinalIgnoreCase));
                    if (!on) settings.AiHiddenFolders.Add(folder);
                    SaveSettings();
                }
                ImGui.SameLine();
                ImGui.TextWrapped(folder);
                ImGui.PopID();
            }
        }
        SolTheme.SectionHeader("ADDED");
        for (int i = 0; i < settings.AiFolders.Count; i++)
        {
            ImGui.PushID(i);
            if (UiAuto.SmallButton("x")) { settings.AiFolders.RemoveAt(i--); SaveSettings(); ImGui.PopID(); continue; }
            ImGui.SameLine();
            if (Directory.Exists(settings.AiFolders[i])) ImGui.TextWrapped(settings.AiFolders[i]);
            else ImGui.TextColored(SolTheme.Faint, settings.AiFolders[i] + " (missing)");
            ImGui.PopID();
        }
        string add = aiNewFolder.Trim().Trim('"');
        ImGui.SetNextItemWidth(-(ButtonWidth("Add") + ImGui.GetStyle().ItemSpacing.X));
        UiAuto.InputTextWithHint("##ai-folder", "folder path", ref aiNewFolder, 512);
        ImGui.SameLine();
        ImGui.BeginDisabled(!Directory.Exists(add) || settings.AiFolders.Contains(add, StringComparer.OrdinalIgnoreCase));
        if (UiAuto.Button("Add")) { settings.AiFolders.Add(Path.GetFullPath(add)); aiNewFolder = ""; SaveSettings(); }
        ImGui.EndDisabled();
    }

    /** Display label of an inspected object. */
    private string AiLabel(object source) =>
        viewport?.ObjectName?.Invoke(source) ?? inspectionWorld?.Find(source)?.Name ?? source.ToString() ?? "object";

    /** Stable id of an inspected object. */
    private string AiId(object source) =>
        inspectionWorld?.Find(source)?.Id ?? "runtime:" + RuntimeHelpers.GetHashCode(source).ToString("x8");

    /** Pins the currently inspected object into the AI context. */
    private void PinAiObject()
    {
        if ((viewport?.HoveredObject ?? viewport?.SelectedObject) is not { } source) return;
        string id = AiId(source), label = AiLabel(source);
        if (aiPinned.All(pin => pin.Id != id)) aiPinned.Add((id, label));
        aiAttention.Record(AttentionKind.Pin, id, label);
    }

    /** Hover counts after 300 ms on one object; selection at once; a camera jump onto the selection is a focus. */
    private void TrackAiAttention()
    {
        if (aiPaused || viewport is not { } view) return;
        double now = ImGui.GetTime();
        var hovered = view.HoveredObject;
        if (!ReferenceEquals(hovered, aiHoverCandidate)) { aiHoverCandidate = hovered; aiHoverSince = now; }
        else if (hovered is not null && !ReferenceEquals(hovered, aiLastHover) && now - aiHoverSince >= .3)
        {
            aiLastHover = hovered;
            aiAttention.Record(AttentionKind.Hover, AiId(hovered), AiLabel(hovered));
        }
        var selected = view.SelectedObject;
        if (!ReferenceEquals(selected, aiLastSelected))
        {
            aiLastSelected = selected;
            if (selected is not null) aiAttention.Record(AttentionKind.Select, AiId(selected), AiLabel(selected));
        }
        var pivot = view.Camera.Pivot;
        if (pivot != aiLastPivot)
        {
            if (selected is not null && view.SelectedBounds is { IsFinite: true } bounds && Vector3.Distance(pivot, bounds.Center) < .01f
                && Vector3.Distance(aiLastPivot, bounds.Center) >= .01f)
                aiAttention.Record(AttentionKind.Focus, AiId(selected), AiLabel(selected));
            aiLastPivot = pivot;
        }
    }

    /** Explorer files over the terminal (or anywhere while it has focus) can be dropped; anything else shows no-drop. */
    private void AiTerminalDragOver(object? sender, DragEventArgs e) =>
        e.Effect = AiTerminalDropFiles(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;

    /** Types the dropped files' quoted paths into the terminal and brings Solforge forward so typing can follow. */
    private void AiTerminalDrop(object? sender, DragEventArgs e)
    {
        if (AiTerminalDropFiles(e) is not { } files) return;
        aiTerminal.DropFiles(files);
        Activate();
    }

    /** The dropped file paths when they would reach a running terminal: under the pointer, or the terminal has focus. */
    private string[]? AiTerminalDropFiles(DragEventArgs e)
    {
        if (!aiTerminal.Accepting || !aiTerminal.Visible || e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return null;
        var point = PointToClient(new System.Drawing.Point(e.X, e.Y));
        var (min, max) = aiTerminal.Area;
        bool over = point.X >= min.X && point.Y >= min.Y && point.X < max.X && point.Y < max.Y;
        return over || aiTerminal.Focused ? files : null;
    }

    /** Keys typed into the terminal never reach Studio shortcuts (Ctrl+Z, Ctrl+S, Q/W/E/R...). */
    private void ClaimAiKeyboard()
    {
        if (aiTerminal.Focused && aiTerminal.Terminal is not null) ImGui.GetIO().WantTextInput = true;
    }

    /** Hook implemented in StudioForm.Ai.Host.cs: starts the MCP service when needed. */
    partial void EnsureAiService();
    /** Hook implemented in StudioForm.Ai.Host.cs: stops the MCP service. */
    partial void StopAiService();
    /** Hook implemented in StudioForm.Ai.Host.cs: prepares the engine launch files for a folder. */
    partial void PrepareAiEngine(string folder);
    /** Hook implemented in StudioForm.Ai.Host.cs: reports the MCP service status text. */
    partial void AiServiceStatus(ref string status);
    /** Hook implemented in StudioForm.Ai.Host.cs: sends a viewport screenshot to the AI. */
    partial void SendAiScreenshot();
    /** Hook implemented in StudioForm.Ai.Host.cs: draws pending approval requests. */
    partial void DrawAiRequests();
    /** Hook implemented in StudioForm.Ai.Host.cs: draws the AI edit list with undo. */
    partial void DrawAiEdits();
}
