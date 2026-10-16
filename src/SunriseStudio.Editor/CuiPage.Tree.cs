using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class CuiPage
{
    private readonly HashSet<int> hiddenWidgets = [], expandedWidgets = [];
    private HashSet<int> hiddenWidgetTree = [];
    private Dictionary<int, int[]> widgetChildren = [];
    private Dictionary<int, CuiComposedNode> widgetNodes = [];
    private (int Id, int Depth, bool HasChildren)[] widgetRows = [];
    private CuiComposition? treeScene;
    private uint treeHierarchy;
    private string treeFilter = "";
    private bool treeDirty = true;

    /** Hidden widgets are view state for the open screen; they never change package data. */
    private void PrepareWidgetTree(CuiComposition scene)
    {
        if (treeHierarchy != document!.TagHash)
        { hiddenWidgets.Clear(); expandedWidgets.Clear(); treeHierarchy = document.TagHash; treeDirty = true; }
        if (!ReferenceEquals(treeScene, scene))
        {
            treeScene = scene; treeDirty = true;
            widgetNodes = scene.Nodes.ToDictionary(node => node.Id);
            var ids = scene.Nodes.Select(node => node.Id).ToHashSet();
            hiddenWidgets.IntersectWith(ids); expandedWidgets.IntersectWith(ids);
            // Unparented nodes and nodes whose parent is absent are listed at the top level.
            widgetChildren = scene.Nodes.Where(node => node.Id != 0)
                .GroupBy(node => ids.Contains(node.ParentId) ? node.ParentId : 0)
                .ToDictionary(group => group.Key, group => group.Select(node => node.Id).Order().ToArray());
        }
        if (revealComposed && selectedComposed > 0)
        {
            var parents = scene.Nodes.ToDictionary(node => node.Id, node => node.ParentId);
            HashSet<int> visited = [];
            for (int id = parents.GetValueOrDefault(selectedComposed, -1); id > 0 && visited.Add(id); id = parents.GetValueOrDefault(id, -1))
                expandedWidgets.Add(id);
            treeDirty = true;
        }
        if (treeFilter != filter) { treeFilter = filter; treeDirty = true; }
        if (!treeDirty) return;
        treeDirty = false;
        hiddenWidgetTree = [];
        var pending = new Stack<int>(hiddenWidgets);
        while (pending.TryPop(out int id))
            if (hiddenWidgetTree.Add(id) && widgetChildren.TryGetValue(id, out var nested))
                foreach (int child in nested) pending.Push(child);
        HashSet<int>? included = null;
        if (filter.Length != 0)
        {
            var parents = scene.Nodes.ToDictionary(node => node.Id, node => node.ParentId);
            included = [];
            foreach (var node in scene.Nodes.Where(node => node.Id != 0 && (StructureName(node).Contains(filter, StringComparison.OrdinalIgnoreCase)
                || node.Id.ToString().Contains(filter, StringComparison.Ordinal))))
                for (int id = node.Id; id > 0 && included.Add(id); id = parents.GetValueOrDefault(id, -1)) { }
        }
        List<(int, int, bool)> rows = [];
        var stack = new Stack<(int Id, int Depth)>(widgetChildren.GetValueOrDefault(0, []).Reverse().Select(id => (id, 0)));
        while (stack.TryPop(out var item))
        {
            if (included is not null && !included.Contains(item.Id)) continue;
            var nested = widgetChildren.GetValueOrDefault(item.Id, []);
            rows.Add((item.Id, item.Depth, nested.Length != 0));
            if (included is not null || expandedWidgets.Contains(item.Id))
                foreach (int child in nested.Reverse()) stack.Push((child, item.Depth + 1));
        }
        widgetRows = rows.ToArray();
    }

    private unsafe void DrawWidgetTree(CuiComposition scene)
    {
        PrepareWidgetTree(scene);
        var nodes = widgetNodes;
        if (ImGuiIcons.Button("cui-reveal", StudioIcon.Focus, "Reveal selection")) revealComposed = true;
        ImGui.SameLine();
        if (ImGuiIcons.Button("cui-show-all", StudioIcon.Eye, "Show all widgets")) { hiddenWidgets.Clear(); treeDirty = true; }
        ImGui.SameLine();
        if (ImGuiIcons.Button("cui-collapse", StudioIcon.Tree, "Collapse tree")) { expandedWidgets.Clear(); treeDirty = true; }
        ImGui.SameLine(); ImGui.SetNextItemWidth(-1);
        UiAuto.InputTextWithHint("##cui-composed-filter", "Find widget", ref filter, 256);
        float rowHeight = ImGui.GetFrameHeight(), iconSize = rowHeight - 4;
        if (!ImGui.BeginTable("cui-widgets", 2, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp,
            ImGui.GetContentRegionAvail())) return;
        ImGui.TableSetupColumn("Widget", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Visibility", ImGuiTableColumnFlags.WidthFixed, iconSize + 2);
        if (revealComposed)
        {
            int index = Array.FindIndex(widgetRows, row => row.Id == selectedComposed);
            if (index >= 0) ImGui.SetScrollY(Math.Max(0, index * rowHeight - ImGui.GetWindowHeight() * .4f));
            revealComposed = false;
        }
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(widgetRows.Length, rowHeight);
        while (clipper.Step())
            for (int index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var (id, depth, hasChildren) = widgetRows[index];
                var node = nodes[id];
                ImGui.PushID(id);
                ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                ImGui.TableNextColumn();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Min(depth, 12) * 10);
                bool expanded = filter.Length != 0 || expandedWidgets.Contains(id);
                var chevron = ImGui.GetCursorScreenPos();
                if (hasChildren && filter.Length == 0)
                {
                    if (ImGui.InvisibleButton("expand", new(12, iconSize)))
                    { if (!expandedWidgets.Remove(id)) expandedWidgets.Add(id); treeDirty = true; }
                    ImGuiIcons.Draw(ImGui.GetWindowDrawList(), expanded ? StudioIcon.Collapse : StudioIcon.Expand,
                        chevron + new Vector2(2, (iconSize - 8) / 2), 8, ImGui.GetColorU32(ImGuiCol.TextDisabled));
                }
                else ImGui.Dummy(new(12, iconSize));
                ImGui.SameLine(0, 2);
                bool visual = node.NativeTypes.HasLayout(node.Definition.TypeHash, node.Definition.AuxiliaryTypeHash);
                bool bound = applyBindings && bindingHidden.Contains(id), hidden = hiddenWidgetTree.Contains(id);
                var liveState = LiveOverlay ? liveJoin.State(id) : (CuiLiveState?)null;
                bool liveHidden = liveState is CuiLiveState.Hidden or CuiLiveState.HiddenByParent, notBuilt = liveState == CuiLiveState.NotBuilt;
                if (notBuilt) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * .45f);
                var symbol = ImGui.GetCursorScreenPos();
                ImGui.Dummy(new(14, iconSize));
                ImGuiIcons.Draw(ImGui.GetWindowDrawList(), WidgetIcon(node), symbol + new Vector2(1, (iconSize - 12) / 2), 12,
                    ImGui.GetColorU32(visual && !hidden && !bound ? StudioTheme.Muted : new Vector4(.28f, .31f, .36f, 1)));
                ImGui.SameLine(0, 4);
                bool muted = hidden || bound || !visual || liveHidden;
                if (muted) ImGui.PushStyleColor(ImGuiCol.Text, StudioTheme.Muted);
                if (UiAuto.Selectable(StructureName(node), id == selectedComposed, ImGuiSelectableFlags.AllowDoubleClick, new(0, iconSize)))
                {
                    selectedComposed = id;
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) focusComposed = true;
                }
                if (muted) ImGui.PopStyleColor();
                if (notBuilt) ImGui.PopStyleVar();
                // The Game state icon, not an eye, so the mark never reads as a second visibility toggle.
                if (liveHidden)
                    ImGuiIcons.Draw(ImGui.GetWindowDrawList(), StudioIcon.Loop, new(ImGui.GetItemRectMax().X - 12, ImGui.GetItemRectMin().Y
                        + (iconSize - 10) / 2), 10, ImGui.GetColorU32(StudioTheme.Muted with { W = LiveStale ? LiveStaleAlpha : 1 }));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{node.Definition.Name} / Node {id}{(bound ? "\nHidden by binding" : "")}"
                        + (node.Limitation.Length == 0 ? "" : "\n" + node.Limitation)
                        + (liveState is { } state ? "\nIn game: " + LiveStateText(state) + (LiveStale ? " (stale)" : "") : ""));
                ImGui.TableNextColumn();
                bool own = hiddenWidgets.Contains(id), inherited = hidden && !own;
                ImGui.BeginDisabled(inherited);
                if (ImGuiIcons.Button("visibility", hidden ? StudioIcon.EyeOff : StudioIcon.Eye,
                    inherited ? "Hidden by parent" : own ? "Show widget and children" : "Hide widget and children", size: iconSize))
                { if (!hiddenWidgets.Remove(id)) hiddenWidgets.Add(id); treeDirty = true; }
                ImGui.EndDisabled();
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
        ImGui.EndTable();
    }

    private static StudioIcon WidgetIcon(CuiComposedNode node) =>
        node.NativeTypes.IsContainer(node.Definition.TypeHash, node.Definition.AuxiliaryTypeHash) ? StudioIcon.Folder
            : node.Definition.TypeHash == CuiTypeRegistry.FrameType ? StudioIcon.Frame
            : node.NativeTypes.HasLayout(node.Definition.TypeHash, node.Definition.AuxiliaryTypeHash) ? StudioIcon.Cube
            : StudioIcon.Filter;
}
