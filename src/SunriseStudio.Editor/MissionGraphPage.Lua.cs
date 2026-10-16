using ImGuiNET;
using SunriseStudio.Missions;

namespace SunriseStudio.Editor;

public sealed partial class MissionGraphPage
{
    private sealed record LuaDraft(string Original, string Draft, object Document, string Owner, string? Path);
    private readonly Dictionary<string, LuaDraft> luaDrafts = new(StringComparer.Ordinal);
    private object luaDraftDocument = new();
    private bool HasLuaDrafts => luaDrafts.Values.Any(value => value.Draft != value.Original);
    private const string LuaTabFirst = "Apply or revert the Lua tab first.";

    private LuaDraft ReadLuaDraft(string id, string current, string owner, string? path)
    {
        if (!luaDrafts.TryGetValue(id, out var entry)) return new(current, current, luaDraftDocument, owner, path);
        if (entry.Draft == entry.Original && !Matches(entry, current, owner, path))
            return new(current, current, luaDraftDocument, owner, path);
        return entry;
    }

    private bool Matches(LuaDraft draft, string current, string owner, string? path) =>
        ReferenceEquals(draft.Document, luaDraftDocument) && draft.Owner == owner && draft.Path == path && draft.Original == current;

    private void RequireNoLuaDrafts()
    {
        if (HasLuaDrafts) throw new InvalidOperationException("Apply or revert open Lua edits first.");
    }

    /**
     * Leaf edits may coexist; replacing an overlapping field requires its other draft to be resolved.
     * Field and node edits wait for the Lua tab draft; plain changes fold it in.
     */
    private void RequireLuaChange(IReadOnlyCollection<string>? applied)
    {
        if (applied is not null && HasSourceDraft) throw new InvalidOperationException(LuaTabFirst);
        var pending = luaDrafts.Where(pair => pair.Value.Draft != pair.Value.Original).ToArray();
        if (pending.Length == 0) return;
        if (applied is null || applied.Count == 0) { RequireNoLuaDrafts(); return; }
        var accepted = pending.Where(pair => applied.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
        if (accepted.Length == 0 || pending.Any(pair => !ReferenceEquals(pair.Value.Document, luaDraftDocument)))
            throw new InvalidOperationException("The Lua draft belongs to another document; revert it before applying.");
        foreach (var other in pending.Where(pair => !applied.Contains(pair.Key)).Select(pair => pair.Value))
            if (accepted.Any(value => value.Owner == other.Owner && Overlaps(value.Path, other.Path)))
                throw new InvalidOperationException("Apply or revert the other Lua draft in this field first.");

        static bool Overlaps(string? first, string? second) => first is null || second is null || first == second
            || first.StartsWith(second + "/", StringComparison.Ordinal) || second.StartsWith(first + "/", StringComparison.Ordinal)
            || first.StartsWith(second + ".", StringComparison.Ordinal) || second.StartsWith(first + ".", StringComparison.Ordinal);
    }

    private void ClearLuaDrafts()
    {
        luaDrafts.Clear(); luaDraftDocument = new();
    }

    /**
     * Edits one Lua expression in place. The caller disables it for a read-only document.
     * @param id Key for the draft; unique among the editors on screen.
     * @param current The expression as it stands in the source.
     * @param preview False for an always-open editor with no Edit/Preview toggle.
     * @return The draft when Apply is pressed; otherwise null.
     */
    private string? LuaEditor(string id, string current, string owner, string? path = null, bool preview = true)
    {
        var entry = ReadLuaDraft(id, current, owner, path);
        bool matches = Matches(entry, current, owner, path);
        string draft = entry.Draft;
        int lines = Math.Clamp(draft.Count(value => value == '\n') + 1, 2, 18);
        ImGui.PushID(id);
        LuaText(id, ref draft, new(-1, (lines + 1.5f) * ImGui.GetTextLineHeight()), preview: preview);
        luaDrafts[id] = entry with { Draft = draft };
        if (!matches) ImGui.TextWrapped("This field changed. Copy your draft, then revert.");
        ImGui.BeginDisabled(!matches || draft == current);
        bool apply = UiAuto.SmallButton("Apply");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(matches && draft == current);
        if (UiAuto.SmallButton("Revert")) luaDrafts[id] = new(current, current, luaDraftDocument, owner, path);
        ImGui.EndDisabled();
        ImGui.PopID();
        return apply ? draft : null;
    }

    /** An empty draft removes the field; any other draft replaces the field's expression. */
    private bool FieldLua(string node, string path)
    {
        string current = graph!.Field(node, path)?.Value.Read(graph.Source) ?? "";
        string id = node + "\n" + path;
        if (LuaEditor(id, current, node, path) is not { } draft) return false;
        return ChangeLua(document => draft.Trim().Length != 0 ? document.SetField(node, path, draft)
            : document.Field(node, path) is null ? document : document.RemoveField(node, path), id);
    }

    /** A node written as Lua code is replaced as one expression; the selection keeps its position. */
    private void EntryLua(MissionGraphNode node)
    {
        ImGui.TextDisabled("Custom Lua node");
        if (LuaEditor(node.Key, node.Entry.Value.Read(graph!.Source), node.Key) is not { } draft) return;
        int index = graph.Group(node.GroupKey).Nodes.ToList().FindIndex(value => value.Key == node.Key);
        if (ChangeLua(document => document.SetEntry(node.Key, draft), node.Key))
            selected = graph.Group(node.GroupKey).Nodes.ElementAtOrDefault(index)?.Key ?? "";
    }
}
