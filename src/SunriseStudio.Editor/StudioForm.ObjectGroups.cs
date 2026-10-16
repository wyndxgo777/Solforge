using System.Numerics;
using ImGuiNET;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Project groups of scene objects: the group list, the group inspector and group selection. */
public sealed partial class StudioForm
{
    private sealed record GroupMemberSources(IReadOnlyList<(ObjectEditTarget Member, object Source)> Present,
        IReadOnlyList<ObjectEditTarget> Missing);

    private string? selectedGroupId;
    private Dictionary<string, ProjectObjectGroup> groupByMember = new(StringComparer.Ordinal);
    private int groupIndexRevision = -1;
    private (object? Source, int Revision, ObjectEditTarget? Target, string Reason) groupCandidate;
    private string groupNameDraft = "", groupDraftId = "";
    private Vector3 groupMove, groupTurn, groupScale = Vector3.One;
    // The result of the last collision part change or selection of a group.
    private string groupPartsNote = "";
    // The last GroupSources answer; valid for one group record, project revision and world.
    private (ProjectObjectGroup? Group, int Revision, WorldDocument? World, GroupMemberSources Sources) groupSources;

    /** The group that holds this edit target key, if any. */
    private ProjectObjectGroup? GroupOf(string key)
    {
        if (groupIndexRevision != workspace.ProjectRevision)
        {
            groupIndexRevision = workspace.ProjectRevision;
            groupByMember = new(StringComparer.Ordinal);
            foreach (var group in workspace.Project?.ObjectGroups ?? [])
                foreach (string member in group.Keys) groupByMember[member] = group;
        }
        return groupByMember.GetValueOrDefault(key);
    }

    /** The selected object's target when it can join a group: only objects with handles can. */
    private ObjectEditTarget? GroupCandidate(out string reason)
    {
        if (!ReferenceEquals(groupCandidate.Source, selectedObject) || groupCandidate.Revision != workspace.ProjectRevision)
        {
            ObjectEditTarget? target = null;
            string why = "Select an object first.";
            if (selectedObject is { } source && workspace.Catalog is { } catalog)
                target = TransformHandles.Read(catalog, source, inspectionWorld, out why)?.Info.Target;
            groupCandidate = (selectedObject, workspace.ProjectRevision, target, why);
        }
        reason = groupCandidate.Reason;
        return groupCandidate.Target;
    }

    /** Members with their scene object in the open world, and the members it does not hold. */
    private GroupMemberSources GroupSources(ProjectObjectGroup group)
    {
        // Each member lookup scans the scene, so the answer is kept until the inputs change.
        var world = workspace.World;
        if (ReferenceEquals(groupSources.Group, group) && groupSources.Revision == workspace.ProjectRevision
            && ReferenceEquals(groupSources.World, world)) return groupSources.Sources;
        List<(ObjectEditTarget, object)> present = [];
        List<ObjectEditTarget> missing = [];
        foreach (var member in group.Members)
            if (FindEditedSource(member) is { } source) present.Add((member, source));
            else missing.Add(member);
        groupSources = (group, workspace.ProjectRevision, world, new(present, missing));
        return groupSources.Sources;
    }

    private string MemberName(object source) => ProjectName(source) ?? inspectionWorld?.Find(source)?.Name ?? "Object";

    /** Members outside the open world stay where they are; the pivot also covers the group's collision parts. */
    private GroupTransformFrame? ReadGroupFrame(PackageCatalog catalog, string id, out string reason)
    {
        if (workspace.Project?.FindObjectGroup(id) is not { } group) { reason = "This group no longer exists."; return null; }
        var sources = GroupSources(group).Present.Select(pair => pair.Source).ToArray();
        if (group.CollisionParts is not { Count: > 0 }) return GroupTransforms.Read(catalog, sources, inspectionWorld, MemberName, out reason);
        if (GroupCollisionParts(group, out reason) is not { } parts) return null;
        if (sources.Length == 0 && parts.Parts.Count == 0) { reason = "None of this group is in the open map."; return null; }
        var frame = sources.Length == 0 ? new GroupTransformFrame(Vector3.Zero, [])
            : GroupTransforms.Read(catalog, sources, inspectionWorld, MemberName, out reason);
        if (frame is null) return null;
        Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        foreach (var member in frame.Members)
        {
            minimum = Vector3.Min(minimum, member.Frame.Pivot);
            maximum = Vector3.Max(maximum, member.Frame.Pivot);
        }
        if (parts.Parts.Count > 0)
        {
            minimum = Vector3.Min(minimum, parts.Min);
            maximum = Vector3.Max(maximum, parts.Max);
        }
        reason = "";
        return frame with { Pivot = (minimum + maximum) / 2 };
    }

    private sealed record GroupParts(CollisionScene Scene, IReadOnlyList<CollisionPartRef> Parts, Vector3 Min, Vector3 Max);

    /**
     * The group's collision parts in the loaded world collision, with their world box.
     * @param reason Why the group cannot move; its parts would be left behind.
     */
    private GroupParts? GroupCollisionParts(ProjectObjectGroup group, out string reason)
    {
        if (viewport?.Collision is not { } scene)
        {
            reason = "Show the Collision layer to move this group";
            return null;
        }
        var (found, _) = CollisionPartKey.Resolve(scene, (group.CollisionParts ?? []).Select(FromProjectPart));
        if (found.Count > 0 && CollisionPartEdit.Refusal(scene, found) is { } refused)
        {
            reason = refused;
            return null;
        }
        Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        foreach (var byPlacement in found.GroupBy(part => part.Placement))
        {
            var placement = scene.Placements[byPlacement.Key];
            var shape = scene.Pieces[placement.Piece].Shape;
            var (low, high) = CollisionParts.Of(shape).Bounds(shape, byPlacement.Select(part => part.Part), placement.World);
            minimum = Vector3.Min(minimum, low);
            maximum = Vector3.Max(maximum, high);
        }
        reason = "";
        return new(scene, found, minimum, maximum);
    }

    /** Selects every member in the view; the first one fills the object fields. */
    private void SelectObjectGroup(string id)
    {
        if (workspace.Project?.FindObjectGroup(id) is not { } group || viewport is null) return;
        var items = new List<object>();
        foreach (var (_, source) in GroupSources(group).Present)
        {
            if (inspectionWorld?.Find(source) is { } row && pointProxies.TryGetValue(row.Id, out var point)) items.Add(point);
            else if (source is MeshInstance or SceneMarker) items.Add(source);
        }
        viewport.SetSelectedItems(items);
        OnViewportSelection(viewport.SelectedObject);
        // OnViewportSelection clears the group, so it is set after.
        selectedGroupId = id;
    }

    private void CreateGroupFromSelection(ObjectEditTarget target)
    {
        int count = workspace.Project?.ObjectGroups.Count ?? 0;
        string? id = null;
        workspace.Run(() => id = workspace.CreateObjectGroup($"Group {count + 1}", [target]));
        if (id is not null) SelectObjectGroup(id);
    }

    /** Matches the view to the selected group's saved members, or to one object once it is gone. */
    private void ReselectGroup()
    {
        if (selectedGroupId is not { } id) return;
        if (workspace.Project?.FindObjectGroup(id) is not null) SelectObjectGroup(id);
        else if (viewport is null) selectedGroupId = null;
        else
        {
            viewport.SetSelection(viewport.SelectedObject);
            OnViewportSelection(viewport.SelectedObject);
        }
    }

    private void SetGroupMembers(ProjectObjectGroup group, IReadOnlyList<ObjectEditTarget> members)
    {
        // A group needs one member, so removing the last one removes the group.
        if (members.Count == 0 && group.CollisionParts is null) { RemoveGroup(group); return; }
        workspace.Run(() => workspace.SetObjectGroupMembers(group.Id, members));
        if (selectedGroupId == group.Id) ReselectGroup();
    }

    private void SetGroupCollisionParts(ProjectObjectGroup group, IReadOnlyList<ProjectCollisionPart> parts)
    {
        if (parts.Count == 0 && group.Members.Count == 0) RemoveGroup(group);
        else workspace.Run(() => workspace.SetObjectGroupCollisionParts(group.Id, parts));
    }

    private void RemoveGroup(ProjectObjectGroup group)
    {
        workspace.Run(() => workspace.RemoveObjectGroup(group.Id));
        if (selectedGroupId == group.Id) ReselectGroup();
    }

    /** Adds the view's chosen collision parts to a group, or to a new one when group is null. */
    private void AddCollisionPartsToGroup(AssetViewport view, ProjectObjectGroup? group)
    {
        if (view.Collision is not { } scene) return;
        var held = group?.CollisionParts ?? [];
        var added = CollisionPartKey.Of(scene, view.CollisionPartSelection).Select(ToProjectPart).Where(part => !held.Contains(part)).ToArray();
        string name = group?.Name ?? $"Group {(workspace.Project?.ObjectGroups.Count ?? 0) + 1}";
        int total = held.Count + added.Length;
        if (total > ProjectObjectGroup.MaximumCollisionParts)
        {
            groupPartsNote = $"Too many collision parts ({total:N0} of {ProjectObjectGroup.MaximumCollisionParts:N0})";
            return;
        }
        workspace.Run(() =>
        {
            if (group is null) workspace.CreateObjectGroup(name, [], added);
            else workspace.SetObjectGroupCollisionParts(group.Id, [.. held, .. added]);
        });
        groupPartsNote = workspace.Error.Length == 0 ? $"{name} holds {total:N0} collision parts." : "Not added: " + workspace.Error;
    }

    /** Chooses the group's collision parts in the world view; parts not in this map are counted. */
    private void SelectGroupCollisionParts(ProjectObjectGroup group)
    {
        if (viewport?.Collision is not { } scene) { groupPartsNote = "Show the Collision layer, then select the parts again."; return; }
        var (found, missing) = CollisionPartKey.Resolve(scene, (group.CollisionParts ?? []).Select(FromProjectPart));
        groupPartsNote = missing.Count > 0 ? $"{missing.Count} collision parts are not in this map." : "";
        if (found.Count > 0) viewport.SelectCollisionParts(found);
    }

    private static ProjectCollisionPart ToProjectPart(CollisionPartKey key) => new(key.EntityTag, key.ListTag, key.ListRow,
        key.CompoundTag, key.Instance, key.ShapeTag, key.ShapeOffset, key.Bone, key.Ordinal, key.ShapeKey);

    private static CollisionPartKey FromProjectPart(ProjectCollisionPart part) => new(part.EntityTag, part.ListTag, part.ListRow,
        part.CompoundTag, part.Instance, part.ShapeTag, part.ShapeOffset, part.Bone, part.Ordinal, part.ShapeKey);

    private void ApplyGroupChange(string id, TransformChange change, GroupTransformFrame? frame = null)
    {
        if (workspace.Catalog is not { } catalog || workspace.Project?.FindObjectGroup(id) is not { } group) return;
        var world = inspectionWorld;
        workspace.Run(() =>
        {
            var members = frame ?? ReadGroupFrame(catalog, id, out string reason)
                ?? throw new InvalidDataException(reason);
            var edits = GroupTransforms.CreateEdits(catalog, members, change, world);
            GroupParts? parts = null;
            if (group.CollisionParts is { Count: > 0 })
                parts = GroupCollisionParts(group, out string refused) ?? throw new InvalidDataException(refused);
            workspace.TransformGroup(edits, parts?.Scene, parts?.Parts ?? [], change.Matrix(members.Pivot), $"Transformed {group.Name}");
        });
    }

    /** The Groups list above the object tree; world views with a project only. */
    private void DrawObjectGroups()
    {
        if (workspace.World is null || workspace.Project is not { } project) return;
        var groups = project.ObjectGroups;
        if (!ImGui.CollapsingHeader($"Groups ({groups.Count})###object-groups", ImGuiTreeNodeFlags.DefaultOpen)) return;
        var target = GroupCandidate(out string reason);
        bool grouped = target is not null && GroupOf(target.Key) is not null;
        ImGui.BeginDisabled(!workspace.CanEdit || target is null || grouped);
        if (UiAuto.Button("New group from selection", new(-1, 0)) && target is not null) CreateGroupFromSelection(target);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(target is null ? reason : grouped ? "The selected object is already in a group." : "Makes a group of the selected object");
        foreach (var group in groups)
        {
            ImGui.PushID(group.Id);
            if (UiAuto.Selectable($"{group.Name} ({group.Keys.Count()})", selectedGroupId == group.Id)) SelectObjectGroup(group.Id);
            ImGui.PopID();
        }
        ImGui.Separator();
    }

    /** The selected object's group line: select or leave its group, or join one. */
    private void DrawGroupMembership()
    {
        if (workspace.World is null || workspace.Project is not { } project) return;
        var target = GroupCandidate(out string reason);
        ImGui.BeginDisabled(!workspace.CanEdit);
        if (target is not null && GroupOf(target.Key) is { } group)
        {
            ImGui.TextDisabled("Group"); ImGui.SameLine(); ImGui.TextUnformatted(group.Name);
            if (UiAuto.SmallButton("Select group")) SelectObjectGroup(group.Id);
            ImGui.SameLine();
            if (UiAuto.SmallButton("Remove from group"))
                SetGroupMembers(group, group.Members.Where(member => member.Key != target.Key).ToArray());
        }
        else
        {
            ImGui.BeginDisabled(target is null);
            ImGui.SetNextItemWidth(-1);
            if (UiAuto.BeginCombo("##add-to-group", "Add to group"))
            {
                foreach (var each in project.ObjectGroups)
                {
                    ImGui.PushID(each.Id);
                    if (UiAuto.Selectable(each.Name) && target is not null) SetGroupMembers(each, [.. each.Members, target]);
                    ImGui.PopID();
                }
                if (UiAuto.Selectable("New group") && target is not null) CreateGroupFromSelection(target);
                UiAuto.EndCombo();
            }
            ImGui.EndDisabled();
            if (target is null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Only objects with move handles can join a group. " + reason);
        }
        ImGui.EndDisabled();
    }

    /** Inspector for the selected group; false when it no longer exists. */
    private bool DrawGroupInspector()
    {
        if (workspace.Project?.FindObjectGroup(selectedGroupId!) is not { } group) { ReselectGroup(); return false; }
        if (groupDraftId != group.Id)
        {
            groupDraftId = group.Id; groupNameDraft = group.Name;
            groupMove = groupTurn = Vector3.Zero; groupScale = Vector3.One;
            groupPartsNote = "";
        }
        var (present, missing) = GroupSources(group);
        ImGui.TextWrapped(group.Name);
        ImGui.TextDisabled($"Group of {group.Members.Count:N0} objects"
            + (group.CollisionParts is { } held ? $" and {held.Count:N0} collision parts" : ""));
        ImGui.BeginDisabled(!workspace.CanEdit);
        ImGui.SetNextItemWidth(-1);
        UiAuto.InputTextWithHint("##group-name", "Group name", ref groupNameDraft, ProjectObjectName.MaximumNameLength);
        if (ImGui.IsItemDeactivatedAfterEdit() && groupNameDraft.Trim() != group.Name)
        {
            string text = groupNameDraft;
            workspace.Run(() => workspace.RenameObjectGroup(group.Id, text));
        }
        ImGui.EndDisabled();
        ImGui.BeginDisabled(present.Count == 0 || viewport?.Camera.PivotFree != true);
        if (ImGuiIcons.Button("group-frame", StudioIcon.Focus, "Frame group")) { viewport!.FocusSelection(); focusWorldScene = true; }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!workspace.CanEdit);
        if (UiAuto.Button("Delete group")) RemoveGroup(group);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Removes the group only; its objects and their edits stay.");
        if (ImGui.CollapsingHeader("Transform group", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.BeginDisabled(!workspace.CanEdit || present.Count == 0 && group.CollisionParts is not { Count: > 0 });
            ImGui.TextDisabled("Move by"); ImGui.SetNextItemWidth(-1); ImGui.InputFloat3("##group-move", ref groupMove, "%.3f");
            ImGui.TextDisabled("Rotate by"); ImGui.SetNextItemWidth(-1); ImGui.InputFloat3("##group-turn", ref groupTurn, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("XYZ degrees, around the group's centre");
            ImGui.TextDisabled("Scale by"); ImGui.SetNextItemWidth(-1); ImGui.InputFloat3("##group-scale", ref groupScale, "%.4f");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Equal values also resize objects that store a scale; unequal values only spread positions");
            if (UiAuto.Button("Apply to group", new(-1, 0)))
                ApplyGroupChange(group.Id, new(groupMove, groupTurn == Vector3.Zero ? Quaternion.Identity
                    : DegreesToQuaternion(groupTurn), groupScale));
            ImGui.EndDisabled();
        }
        if (ImGui.CollapsingHeader("Objects", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var (member, source) in present)
            {
                var row = inspectionWorld?.Find(source);
                ImGui.PushID(row?.Id ?? MemberName(source));
                ImGui.BeginDisabled(!workspace.CanEdit);
                bool remove = UiAuto.SmallButton("x");
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Remove from group");
                ImGui.SameLine();
                if (UiAuto.Selectable(MemberName(source)) && row is not null) SelectWorldObject(row);
                ImGui.PopID();
                if (remove) SetGroupMembers(group, group.Members.Where(other => other.Key != member.Key).ToArray());
            }
            foreach (var member in missing)
            {
                ImGui.PushID(member.Key);
                ImGui.BeginDisabled(!workspace.CanEdit);
                bool remove = UiAuto.SmallButton("x");
                ImGui.EndDisabled();
                ImGui.SameLine();
                string label = ProjectNames().GetValueOrDefault("edit:" + member.Key) ?? $"{member.Layout} {member.SourceTag:X8}";
                ImGui.TextDisabled(label + ": not in this map");
                ImGui.PopID();
                if (remove) SetGroupMembers(group, group.Members.Where(other => other.Key != member.Key).ToArray());
            }
        }
        bool partsOpen = group.CollisionParts is { } parts
            && ImGui.CollapsingHeader($"Collision parts ({parts.Count:N0})###group-parts", ImGuiTreeNodeFlags.DefaultOpen);
        if (group.CollisionParts is not null) StudioUi.Tip("They move with the group");
        if (partsOpen)
        {
            if (UiAuto.Button("Select collision parts", new(-1, 0))) SelectGroupCollisionParts(group);
            ImGui.BeginDisabled(!workspace.CanEdit);
            if (UiAuto.Button("Remove collision parts", new(-1, 0))) SetGroupCollisionParts(group, []);
            ImGui.EndDisabled();
        }
        DrawAccentNote(groupPartsNote);
        return true;
    }
}
