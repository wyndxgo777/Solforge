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
    private readonly TerminalView aiTerminal = new();
    private readonly AttentionLog aiAttention = new();
    private readonly List<(string Id, string Label)> aiPinned = [];
    private readonly Dictionary<string, string?> aiResolved = new(StringComparer.Ordinal);
    private int aiEngine;
    private string aiCustomCommand = "", aiNewFolder = "", aiFreshPath = "";
    private string? aiInstalling;
    private (string Text, bool Ok)? aiInstallNote;
    private double aiNextResolve;
    private bool aiKeyHooked;
    private volatile bool aiPaused;
    private long aiShortcutFrame = -1;
    private object? aiHoverCandidate, aiLastHover, aiLastSelected;
    private double aiHoverSince;
    private Vector3 aiLastPivot;

    /** Shows the AI workspace (Ctrl+Shift+A), or its own window when it is popped out. */
    public void OpenAiWorkspace()
    {
        if (ShowPopout(StudioPanelKind.AI, true)) return;
        layout.Show(StudioPanelKind.AI);
        maximizedPanel = null;
        layoutDirty = true;
    }

    private void HandleAiShortcuts()
    {
        TrackAiAttention();
        PublishAiContext();
        if (!ShortcutInput.Pressed(ShortcutActions.OpenAiWorkspace) || aiShortcutFrame == ShortcutInput.Frame) return;
        aiShortcutFrame = ShortcutInput.Frame;
        OpenAiWorkspace();
    }

    private void DrawAiWorkspace(Vector2 position, Vector2 size)
    {
        BeginPanel("AI workspace", position, size);
        TrackAreaFocus();
        if (!aiKeyHooked)
        {
            KeyPress += (_, e) => aiTerminal.OnChar(e.KeyChar);
            FormClosed += (_, _) => { aiTerminal.Dispose(); StopAiService(); };
            aiKeyHooked = true;
        }
        EnsureAiService();
        if (aiResolved.Count == 0) ResolveAiEngines(true);
        if (aiInstalling is not null && aiTerminal.Terminal is not { HasExited: false }) FinishAiInstall();
        var engine = AiEngines[aiEngine];
        bool missing = engine.Command.Length > 0 && AiResolved(engine.Command) is null;
        DrawAiHeader(engine, missing);
        DrawAiRequests();
        ImGui.Separator();
        // A missing engine shows how to install it instead of launching; a session still running stays in view.
        bool card = missing && aiInstalling != engine.Command;
        // The terminal handles its own wheel; the install card alone may scroll in a short panel.
        ImGui.BeginChild("ai-terminal-area", Vector2.Zero, ImGuiChildFlags.None,
            card && aiTerminal.Terminal is null ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (aiInstallNote is { } note) AiNote(note.Ok ? SolTheme.Ok : SolTheme.Sun, note.Text);
        if (card && aiTerminal.Terminal is { HasExited: false })
            AiNote(SolTheme.Sun, $"{engine.Name} is not installed. Stop the running session to install it.");
        else if (card) DrawAiInstallCard(engine);
        if (gui is not null && (!card || aiTerminal.Terminal is not null)) aiTerminal.Draw(gui.MonospaceFont, ImGui.GetContentRegionAvail());
        ImGui.EndChild();
        ImGui.End();
    }

    /**
     * Engine picker, Start/Stop, the Context and Folders dropdowns and the MCP status. Items flow onto a second row
     * when the panel is narrow, and the status shrinks to a coloured dot.
     */
    private void DrawAiHeader(AiEngine engine, bool missing)
    {
        var style = ImGui.GetStyle();
        float room = ImGui.GetContentRegionAvail().X, spacing = style.ItemSpacing.X, x = 0;
        float comboWidth = Math.Clamp(room * .45f, Math.Min(room, 90 * scale), 170 * scale);
        AiFlow(ref x, comboWidth, room);
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.BeginCombo("##ai-engine", AiEngineLabel(engine)))
        {
            if (ImGui.IsWindowAppearing()) ResolveAiEngines(true);
            for (int i = 0; i < AiEngines.Length; i++)
                if (ImGui.Selectable(AiEngineLabel(AiEngines[i]), i == aiEngine) && i != aiEngine) { aiEngine = i; aiInstallNote = null; }
            ImGui.EndCombo();
        }
        StudioUi.Tip("Runs your own install and login; Solforge never stores API keys.");
        bool custom = engine.Command.Length == 0;
        if (custom)
        {
            float inputWidth = Math.Clamp(room - x - spacing, Math.Min(room, 120 * scale), 260 * scale);
            AiFlow(ref x, inputWidth, room);
            ImGui.SetNextItemWidth(inputWidth);
            ImGui.InputTextWithHint("##ai-custom", "command line", ref aiCustomCommand, 512);
        }
        string command = custom ? aiCustomCommand.Trim() : engine.Command;
        string start = aiTerminal.Terminal is { HasExited: false } && aiInstalling is null ? "Restart##ai-start" : "Start##ai-start";
        AiFlow(ref x, SolTheme.PrimaryButtonWidth(start) + spacing + ButtonWidth("Stop"), room);
        ImGui.BeginDisabled(command.Length == 0 || missing);
        if (SolTheme.PrimaryButton(start)) StartAiEngine(engine, command);
        ImGui.EndDisabled();
        if (missing) StudioUi.Tip($"{engine.Name} is not installed; the panel below shows how.");
        ImGui.SameLine();
        ImGui.BeginDisabled(aiTerminal.Terminal is not { HasExited: false });
        if (ImGui.Button("Stop")) aiTerminal.Stop();
        ImGui.EndDisabled();

        // The Context button gives up width so Folders and the status dot still fit beside it.
        float folders = ButtonWidth("Folders"), dot = ImGui.GetFrameHeight();
        float contextRoom = Math.Min(300 * scale, Math.Max(60 * scale, room - folders - dot - 2 * spacing)) - style.FramePadding.X * 2;
        string context = StudioUi.Fit("Context: " + AiContextShort(), contextRoom);
        AiFlow(ref x, ButtonWidth(context) + spacing + folders, room);
        if (ImGui.Button(context + "###ai-context")) ImGui.OpenPopup(AiContextPopup);
        StudioUi.Tip("What the AI sees now: hovered, selected, camera, open file, pins and the attention log.");
        DrawAiContextPopup();
        ImGui.SameLine();
        if (ImGui.Button("Folders")) ImGui.OpenPopup(AiFoldersPopup);
        StudioUi.Tip("Folders outside the project the engine may read (Claude Code --add-dir).");
        DrawAiFoldersPopup();
        DrawAiStatus(x, room);
    }

    /** The full status text right-aligned when it fits; otherwise a coloured dot carrying it as a tooltip. */
    private void DrawAiStatus(float x, float room)
    {
        string status = "MCP off";
        AiServiceStatus(ref status);
        var color = status.StartsWith("MCP :", StringComparison.Ordinal) ? SolTheme.Ok
            : status.StartsWith("MCP error", StringComparison.Ordinal) ? SolTheme.Bad : SolTheme.Dim;
        float spacing = ImGui.GetStyle().ItemSpacing.X, left = room - x - spacing;
        float width = ImGui.CalcTextSize(status).X, dot = ImGui.GetFrameHeight();
        if (left >= width)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + left - width);
            ImGui.TextColored(color, status);
            return;
        }
        if (left >= dot) { ImGui.SameLine(); ImGui.SetCursorPosX(ImGui.GetCursorPosX() + left - dot); }
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

    private static void AiNote(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static float ButtonWidth(string label) => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;

    private string AiEngineLabel(AiEngine engine) =>
        engine.Command.Length > 0 && aiResolved.Count > 0 && AiResolved(engine.Command) is null ? engine.Name + " (not installed)" : engine.Name;

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

    /** Engines run by full path with the refreshed PATH, so an install made after Solforge started works at once. */
    private void StartAiEngine(AiEngine engine, string command)
    {
        string folder = workspace.Project?.DirectoryPath ?? Environment.CurrentDirectory;
        PrepareAiEngine(folder);
        ResolveAiEngines(true);
        aiInstalling = null;
        aiInstallNote = null;
        var environment = new Dictionary<string, string> { ["PATH"] = aiFreshPath };
        // cmd /k keeps a prompt after the engine exits.
        if (engine.Command.Length == 0) { aiTerminal.Start($"cmd.exe /d /k {command}", folder, 120, 32, environment); return; }
        if (AiResolved(engine.Command) is not { } executable) return;
        string arguments = engine.Command == "claude" && workspace.Project is not null ? AiClaudeArguments(folder) : "";
        aiTerminal.Start(EngineLaunch.CmdLine(executable, arguments), folder, 120, 32, environment);
    }

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
            if (ImGui.Button("Open nodejs.org")) Process.Start(new ProcessStartInfo("https://nodejs.org") { UseShellExecute = true })?.Dispose();
        }
        ImGui.Spacing();
        ImGui.TextDisabled(engine.PowerShell ? "Run in PowerShell:" : "Run:");
        string line = engine.Install;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##ai-install-line", ref line, 512, ImGuiInputTextFlags.ReadOnly);
        if (engine.Note.Length > 0) ImGui.TextWrapped(engine.Note);
        ImGui.BeginDisabled(!ready);
        if (SolTheme.PrimaryButton("Install in terminal")) RunAiInstall(engine);
        ImGui.EndDisabled();
        StudioUi.Tip("Runs the command above in this terminal. Solforge checks again when it finishes.");
        ImGui.SameLine();
        if (ImGui.Button("Check again")) ResolveAiEngines(true);
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

    private void DrawAiContext()
    {
        bool publish = !aiPaused;
        if (ImGui.Checkbox("Publish context", ref publish)) aiPaused = !publish;
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
        if (ImGui.Button("Send screenshot")) SendAiScreenshot();
        StudioUi.Tip("Save the viewport as a PNG in the project and paste its path into the terminal.");
        if (aiPinned.Count > 0)
        {
            SolTheme.SectionHeader("PINNED");
            for (int i = 0; i < aiPinned.Count; i++)
            {
                ImGui.PushID(i);
                if (ImGui.SmallButton("x")) { aiPinned.RemoveAt(i--); ImGui.PopID(); continue; }
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
                if (ImGui.Checkbox("##on", ref on))
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
            if (ImGui.SmallButton("x")) { settings.AiFolders.RemoveAt(i--); SaveSettings(); ImGui.PopID(); continue; }
            ImGui.SameLine();
            if (Directory.Exists(settings.AiFolders[i])) ImGui.TextWrapped(settings.AiFolders[i]);
            else ImGui.TextColored(SolTheme.Faint, settings.AiFolders[i] + " (missing)");
            ImGui.PopID();
        }
        string add = aiNewFolder.Trim().Trim('"');
        ImGui.SetNextItemWidth(-(ButtonWidth("Add") + ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint("##ai-folder", "folder path", ref aiNewFolder, 512);
        ImGui.SameLine();
        ImGui.BeginDisabled(!Directory.Exists(add) || settings.AiFolders.Contains(add, StringComparer.OrdinalIgnoreCase));
        if (ImGui.Button("Add")) { settings.AiFolders.Add(Path.GetFullPath(add)); aiNewFolder = ""; SaveSettings(); }
        ImGui.EndDisabled();
        ImGui.EndPopup();
    }

    private string AiLabel(object source) =>
        viewport?.ObjectName?.Invoke(source) ?? inspectionWorld?.Find(source)?.Name ?? source.ToString() ?? "object";

    private string AiId(object source) =>
        inspectionWorld?.Find(source)?.Id ?? "runtime:" + RuntimeHelpers.GetHashCode(source).ToString("x8");

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

    /** Keys typed into the terminal never reach Studio shortcuts (Ctrl+Z, Ctrl+S, Q/W/E/R...). */
    private void ClaimAiKeyboard()
    {
        if (aiTerminal.Focused && aiTerminal.Terminal is not null) ImGui.GetIO().WantTextInput = true;
    }

    partial void EnsureAiService();
    partial void StopAiService();
    partial void PrepareAiEngine(string folder);
    partial void AiServiceStatus(ref string status);
    partial void SendAiScreenshot();
    partial void DrawAiRequests();
    partial void DrawAiEdits();
}
