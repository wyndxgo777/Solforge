using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Solforge Phase 03B: Window menu, layout presets (Studio.md §3.9) and the Ctrl+Space Asset browser drawer. */
public sealed partial class StudioForm
{
    private StudioLayoutSlot? browserSlot;
    private long browserToggleFrame = -1;

    /**
     * A new install has no settings file yet; any saved layout, even an upstream one, is kept.
     * Smoke runs keep the upstream default their layout checks were written against.
     */
    private void UseFirstRunLayout()
    {
        if (!options.Smoke && settings.Layout is null && !File.Exists(settingsPath)) ApplyLayoutPreset(StudioLayout.Ue5Preset);
    }

    public void ApplyLayoutPreset(string name)
    {
        foreach (var popout in popouts.ToArray()) ClosePopout(popout.Panel);
        layout = StudioLayout.Preset(name);
        maximizedPanel = null;
        browserSlot = null;
        settings.BrowserWidth = settings.InspectorWidth = 300;
        settings.ChangesHeight = 190;
        // UE5 docks the object tree as its own Outliner area, so the copy inside the scene view starts hidden.
        settings.ShowSceneTree = name != StudioLayout.Ue5Preset;
        settings.SceneTreeWidth = name == StudioLayout.SolforgePreset ? 160 : 280;
        layoutDirty = true;
    }

    private void DrawWindowMenu()
    {
        if (!ImGui.BeginMenu("Window")) return;
        if (ImGui.BeginMenu("Layouts"))
        {
            foreach (string name in StudioLayout.PresetNames)
                if (ImGui.MenuItem(name)) ApplyLayoutPreset(name);
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem("Asset browser drawer", ShortcutInput.Text(ShortcutActions.ToggleBrowser))) ToggleBrowserDrawer();
        ImGui.Separator();
        DrawLayoutMenu();
        ImGui.EndMenu();
    }

    private void HandleLayoutShortcuts()
    {
        // Popout windows run the same handler; one press toggles once.
        if (!ShortcutInput.Pressed(ShortcutActions.ToggleBrowser) || browserToggleFrame == ShortcutInput.Frame) return;
        browserToggleFrame = ShortcutInput.Frame;
        // Ctrl sinks and Space rises in the player camera, so the chord stays with the camera there.
        if (viewport is { PlayerCamera: true, IsHovered: true }) return;
        ToggleBrowserDrawer();
    }

    /** Takes the Asset browser area out, or puts it back beside the neighbour it had. */
    public void ToggleBrowserDrawer()
    {
        if (ShowPopout(StudioPanelKind.Browser, true)) return;
        if (layout.Find(StudioPanelKind.Browser) is not null)
        {
            if (layout.Remove(StudioPanelKind.Browser) is not { } slot) return;
            browserSlot = slot;
        }
        else
        {
            if (browserSlot is not { } slot || !layout.Reinsert(StudioPanelKind.Browser, slot)) layout.Show(StudioPanelKind.Browser);
            browserSlot = null;
        }
        maximizedPanel = null;
        layoutDirty = true;
    }
}
