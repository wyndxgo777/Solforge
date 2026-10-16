using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

/** One selected object with the handle frame it transforms in. */
public sealed record GroupMember(object Source, string Name, TransformHandleFrame Frame);

/** Pivot is the centre of the box around the member pivots. */
public sealed record GroupTransformFrame(Vector3 Pivot, IReadOnlyList<GroupMember> Members);

/** One transform about one pivot for many objects, each mapped through its own parent. */
public static class GroupTransforms
{
    /**
     * Every member needs its own handle frame, so a group transforms whole or not at all.
     * @param name Labels a member in the reason and in its edit.
     * @param reason Why the group gets no handles; empty when it gets them.
     */
    public static GroupTransformFrame? Read(PackageCatalog catalog, IReadOnlyList<object> sources, WorldDocument? world,
        Func<object, string> name, out string reason)
    {
        if (sources.Count == 0)
        {
            reason = "None of this group's objects are in the open map.";
            return null;
        }
        var members = new List<GroupMember>(sources.Count);
        foreach (var source in sources)
        {
            if (TransformHandles.Read(catalog, source, world, out string refused) is not { } frame)
            {
                reason = $"{name(source)}: {refused}";
                return null;
            }
            members.Add(new(source, name(source), frame));
        }
        // A static instance drawn under a member placement would move twice.
        foreach (var member in members)
            if (member.Source is MeshInstance { InstanceSourceTag: not 0, PlacementSourceTag: not 0 } mesh
                && members.FirstOrDefault(other => other.Frame.Info.Target is
                    { Layout: ObjectEditLayout.Placement or ObjectEditLayout.SlotPlacement } owner
                    && owner.SourceTag == mesh.PlacementSourceTag && owner.Offset == mesh.PlacementSourceOffset) is { } parent)
            {
                reason = $"{member.Name} sits inside {parent.Name}; remove one of them from the group.";
                return null;
            }
        Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        foreach (var member in members)
        {
            minimum = Vector3.Min(minimum, member.Frame.Pivot);
            maximum = Vector3.Max(maximum, member.Frame.Pivot);
        }
        reason = "";
        return new((minimum + maximum) / 2, members);
    }

    /** Builds every member's edit before any is saved; one failing member fails the change. */
    public static IReadOnlyList<ObjectEdit> CreateEdits(PackageCatalog catalog, GroupTransformFrame frame,
        TransformChange change, WorldDocument? world)
    {
        var edits = new List<ObjectEdit>(frame.Members.Count);
        foreach (var member in frame.Members)
        {
            ObjectEdit? edit;
            try { edit = TransformHandles.CreateEdit(catalog, member.Source, member.Frame, frame.Pivot, change, world, member.Name); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException)
            { throw new InvalidDataException($"{member.Name}: {error.Message}", error); }
            if (edit is not null) edits.Add(edit);
        }
        return edits;
    }
}
