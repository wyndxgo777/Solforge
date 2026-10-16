using System.Numerics;
using ImGuiNET;
using SunriseStudio.Missions;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class MissionGraphPage
{
    // A body group shows the statements of one top-level function or callback.
    private const string BodyPrefix = "body:";
    private static readonly Vector4 ModuleTint = new(.25f, .35f, .5f, 1), FunctionTint = new(.42f, .3f, .55f, 1),
        FlowTint = new(.62f, .33f, .14f, 1), CallbackTint = new(.22f, .45f, .3f, 1), ValueTint = new(.3f, .32f, .36f, 1);

    private static string BodyKey(string node) => BodyPrefix + node;

    /** The node whose body a group key shows; null for any other group. */
    private static string? BodyOwner(string key) => key.StartsWith(BodyPrefix, StringComparison.Ordinal) ? key[BodyPrefix.Length..] : null;

    private bool GroupExists(string key) => key == MissionScriptGraph.Root
        || BodyOwner(key) is { } body && scriptGraph?.FindNode(body) is { } owner && scriptGraph.CanOpen(owner)
        || graph?.Groups.Any(group => group.Key == key) == true;

    private string GroupTitle(string key) => key == MissionScriptGraph.Root ? "Script"
        : BodyOwner(key) is { } owner ? "Script / " + (scriptGraph?.FindNode(owner)?.Title ?? "?")
        : graph!.Group(key).Title;

    /** The group picker and canvas buttons, then the canvas beside the inspector. */
    private void DrawNodes(bool editable)
    {
        if (IsScriptGroup && HasSourceDraft) editable = false;
        ImGui.SetNextItemWidth(Math.Clamp(ImGui.GetContentRegionAvail().X * .4f, 120, 300));
        if (UiAuto.BeginCombo("##graph-group", GroupTitle(groupKey), ImGuiComboFlags.HeightLarge))
        {
            if (UiAuto.Selectable("Script", groupKey == MissionScriptGraph.Root)) OpenGroup(MissionScriptGraph.Root);
            foreach (var node in (scriptGraph?.Nodes.Where(scriptGraph.CanOpen) ?? []).Concat(scriptGraph?.Callbacks ?? []))
                if (UiAuto.Selectable("    " + node.Title + "##" + node.Key, groupKey == BodyKey(node.Key))) OpenGroup(BodyKey(node.Key));
            foreach (var group in graph!.Groups)
                if (UiAuto.Selectable(group.Title + "##" + group.Key, group.Key == groupKey)) OpenGroup(group.Key);
            UiAuto.EndCombo();
        }
        if (groupKey != MissionScriptGraph.Root)
        {
            ImGui.SameLine();
            if (ImGuiIcons.Button("mission-up", StudioIcon.Back, "Up to script")) OpenGroup(MissionScriptGraph.Root);
        }
        var (nodes, edges, actions) = CanvasModel(editable);
        string frameKey = ShortcutInput.Text(ShortcutActions.FrameAll2D);
        ImGui.SameLine();
        if (ImGuiIcons.Button("mission-frame", StudioIcon.Frame, frameKey.Length == 0 ? "Frame all" : $"Frame all ({frameKey})"))
            canvas.Frame(groupKey);
        ImGui.SameLine();
        if (ImGuiIcons.Button("mission-arrange", StudioIcon.Grid, "Arrange as a tree")) canvas.Arrange(groupKey, nodes, edges);
        ImGui.SameLine();
        ImGui.BeginDisabled(!editable);
        if (ImGuiIcons.Button("mission-add", StudioIcon.Plus, "Add node"))
        {
            if (IsScriptGroup) { addScriptScope = ""; ImGui.OpenPopup("Add script node"); }
            else { ResetTemplate(); ImGui.OpenPopup("Add mission node"); }
        }
        if (IsScriptGroup) DrawScriptTemplates(); else DrawTemplatePopup();
        ImGui.EndDisabled();
        if (IsScriptGroup && scriptGraph is null) { ImGui.TextWrapped("The script view cannot read this script: " + scriptError); return; }
        // The canvas keeps at least half the width.
        var size = ImGui.GetContentRegionAvail(); float inspectorWidth = Math.Min(Math.Clamp(size.X * .34f, 220, 460), size.X * .5f);
        ImGui.BeginChild("mission-canvas", new(Math.Max(100, size.X - inspectorWidth - 8), size.Y), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        canvas.Draw(groupKey, nodes, edges, editable, ref selected, actions);
        ImGui.EndChild(); ImGui.SameLine();
        // Positions are saved once a drag ends, not on every frame of it.
        if (canvas.Moved && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) { canvas.Moved = false; SaveLayout(); }
        ImGui.BeginChild("mission-inspector", new(0, size.Y), ImGuiChildFlags.Borders);
        if (canvas.SelectedEdge is { } edge) EdgeInspector(edge, editable, actions, nodes);
        else if (IsScriptGroup) ScriptInspector(editable);
        else Inspector(editable);
        ImGui.EndChild();
    }

    private bool IsScriptGroup => groupKey == MissionScriptGraph.Root || BodyOwner(groupKey) is not null;

    private void OpenGroup(string key)
    {
        if (!GroupExists(key)) return;
        groupKey = key; selected = ""; advancedField = ""; advancedValue = ""; addScriptScope = "";
    }

    private (MissionGraphDocument? Document, string Group, bool Editable, (IReadOnlyList<CanvasNode>, IReadOnlyList<CanvasEdge>, CanvasActions) Model) canvasCache;

    /** The nodes, links and edit actions of the group on screen; rebuilt only when the document or group changes. */
    private (IReadOnlyList<CanvasNode>, IReadOnlyList<CanvasEdge>, CanvasActions) CanvasModel(bool editable)
    {
        if (ReferenceEquals(canvasCache.Document, graph) && canvasCache.Group == groupKey && canvasCache.Editable == editable)
            return canvasCache.Model;
        var model = BuildCanvasModel(editable);
        canvasCache = (graph, groupKey, editable, model);
        return model;
    }

    /** Script groups come from the script graph; flow and campaign groups come from the document. */
    private (IReadOnlyList<CanvasNode>, IReadOnlyList<CanvasEdge>, CanvasActions) BuildCanvasModel(bool editable)
    {
        if (IsScriptGroup)
        {
            if (scriptGraph is null) return ([], [], new());
            string? owner = BodyOwner(groupKey);
            var (body, links) = owner is null ? (scriptGraph.Nodes, scriptGraph.Edges) : scriptGraph.Body(scriptGraph.Node(owner));
            string prefix = owner is null ? "Uses " : "";
            var nodes = body.Select(ScriptCanvasNode).ToArray();
            var edges = links.Select(edge => new CanvasEdge(edge.Source, edge.Target, false, prefix + edge.Label)).ToArray();
            return (nodes, edges, new() { Open = OpenScriptNode, Remove = editable ? key => RemoveScriptNode(key) : null });
        }
        var group = graph!.Group(groupKey);
        bool flowSteps = group.IsFlowSteps;
        var flowNodes = group.Nodes.Select(node => new CanvasNode(node.Key, Friendly(node.Id), NodeLabel(node, group), NodeCode(node, group),
            group.IsFlowFacts ? CallbackTint : FlowTint)).ToArray();
        var keys = group.Nodes.Select(node => node.Key).ToHashSet(StringComparer.Ordinal);
        var flowEdges = graph.Edges.Where(edge => keys.Contains(edge.Source) && keys.Contains(edge.Target))
            .Select(edge => new CanvasEdge(edge.Source, edge.Target, flowSteps && edge.Kind == "After",
                edge.Kind == "After" ? "Runs after" : edge.Kind == "Next" ? "Next step" : edge.Kind)).ToArray();
        return (flowNodes, flowEdges, new()
        {
            Connect = flowSteps ? (from, to) => Change(document => document.Connect(from, to)) : null,
            Disconnect = flowSteps ? (from, to) => Change(document => document.Connect(from, to, false)) : null,
            Reconnect = flowSteps ? (from, previous, to) => Change(document =>
                document.Connect(from, previous, false).Connect(from, to)) : null,
            Remove = key => Change(document => document.RemoveNode(key))
        });
    }

    /** A node shows its code without the comment lines above it; the inspector keeps them. */
    private static string Preview(string code)
    {
        var lines = code.Split('\n');
        int first = 0;
        while (first < lines.Length - 1 && lines[first].TrimStart().StartsWith("--", StringComparison.Ordinal)) first++;
        return string.Join('\n', lines[first..]);
    }

    private void OpenScriptNode(string key)
    {
        var node = scriptGraph?.FindNode(key);
        if (node is not null && scriptGraph!.CanOpen(node)) OpenGroup(BodyKey(key));
        else if (node?.FlowGroup is { } flow && graph!.Groups.FirstOrDefault(group => group.Key.StartsWith(flow + "/", StringComparison.Ordinal)) is { } first)
            OpenGroup(first.Key);
    }

    private static Vector4 Tint(MissionScriptNodeKind kind) => kind switch
    {
        MissionScriptNodeKind.Module => ModuleTint, MissionScriptNodeKind.Function => FunctionTint, MissionScriptNodeKind.Flow => FlowTint,
        MissionScriptNodeKind.Callback or MissionScriptNodeKind.Call or MissionScriptNodeKind.ApiCall => CallbackTint,
        MissionScriptNodeKind.If or MissionScriptNodeKind.ElseIf or MissionScriptNodeKind.Else => FlowTint,
        MissionScriptNodeKind.For or MissionScriptNodeKind.While or MissionScriptNodeKind.Repeat or MissionScriptNodeKind.Until => FunctionTint,
        _ => ValueTint
    };

    private static string KindLabel(MissionScriptNode node) => node.Kind switch
    {
        MissionScriptNodeKind.Module => "module",
        MissionScriptNodeKind.Function => "function",
        MissionScriptNodeKind.Flow => "flow graph",
        MissionScriptNodeKind.Callback => node.ApiEvents is [var served] ? served.Name.ToLowerInvariant() + " callback" : "callback",
        MissionScriptNodeKind.ApiCall => "call on " + node.ApiCall!.Receiver!.Describe(),
        MissionScriptNodeKind.Setting => "returned setting",
        MissionScriptNodeKind.Value => "value",
        MissionScriptNodeKind.If => "if / branches", MissionScriptNodeKind.ElseIf => "else if", MissionScriptNodeKind.Else => "else branch",
        MissionScriptNodeKind.While => "while loop", MissionScriptNodeKind.For => "for loop",
        MissionScriptNodeKind.Repeat => "repeat loop", MissionScriptNodeKind.Until => "loop condition",
        MissionScriptNodeKind.Scope => "local scope", MissionScriptNodeKind.Call => "call",
        MissionScriptNodeKind.Assignment => "assignment", MissionScriptNodeKind.Return => "return", MissionScriptNodeKind.Break => "break",
        _ => "custom Lua"
    };

    /** A module shows its local name; its require line is the code below it. */
    private static string NodeTitle(MissionScriptNode node) =>
        node.Kind == MissionScriptNodeKind.Module ? string.Join(", ", node.Defines) : node.Title;

    private CanvasNode ScriptCanvasNode(MissionScriptNode node) => new(node.Key, NodeTitle(node), KindLabel(node),
        node.Kind is MissionScriptNodeKind.Code or MissionScriptNodeKind.Module or MissionScriptNodeKind.Setting
            ? Preview(scriptGraph!.Code(node)) : null, Tint(node.Kind), Rows: ApiRows(node));

    private string NodeLabel(MissionGraphNode node, MissionGraphGroup group)
    {
        if (node.Table is null) return "Custom Lua";
        if (group.IsFlowSteps) return visual?.ReadFlowAction(node.Key) is { } action ? TemplateName(action.Kind) : "Custom Lua";
        return Friendly(node.Kind);
    }

    /** Code that no typed field can show stays visible inside the node. */
    private string? NodeCode(MissionGraphNode node, MissionGraphGroup group)
    {
        if (node.Table is null) return node.Entry.Value.Read(graph!.Source);
        if (group.IsFlowSteps && visual?.ReadFlowAction(node.Key) is null)
            return graph!.Field(node.Key, "run")?.Value.Read(graph.Source);
        if (group.IsFlowFacts) return graph!.Field(node.Key, "observe")?.Value.Read(graph.Source);
        return null;
    }

    /** The selected link; an editable one gets a Disconnect button. */
    private void EdgeInspector(CanvasEdge edge, bool editable, CanvasActions actions, IReadOnlyList<CanvasNode> nodes)
    {
        ImGui.TextUnformatted("Link"); ImGui.Separator();
        ImGui.TextWrapped(NodeCanvas.LinkText(edge, nodes));
        if (edge.Label.Length != 0) ImGui.TextDisabled(edge.Label);
        if (!edge.Editable) { ImGui.TextWrapped(NodeCanvas.FixedLinkNote); return; }
        ImGui.BeginDisabled(!editable || actions.Disconnect is null);
        if (UiAuto.Button("Disconnect")) actions.Disconnect!(edge.Source, edge.Target);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Or drag the link off its input, or right-click it");
        ImGui.EndDisabled();
    }

    private void RemoveSelected() => Change(document => document.RemoveNode(selected));
}
