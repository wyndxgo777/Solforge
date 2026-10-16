using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private StudioLayout layout;
    private int? maximizedPanel;
    private Action? pendingLayoutChange;
    private bool layoutDirty;
    public StudioLayout EditorLayout => layout;

    // Solforge: Reset layout uses the Solforge preset; Window > Layouts keeps the classic and upstream ones.
    public void ResetLayout() => ApplyLayoutPreset(StudioLayout.SolforgePreset);

    private static string PanelTitle(StudioPanelKind kind) => kind switch
    {
        StudioPanelKind.Browser => "Asset browser",
        StudioPanelKind.ObjectTree => "Object tree",
        StudioPanelKind.Changes => "Project changes",
        StudioPanelKind.MissionGraph => "Mission Graph",
        StudioPanelKind.Empty => "Choose editor",
        _ => kind.ToString()
    };

    private void DrawLayout(Vector2 position, Vector2 size)
    {
        UseProjectLayout();
        if (!workspace.IsBusy && workspace.World is null)
        { focusWorldScene = focusMissionPage = false; }
        // A focus request shows a popped-out editor's window instead of docking a second copy.
        if ((focusAssets || focusLive || focusAssetPage || focusWorldScene || focusMissionPage)
            && !ShowPopout(StudioPanelKind.Workspace))
        {
            bool absent = layout.Find(StudioPanelKind.Workspace) is null;
            var editor = layout.Show(StudioPanelKind.Workspace);
            layoutDirty |= absent;
            if (maximizedPanel is not null) maximizedPanel = editor.Id;
        }
        if (focusMissionGraph && !ShowPopout(StudioPanelKind.MissionGraph))
        {
            bool absent = layout.Find(StudioPanelKind.MissionGraph) is null;
            var graph = layout.Show(StudioPanelKind.MissionGraph);
            layoutDirty |= absent;
            if (maximizedPanel is not null) maximizedPanel = graph.Id;
        }
        if (focusHierarchy && !ShowPopout(StudioPanelKind.Browser))
        {
            bool absent = layout.Find(StudioPanelKind.Browser) is null;
            layout.Show(StudioPanelKind.Browser);
            layoutDirty |= absent;
            maximizedPanel = null;
        }
        var root = maximizedPanel is { } id ? layout.Find(id) : null;
        if (root is not { IsLeaf: true }) { root = layout.Root; maximizedPanel = null; }
        if (layout.Find(StudioPanelKind.MissionGraph) is null && !IsPoppedOut(StudioPanelKind.MissionGraph))
            missionGraphActive = false;
        DrawLayoutNode(root, position, size);
        if (pendingLayoutChange is { } change)
        {
            pendingLayoutChange = null;
            change();
            layoutDirty = true;
        }
        if (layoutDirty && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) SaveLayout();
    }

    /** Split bounds shrink proportionally when the window cannot fit every area's minimum. */
    private void DrawLayoutNode(StudioLayoutNode node, Vector2 position, Vector2 size)
    {
        if (node.IsLeaf) { DrawLayoutPanel(node, position, size); return; }
        bool columns = node.Axis == StudioSplitAxis.Columns;
        float divider = Math.Min(5 * scale, columns ? size.X : size.Y);
        float span = Math.Max(0, (columns ? size.X : size.Y) - divider);
        var firstMin = StudioLayout.MinimumSize(node.First!, scale);
        var secondMin = StudioLayout.MinimumSize(node.Second!, scale);
        float a = columns ? firstMin.X : firstMin.Y, b = columns ? secondMin.X : secondMin.Y;
        float shrink = Math.Min(1, span / (a + b));
        float cut = Math.Clamp(span * node.Ratio, a * shrink, Math.Max(a * shrink, span - b * shrink));
        // A folded side panel keeps its ratio for when it opens again and takes only a strip now.
        bool firstFolded = IsFolded(node.First!), secondFolded = IsFolded(node.Second!);
        if (firstFolded != secondFolded)
        {
            float whole = columns ? size.X : size.Y, strip = Math.Min(whole, FoldStrip(columns));
            cut = firstFolded ? strip : whole - strip;
            var firstPart = columns ? new Vector2(cut, size.Y) : new Vector2(size.X, cut);
            var rest = columns ? new Vector2(cut, 0) : new Vector2(0, cut);
            DrawLayoutNode(node.First!, position, firstPart);
            DrawLayoutNode(node.Second!, position + rest, Vector2.Max(Vector2.One, size - rest));
            return;
        }
        var firstSize = columns ? new Vector2(cut, size.Y) : new Vector2(size.X, cut);
        var offset = columns ? new Vector2(cut, 0) : new Vector2(0, cut);
        var splitterSize = columns ? new Vector2(divider, size.Y) : new Vector2(size.X, divider);
        DrawLayoutNode(node.First!, position, firstSize);
        Splitter($"layout-{node.Id}", position + offset, splitterSize, delta =>
        {
            if (span <= 0) return;
            node.Ratio = Math.Clamp((cut + (columns ? delta.X : delta.Y)) / span, .01f, .99f);
            layoutDirty = true;
        });
        var secondOffset = columns ? new Vector2(cut + divider, 0) : new Vector2(0, cut + divider);
        DrawLayoutNode(node.Second!, position + secondOffset, Vector2.Max(Vector2.One, size - secondOffset));
    }

    private void DrawLayoutPanel(StudioLayoutNode node, Vector2 position, Vector2 size)
    {
        if (IsFolded(node) && maximizedPanel != node.Id) { DrawFoldedPanel(node, position, size); return; }
        float header = Math.Min(size.Y, ImGui.GetFrameHeight() + 8 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4 * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        BeginPanel($"area-{node.Id}-header", position, new(size.X, header), ImGuiWindowFlags.NoScrollbar);
        if (node.Panel != StudioPanelKind.Empty) TrackAreaFocus(node.Panel == StudioPanelKind.MissionGraph);
        float button = ImGui.GetFrameHeight();
        // Solforge: Sol panel header (notch + uppercase title); the combo keeps its id and behaviour.
        float titleWidth = Math.Max(button, size.X - button - 16 * scale - SolTheme.HeaderIndent);
        SolTheme.PanelHeader(PanelTitle(node.Panel), titleWidth);
        ImGui.SetNextItemWidth(titleWidth);
        SolTheme.PushHeaderCombo();
        if (UiAuto.BeginCombo("##editor-type", ""))
        {
            foreach (var kind in Enum.GetValues<StudioPanelKind>())
            {
                if (UiAuto.Selectable(PanelTitle(kind), node.Panel == kind))
                    pendingLayoutChange = () => { ClosePopout(kind); layout.SetPanel(node.Id, kind); };
                if (kind != node.Panel && kind != StudioPanelKind.Empty && ImGui.IsItemHovered())
                {
                    if (layout.Find(kind) is not null) ImGui.SetTooltip("Swap with the open editor");
                    else if (IsPoppedOut(kind)) ImGui.SetTooltip("Bring back from its own window");
                }
            }
            UiAuto.EndCombo();
        }
        ImGui.PopStyleColor(3);
        ImGui.SameLine(0, 4 * scale);
        if (UiAuto.Button("...", new(button, 0))) ImGui.OpenPopup("Area");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Area options");
        if (ImGui.BeginPopup("Area"))
        {
            bool canSplit = layout.Panels.Count < StudioLayout.MaximumPanels;
            if (UiAuto.MenuItem("Split left / right", "", false, canSplit && size.X >= 280 * scale))
                pendingLayoutChange = () => { layout.Split(node.Id, StudioSplitAxis.Columns); maximizedPanel = null; };
            if (UiAuto.MenuItem("Split top / bottom", "", false, canSplit && size.Y >= 220 * scale))
                pendingLayoutChange = () => { layout.Split(node.Id, StudioSplitAxis.Rows); maximizedPanel = null; };
            ImGui.Separator();
            if (UiAuto.MenuItem(maximizedPanel is null ? "Maximize area" : "Restore areas"))
                pendingLayoutChange = () => maximizedPanel = maximizedPanel is null ? node.Id : null;
            if (UiAuto.MenuItem("Pop out window", "", false, node.Panel != StudioPanelKind.Empty && renderer is not null))
                pendingLayoutChange = () => PopOut(node);
            if (StudioLayoutNode.Collapsible(node.Panel))
            {
                ImGui.Separator();
                if (UiAuto.MenuItem("Collapse to a strip", "", node.Collapsed == true))
                    pendingLayoutChange = () => node.Collapsed = node.Collapsed == true ? false : true;
                if (UiAuto.MenuItem("Collapse while empty", "", node.Collapsed is null))
                    pendingLayoutChange = () => node.Collapsed = node.Collapsed is null ? false : null;
            }
            if (UiAuto.MenuItem("Close area", "", false, node != layout.Root))
                pendingLayoutChange = () => { layout.Close(node.Id); maximizedPanel = null; };
            ImGui.EndPopup();
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
        position.Y += header;
        size.Y = Math.Max(1, size.Y - header);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        DrawPanelContent(node.Panel, node.Id, position, size);
        ImGui.PopStyleVar();
    }

    /** Draws one editor below its header; a docked area and a popped-out window share it. */
    private void DrawPanelContent(StudioPanelKind panel, int area, Vector2 position, Vector2 size)
    {
        switch (panel)
        {
            case StudioPanelKind.Workspace: DrawDocument(position, size); break;
            case StudioPanelKind.Browser: DrawBrowser(position, size); break;
            case StudioPanelKind.Inspector: DrawInspector(position, size); break;
            case StudioPanelKind.Changes: DrawChanges(position, size); break;
            case StudioPanelKind.AI: DrawAiWorkspace(position, size); break;
            case StudioPanelKind.MissionGraph:
                if (focusMissionGraph) ImGui.SetNextWindowFocus();
                BeginPanel("Mission graph area", position, size);
                if (focusMissionGraph || ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
                    missionGraphActive = true;
                focusMissionGraph = false;
                missionGraph.Draw(workspace);
                ImGui.End();
                break;
            case StudioPanelKind.ObjectTree:
                BeginPanel("Object tree", position, size);
                TrackAreaFocus();
                DrawObjectTree("layout-tree");
                ImGui.End();
                break;
            default:
                BeginPanel($"area-{area}-empty", position, size);
                ImGui.End();
                break;
        }
    }

    private void DrawLayoutMenu()
    {
        if (UiAuto.BeginMenu("Panels"))
        {
            foreach (var kind in Enum.GetValues<StudioPanelKind>().Where(kind => kind != StudioPanelKind.Empty))
                if (UiAuto.MenuItem(PanelTitle(kind), "", layout.Find(kind) is not null || IsPoppedOut(kind))
                    && !ShowPopout(kind, true))
                {
                    layout.Show(kind);
                    maximizedPanel = null;
                    layoutDirty = true;
                }
            UiAuto.EndMenu();
        }
        if (UiAuto.MenuItem("Restore areas", "", false, maximizedPanel is not null)) maximizedPanel = null;
        if (UiAuto.MenuItem("Reset layout")) ResetLayout();
    }

    private void SaveLayout()
    {
        settings.Layout = layout.Save();
        if (workspace.Project?.DirectoryPath is { } project) settings.ProjectLayouts[ProjectLayoutKey(project)] = settings.Layout.Value;
        layoutDirty = false;
        SaveSettings();
    }

    private string? layoutProject;

    private static string ProjectLayoutKey(string folder) => Path.GetFullPath(folder).TrimEnd('\\', '/').ToUpperInvariant();

    /** Each project keeps its own layout; opening one restores what it had, else keeps the current layout for it. */
    private void UseProjectLayout()
    {
        string? project = workspace.Project?.DirectoryPath;
        if (project == layoutProject) return;
        layoutProject = project;
        if (project is null || options.Smoke) return;
        if (settings.ProjectLayouts.TryGetValue(ProjectLayoutKey(project), out var saved))
        {
            foreach (var popout in popouts.ToArray()) ClosePopout(popout.Panel);
            layout = StudioLayout.Restore(saved, settings.BrowserWidth, settings.InspectorWidth, settings.ChangesHeight);
            maximizedPanel = null;
            browserSlot = null;
        }
        else layoutDirty = true;
    }

    /** A side panel shows as a strip when the user folded it, or while it is empty unless the user pinned it open. */
    private bool IsFolded(StudioLayoutNode node) => node.IsLeaf && StudioLayoutNode.Collapsible(node.Panel)
        && (node.Collapsed ?? PanelEmpty(node.Panel)) && !options.Smoke;

    private bool PanelEmpty(StudioPanelKind kind) => kind switch
    {
        StudioPanelKind.MissionGraph => missionGraph.ScriptPath is null && !missionGraph.IsDirty,
        StudioPanelKind.Browser => workspace.Assets.Length == 0 && workspace.Catalog is null,
        StudioPanelKind.Changes => workspace.Project is null,
        StudioPanelKind.ObjectTree => sceneTree is null,
        StudioPanelKind.Inspector => workspace.Document is null && selectedObject is null,
        _ => false
    };

    private float FoldStrip(bool columns) => ImGui.GetFrameHeight() + (columns ? 10 : 8) * scale;

    /** The strip: an expand button and the panel's name; clicking anywhere on it opens the panel. */
    private void DrawFoldedPanel(StudioLayoutNode node, Vector2 position, Vector2 size)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4 * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        BeginPanel($"area-{node.Id}-folded", position, size, ImGuiWindowFlags.NoScrollbar);
        bool open = UiAuto.Button("+", new(ImGui.GetFrameHeight(), 0));
        string title = PanelTitle(node.Panel);
        StudioUi.Tip($"Open {title}" + (node.Collapsed is null ? " (folded while empty; its area menu can keep it open)" : ""));
        if (size.X < size.Y)
        {
            // Columns: the name runs down the strip, one letter per line.
            var draw = ImGui.GetWindowDrawList();
            uint color = ImGui.GetColorU32(ImGuiCol.TextDisabled);
            var at = ImGui.GetCursorScreenPos() + new Vector2((ImGui.GetFrameHeight() - ImGui.CalcTextSize("M").X) / 2, 4 * scale);
            foreach (char letter in title.ToUpperInvariant())
            {
                if (at.Y + ImGui.GetTextLineHeight() > position.Y + size.Y - 4 * scale) break;
                if (letter != ' ') draw.AddText(at, color, letter.ToString());
                at.Y += ImGui.GetTextLineHeight() * (letter == ' ' ? .5f : .95f);
            }
        }
        else { ImGui.SameLine(); ImGui.AlignTextToFramePadding(); ImGui.TextDisabled(title); }
        if (!open && ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) open = true;
        if (open) pendingLayoutChange = () => node.Collapsed = false;
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private void TrackAreaFocus(bool mission = false)
    {
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) missionGraphActive = mission;
    }
}
