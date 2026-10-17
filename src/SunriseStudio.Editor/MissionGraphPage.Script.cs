using ImGuiNET;
using SunriseStudio.Missions;
using SunriseStudio.Viewport;
using static SunriseStudio.Editor.StudioUi;

namespace SunriseStudio.Editor;

/**
 * MissionGraphPage part: inspector and edits for nodes of a parsed Lua script (body templates, move, remove,
 * inputs).
 */
public sealed partial class MissionGraphPage
{
    private string addScriptScope = "";

    /** Typed inputs edit exact source spans; code blocks retain unsupported constructs. */
    private void ScriptInspector(bool editable)
    {
        if (HasSourceDraft) ImGui.TextWrapped(LuaTabFirst);
        if (scriptGraph is null) { ImGui.TextWrapped(scriptError); return; }
        DrawApiStatus();
        var siblings = Siblings(scriptGraph);
        var node = siblings.FirstOrDefault(value => value.Key == selected);
        if (node is null)
        {
            ImGui.TextDisabled("Select a node");
            if (groupKey != MissionScriptGraph.Root) return;
            ImGui.PushTextWrapPos(0);
            ImGui.TextDisabled("Double-click a function, callback or flow to open it.");
            ImGui.PopTextWrapPos();
            return;
        }
        ImGui.TextUnformatted(NodeTitle(node)); ImGui.TextDisabled(KindLabel(node)); ImGui.Separator();
        ImGui.BeginDisabled(!editable || node.Structural);
        if (ImGuiIcons.Button("earlier", StudioIcon.Up, "Move before the previous node")) MoveScriptNode(node.Key, -1);
        ImGui.SameLine();
        if (ImGuiIcons.Button("later", StudioIcon.Down, "Move after the next node")) MoveScriptNode(node.Key, 1);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!editable || node.Structural && node.Kind is not (MissionScriptNodeKind.ElseIf or MissionScriptNodeKind.Else));
        if (ImGuiIcons.Button("remove", StudioIcon.Remove, "Remove node")) { RemoveScriptNode(node.Key); ImGui.EndDisabled(); return; }
        ImGui.EndDisabled();
        if (scriptGraph.CanOpen(node))
        {
            ImGui.SameLine();
            if (ImGuiIcons.Button("open", StudioIcon.Open, "Open the body")) OpenGroup(BodyKey(node.Key));
        }
        if (node.FlowGroup is not null)
        {
            ImGui.SameLine();
            if (ImGuiIcons.Button("open-flow", StudioIcon.Forward, "Open the flow")) OpenScriptNode(node.Key);
        }
        if (node.Inputs.Count != 0) ScriptInputs(node, editable);
        DrawApiCalls(node, editable);
        DrawApiEvent(node);
        if (scriptGraph.CanOpen(node))
        {
            ImGui.SeparatorText("Add inside");
            ImGui.BeginDisabled(!editable);
            var scopes = scriptGraph.Scopes(node);
            for (int i = 0; i < scopes.Count; i++)
            {
                var scope = scopes[i];
                if (i > 0) Flow(ImGui.GetFrameHeight() + ImGui.CalcTextSize(scope.Title).X + ImGui.GetStyle().ItemSpacing.X * 2);
                string place = scope.Title == "body" ? "the body" : $"the {scope.Title} branch";
                if (ImGuiIcons.Button("add-" + scope.Key, StudioIcon.Plus, "Add to " + place))
                { addScriptScope = scope.Key; ImGui.OpenPopup("Add script node"); }
                ImGui.SameLine(); ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted(scope.Title);
            }
            if (node.Kind == MissionScriptNodeKind.If)
            {
                ImGui.SeparatorText("Branches");
                if (UiAuto.SmallButton("Add else if")) AddScript(view => view.AddBranch(view.Node(node.Key), "false"));
                ImGui.SameLine();
                ImGui.BeginDisabled(node.Control!.Bodies.Any(body => body.Label == "else"));
                if (UiAuto.SmallButton("Add else")) AddScript(view => view.AddBranch(view.Node(node.Key), null));
                ImGui.EndDisabled();
            }
            ImGui.EndDisabled();
            DrawScriptTemplates();
        }
        if (node.FallbackReason is { } reason) ImGui.TextWrapped(reason);
        bool showCode = node.Kind is MissionScriptNodeKind.Code or MissionScriptNodeKind.Module or MissionScriptNodeKind.Setting;
        if (!node.Structural && (showCode || UiAuto.CollapsingHeader("Advanced Lua")))
        {
            ImGui.SeparatorText("Code");
            ImGui.BeginDisabled(!editable);
            if (LuaEditor(node.Key + "\ncode", scriptGraph.Code(node), node.Key) is { } draft)
            {
                string key = node.Key;
                // Spans come from a fresh parse of the document being edited, never from the view on screen.
                ChangeLua(document => MissionGraphDocument.Parse(Fresh(document, key, out var fresh).Replace(fresh, draft)), node.Key + "\ncode");
            }
            ImGui.EndDisabled();
        }
        if (groupKey != MissionScriptGraph.Root) return;
        var uses = scriptGraph.Edges.Where(edge => edge.Target == node.Key).ToArray();
        var users = scriptGraph.Edges.Where(edge => edge.Source == node.Key).ToArray();
        if (uses.Length != 0)
        {
            ImGui.SeparatorText("Uses");
            foreach (var edge in uses)
                if (UiAuto.Selectable(edge.Label + "##uses-" + edge.Source)) selected = edge.Source;
        }
        if (users.Length != 0)
        {
            ImGui.SeparatorText("Used by");
            foreach (var edge in users)
                if (UiAuto.Selectable(NodeTitle(scriptGraph.Node(edge.Target)) + "##used-" + edge.Target)) selected = edge.Target;
        }
    }

    /** The nodes shown in the current script group: top-level nodes, or one function's statements. */
    private IReadOnlyList<MissionScriptNode> Siblings(MissionScriptGraph view)
    {
        if (BodyOwner(groupKey) is not { } owner) return view.Nodes;
        return view.FindNode(owner) is { } node ? view.Body(node).Nodes : [];
    }

    /** Finds a node by key in a fresh parse of document; keys are stable across unrelated edits. */
    private MissionScriptGraph Fresh(MissionGraphDocument document, string key, out MissionScriptNode node)
    {
        var view = MissionScriptGraph.Parse(document.Source, document.Groups, apiContext);
        node = Siblings(view).FirstOrDefault(value => value.Key == key)
            ?? throw new InvalidOperationException("The node changed; select it again.");
        return view;
    }

    private void RemoveScriptNode(string key)
    {
        if (Change(document =>
        {
            var view = Fresh(document, key, out var node);
            return node.Structural && node.Kind is not (MissionScriptNodeKind.ElseIf or MissionScriptNodeKind.Else)
                ? document : MissionGraphDocument.Parse(view.Remove(node));
        })) selected = "";
    }

    private void MoveScriptNode(string key, int direction) => Change(document =>
    {
        var view = Fresh(document, key, out _);
        return MissionGraphDocument.Parse(view.Move(Siblings(view), key, direction));
    });

    // Lua control flow and lib.flow calls; Sunrise API calls come from the schema palette.
    private static readonly (string Name, string Code)[] BodyTemplates =
    [
        ("If", "if false then\nend"),
        ("If / else", "if false then\nelse\nend"), ("While", "while false do\nend"),
        ("Counted loop", "for i = 1, 3 do\nend"), ("For each", "for key, item in pairs({}) do\nend"),
        ("Repeat until", "repeat\nuntil true"), ("Local scope", "do\nend"),
        ("Assignment", "local value = nil"), ("Return", "return"),
        ("Advance flow", "graph:advance(context, state)"),
        ("Send event to flow", "graph:handle(context, state, event)")
    ];

    /** New script nodes are real code: a function, a value, a callback or a statement. */
    private void DrawScriptTemplates()
    {
        if (!ImGui.BeginPopup("Add script node")) return;
        if (groupKey == MissionScriptGraph.Root && addScriptScope.Length == 0)
        {
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
            ImGui.SetNextItemWidth(200);
            UiAuto.InputTextWithHint("##new-name", "Name", ref newId, 64);
            Tip("Name of a new function or value");
            string name = LuaScript.IsName(newId) ? newId : "";
            ImGui.BeginDisabled(name.Length == 0);
            string parameters = string.Join(", ", apiSchema?.Program.StartParameters ?? []);
            if (UiAuto.Selectable("Function")) AddScript(view => view.AddStatement($"local function {name}({parameters})\nend"));
            if (UiAuto.Selectable("Value")) AddScript(view => view.AddStatement($"local {name} = nil"));
            ImGui.EndDisabled();
            foreach (var (label, code) in BodyTemplates)
                if (label != "Return" && UiAuto.Selectable(label)) AddScript(view => view.AddStatement(code));
            DrawCallbackPalette();
            if (apiSchema is null) { ImGui.TextDisabled("No API schema"); ApiStatusTip(); }
        }
        else
        {
            string scope = addScriptScope;
            if (scope.Length == 0)
            {
                var owner = scriptGraph!.Node(BodyOwner(groupKey)!);
                scope = scriptGraph.Scopes(owner).First().Key;
            }
            foreach (var (label, code) in BodyTemplates)
                if (UiAuto.Selectable(label)) { AddScript(view => view.AddToScope(scope, code)); addScriptScope = ""; }
            if (scriptGraph!.CanBreak(scope) && UiAuto.Selectable("Break"))
            { AddScript(view => view.AddToScope(scope, "break")); addScriptScope = ""; }
            DrawMemberPalette(scope);
            if (apiSchema is null) { ImGui.TextDisabled("No API schema"); ApiStatusTip(); }
        }
        ImGui.EndPopup();
    }

    private void AddScript(Func<MissionScriptGraph, string> edit) =>
        Change(document => MissionGraphDocument.Parse(edit(MissionScriptGraph.Parse(document.Source, document.Groups))));

    /** Draft editors for a node's inputs; Apply writes every changed input at once. */
    private void ScriptInputs(MissionScriptNode node, bool editable)
    {
        ImGui.SeparatorText("Inputs");
        var changes = new List<(string Name, string Value)>();
        bool currentInputs = true;
        ImGui.BeginDisabled(!editable);
        foreach (var input in node.Inputs)
        {
            string id = node.Key + "\ninput:" + input.Name, current = input.Span.Read(graph!.Source);
            var draft = ReadLuaDraft(id, current, node.Key, "input:" + input.Name);
            currentInputs &= Matches(draft, current, node.Key, "input:" + input.Name);
            string text = draft.Draft;
            ImGui.TextUnformatted(input.Name); ImGui.SetNextItemWidth(-1);
            bool multiline = text.Contains('\n');
            var flags = multiline ? ImGuiInputTextFlags.AllowTabInput : ImGuiInputTextFlags.None;
            CodeInput("##" + id, ref text, 64 * 1024, new(-1, 90), flags, multiline);
            luaDrafts[id] = draft with { Draft = text };
            if (text != current) changes.Add((input.Name, text));
        }
        if (!currentInputs) ImGui.TextWrapped("These inputs changed. Copy your drafts, then revert.");
        ImGui.BeginDisabled(changes.Count == 0 || !currentInputs);
        if (UiAuto.SmallButton("Apply##inputs")) ApplyScriptInputs(node.Key, changes);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(changes.Count == 0 && currentInputs);
        if (UiAuto.SmallButton("Revert##inputs"))
            foreach (var input in node.Inputs)
            {
                string current = input.Span.Read(graph!.Source);
                luaDrafts[node.Key + "\ninput:" + input.Name] = new(current, current, luaDraftDocument, node.Key, "input:" + input.Name);
            }
        ImGui.EndDisabled();
        ImGui.EndDisabled();
    }

    /** Inputs apply in order, each to a fresh parse, so no input edits a stale span. */
    private bool ApplyScriptInputs(string key, IReadOnlyList<(string Name, string Value)> changes) => ChangeLua(document =>
    {
        var current = document;
        foreach (var input in changes.OrderBy(value => value.Name is "Names" or "Call" ? 1 : 0))
        {
            var view = MissionScriptGraph.Parse(current.Source, current.Groups);
            current = MissionGraphDocument.Parse(view.SetInput(view.Node(key), input.Name, input.Value));
        }
        return current;
    }, changes.Select(input => key + "\ninput:" + input.Name).ToArray());
}
