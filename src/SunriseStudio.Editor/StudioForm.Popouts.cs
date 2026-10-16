using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Editors in their own windows. Each window has its own ImGui context; the main tick draws them after itself. */
public sealed partial class StudioForm
{
    private readonly List<StudioPopout> popouts = [];

    // The old Mission Graph and Node Graph ids (saved popouts, callers) name the unified Graph panel.
    private bool IsPoppedOut(StudioPanelKind kind) => popouts.Any(popout => popout.Panel == StudioLayout.Canonical(kind));

    /** Reopens the saved windows; an editor the layout already shows stays docked. */
    private void RestorePopouts()
    {
        if (options.Smoke) { settings.Popouts = []; return; }
        foreach (var state in settings.Popouts.ToArray())
            if (layout.Find(state.Panel) is null && !IsPoppedOut(state.Panel))
            {
                // A window that cannot open docks its editor; the editor must stay reachable.
                try { OpenPopout(state.Panel, new Rectangle(state.X, state.Y, state.Width, state.Height)); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { workspace.Error = error.Message; layout.Show(state.Panel); }
            }
        RememberPopouts();
    }

    /** Once per settings file: a saved layout with no AI area and no AI window gets the panel on the right. */
    private void OfferAiPanel()
    {
        if (options.Smoke || settings.AiPanelOffered) return;
        settings.AiPanelOffered = true;
        if (layout.Find(StudioPanelKind.AI) is null && !IsPoppedOut(StudioPanelKind.AI) && layout.AddAiRight()) layoutDirty = true;
        layoutDirty = true;
    }

    /** The area closes and its editor moves to a new window; the last area stays, empty. */
    private void PopOut(StudioLayoutNode node)
    {
        var kind = node.Panel;
        if (kind == StudioPanelKind.Empty || IsPoppedOut(kind)) return;
        if (node == layout.Root) node.Panel = StudioPanelKind.Empty;
        else layout.Close(node.Id);
        maximizedPanel = null;
        layoutDirty = true;
        OpenPopout(kind, null);
    }

    private void OpenPopout(StudioPanelKind kind, Rectangle? bounds)
    {
        kind = StudioLayout.Canonical(kind);
        var popout = new StudioPopout(kind, PanelTitle(kind), studioIcon, bounds);
        popout.Show(this);
        try { popout.Attach(renderer!, gui!, scale); }
        catch { popout.Dispose(); throw; }
        popouts.Add(popout);
        RememberPopouts();
    }

    /** Puts the editor back into the main window. */
    private void PopIn(StudioPopout popout)
    {
        var kind = popout.Panel;
        ClosePopout(kind);
        if (layout.Find(kind) is null) layout.Show(kind);
        layoutDirty = true;
    }

    /** Closes the editor's window, if it has one, without docking it. */
    private void ClosePopout(StudioPanelKind kind)
    {
        kind = StudioLayout.Canonical(kind);
        if (popouts.FirstOrDefault(popout => popout.Panel == kind) is not { } popout) return;
        popouts.Remove(popout);
        popout.Dispose();
        RememberPopouts();
    }

    /**
     * Shows a popped-out editor's window; a minimized one is restored.
     * @param activate Also gives the window keyboard focus.
     * @return False when the editor is docked.
     */
    private bool ShowPopout(StudioPanelKind kind, bool activate = false)
    {
        kind = StudioLayout.Canonical(kind);
        if (popouts.FirstOrDefault(popout => popout.Panel == kind) is not { } popout) return false;
        if (popout.WindowState == FormWindowState.Minimized) popout.WindowState = FormWindowState.Normal;
        if (activate) popout.Activate();
        return true;
    }

    private void RememberPopouts() => settings.Popouts = popouts.Select(popout =>
    {
        var bounds = popout.WindowState == FormWindowState.Normal ? popout.Bounds : popout.RestoreBounds;
        return new StudioPopoutState(popout.Panel, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }).ToList();

    /** Draws each window with its own target and context, after the main window's frame. */
    private void DrawPopouts(float delta)
    {
        foreach (var popout in popouts.ToArray())
        {
            if (popout.PopInRequested) { PopIn(popout); continue; }
            var size = popout.ClientSize;
            if (!popout.IsAttached || popout.WindowState == FormWindowState.Minimized || size.Width <= 0 || size.Height <= 0)
                continue;
            renderer!.BeginWindowFrame(popout.Target, size.Width, size.Height, StudioTheme.Canvas);
            try
            {
                popout.Gui.NewFrame(delta);
                ShortcutInput.BeginFrame(popout.Gui.IsInputActive && EditorReady && !workspace.IsBusy);
                // Solforge: busy or loading, a popped-out editor stays drawn; DrawPanelContent holds the editing ones.
                if (EditorReady && !workspace.IsBusy) { HandleShortcuts(); DrawPopoutContent(popout, size); HandleViewShortcuts(); }
                else if (EditorReady || LoadOverlay) DrawPopoutContent(popout, size);
                popout.Chrome.EndFrame();
                popout.Gui.Render();
            }
            finally { renderer.EndWindowFrame(popout.Target); }
        }
        // Code after the frame expects the main window's context.
        gui?.MakeCurrent();
    }

    private void DrawPopoutContent(StudioPopout popout, Size client)
    {
        var size = new Vector2(client.Width, client.Height);
        float header = ImGui.GetFrameHeight() + 8 * scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4 * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        BeginPanel("popout-header", Vector2.Zero, new(size.X, header), ImGuiWindowFlags.NoScrollbar);
        // The header is the caption wherever no control sits; its close button docks the editor.
        popout.Chrome.Scale = scale;
        popout.Chrome.CaptionHeight = header;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(PanelTitle(popout.Panel));
        float titleEnd = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + ImGui.GetStyle().ItemSpacing.X;
        string label = "Pop in";
        float width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
        float buttons = 3 * 46 * scale;
        ImGui.SameLine(MathF.Max(titleEnd, size.X - buttons - width - 8 * scale));
        if (UiAuto.Button(label)) popout.RequestPopIn();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Put this editor back into the main window");
        popout.Chrome.DrawButtons(header, popout.RequestPopIn);
        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        DrawPanelContent(popout.Panel, 0, new(0, header), new(size.X, MathF.Max(1, size.Y - header)));
        ImGui.PopStyleVar();
    }
}
