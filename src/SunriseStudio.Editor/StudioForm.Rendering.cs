using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * The 3D viewport render switch (toolbar, View menu, Ctrl+Shift+R, Settings > Rendering). Off stops every 3D draw in the
 * scene and asset viewports and shrinks their frame targets; panels, the Live link and the MCP tools keep running. It sits
 * above the Destiny 2 auto-suspend: off wins, and on still lets the auto-suspend pause rendering while the game runs.
 */
public sealed partial class StudioForm
{
    private long renderToggleFrame = -1, settingsShortcutFrame = -1;

    public void SetViewportRendering(bool on)
    {
        if (settings.ViewportRendering == on) return;
        settings.ViewportRendering = on;
        if (viewport is not null) viewport.RenderingOff = !on;
        if (assetViewport is not null) assetViewport.RenderingOff = !on;
        SaveSettings();
    }

    /** Seconds between frames: the game-sharing caps first, then the user's Settings caps; 0 draws every vsync. */
    private double FrameInterval(bool suspended, bool renderingOff)
    {
        bool focused = ContainsFocus;
        int cap = livePage.IsAttached ? (focused ? 30 : 10)
            : suspended ? (focused ? 15 : 5)
            : renderingOff ? (focused ? 30 : 10) : 0;
        int user = focused ? settings.FpsFocused : settings.FpsBackground;
        if (user > 0) cap = cap == 0 ? user : Math.Min(cap, user);
        return cap > 0 ? 1.0 / cap : 0;
    }

    /** Popout windows run the same handler; one press toggles once. */
    private void HandleRenderingShortcuts()
    {
        if (ShortcutInput.Pressed(ShortcutActions.ToggleViewportRendering) && renderToggleFrame != ShortcutInput.Frame)
        {
            renderToggleFrame = ShortcutInput.Frame;
            SetViewportRendering(!settings.ViewportRendering);
        }
        if (ShortcutInput.Pressed(ShortcutActions.OpenSettings) && settingsShortcutFrame != ShortcutInput.Frame)
        {
            settingsShortcutFrame = ShortcutInput.Frame;
            preferencesOpen = true;
        }
    }

    private void DrawRenderingMenuItem()
    {
        bool on = settings.ViewportRendering;
        if (ImGui.MenuItem("3D viewport rendering", ShortcutInput.Text(ShortcutActions.ToggleViewportRendering), ref on)) SetViewportRendering(on);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Off stops all 3D drawing to save GPU and memory; panels and the AI tools keep working.");
    }

    /** Toolbar: an eye that is lit while the 3D viewport renders. */
    private void DrawRenderingToolbarButton()
    {
        bool on = settings.ViewportRendering;
        string keys = ShortcutInput.Text(ShortcutActions.ToggleViewportRendering);
        string tip = (on ? "3D viewport rendering is on. Click to turn it off and save GPU and memory."
            : "3D viewport rendering is off. Click to turn it on.") + (keys.Length > 0 ? $" ({keys})" : "");
        if (destiny2Detected && settings.PauseRenderWhenGameRunning && !livePage.IsAttached && on)
            tip += "\nDestiny 2 is running, so rendering is paused anyway until the game closes.";
        if (ImGuiIcons.Button("tb-render", on ? StudioIcon.Eye : StudioIcon.EyeOff, tip, on)) SetViewportRendering(!on);
    }
}
