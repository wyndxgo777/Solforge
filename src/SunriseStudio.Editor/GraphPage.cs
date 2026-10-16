using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** The canvas shows authored links without assigning runtime behavior to unknown nodes. */
public sealed class GraphPage : IStudioPage
{
    private AssetGraph? active;
    private readonly Dictionary<string, Vector2> positions = [];
    private Vector2 pan = new(28, 28);
    private float zoom = 1;
    private string selected = "";
    public string Id => "sunrise.graph";
    public string Title => "Graph";
    public bool CanOpen(StudioDocument document) => document.Inspection.Graph?.Nodes.Count > 0;

    /** Edges and nodes outside the canvas are skipped; a click on a node selects its property. */
    public void Draw(StudioPageContext context)
    {
        if (context.Workspace.Document?.Inspection.Graph is not { } graph) return;
        if (!ReferenceEquals(active, graph)) Layout(graph);
        if (UiAuto.Button("Reset view")) { pan = new(28, 28); zoom = 1; }
        ImGui.SameLine(); ImGui.TextDisabled($"{graph.Nodes.Count:N0} nodes");
        var origin = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail();
        ImGui.InvisibleButton("graph-canvas", size, ImGuiButtonFlags.MouseButtonLeft
            | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
        var io = ImGui.GetIO();
        bool hovered = ImGui.IsItemHovered();
        var navigation = ShortcutInput.Canvas(hovered);
        pan += navigation.Pan;
        if (navigation.Zoom != 0)
        {
            var relative = navigation.ZoomAnchor - origin - pan;
            float next = Math.Clamp(zoom * MathF.Pow(1.15f, navigation.Zoom), .2f, 2);
            pan += relative * (1 - next / zoom);
            zoom = next;
        }
        var draw = ImGui.GetWindowDrawList();
        var end = origin + size;
        draw.AddRectFilled(origin, end, ImGui.GetColorU32(StudioTheme.Panel));
        draw.PushClipRect(origin, end, true);
        foreach (var edge in graph.Edges)
        {
            if (!positions.TryGetValue(edge.SourceId, out var source) || !positions.TryGetValue(edge.TargetId, out var target)) continue;
            var from = origin + pan + (source + new Vector2(210, 28)) * zoom;
            var to = origin + pan + (target + new Vector2(0, 28)) * zoom;
            if (Math.Max(from.X, to.X) < origin.X || Math.Min(from.X, to.X) > end.X
                || Math.Max(from.Y, to.Y) < origin.Y || Math.Min(from.Y, to.Y) > end.Y) continue;
            float bend = Math.Max(40, Math.Abs(to.X - from.X) * .5f);
            draw.AddBezierCubic(from, from + new Vector2(bend, 0), to - new Vector2(bend, 0), to,
                ImGui.GetColorU32(new Vector4(.42f, .33f, .28f, 1)), Math.Max(1, 1.5f * zoom));
        }
        foreach (var node in graph.Nodes)
        {
            var min = origin + pan + positions[node.Id] * zoom;
            var max = min + new Vector2(210, 56) * zoom;
            if (max.X < origin.X || min.X > end.X || max.Y < origin.Y || min.Y > end.Y) continue;
            bool hit = hovered && io.MousePos.X >= min.X && io.MousePos.X <= max.X
                && io.MousePos.Y >= min.Y && io.MousePos.Y <= max.Y;
            if (hit && ShortcutInput.Clicked(ShortcutSection.View2D, ShortcutActions.Select2D, out _))
            {
                selected = node.Id;
                context.SelectProperty(new("Node", node.Label, node.Offset));
            }
            draw.AddRectFilled(min, max, ImGui.GetColorU32(hit ? new Vector4(.14f, .16f, .20f, 1)
                : new Vector4(.095f, .11f, .14f, 1)), 5 * zoom);
            draw.AddRect(min, max, ImGui.GetColorU32(selected == node.Id ? StudioTheme.Accent
                : new Vector4(.22f, .25f, .30f, 1)), 5 * zoom);
            draw.PushClipRect(min + new Vector2(8, 0) * zoom, max - new Vector2(8, 0) * zoom, true);
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * zoom, min + new Vector2(10, 9) * zoom,
                ImGui.GetColorU32(ImGuiCol.Text), node.Label);
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * .8f * zoom, min + new Vector2(10, 31) * zoom,
                ImGui.GetColorU32(ImGuiCol.TextDisabled), $"{node.Offset:X8}");
            draw.PopClipRect();
            if (hit) ImGui.SetTooltip(node.Label);
        }
        draw.PopClipRect();
    }

    /** Columns by link depth from the roots; nodes no root reaches start their own columns. */
    private void Layout(AssetGraph graph)
    {
        active = graph; positions.Clear(); selected = ""; pan = new(28, 28); zoom = 1;
        var ids = graph.Nodes.Select(node => node.Id).ToHashSet();
        var children = graph.Edges.Where(edge => ids.Contains(edge.TargetId)).ToLookup(edge => edge.SourceId);
        var targets = graph.Edges.Select(edge => edge.TargetId).ToHashSet();
        var queue = new Queue<(string Id, int Depth)>();
        foreach (var node in graph.Nodes.Where(node => !targets.Contains(node.Id))) queue.Enqueue((node.Id, 0));
        var counts = new Dictionary<int, int>();
        void AddQueue()
        {
            while (queue.TryDequeue(out var row))
            {
                if (positions.ContainsKey(row.Id)) continue;
                int index = counts.GetValueOrDefault(row.Depth);
                counts[row.Depth] = index + 1;
                positions[row.Id] = new(row.Depth * 285, index * 86);
                foreach (var edge in children[row.Id]) queue.Enqueue((edge.TargetId, row.Depth + 1));
            }
        }
        AddQueue();
        foreach (var node in graph.Nodes.Where(node => !positions.ContainsKey(node.Id)))
        { queue.Enqueue((node.Id, 0)); AddQueue(); }
    }
}
