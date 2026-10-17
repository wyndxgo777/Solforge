using ImGuiNET;
using SunriseStudio.Missions;

namespace SunriseStudio.Editor;

/** MissionGraphPage part: the All/Any condition tree editor for flow nodes. */
public sealed partial class MissionGraphPage
{
    /** All/Any groups preserve custom predicate leaves until an explicit replacement or removal. */
    private bool DrawFlowCondition(string label, MissionGraphNode node, MissionGraphGroup group, string field)
    {
        var facts = graph!.NodesIn(group, "facts").Select(value => value.Id).ToArray();
        MissionCondition? condition;
        ImGui.SeparatorText(label);
        try { condition = MissionConditions.Read(graph.Field(node.Key, field)?.Value.Read(graph.Source), visual!.Bindings); }
        catch (InvalidDataException error) { ImGui.TextWrapped(error.Message); return FieldLua(node.Key, field); }
        ImGui.PushID(field);
        bool changed = DrawCondition(condition, facts, true, node.Key + "\n" + field, node.Key, out var replacement, out var draftId);
        if (changed)
        {
            MissionGraphDocument Apply(MissionGraphDocument document) =>
                new MissionVisualAuthoring(document, references).SetCondition(node.Key, field, replacement);
            if (draftId is null) Change(Apply); else ChangeLua(Apply, draftId);
        }
        // Custom Lua already shows its editor; a typed condition keeps its code one click away.
        else if (condition is not MissionCustomCondition && UiAuto.TreeNode("Lua"))
        { changed = FieldLua(node.Key, field); ImGui.TreePop(); }
        ImGui.PopID();
        return changed;
    }

    /** @param path Draft key for a custom Lua leaf; unique per node, field and child position. */
    private bool DrawCondition(MissionCondition? condition, IReadOnlyList<string> facts, bool root, string path, string owner,
        out MissionCondition? replacement, out string? draftId)
    {
        draftId = null;
        replacement = condition;
        string kind = condition switch { null => "Immediately", MissionFactCondition => "Fact",
            MissionConditionGroup group => group.All ? "All" : "Any", _ => "Custom Lua" };
        ImGui.SetNextItemWidth(-1);
        if (UiAuto.BeginCombo("##condition-kind", kind))
        {
            if (root && UiAuto.Selectable("Immediately", condition is null)) replacement = null;
            ImGui.BeginDisabled(facts.Count == 0);
            foreach (string choice in new[] { "Fact", "All", "Any" })
                if (UiAuto.Selectable(choice, kind == choice) && choice != kind)
                {
                    var first = condition is MissionFactCondition fact ? fact : new MissionFactCondition(facts[0]);
                    replacement = choice == "Fact" ? first : condition is MissionConditionGroup group
                        ? group with { All = choice == "All" } : new MissionConditionGroup(choice == "All", [condition ?? first]);
                }
            ImGui.EndDisabled(); UiAuto.EndCombo();
        }
        if (!Equals(replacement, condition)) return true;
        if (condition is MissionFactCondition leaf)
        {
            ImGui.SetNextItemWidth(-1);
            if (UiAuto.BeginCombo("##fact", Friendly(leaf.Fact)))
            {
                foreach (string fact in facts)
                    if (UiAuto.Selectable(Friendly(fact) + "##" + fact, fact == leaf.Fact)) replacement = new MissionFactCondition(fact);
                UiAuto.EndCombo();
            }
            return !Equals(replacement, condition);
        }
        if (condition is MissionConditionGroup children)
        {
            ImGui.Indent(10);
            for (int i = 0; i < children.Children.Count; i++)
            {
                ImGui.PushID(i);
                bool changed = DrawCondition(children.Children[i], facts, false, path + "/" + i, owner, out var child, out var childDraft);
                ImGui.BeginDisabled(children.Children.Count == 1);
                bool remove = UiAuto.SmallButton("Remove condition"); ImGui.EndDisabled(); ImGui.PopID();
                if (changed || remove)
                {
                    var next = children.Children.ToList();
                    if (remove) next.RemoveAt(i); else next[i] = child!;
                    draftId = remove ? null : childDraft;
                    replacement = children with { Children = next }; ImGui.Unindent(10); return true;
                }
                ImGui.Spacing();
            }
            ImGui.BeginDisabled(facts.Count == 0 || children.Children.Count >= 64);
            bool add = UiAuto.SmallButton("Add condition"); ImGui.EndDisabled();
            if (add) replacement = children with { Children = children.Children.Append(new MissionFactCondition(facts[0])).ToArray() };
            ImGui.Unindent(10); return add;
        }
        if (condition is not MissionCustomCondition custom
            || LuaEditor(path, custom.Source, owner, path[(owner.Length + 1)..]) is not { } draft) return false;
        // An empty root means "immediately"; an empty leaf would leave a hole in its group.
        if (draft.Trim().Length != 0) replacement = new MissionCustomCondition(draft);
        else if (root) replacement = null;
        else { Fail("Use Remove condition to drop a condition."); return false; }
        draftId = path;
        return true;
    }
}
