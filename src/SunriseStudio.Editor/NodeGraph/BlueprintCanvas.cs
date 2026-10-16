using System.Numerics;
using ImGuiNET;
using Solforge.Graphs;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * Blueprint-style canvas for Solforge.Graphs documents: smooth pan and zoom, typed coloured pins, drag a wire into
 * empty space for a filtered add menu (also right-click or Tab), reroutes (double-click a wire), comment boxes,
 * box and multi-select, align and distribute, clipboard, functions (double-click a call to open it), node
 * problems from live validation, and a minimap. Every edit goes through GraphEditor, so undo covers it.
 */
internal sealed class BlueprintCanvas
{
    private enum Mode { None, MoveNodes, Link, Box, ResizeComment, Minimap }
    private readonly record struct DragPin(string Node, string Pin, bool Output, PinType Type);
    private readonly record struct Hit(GraphNode? Node, PinDef? Pin, GraphNode? PinNode, GraphComment? Comment, bool CommentCorner, GraphLink? Link);

    private const float MinZoom = .1f, MaxZoom = 2.5f, MapWidth = 200, MapHeight = 140;
    private Vector2 pan = new(60), targetPan = new(60), anchorScreen, anchorWorld, origin, size = new(800, 600), boxStart;
    private float zoom = 1, targetZoom = 1;
    private bool zooming, fitRequested = true, moved, focusQuery;
    private Mode mode;
    private DragPin? linkFrom, menuPin;
    private string resizing = "", query = "", contextNode = "", status = "";
    private double statusUntil;
    private int menuIndex;
    private Vector2 menuWorld;
    private GraphLink? selectedLink, contextLink;
    private HashSet<(string, string)> linked = [];

    /** Live validation results by node id (main graph or the open function). */
    public IReadOnlyDictionary<string, List<GraphDiagnostic>> Problems { get; set; } = new Dictionary<string, List<GraphDiagnostic>>();
    public bool ShowMinimap { get; set; } = true;
    public float Zoom => zoom;

    public void FrameAll() => fitRequested = true;

    public void Say(string text)
    {
        status = text;
        statusUntil = ImGui.GetTime() + 4;
    }

    private Vector2 Screen(Vector2 world) => origin + pan + world * zoom;
    private Vector2 World(Vector2 screen) => (screen - origin - pan) / zoom;
    private static uint U32(Vector4 color) => ImGui.GetColorU32(color);
    private static Vector4 V(Vector4 color, float alpha) => color with { W = alpha };

    private (Vector2 Min, Vector2 Max) Box(GraphEditor editor, GraphNode node)
    {
        var (width, height) = GraphLayout.Size(editor.Document, node, editor.Scope);
        var min = Screen(new(node.X, node.Y));
        return (min, min + new Vector2(width, height) * zoom);
    }

    private Vector2 PinPosition(GraphEditor editor, GraphNode node, PinDef pin)
    {
        var (min, max) = Box(editor, node);
        if (node.Type == NodeLibrary.Reroute) return (min + max) / 2;
        float x = pin.Output ? max.X : min.X;
        if (node.Collapsed) return new(x, min.Y + GraphLayout.Header / 2 * zoom);
        int index = 0;
        foreach (var other in editor.PinsOf(node))
        {
            if (other.Output != pin.Output) continue;
            if (other.Id == pin.Id) break;
            index++;
        }
        return new(x, min.Y + (GraphLayout.Header + index * GraphLayout.Row + GraphLayout.Row / 2) * zoom);
    }

    /** Draws the canvas into the remaining region and applies this frame's input. */
    public void Draw(GraphEditor editor)
    {
        var io = ImGui.GetIO();
        origin = ImGui.GetCursorScreenPos();
        size = Vector2.Max(ImGui.GetContentRegionAvail(), new(50));
        ImGui.InvisibleButton("blueprint-canvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ShortcutInput.Enabled && ImGui.IsItemHovered();
        bool focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (fitRequested) { Fit(editor, editor.Nodes); fitRequested = false; }
        Navigate(editor, hovered, focused, io);

        linked = editor.Links.SelectMany(link => new[] { (link.FromNode, link.FromPin), (link.ToNode, link.ToPin) }).ToHashSet();
        var hit = hovered ? HitTest(editor, io.MousePos) : default;
        HandleMouse(editor, hovered, hit, io);

        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + size, true);
        draw.AddRectFilled(origin, origin + size, U32(new Vector4(.07f, .075f, .09f, 1)));
        DrawGrid(draw);
        DrawComments(draw, editor);
        DrawLinks(draw, editor, hit);
        foreach (var node in editor.Nodes) DrawNode(draw, editor, node, hit);
        if (mode == Mode.Link && linkFrom is { } from && editor.Node(from.Node) is { } fromNode
            && editor.Document.Pin(fromNode, from.Pin, from.Output, editor.Scope) is { } fromPin)
        {
            var a = PinPosition(editor, fromNode, fromPin);
            var color = U32(PinTypes.ColorOf(from.Type));
            if (from.Output) Wire(draw, a, io.MousePos, color, from.Type == PinType.Exec ? 3 : 2);
            else Wire(draw, io.MousePos, a, color, from.Type == PinType.Exec ? 3 : 2);
        }
        if (mode == Mode.Box)
        {
            draw.AddRectFilled(Vector2.Min(boxStart, io.MousePos), Vector2.Max(boxStart, io.MousePos), U32(V(StudioTheme.Accent, .08f)));
            draw.AddRect(Vector2.Min(boxStart, io.MousePos), Vector2.Max(boxStart, io.MousePos), U32(V(StudioTheme.Accent, .7f)));
        }
        if (ShowMinimap && editor.Nodes.Count > 0) DrawMinimap(draw, editor, hovered, io);
        DrawOverlay(draw, editor);
        draw.PopClipRect();
        if (hit.Node is { } tipNode && Problems.TryGetValue(tipNode.Id, out var problems) && problems.Count > 0)
            ImGui.SetTooltip(string.Join("\n", problems.Select(problem => $"{problem.Severity}: {problem.Message}")));
        else if (hit.Pin is { } tipPin && hit.PinNode is not null)
            ImGui.SetTooltip($"{(tipPin.Name.Length > 0 ? tipPin.Name : tipPin.Id)}: {PinTypes.Label(tipPin.Type)}{(tipPin.Multi ? " (many)" : "")}");
        if ((hovered || focused) && !io.WantTextInput) HandleKeys(editor, io, hovered);
        DrawMenus(editor);
    }

    // Navigation.

    private void Navigate(GraphEditor editor, bool hovered, bool focused, ImGuiIOPtr io)
    {
        var input = ShortcutInput.Canvas(hovered && mode is Mode.None);
        if (input.Pan != Vector2.Zero) { pan += input.Pan; targetPan += input.Pan; anchorScreen += input.Pan; }
        if (input.Zoom != 0)
        {
            float next = Math.Clamp(targetZoom * MathF.Pow(1.15f, input.Zoom), MinZoom, MaxZoom);
            anchorScreen = input.ZoomAnchor;
            anchorWorld = World(anchorScreen);
            targetZoom = next;
            targetPan = anchorScreen - origin - anchorWorld * targetZoom;
            zooming = true;
        }
        if ((hovered || focused) && ShortcutInput.Pressed(ShortcutActions.FrameAll2D)) Fit(editor, editor.Nodes);
        if ((hovered || focused) && ShortcutInput.Pressed(ShortcutActions.FrameSelection2D))
            Fit(editor, editor.Selection.Count > 0 ? editor.Nodes.Where(node => editor.Selection.Contains(node.Id)).ToList() : editor.Nodes);
        // Ease towards the target: zoom keeps the anchor fixed, pan glides.
        float blend = 1 - MathF.Exp(-Math.Clamp(io.DeltaTime, .001f, .1f) * 18);
        if (zooming)
        {
            zoom += (targetZoom - zoom) * blend;
            if (MathF.Abs(targetZoom - zoom) < .0005f) { zoom = targetZoom; zooming = false; pan = targetPan; }
            else pan = anchorScreen - origin - anchorWorld * zoom;
        }
        else
        {
            pan = Vector2.Lerp(pan, targetPan, blend);
            if (Vector2.DistanceSquared(pan, targetPan) < .25f) pan = targetPan;
        }
    }

    /** Eases the view to fit these nodes. */
    private void Fit(GraphEditor editor, IReadOnlyList<GraphNode> nodes)
    {
        if (nodes.Count == 0) { targetPan = new(60); targetZoom = 1; zooming = false; return; }
        float left = nodes.Min(node => node.X), top = nodes.Min(node => node.Y);
        float right = nodes.Max(node => node.X + GraphLayout.Size(editor.Document, node, editor.Scope).Width);
        float bottom = nodes.Max(node => node.Y + GraphLayout.Size(editor.Document, node, editor.Scope).Height);
        var extent = new Vector2(right - left, bottom - top) + new Vector2(160);
        float next = Math.Clamp(MathF.Min(size.X / extent.X, size.Y / extent.Y), MinZoom, 1.2f);
        var center = new Vector2(left + right, top + bottom) / 2;
        // Centre the nodes, then ease the zoom around the middle of the view.
        anchorWorld = center;
        anchorScreen = origin + size / 2;
        targetZoom = next;
        targetPan = size / 2 - center * next;
        zooming = true;
        pan = anchorScreen - origin - anchorWorld * zoom;
    }

    // Hit testing.

    private Hit HitTest(GraphEditor editor, Vector2 mouse)
    {
        float radius = MathF.Max(6, 8 * zoom);
        for (int i = editor.Nodes.Count - 1; i >= 0; i--)
        {
            var node = editor.Nodes[i];
            var (min, max) = Box(editor, node);
            if (mouse.X < min.X - radius || mouse.X > max.X + radius || mouse.Y < min.Y - radius || mouse.Y > max.Y + radius) continue;
            if (!node.Collapsed || node.Type == NodeLibrary.Reroute)
                foreach (var pin in editor.PinsOf(node))
                    if (Vector2.Distance(PinPosition(editor, node, pin), mouse) <= radius)
                        return new(node, pin, node, null, false, null);
            if (mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y) return new(node, null, null, null, false, null);
        }
        if (editor.Scope is null)
            for (int i = editor.Document.Comments.Count - 1; i >= 0; i--)
            {
                var comment = editor.Document.Comments[i];
                var min = Screen(new(comment.X, comment.Y));
                var max = min + new Vector2(comment.Width, comment.Height) * zoom;
                if (Vector2.Distance(mouse, max) < 14) return new(null, null, null, comment, true, null);
                if (mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= min.Y + 26 * zoom)
                    return new(null, null, null, comment, false, null);
            }
        foreach (var link in editor.Links)
            if (Ends(editor, link) is { } ends && NearWire(ends.A, ends.B, mouse, 5)) return new(null, null, null, null, false, link);
        return default;
    }

    private (Vector2 A, Vector2 B, PinType Type)? Ends(GraphEditor editor, GraphLink link)
    {
        if (editor.Node(link.FromNode) is not { } from || editor.Node(link.ToNode) is not { } to) return null;
        if (editor.Document.Pin(from, link.FromPin, true, editor.Scope) is not { } output
            || editor.Document.Pin(to, link.ToPin, false, editor.Scope) is not { } input) return null;
        return (PinPosition(editor, from, output), PinPosition(editor, to, input), output.Type);
    }

    private (Vector2, Vector2) Controls(Vector2 a, Vector2 b)
    {
        float dx = MathF.Max(40 * zoom, MathF.Abs(b.X - a.X) * .5f);
        return (a + new Vector2(dx, 0), b - new Vector2(dx, 0));
    }

    private bool NearWire(Vector2 a, Vector2 b, Vector2 mouse, float distance)
    {
        var (c1, c2) = Controls(a, b);
        var previous = a;
        for (int i = 1; i <= 24; i++)
        {
            float t = i / 24f, u = 1 - t;
            var point = u * u * u * a + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * b;
            var segment = point - previous;
            float along = segment.LengthSquared() < 1e-4f ? 0 : Math.Clamp(Vector2.Dot(mouse - previous, segment) / segment.LengthSquared(), 0, 1);
            if (Vector2.Distance(previous + segment * along, mouse) < distance) return true;
            previous = point;
        }
        return false;
    }

    // Mouse.

    private void HandleMouse(GraphEditor editor, bool hovered, Hit hit, ImGuiIOPtr io)
    {
        var mouse = io.MousePos;
        bool inMap = ShowMinimap && InMinimap(mouse) && editor.Nodes.Count > 0;
        if (hovered && !inMap && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            moved = false;
            if (hit.Pin is { } pin && hit.PinNode is { } pinNode)
            {
                if (io.KeyAlt) editor.BreakPin(pinNode.Id, pin.Id, pin.Output);
                else if (!pin.Output && pin.Type != PinType.Exec && !pin.Multi
                    && editor.Links.FirstOrDefault(link => link.ToNode == pinNode.Id && link.ToPin == pin.Id) is { } existing
                    && editor.Node(existing.FromNode) is { } source)
                {
                    // Pick the wire up by its loose end.
                    editor.Disconnect(existing);
                    linkFrom = new(existing.FromNode, existing.FromPin, true, editor.Document.Pin(source, existing.FromPin, true, editor.Scope)?.Type ?? pin.Type);
                    mode = Mode.Link;
                }
                else { linkFrom = new(pinNode.Id, pin.Id, pin.Output, pin.Type); mode = Mode.Link; }
            }
            else if (hit.Node is { } node)
            {
                selectedLink = null;
                if (io.KeyCtrl) { if (!editor.Selection.Remove(node.Id)) editor.Selection.Add(node.Id); }
                else if (io.KeyShift) editor.Selection.Add(node.Id);
                else if (!editor.Selection.Contains(node.Id)) { editor.Selection.Clear(); editor.Selection.Add(node.Id); }
                mode = Mode.MoveNodes;
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    mode = Mode.None;
                    if (node.Type == NodeLibrary.FunctionCall && editor.Document.Function(node.Get("function")) is { } function)
                    { editor.Enter(function.Id); FrameAll(); }
                    else if (node.Type is not NodeLibrary.Reroute) editor.ToggleCollapsed([node.Id]);
                }
            }
            else if (hit.Comment is { } comment)
            {
                selectedLink = null;
                if (hit.CommentCorner) { resizing = comment.Id; mode = Mode.ResizeComment; }
                else
                {
                    if (!io.KeyCtrl && !io.KeyShift && !editor.Selection.Contains(comment.Id)) editor.Selection.Clear();
                    editor.Selection.Add(comment.Id);
                    mode = Mode.MoveNodes;
                }
            }
            else if (hit.Link is { } wire)
            {
                selectedLink = wire;
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    var at = World(mouse);
                    var knot = editor.InsertReroute(wire, at.X, at.Y);
                    editor.Selection.Clear(); editor.Selection.Add(knot.Id);
                    selectedLink = null;
                }
            }
            else
            {
                if (!io.KeyCtrl && !io.KeyShift) editor.Selection.Clear();
                selectedLink = null;
                mode = Mode.Box;
                boxStart = mouse;
            }
        }

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && io.MouseDelta != Vector2.Zero)
        {
            var delta = io.MouseDelta / zoom;
            if (mode == Mode.MoveNodes && editor.Selection.Count > 0)
            {
                if (!moved) { editor.Checkpoint("Move"); moved = true; }
                editor.Move(editor.Selection, delta.X, delta.Y);
            }
            else if (mode == Mode.ResizeComment && editor.Document.Comments.FirstOrDefault(comment => comment.Id == resizing) is { } box)
            {
                if (!moved) { editor.Checkpoint("Resize comment"); moved = true; }
                box.Width = MathF.Max(120, box.Width + delta.X);
                box.Height = MathF.Max(60, box.Height + delta.Y);
            }
        }
        if (hovered && inMap && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) mode = Mode.Minimap;
        if (mode == Mode.Minimap && ImGui.IsMouseDown(ImGuiMouseButton.Left) && MapToWorld(editor, mouse) is { } target)
        {
            targetPan = pan = size / 2 - target * zoom;
            zooming = false;
        }

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (mode == Mode.Link && linkFrom is { } from) DropLink(editor, from, hovered ? HitTest(editor, mouse) : default, mouse, hovered);
            else if (mode == Mode.Box)
            {
                var a = World(Vector2.Min(boxStart, mouse)); var b = World(Vector2.Max(boxStart, mouse));
                foreach (var node in editor.Nodes)
                {
                    var (width, height) = GraphLayout.Size(editor.Document, node, editor.Scope);
                    if (node.X <= b.X && node.X + width >= a.X && node.Y <= b.Y && node.Y + height >= a.Y) editor.Selection.Add(node.Id);
                }
                if (editor.Scope is null)
                    foreach (var comment in editor.Document.Comments)
                        if (comment.X >= a.X && comment.Y >= a.Y && comment.X + comment.Width <= b.X && comment.Y + comment.Height <= b.Y)
                            editor.Selection.Add(comment.Id);
            }
            mode = Mode.None;
            linkFrom = null;
        }

        // Right click (without a pan drag): context menus, or the add menu on empty canvas.
        if (hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Right) && io.MouseDragMaxDistanceSqr[1] < 16)
        {
            if (hit.Node is { } node)
            {
                if (!editor.Selection.Contains(node.Id)) { editor.Selection.Clear(); editor.Selection.Add(node.Id); }
                contextNode = node.Id;
                ImGui.OpenPopup("bp-node");
            }
            else if (hit.Link is { } wire) { contextLink = wire; ImGui.OpenPopup("bp-link"); }
            else OpenAddMenu(World(mouse), null);
        }
    }

    private void DropLink(GraphEditor editor, DragPin from, Hit target, Vector2 mouse, bool hovered)
    {
        string? problem = null;
        if (target.Pin is { } pin && target.PinNode is { } pinNode)
        {
            if (pin.Output == from.Output) problem = "Link an output to an input.";
            else problem = from.Output ? editor.Connect(from.Node, from.Pin, pinNode.Id, pin.Id) : editor.Connect(pinNode.Id, pin.Id, from.Node, from.Pin);
        }
        else if (target.Node is { } node && node.Id != from.Node)
        {
            // Dropped on a node: the first pin that can take the wire.
            var candidate = editor.PinsOf(node).FirstOrDefault(other => other.Output != from.Output
                && (from.Output ? PinTypes.Compatible(from.Type, other.Type) : PinTypes.Compatible(other.Type, from.Type)))
                ?? editor.PinsOf(node).FirstOrDefault(other => other.Output != from.Output
                    && (from.Output ? PinTypes.Connectable(from.Type, other.Type) : PinTypes.Connectable(other.Type, from.Type)));
            if (candidate is null) problem = $"{NodeLibrary.Find(node.Type)?.Title} has no pin for {PinTypes.Label(from.Type)}.";
            else problem = from.Output ? editor.Connect(from.Node, from.Pin, node.Id, candidate.Id) : editor.Connect(node.Id, candidate.Id, from.Node, from.Pin);
        }
        else if (hovered && target.Comment is null) OpenAddMenu(World(mouse), from);
        if (problem is not null) Say(problem);
    }

    // Keys.

    private void HandleKeys(GraphEditor editor, ImGuiIOPtr io, bool hovered)
    {
        bool ctrl = io.KeyCtrl;
        var mouseWorld = World(hovered ? io.MousePos : origin + size / 2);
        if (ImGui.IsKeyPressed(ImGuiKey.Delete) || ImGui.IsKeyPressed(ImGuiKey.Backspace) || ShortcutInput.Pressed(ShortcutActions.Delete2D))
        {
            if (selectedLink is { } link) { editor.Disconnect(link); selectedLink = null; }
            else editor.Remove(editor.Selection.ToList());
        }
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.C) && editor.Selection.Count > 0) ImGui.SetClipboardText(editor.Copy());
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.X) && editor.Selection.Count > 0) { ImGui.SetClipboardText(editor.Copy()); editor.Remove(editor.Selection.ToList()); }
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.V))
        {
            if (editor.Paste(ImGui.GetClipboardText() ?? "", mouseWorld.X, mouseWorld.Y).Count == 0) Say("The clipboard holds no nodes.");
        }
        if ((ctrl && ImGui.IsKeyPressed(ImGuiKey.D)) || ShortcutInput.Pressed(ShortcutActions.Duplicate2D)) editor.Duplicate();
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.A)) { editor.Selection.Clear(); editor.Selection.UnionWith(editor.Nodes.Select(node => node.Id)); }
        if (!ctrl && ImGui.IsKeyPressed(ImGuiKey.Tab)) OpenAddMenu(mouseWorld, null);
        if (!ctrl && !io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.C) && editor.Scope is null) editor.AddComment(mouseWorld.X, mouseWorld.Y);
        if (!ctrl && ImGui.IsKeyPressed(ImGuiKey.Home)) Fit(editor, editor.Nodes);
        if (io.KeyShift && !ctrl)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.W)) editor.Align(AlignMode.Top);
            if (ImGui.IsKeyPressed(ImGuiKey.S)) editor.Align(AlignMode.Bottom);
            if (ImGui.IsKeyPressed(ImGuiKey.A)) editor.Align(AlignMode.Left);
            if (ImGui.IsKeyPressed(ImGuiKey.D)) editor.Align(AlignMode.Right);
            if (ImGui.IsKeyPressed(ImGuiKey.H)) editor.Distribute(true);
            if (ImGui.IsKeyPressed(ImGuiKey.V)) editor.Distribute(false);
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape) && editor.Scope is not null) { editor.Enter(null); FrameAll(); }
    }

    // Menus.

    private void OpenAddMenu(Vector2 world, DragPin? pin)
    {
        menuWorld = world;
        menuPin = pin;
        query = "";
        menuIndex = 0;
        focusQuery = true;
        ImGui.OpenPopup("bp-add");
    }

    private void DrawMenus(GraphEditor editor)
    {
        if (ImGui.BeginPopup("bp-add"))
        {
            if (focusQuery) { ImGui.SetKeyboardFocusHere(); focusQuery = false; }
            ImGui.SetNextItemWidth(320);
            if (ImGui.InputTextWithHint("##add-query", "Search nodes", ref query, 128)) menuIndex = 0;
            if (menuPin is { } pin) ImGui.TextDisabled($"Linking from {PinTypes.Label(pin.Type)} {(pin.Output ? "output" : "input")}");
            var results = editor.Search(query, menuPin?.Type, menuPin?.Output ?? true);
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) menuIndex = Math.Min(menuIndex + 1, Math.Max(0, results.Count - 1));
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) menuIndex = Math.Max(menuIndex - 1, 0);
            bool enter = ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter);
            ImGui.BeginChild("##add-results", new Vector2(320, 340));
            string category = "";
            for (int i = 0; i < Math.Min(results.Count, 250); i++)
            {
                var result = results[i];
                if (query.Length == 0 && result.Type.Category != category)
                {
                    category = result.Type.Category;
                    ImGui.TextDisabled(category);
                }
                bool chosen = i == menuIndex;
                string label = $"{result.Type.Title}{(result.NeedsConversion ? "  (converts)" : "")}";
                if (query.Length > 0) label += $"  [{result.Type.Category}]";
                if (ImGui.Selectable($"{label}##{result.Type.Id}", chosen) || chosen && enter)
                {
                    var at = menuWorld;
                    if (menuPin is { Output: false }) at.X -= GraphLayout.Width + 60;
                    editor.AddFromSearch(result, at.X, at.Y, menuPin is { } from ? (from.Node, from.Pin, from.Output) : null);
                    menuPin = null;
                    ImGui.CloseCurrentPopup();
                    break;
                }
                if (ImGui.IsItemHovered() && result.Type.Description.Length > 0) ImGui.SetTooltip(result.Type.Description);
                if (chosen && (ImGui.IsKeyPressed(ImGuiKey.DownArrow) || ImGui.IsKeyPressed(ImGuiKey.UpArrow))) ImGui.SetScrollHereY();
            }
            if (results.Count == 0) ImGui.TextDisabled("No node fits here.");
            ImGui.EndChild();
            if (ImGui.IsKeyPressed(ImGuiKey.Escape)) { menuPin = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("bp-node"))
        {
            var node = editor.Node(contextNode);
            int count = editor.Selection.Count;
            if (ImGui.MenuItem("Delete", "Del")) editor.Remove(editor.Selection.ToList());
            if (ImGui.MenuItem("Duplicate", "Ctrl+D")) editor.Duplicate();
            if (ImGui.MenuItem("Copy", "Ctrl+C")) ImGui.SetClipboardText(editor.Copy());
            if (ImGui.MenuItem("Fold / unfold", "double-click")) editor.ToggleCollapsed(editor.Selection.ToList());
            if (ImGui.MenuItem("Comment selection", "C", false, editor.Scope is null)) editor.AddComment(0, 0);
            ImGui.Separator();
            if (ImGui.MenuItem("Collapse to function", "", false, editor.Scope is null && count > 0))
            {
                try { editor.CollapseToFunction("Function " + (editor.Document.Functions.Count + 1)); }
                catch (InvalidOperationException exception) { Say(exception.Message); }
            }
            if (node is { Type: NodeLibrary.FunctionCall })
            {
                if (ImGui.MenuItem("Open function")) { editor.Enter(node.Get("function")); FrameAll(); }
                if (ImGui.MenuItem("Expand function here")) editor.ExpandFunctionCall(node.Id);
            }
            if (ImGui.BeginMenu("Align", count > 1))
            {
                foreach (var (align, label, keys) in new[]
                {
                    (AlignMode.Left, "Left", "Shift+A"), (AlignMode.Right, "Right", "Shift+D"), (AlignMode.Top, "Top", "Shift+W"),
                    (AlignMode.Bottom, "Bottom", "Shift+S"), (AlignMode.CenterHorizontal, "Centre horizontally", ""), (AlignMode.CenterVertical, "Centre vertically", "")
                })
                    if (ImGui.MenuItem(label, keys)) editor.Align(align);
                ImGui.Separator();
                if (ImGui.MenuItem("Distribute horizontally", "Shift+H", false, count > 2)) editor.Distribute(true);
                if (ImGui.MenuItem("Distribute vertically", "Shift+V", false, count > 2)) editor.Distribute(false);
                ImGui.EndMenu();
            }
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("bp-link"))
        {
            if (contextLink is { } link)
            {
                if (ImGui.MenuItem("Delete link")) editor.Disconnect(link);
                if (ImGui.MenuItem("Add reroute"))
                {
                    var at = World(ImGui.GetWindowPos());
                    editor.InsertReroute(link, at.X, at.Y);
                }
            }
            ImGui.EndPopup();
        }
    }

    // Drawing.

    private void DrawGrid(ImDrawListPtr draw)
    {
        float step = 32 * zoom;
        if (step < 8) step *= 4;
        uint minor = U32(new Vector4(1, 1, 1, .035f)), major = U32(new Vector4(1, 1, 1, .075f));
        var start = new Vector2(pan.X % step, pan.Y % step);
        int index = (int)MathF.Floor(-pan.X / step);
        for (float x = start.X; x < size.X; x += step, index++)
            draw.AddLine(origin + new Vector2(x, 0), origin + new Vector2(x, size.Y), index % 8 == 0 ? major : minor);
        index = (int)MathF.Floor(-pan.Y / step);
        for (float y = start.Y; y < size.Y; y += step, index++)
            draw.AddLine(origin + new Vector2(0, y), origin + new Vector2(size.X, y), index % 8 == 0 ? major : minor);
    }

    private void DrawComments(ImDrawListPtr draw, GraphEditor editor)
    {
        if (editor.Scope is not null) return;
        var font = ImGui.GetFont();
        foreach (var comment in editor.Document.Comments)
        {
            var min = Screen(new(comment.X, comment.Y));
            var max = min + new Vector2(comment.Width, comment.Height) * zoom;
            var parsed = GraphValue.Parse(PinType.Color, comment.Color);
            var color = new Vector4((float)parsed.X, (float)parsed.Y, (float)parsed.Z, 1);
            draw.AddRectFilled(min, max, U32(V(color, .22f)), 6 * zoom);
            draw.AddRectFilled(min, new(max.X, min.Y + 26 * zoom), U32(V(color, .85f)), 6 * zoom, ImDrawFlags.RoundCornersTop);
            bool selected = editor.Selection.Contains(comment.Id);
            draw.AddRect(min, max, U32(selected ? StudioTheme.Accent : V(color, .9f)), 6 * zoom, ImDrawFlags.None, selected ? 2 : 1);
            if (zoom > .25f) draw.AddText(font, ImGui.GetFontSize() * 1.15f * MathF.Max(zoom, .6f), min + new Vector2(8, 4) * zoom, U32(new Vector4(1, 1, 1, .95f)), comment.Text);
            draw.AddTriangleFilled(max, max - new Vector2(12, 0), max - new Vector2(0, 12), U32(V(color, .9f)));
        }
    }

    private void DrawLinks(ImDrawListPtr draw, GraphEditor editor, Hit hit)
    {
        foreach (var link in editor.Links)
        {
            if (Ends(editor, link) is not { } ends) continue;
            bool lit = link == selectedLink || link == hit.Link || editor.Selection.Contains(link.FromNode) || editor.Selection.Contains(link.ToNode);
            var color = PinTypes.ColorOf(ends.Type);
            if (!lit) color = V(color, .75f);
            float thickness = (ends.Type == PinType.Exec ? 3 : 2) * MathF.Max(.6f, MathF.Min(zoom, 1.4f)) + (lit ? 1 : 0);
            Wire(draw, ends.A, ends.B, U32(color), thickness);
        }
    }

    private void Wire(ImDrawListPtr draw, Vector2 a, Vector2 b, uint color, float thickness)
    {
        var (c1, c2) = Controls(a, b);
        draw.AddBezierCubic(a, c1, c2, b, color, thickness);
    }

    private static Vector4 HeaderColor(NodeType? type) => type?.Category switch
    {
        "Events" or "Perk Events" => new(.58f, .14f, .14f, 1),
        "Flow" => new(.32f, .33f, .37f, 1),
        "Math" or "Vector" or "Logic" or "Color" => new(.20f, .42f, .24f, 1),
        "Output" => new(.62f, .36f, .08f, 1),
        "Constants" or "Parameters" or "Textures" => new(.16f, .32f, .48f, 1),
        "Conversion" or "Utility" => new(.26f, .26f, .28f, 1),
        "Functions" => new(.24f, .28f, .62f, 1),
        "Actions" or "Perk Actions" => new(.16f, .38f, .58f, 1),
        "Perk Conditions" or "State" => new(.46f, .34f, .12f, 1),
        "Stats" or "Perks" or "Fields" or "Visuals" or "Archetype" or "Perk Behavior" => new(.42f, .24f, .52f, 1),
        "Entities" => new(.12f, .40f, .50f, 1),
        _ => new(.26f, .29f, .36f, 1)
    };

    private void DrawPin(ImDrawListPtr draw, Vector2 at, PinType type, bool connected, bool hot)
    {
        float radius = MathF.Max(3, 5 * zoom) * (hot ? 1.3f : 1);
        uint color = U32(PinTypes.ColorOf(type));
        if (type == PinType.Exec)
        {
            var a = at + new Vector2(-radius, -radius); var b = at + new Vector2(-radius, radius); var c = at + new Vector2(radius, 0);
            if (connected) draw.AddTriangleFilled(a, b, c, color); else draw.AddTriangle(a, b, c, color, 1.5f);
            return;
        }
        draw.AddCircleFilled(at, radius, U32(new Vector4(.08f, .08f, .1f, 1)));
        draw.AddCircle(at, radius, color, 0, 1.6f);
        if (connected) draw.AddCircleFilled(at, radius * .55f, color);
    }

    private void DrawNode(ImDrawListPtr draw, GraphEditor editor, GraphNode node, Hit hit)
    {
        var (min, max) = Box(editor, node);
        if (max.X < origin.X || min.X > origin.X + size.X || max.Y < origin.Y || min.Y > origin.Y + size.Y) return;
        var type = NodeLibrary.Find(node.Type);
        bool selected = editor.Selection.Contains(node.Id), hot = hit.Node == node;
        Problems.TryGetValue(node.Id, out var problems);
        bool error = problems?.Any(problem => problem.Severity == DiagnosticSeverity.Error) == true;
        bool warning = !error && problems?.Any(problem => problem.Severity == DiagnosticSeverity.Warning) == true;
        var pins = editor.PinsOf(node);
        if (node.Type == NodeLibrary.Reroute)
        {
            var center = (min + max) / 2;
            var pinType = pins.Count > 0 ? pins[0].Type : PinType.Any;
            draw.AddCircleFilled(center, 6 * MathF.Max(zoom, .6f), U32(PinTypes.ColorOf(pinType)));
            if (selected || hot) draw.AddCircle(center, 9 * MathF.Max(zoom, .6f), U32(selected ? StudioTheme.Accent : new Vector4(1, 1, 1, .6f)), 0, 2);
            return;
        }
        float rounding = 6 * zoom;
        draw.AddRectFilled(min + new Vector2(3, 4) * zoom, max + new Vector2(3, 4) * zoom, U32(new Vector4(0, 0, 0, .35f)), rounding);
        draw.AddRectFilled(min, max, U32(new Vector4(.12f, .13f, .16f, .96f)), rounding);
        draw.AddRectFilled(min, new(max.X, min.Y + GraphLayout.Header * zoom), U32(HeaderColor(type)), rounding,
            node.Collapsed ? ImDrawFlags.None : ImDrawFlags.RoundCornersTop);
        var outline = selected ? StudioTheme.Accent : error ? new Vector4(.95f, .25f, .25f, 1) : warning ? new Vector4(.95f, .7f, .2f, 1)
            : hot ? new Vector4(.6f, .65f, .72f, 1) : new Vector4(.05f, .05f, .06f, 1);
        draw.AddRect(min, max, U32(outline), rounding, ImDrawFlags.None, selected || error ? 2.5f : 1.2f);

        var font = ImGui.GetFont();
        float fontSize = ImGui.GetFontSize() * zoom;
        bool text = zoom >= .32f;
        string title = node.Type == NodeLibrary.FunctionCall
            ? "f  " + (editor.Document.Function(node.Get("function"))?.Name ?? "missing function") : type?.Title ?? node.Type;
        if (node.Collapsed) title = "+ " + title;
        if (text) draw.AddText(font, fontSize, min + new Vector2(10, (GraphLayout.Header - ImGui.GetFontSize()) / 2) * zoom, U32(new Vector4(1, 1, 1, .95f)), title);
        if (error || warning)
        {
            var badge = new Vector2(max.X - 12 * zoom, min.Y + GraphLayout.Header / 2 * zoom);
            draw.AddCircleFilled(badge, 7 * zoom, U32(error ? new Vector4(.9f, .2f, .2f, 1) : new Vector4(.95f, .7f, .2f, 1)));
            if (text) draw.AddText(font, fontSize * .9f, badge - new Vector2(2, 7) * zoom, U32(new Vector4(0, 0, 0, 1)), "!");
        }
        if (node.Collapsed)
        {
            foreach (bool output in new[] { false, true })
                if (pins.Any(pin => pin.Output == output))
                    DrawPin(draw, new(output ? max.X : min.X, min.Y + GraphLayout.Header / 2 * zoom),
                        pins.First(pin => pin.Output == output).Type, pins.Any(pin => pin.Output == output && linked.Contains((node.Id, pin.Id))), false);
            return;
        }
        var label = U32(new Vector4(.82f, .85f, .9f, 1));
        var dim = U32(new Vector4(.55f, .6f, .68f, 1));
        foreach (var pin in pins)
        {
            var at = PinPosition(editor, node, pin);
            bool connected = linked.Contains((node.Id, pin.Id));
            DrawPin(draw, at, pin.Type, connected, hit.PinNode == node && hit.Pin == pin);
            if (!text) continue;
            string name = pin.Name;
            if (!pin.Output && !connected && pin.Type is not (PinType.Exec or PinType.Any or PinType.Stat or PinType.Perk or PinType.Field))
            {
                string value = node.Get(pin.Id, pin.Default ?? "");
                if (value.Length > 0) name = (name.Length > 0 ? name + "  " : "") + (value.Length > 12 ? value[..12] + "…" : value);
            }
            if (pin.Multi) name += " [ ]";
            if (name.Length == 0) continue;
            float y = at.Y - fontSize / 2;
            if (pin.Output)
            {
                float width = ImGui.CalcTextSize(name).X * zoom;
                draw.AddText(font, fontSize * .92f, new(at.X - 12 * zoom - width, y), label, name);
            }
            else draw.AddText(font, fontSize * .92f, new(at.X + 12 * zoom, y), label, name);
        }
        if (!text || type is null || node.Type == NodeLibrary.FunctionCall) return;
        int rows = Math.Max(pins.Count(pin => !pin.Output), pins.Count(pin => pin.Output));
        float row = min.Y + (GraphLayout.Header + rows * GraphLayout.Row + 4) * zoom;
        foreach (var property in type.Properties.Where(property => property.Kind != PropertyKind.Code))
        {
            string value = node.Get(property.Id, property.Default);
            if (value.Length > 22) value = value[..22] + "…";
            draw.AddText(font, fontSize * .85f, new(min.X + 10 * zoom, row), dim, $"{property.Name}: {value}");
            row += GraphLayout.Row * zoom;
        }
    }

    private void DrawOverlay(ImDrawListPtr draw, GraphEditor editor)
    {
        string scope = editor.Scope is { } function ? $"{editor.Document.Name}  >  {function.Name}   (Esc returns)" : editor.Document.Name;
        draw.AddText(origin + new Vector2(10, 8), U32(new Vector4(1, 1, 1, .55f)), $"{editor.Document.Kind} graph  |  {scope}  |  {zoom * 100:0}%");
        if (status.Length > 0 && ImGui.GetTime() < statusUntil)
            draw.AddText(origin + new Vector2(10, size.Y - 26), U32(new Vector4(1, .75f, .4f, 1)), status);
        if (editor.Nodes.Count == 0)
            draw.AddText(origin + size / 2 - new Vector2(150, 8), U32(new Vector4(1, 1, 1, .4f)), "Right-click or press Tab to add a node");
    }

    // Minimap.

    private (Vector2 Min, Vector2 Max) MapRect() => (origin + size - new Vector2(MapWidth + 12, MapHeight + 12), origin + size - new Vector2(12));
    private bool InMinimap(Vector2 point) { var (min, max) = MapRect(); return point.X >= min.X && point.Y >= min.Y && point.X <= max.X && point.Y <= max.Y; }

    private (Vector2 WorldMin, float Scale)? MapSpace(GraphEditor editor)
    {
        if (editor.Nodes.Count == 0) return null;
        var viewMin = World(origin); var viewMax = World(origin + size);
        float left = MathF.Min(viewMin.X, editor.Nodes.Min(node => node.X)), top = MathF.Min(viewMin.Y, editor.Nodes.Min(node => node.Y));
        float right = MathF.Max(viewMax.X, editor.Nodes.Max(node => node.X + GraphLayout.Width));
        float bottom = MathF.Max(viewMax.Y, editor.Nodes.Max(node => node.Y + GraphLayout.Size(editor.Document, node, editor.Scope).Height));
        float scale = MathF.Min(MapWidth / MathF.Max(1, right - left), MapHeight / MathF.Max(1, bottom - top));
        return (new(left, top), scale);
    }

    private Vector2? MapToWorld(GraphEditor editor, Vector2 point)
    {
        if (MapSpace(editor) is not { } space) return null;
        var (min, _) = MapRect();
        return space.WorldMin + (point - min) / space.Scale;
    }

    private void DrawMinimap(ImDrawListPtr draw, GraphEditor editor, bool hovered, ImGuiIOPtr io)
    {
        if (MapSpace(editor) is not { } space) return;
        var (min, max) = MapRect();
        draw.AddRectFilled(min, max, U32(new Vector4(.04f, .045f, .055f, .88f)), 4);
        draw.AddRect(min, max, U32(new Vector4(1, 1, 1, hovered && InMinimap(io.MousePos) ? .35f : .15f)), 4);
        Vector2 Map(Vector2 world) => min + (world - space.WorldMin) * space.Scale;
        foreach (var comment in editor.Scope is null ? editor.Document.Comments : new List<GraphComment>())
            draw.AddRectFilled(Map(new(comment.X, comment.Y)), Map(new(comment.X + comment.Width, comment.Y + comment.Height)), U32(new Vector4(.4f, .5f, .6f, .18f)));
        foreach (var node in editor.Nodes)
        {
            var (width, height) = GraphLayout.Size(editor.Document, node, editor.Scope);
            var color = editor.Selection.Contains(node.Id) ? StudioTheme.Accent : Problems.ContainsKey(node.Id) && Problems[node.Id].Any(problem => problem.Severity == DiagnosticSeverity.Error)
                ? new Vector4(.9f, .25f, .25f, 1) : HeaderColor(NodeLibrary.Find(node.Type));
            draw.AddRectFilled(Map(new(node.X, node.Y)), Map(new(node.X + width, node.Y + height)), U32(color));
        }
        draw.AddRect(Vector2.Max(min, Map(World(origin))), Vector2.Min(max, Map(World(origin + size))), U32(new Vector4(1, 1, 1, .7f)));
    }
}
