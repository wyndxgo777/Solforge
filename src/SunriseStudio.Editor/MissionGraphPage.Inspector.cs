using ImGuiNET;
using SunriseStudio.Missions;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Flow and campaign node inspector: typed fields, then Advanced Lua. */
public sealed partial class MissionGraphPage
{
    private MissionVisualAuthoring? visual;
    private string pickerSearch = "", typedLeg = "", typedNode = "";
    private MissionSdkReference? newCue, newFilter;
    // The scene picked for a step's output before its output is picked.
    private MissionSdkReference? outputScene;

    /** Flow and campaign nodes: typed fields first, then any field as Lua. */
    private void Inspector(bool editable)
    {
        var node = graph!.Nodes.FirstOrDefault(value => value.Key == selected && value.GroupKey == groupKey);
        if (node is null) { ImGui.TextDisabled("Select a node"); return; }
        ImGui.TextUnformatted(Friendly(node.Id)); ImGui.Separator();
        ImGui.BeginDisabled(!editable);
        if (ImGuiIcons.Button("earlier", StudioIcon.Up, "Move before the previous node")) Change(document => document.MoveNode(selected, -1));
        ImGui.SameLine();
        if (ImGuiIcons.Button("later", StudioIcon.Down, "Move after the next node")) Change(document => document.MoveNode(selected, 1));
        ImGui.SameLine();
        if (ImGuiIcons.Button("remove", StudioIcon.Remove, "Remove node")) { RemoveSelected(); ImGui.EndDisabled(); return; }
        if (node.Table is null) { EntryLua(node); ImGui.EndDisabled(); return; }
        ImGui.EndDisabled();
        foreach (var callback in scriptGraph?.Callbacks.Where(value => value.CallbackOwner == node.Key) ?? [])
        {
            ImGui.SameLine();
            if (ImGuiIcons.Button("open-" + callback.Key, StudioIcon.Open, "Open " + callback.CallbackPath)) OpenGroup(BodyKey(callback.Key));
        }
        if (DrawTypedInspector(node, editable)) return;
        if (!ImGui.CollapsingHeader("Advanced Lua")) return;
        foreach (var property in node.Table.Fields.Where(value => value.Name is not (null or "id")))
            if (UiAuto.Selectable(Friendly(property.Name!) + "##" + property.Name, advancedField == property.Name))
            { advancedField = property.Name!; advancedValue = property.Value.Read(graph.Source); }
        ImGui.Separator(); ImGui.SetNextItemWidth(-1); UiAuto.InputTextWithHint("##field", "Field / nested.field", ref advancedField, 128);
        CodeInput("##field-value", ref advancedValue, 64 * 1024, new(-1, 150),
            editable ? ImGuiInputTextFlags.AllowTabInput : ImGuiInputTextFlags.ReadOnly);
        ImGui.BeginDisabled(!editable || string.IsNullOrWhiteSpace(advancedField));
        if (UiAuto.SmallButton("Apply##field")) Change(document => document.SetField(selected, advancedField, advancedValue));
        ImGui.EndDisabled();
        ImGui.SeparatorText("SDK references"); ImGui.SetNextItemWidth(-1);
        UiAuto.InputTextWithHint("##sdk-filter", "Find slot, squad, state", ref referenceFilter, 256);
        ImGui.BeginChild("sdk-references", new(0, 0), ImGuiChildFlags.None);
        foreach (var reference in references.Where(item => referenceFilter.Length > 0 &&
            (item.Name.Contains(referenceFilter, StringComparison.OrdinalIgnoreCase)
             || item.Expression.Contains(referenceFilter, StringComparison.OrdinalIgnoreCase))).Take(80))
        {
            if (UiAuto.Selectable(reference.Name + "##" + reference.Expression)) advancedValue = reference.Expression;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(reference.Expression + "\n" + reference.Identity);
        }
        ImGui.EndChild();
    }

    /** Common fields use typed SDK identities; custom expressions remain in Advanced Lua. */
    private bool DrawTypedInspector(MissionGraphNode node, bool editable)
    {
        var group = graph!.Group(node.GroupKey);
        if (visual is null || visual.Bindings.MissionVariable is null) return false;
        if (typedNode != node.Key)
        {
            typedNode = node.Key; typedLeg = ""; newCue = newFilter = outputScene = null;
            if (Current(node.Key, "ends.trigger") is { } currentTrigger)
            {
                var armed = graph.NodesIn(group, "legs").Where(leg => graph.Field(leg.Key, "arm")?.Table?.Fields
                        .Any(value => visual.Resolve(value.Value.Read(graph.Source))?.Identity == currentTrigger.Identity) == true).ToArray();
                if (armed.Length == 1) typedLeg = armed[0].Key;
            }
        }
        ImGui.BeginDisabled(!editable);
        try
        {
            if (group.IsCampaignSteps)
            {
                if (ReferenceField("Objective", node.Key, "directive", MissionReferenceKind.Directive)
                    || ReferenceField("Navpoint", node.Key, "navpoint", MissionReferenceKind.Navpoint)
                    || ReferenceField("Waypoint volume", node.Key, "waypoint", MissionReferenceKind.Volume)) return true;
                if (DrawStepScene(node)) return true;
                ImGui.SeparatorText("Completion");
                LegPicker(group, ref typedLeg);
                // SetTrigger needs a trigger; clearing goes through SetReference.
                if (ReferenceField("Trigger", node.Key, "ends.trigger", MissionReferenceKind.Trigger, (authoring, chosen) => chosen is null
                    ? authoring.SetReference(node.Key, "ends.trigger", MissionReferenceKind.Trigger, null)
                    : authoring.SetTrigger(node.Key, chosen, typedLeg))) return true;
                if (ReferenceField("Ghost scan", node.Key, "ends.ghost_link", MissionReferenceKind.GhostLink)
                    || ReferenceField("Interaction", node.Key, "ends.interact", MissionReferenceKind.Object)
                    || ReferenceField("Scene finished", node.Key, "ends.scene", MissionReferenceKind.Scene)
                    || DrawSceneOutput(node)) return true;
                if (NodeIdField("Encounter cleared", node, group, "ends.clear", "encounters")
                    || NodeIdField("Region reached", node, group, "ends.region", "legs")) return true;
                if (BooleanField("Barrier", node.Key, "barrier") || BooleanField("Revisit", node.Key, "revisit")) return true;
                if (graph.Field(node.Key, "ends") is null || graph.Field(node.Key, "ends")?.Table?.Fields.Count == 0)
                    ImGui.TextDisabled("Completes immediately");
                if (graph.Field(node.Key, "ends.destroyed") is not null) ImGui.TextDisabled("Destroyed-object list: Advanced Lua");
                if (DrawDialogue(node)) return true;
            }
            else if (group.IsCampaignLegs)
            {
                if (ReferenceField("State", node.Key, "state", MissionReferenceKind.State)) return true;
                ImGui.TextDisabled("Trigger and monitor lists: Advanced Lua");
            }
            else if (group.IsFlowSteps)
            {
                DrawFlowAction(node);
                if (BooleanField("Checkpoint", node.Key, "checkpoint")) return true;
                if (DrawFlowCondition("Start when", node, group, "when") || DrawFlowCondition("Finish when", node, group, "await")) return true;
                ImGui.SeparatorText("Dependencies");
                foreach (var other in group.Nodes.Where(value => value.Key != node.Key))
                {
                    bool checkedValue = node.Dependencies?.Contains(other.Id) == true;
                    if (UiAuto.Checkbox(Friendly(other.Id) + "##after-" + other.Key, ref checkedValue))
                    { Change(document => document.Connect(other.Key, node.Key, checkedValue)); return true; }
                }
            }
        }
        finally { ImGui.EndDisabled(); }
        return false;
    }

    /**
     * Picks an SDK reference for one field; a value that does not resolve shows its Lua editor.
     * @param set Writes the pick; null sets the field with SetReference.
     */
    private bool ReferenceField(string label, string node, string path, MissionReferenceKind kind,
        Func<MissionVisualAuthoring, MissionSdkReference?, MissionGraphDocument>? set = null)
    {
        var current = Current(node, path);
        string? custom = graph!.Field(node, path) is not null && current is null ? "Custom Lua" : null;
        if (!Pick(label, current, visual!.Choices(kind), out var chosen, true, custom))
            return custom is not null && FieldLua(node, path);
        Author(authoring => set is null ? authoring.SetReference(node, path, kind, chosen) : set(authoring, chosen));
        return true;
    }

    private MissionSdkReference? Current(string node, string path) => graph!.Field(node, path) is { } field
        ? visual!.Resolve(field.Value.Read(graph.Source)) : null;

    private bool BooleanField(string label, string node, string path)
    {
        string? expression = graph!.Field(node, path)?.Value.Read(graph.Source);
        if (expression is not (null or "true" or "false" or "nil")) { ImGui.TextDisabled(label + ": Custom Lua"); return FieldLua(node, path); }
        bool value = expression == "true";
        if (!UiAuto.Checkbox(label, ref value)) return false;
        Change(document => document.SetField(node, path, value ? "true" : "false")); return true;
    }

    /** Picks a node id of a collection for a field; a value that is not a plain id shows its Lua editor. */
    private bool NodeIdField(string label, MissionGraphNode node, MissionGraphGroup group, string path, string collection)
    {
        var candidates = graph!.NodesIn(group, collection);
        var current = graph.Field(node.Key, path); string? value = current?.StringValue;
        ImGui.TextDisabled(label); ImGui.SetNextItemWidth(-1);
        if (!UiAuto.BeginCombo("##" + path, value is not null ? Friendly(value) : current is null ? "None" : "Custom Lua"))
            return current is not null && value is null && FieldLua(node.Key, path);
        bool changed = false;
        if (UiAuto.Selectable("None", current is null) && current is not null)
        { Change(document => document.RemoveField(node.Key, path)); changed = true; }
        foreach (var item in candidates)
            if (!changed && UiAuto.Selectable(Friendly(item.Id) + "##" + item.Key, item.Id == value))
            { Change(document => document.SetField(node.Key, path, LuaScript.Quote(item.Id))); changed = true; }
        UiAuto.EndCombo(); return changed;
    }

    /** The step's dialogue lines with cue and filter pickers; a line written as custom Lua shows its Lua editor. */
    private bool DrawDialogue(MissionGraphNode node)
    {
        ImGui.SeparatorText("Dialogue");
        if (graph!.Field(node.Key, "lines") is { Table: null })
        { ImGui.TextDisabled("Custom Lua dialogue list"); return FieldLua(node.Key, "lines"); }
        var lines = visual!.Dialogue(node.Key);
        for (int index = 0; index < lines.Count; index++)
        {
            var line = lines[index]; ImGui.PushID(index);
            bool changed = false;
            if (!line.Editable)
            {
                string id = node.Key + "\nlines/" + index;
                if (LuaEditor(id, line.Entry.Value.Read(graph.Source), node.Key, "lines/" + index) is { } draft)
                { int at = index; ChangeLua(document => document.SetListValue(node.Key, "lines", at, draft), id); changed = true; }
            }
            else
            {
                var cues = CueChoices(node);
                if (Pick("Cue", line.Cue, cues, out var cue) && cue is not null)
                { int at = index; Author(authoring => authoring.SetDialogue(node.Key, at, cue, line.Filter)); changed = true; }
                if (!changed && Pick("Filter volume", line.Filter, visual.Choices(MissionReferenceKind.Volume), out var filter, true))
                { int at = index; Author(authoring => authoring.SetDialogue(node.Key, at, line.Cue!, filter)); changed = true; }
            }
            if (!changed && UiAuto.SmallButton("Remove line"))
            { int at = index; Change(document => document.RemoveListValue(node.Key, "lines", at)); changed = true; }
            ImGui.PopID(); if (changed) return true;
        }
        if (Pick("New cue", newCue, CueChoices(node), out var chosen)) newCue = chosen;
        if (Pick("New filter", newFilter, visual.Choices(MissionReferenceKind.Volume), out var filterChoice, true)) newFilter = filterChoice;
        ImGui.BeginDisabled(newCue is null);
        bool add = UiAuto.SmallButton("Add dialogue"); ImGui.EndDisabled();
        if (add) { Author(authoring => authoring.AddDialogue(node.Key, newCue!, newFilter)); return true; }
        return false;
    }

    private IReadOnlyList<MissionSdkReference> CueChoices(MissionGraphNode node)
    {
        var field = graph!.Group(node.GroupKey).Definition?.Field("dialogue_sensor");
        string sensor = field is null ? "" : visual!.Bindings.Expand(field.Value.Read(graph.Source));
        return visual!.Choices(MissionReferenceKind.Dialogue).Where(cue => cue.Sensor == sensor).ToArray();
    }

    /** Names in the list, expressions in the tooltip; an empty label draws no label row. */
    private bool Pick(string label, MissionSdkReference? current, IReadOnlyList<MissionSdkReference> choices,
        out MissionSdkReference? chosen, bool allowNone = false, string? emptyLabel = null)
    {
        chosen = current; bool changed = false;
        if (label.Length > 0) ImGui.TextDisabled(label);
        ImGui.SetNextItemWidth(-1);
        if (!UiAuto.BeginCombo("##pick-" + label, current?.Name ?? emptyLabel ?? (allowNone ? "None" : "Choose"))) return false;
        if (ImGui.IsWindowAppearing()) pickerSearch = "";
        ImGui.SetNextItemWidth(-1); UiAuto.InputTextWithHint("##find", "Find reference", ref pickerSearch, 256);
        if (allowNone && UiAuto.Selectable("None", current is null)) { chosen = null; changed = true; }
        foreach (var item in choices.Where(item => pickerSearch.Length == 0 || item.Name.Contains(pickerSearch, StringComparison.OrdinalIgnoreCase)
            || item.Expression.Contains(pickerSearch, StringComparison.OrdinalIgnoreCase)
            || item.Description.Contains(pickerSearch, StringComparison.OrdinalIgnoreCase)).Take(150))
        {
            if (UiAuto.Selectable(item.Name + "##" + item.Expression, item == current)) { chosen = item; changed = true; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(item.Description.Length > 0 ? item.Description + "\n" + item.Expression : item.Expression);
        }
        UiAuto.EndCombo(); return changed;
    }

    private void LegPicker(MissionGraphGroup group, ref string current)
    {
        var legs = graph!.NodesIn(group, "legs");
        string selectedLeg = current;
        ImGui.TextDisabled("Arm in leg"); ImGui.SetNextItemWidth(-1);
        if (!UiAuto.BeginCombo("##leg", legs.FirstOrDefault(value => value.Key == selectedLeg)?.Id ?? "Choose leg")) return;
        foreach (var leg in legs) if (UiAuto.Selectable(Friendly(leg.Id) + "##" + leg.Key, leg.Key == current)) current = leg.Key;
        UiAuto.EndCombo();
    }
}
