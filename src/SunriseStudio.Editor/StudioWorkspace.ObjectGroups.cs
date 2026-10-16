using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** Group changes are labels like object names: they save at once and are not undo steps. */
public sealed partial class StudioWorkspace
{
    /** @return The new group's id. */
    public string CreateObjectGroup(string name, IReadOnlyList<ObjectEditTarget> members,
        IReadOnlyList<ProjectCollisionPart>? collisionParts = null)
    {
        string id = Guid.NewGuid().ToString("N");
        SaveGroup(new(id, name, members) { CollisionParts = collisionParts }, "Group created");
        return id;
    }

    /** Replaces a group's members. */
    public void SetObjectGroupMembers(string id, IReadOnlyList<ObjectEditTarget> members) =>
        SaveGroup(RequireGroup(id) with { Members = members }, "Group members saved");

    /** Replaces a group's collision parts. */
    public void SetObjectGroupCollisionParts(string id, IReadOnlyList<ProjectCollisionPart> parts) =>
        SaveGroup(RequireGroup(id) with { CollisionParts = parts }, "Group collision parts saved");

    /** Renames a group. */
    public void RenameObjectGroup(string id, string name) => SaveGroup(RequireGroup(id) with { Name = name }, "Group renamed");

    /** Deletes a group. */
    public void RemoveObjectGroup(string id)
    {
        RequireEditable();
        Project!.RemoveObjectGroup(id);
        FinishGroupChange("Group removed");
    }

    private ProjectObjectGroup RequireGroup(string id) => Project?.FindObjectGroup(id)
        ?? throw new InvalidOperationException("This group no longer exists.");

    private void SaveGroup(ProjectObjectGroup group, string status)
    {
        RequireEditable();
        Project!.SetObjectGroup(group);
        FinishGroupChange(status);
    }

    private void FinishGroupChange(string status)
    {
        ProjectRevision++;
        Status = status;
        Changed?.Invoke();
    }
}
