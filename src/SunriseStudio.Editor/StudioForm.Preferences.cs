using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * The Settings window (File or Edit > Settings, the toolbar gear, Ctrl+,). Every value lives in StudioSettings and is saved
 * as it changes; each applies live unless its row says when it takes effect.
 */
public sealed partial class StudioForm
{
    private bool preferencesOpen;
    private string preferencesScripts = "", preferencesBlender = "";
    private bool preferencesSeeded;

    private void DrawPreferences()
    {
        if (!preferencesOpen) { preferencesSeeded = false; return; }
        if (!preferencesSeeded)
        {
            preferencesSeeded = true;
            preferencesScripts = settings.ScriptsPath;
            preferencesBlender = settings.BlenderPath;
        }
        ImGui.SetNextWindowPos(new(ClientSize.Width / 2f, ClientSize.Height / 2f), ImGuiCond.Appearing, new(.5f));
        ImGui.SetNextWindowSize(new Vector2(620, 520) * scale, ImGuiCond.Appearing);
        if (!ImGui.Begin("Settings###solforge-settings", ref preferencesOpen, ImGuiWindowFlags.NoCollapse))
        { ImGui.End(); return; }
        if (ImGui.BeginTabBar("settings-tabs"))
        {
            if (ImGui.BeginTabItem("Rendering")) { DrawRenderingSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("AI")) { DrawAiSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Paths")) { DrawPathSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Live link")) { DrawLiveSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Interface")) { DrawInterfaceSettings(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    private static void SettingNote(string text) { ImGui.PushTextWrapPos(); ImGui.TextDisabled(text); ImGui.PopTextWrapPos(); }

    private void DrawRenderingSettings()
    {
        bool render = settings.ViewportRendering;
        if (ImGui.Checkbox("3D viewport rendering", ref render)) SetViewportRendering(render);
        SettingNote($"Off stops all 3D drawing and shrinks the viewport's GPU targets; every panel and the AI tools keep working. "
            + $"Toolbar eye or {ShortcutInput.Text(ShortcutActions.ToggleViewportRendering)}. Applies now.");
        bool pause = settings.PauseRenderWhenGameRunning;
        if (ImGui.Checkbox("Pause rendering while Destiny 2 runs", ref pause)) { settings.PauseRenderWhenGameRunning = pause; SaveSettings(); }
        SettingNote("Unless the Live link is attached. The viewport's Resume button overrides it until the game closes. Applies now.");
        bool low = settings.LowMemoryMode;
        if (ImGui.Checkbox("Low memory mode (16 GB PCs)", ref low)) { settings.LowMemoryMode = low; SaveSettings(); }
        SettingNote("Bounds the world texture cache. Always on while Destiny 2 runs or the Live link is attached. Applies to the next map or shader load.");
        int budget = settings.TextureBudgetMiB;
        ImGui.SetNextItemWidth(220 * scale);
        if (ImGui.SliderInt("Texture budget (MiB)", ref budget, 64, 2048)) { settings.TextureBudgetMiB = budget; SaveSettings(); }
        SettingNote("Used while Low memory mode is on. Applies to the next map or shader load.");
        int focused = settings.FpsFocused, background = settings.FpsBackground;
        ImGui.SetNextItemWidth(220 * scale);
        if (ImGui.SliderInt("FPS cap, focused", ref focused, 0, 240, focused == 0 ? "no cap" : "%d")) { settings.FpsFocused = focused; SaveSettings(); }
        ImGui.SetNextItemWidth(220 * scale);
        if (ImGui.SliderInt("FPS cap, background", ref background, 0, 240, background == 0 ? "no cap" : "%d")) { settings.FpsBackground = background; SaveSettings(); }
        SettingNote("0 means no cap. Lower caps from the Live link (30/10), Destiny 2 (15/5) and viewport off (30/10) still apply. Applies now.");
    }

    private void DrawAiSettings()
    {
        ImGui.SetNextItemWidth(220 * scale);
        if (ImGui.BeginCombo("Default engine", AiEngines[aiEngine].Name))
        {
            for (int i = 0; i < AiEngines.Length; i++)
                if (ImGui.Selectable(AiEngines[i].Name, i == aiEngine)) SelectAiEngine(i);
            ImGui.EndCombo();
        }
        SettingNote("The engine the AI workspace starts. Applies to the next Start.");
        bool allow = settings.AiAutoAllow;
        if (ImGui.Checkbox("Auto-allow one-time prompts", ref allow)) { settings.AiAutoAllow = allow; aiAllowSeen = null; SaveSettings(); }
        bool resume = settings.AiResumeSessions;
        if (ImGui.Checkbox("Resume last session on Start/Restart", ref resume)) { settings.AiResumeSessions = resume; SaveSettings(); }
        bool mcp = settings.AiMcpEnabled;
        if (ImGui.Checkbox("MCP server (Solforge tools for AI engines)", ref mcp)) SetAiMcp(mcp);
        string status = "MCP off";
        AiServiceStatus(ref status);
        SettingNote(status + ". Off stops the server at once; running engines lose the Solforge tools until it is back on.");
        ImGui.Separator();
        DrawAiFolders();
    }

    private void DrawPathSettings()
    {
        ImGui.TextDisabled("Game install");
        ImGui.TextWrapped(workspace.Catalog?.GameRoot ?? (settings.SunrisePath.Length > 0 ? settings.SunrisePath : "-"));
        if (ImGui.Button("Change installation...")) settingsDialog = true;
        SettingNote("Reopens the packages; the open project stays.");
        ImGui.Spacing();
        ImGui.TextDisabled("Project");
        ImGui.TextWrapped(workspace.Project?.DirectoryPath ?? "No project open");
        ImGui.BeginDisabled(workspace.IsBusy);
        if (ImGui.Button("Open project...")) OpenProjectDialog();
        ImGui.EndDisabled();
        ImGui.Spacing();
        PathRow("Server mission scripts", ref preferencesScripts, value => settings.ScriptsPath = value,
            "Destiny-Sol-main\\mission-scripts or the server's scripts folder. The AI tools search it for mission code. Applies now.");
        PathRow("Blender", ref preferencesBlender, value => settings.BlenderPath = value,
            "blender.exe, read by the AI playbooks (get_settings). Blank: they look for it themselves.");
    }

    private void PathRow(string label, ref string text, Action<string> store, string note)
    {
        ImGui.TextDisabled(label);
        ImGui.PushID(label);
        ImGui.SetNextItemWidth(-ButtonWidth("Save") - ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputText("##path", ref text, 1024);
        ImGui.SameLine();
        string value = text.Trim().Trim('"');
        bool valid = value.Length == 0 || Directory.Exists(value) || File.Exists(value);
        ImGui.BeginDisabled(!valid);
        if (ImGui.Button("Save")) { store(value); SaveSettings(); }
        ImGui.EndDisabled();
        if (!valid) ImGui.TextColored(SolTheme.Sun, "Not found.");
        SettingNote(note);
        ImGui.PopID();
    }

    private void DrawLiveSettings()
    {
        ImGui.TextUnformatted(livePage.IsAttached ? "Attached to a running game." : "Not attached.");
        if (ImGui.Button("Open the Live tab")) { focusLive = true; preferencesOpen = false; }
        SettingNote("Attaching needs core.studio_attach = true in the game's bin\\x64\\Sunrise\\settings.json. While attached, "
            + "Solforge draws at most 30 frames a second (10 in the background) and keeps Low memory mode on.");
        bool pause = settings.PauseRenderWhenGameRunning;
        if (ImGui.Checkbox("Pause rendering while Destiny 2 runs detached", ref pause)) { settings.PauseRenderWhenGameRunning = pause; SaveSettings(); }
    }

    private void DrawInterfaceSettings()
    {
        ImGui.TextDisabled("Layout");
        foreach (string name in StudioLayout.PresetNames)
        {
            if (ImGui.Button(name)) ApplyLayoutPreset(name);
            ImGui.SameLine();
        }
        ImGui.NewLine();
        if (ImGui.Button("Reset layout")) ResetLayout();
        SettingNote("Each project remembers its own layout, including collapsed side panels. Applies now.");
        ImGui.Spacing();
        ImGui.TextDisabled("Theme");
        ImGui.TextUnformatted("Sol (dark)");
        SettingNote("Solforge has one theme for now.");
        ImGui.Spacing();
        float size = settings.UiScale;
        ImGui.SetNextItemWidth(220 * scale);
        if (ImGui.SliderFloat("Interface size", ref size, .8f, 1.5f, "%.2fx")) settings.UiScale = size;
        // Fonts rebuild between frames once the slider is let go.
        if (ImGui.IsItemDeactivatedAfterEdit()) { SaveSettings(); pendingDpi = DeviceDpi; }
        SettingNote("Text and control size on top of the monitor's DPI. Applies when you let go of the slider.");
    }
}
