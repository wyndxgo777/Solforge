namespace SunriseStudio.Projects;

/** Object groups are editor state: one object belongs to one group at most. */
public sealed partial class StudioProject
{
    public IReadOnlyList<ProjectObjectGroup> ObjectGroups => manifest.ObjectGroups ?? [];

    /** The group with this id, or null. */
    public ProjectObjectGroup? FindObjectGroup(string id) => ObjectGroups.FirstOrDefault(group => group.Id == id);

    /** Adds the group, or replaces the one with its id. The name is trimmed. */
    public void SetObjectGroup(ProjectObjectGroup group)
    {
        RequireMutationAllowed();
        ArgumentNullException.ThrowIfNull(group);
        group = Frozen(group with { Name = group.Name?.Trim() ?? "" });
        group.Validate();
        var groups = ObjectGroups.Where(entry => entry.Id != group.Id).Append(group).ToList();
        RequireOneGroupEach(groups);
        groups.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        ReplaceManifest(manifest with { ObjectGroups = groups.AsReadOnly() });
    }

    /** Removes a group if present. */
    public void RemoveObjectGroup(string id)
    {
        RequireMutationAllowed();
        if (FindObjectGroup(id) is null) return;
        var groups = ObjectGroups.Where(group => group.Id != id).ToArray();
        ReplaceManifest(manifest with { ObjectGroups = groups.Length == 0 ? null : Array.AsReadOnly(groups) });
    }

    /** Copies the member lists; an empty part list becomes null, so it is not written. */
    private static ProjectObjectGroup Frozen(ProjectObjectGroup group) => group.Members is null ? group
        : group with
        {
            Members = Array.AsReadOnly(group.Members.ToArray()),
            CollisionParts = group.CollisionParts is { Count: > 0 } parts ? Array.AsReadOnly(parts.ToArray()) : null
        };

    private static void RequireOneGroupEach(IEnumerable<ProjectObjectGroup> groups)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in groups.SelectMany(group => group.Keys))
            if (!seen.Add(key)) throw new InvalidDataException("An object or collision part can be in one group only.");
    }
}
