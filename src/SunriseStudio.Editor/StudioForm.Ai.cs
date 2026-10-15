using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using Solforge.Mcp;
using SunriseStudio.Editor.Ai;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * Solforge Phase 47B: the AI workspace (Studio.md §3.8). An engine picker and an embedded ConPTY terminal
 * rooted at the project folder, beside the live Context panel that shows exactly what the AI can see.
 * The MCP server and tools live in StudioForm.Ai.Host.cs.
 */
public sealed partial class StudioForm
{
    private static readonly (string Name, string Command)[] AiEngines =
        [("Claude Code", "claude"), ("Cline", "cline"), ("Antigravity", "agy"), ("Custom command", "")];
    private readonly TerminalView aiTerminal = new();
    private readonly AttentionLog aiAttention = new();
    private readonly List<(string Id, string Label)> aiPinned = [];
    private int aiEngine;
    private string aiCustomCommand = "";
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
        DrawAiHeader();
        var room = ImGui.GetContentRegionAvail();
        float contextWidth = Math.Clamp(room.X * .32f, Math.Min(room.X, 220 * scale), 340 * scale);
        ImGui.BeginChild("ai-context", new(contextWidth, room.Y), ImGuiChildFlags.Borders);
        DrawAiContext();
        ImGui.EndChild();
        ImGui.SameLine(0, 4 * scale);
        ImGui.BeginChild("ai-terminal-area", new(0, room.Y), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (gui is not null) aiTerminal.Draw(gui.MonospaceFont, ImGui.GetContentRegionAvail());
        ImGui.EndChild();
        ImGui.End();
    }

    private void DrawAiHeader()
    {
        ImGui.SetNextItemWidth(150 * scale);
        if (ImGui.BeginCombo("##ai-engine", AiEngines[aiEngine].Name))
        {
            for (int i = 0; i < AiEngines.Length; i++)
                if (ImGui.Selectable(AiEngines[i].Name, i == aiEngine)) aiEngine = i;
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Runs your own install and login; Solforge never stores API keys.");
        if (AiEngines[aiEngine].Command.Length == 0)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(200 * scale);
            ImGui.InputTextWithHint("##ai-custom", "command line", ref aiCustomCommand, 512);
        }
        string command = AiEngines[aiEngine].Command is { Length: > 0 } builtIn ? builtIn : aiCustomCommand.Trim();
        ImGui.SameLine();
        ImGui.BeginDisabled(command.Length == 0);
        if (SolTheme.PrimaryButton(aiTerminal.Terminal is null ? "Start" : "Restart")) StartAiEngine(command);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(aiTerminal.Terminal is null);
        if (ImGui.Button("Stop")) aiTerminal.Stop();
        ImGui.EndDisabled();
        string status = "MCP off";
        AiServiceStatus(ref status);
        float width = ImGui.CalcTextSize(status).X;
        ImGui.SameLine(0, 8 * scale);
        float spare = ImGui.GetContentRegionAvail().X - width;
        if (spare > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + spare);
        ImGui.TextColored(status.StartsWith("MCP :", StringComparison.Ordinal) ? SolTheme.Ok : SolTheme.Dim, status);
        ImGui.Separator();
    }

    private void StartAiEngine(string command)
    {
        string folder = workspace.Project?.DirectoryPath ?? Environment.CurrentDirectory;
        PrepareAiEngine(folder);
        // cmd /k keeps a prompt after the engine exits; npm installs claude and cline as .cmd shims.
        aiTerminal.Start($"cmd.exe /d /k {command}", folder, 120, 32);
    }

    private void DrawAiContext()
    {
        bool publish = !aiPaused;
        if (ImGui.Checkbox("Publish context", ref publish)) aiPaused = !publish;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Off pauses everything the AI can see about the scene (privacy).");
        SolTheme.SectionHeader("CONTEXT (LIVE)");
        if (aiPaused) { ImGui.TextColored(SolTheme.Sun, "Paused: the AI sees nothing."); }
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
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Keep the hovered (or selected) object in the AI's context.");
        ImGui.SameLine();
        if (ImGui.Button("Send screenshot")) SendAiScreenshot();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Save the viewport as a PNG in the project and paste its path into the terminal.");
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
        DrawAiRequests();
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
}
