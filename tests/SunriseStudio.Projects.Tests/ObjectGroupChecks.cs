using System.Text.Json;
using System.Text.Json.Nodes;
using SunriseStudio.Projects;

/** Checks object groups in the project manifest: version, member targets, trimmed names and reopen. */
internal static class ObjectGroupChecks
{
    private static readonly string Hash = new('a', 64);

    /** Groups save as version 6, reopen whole, refuse bad input, and removal lowers the version. */
    public static void Run(string parent)
    {
        var project = StudioProject.Create(Path.Combine(parent, "object-groups"), "Object groups");
        ObjectEditTarget tree = Target(0x80FAA28E, ObjectEditLayout.StaticInstance, 0x1A0, 48, 4544),
            spawn = Target(0x80A00011, ObjectEditLayout.SpawnPoint, 0x30, 48, -1, 500),
            canopy = Target(0x80FAA33B, ObjectEditLayout.DecoratorInstance, 0x2A8, 16, 42, 0x80FAA33A);
        project.SetObjectGroup(new("b", "  Trees  ", [tree, canopy]));
        var json = JsonNode.Parse(File.ReadAllText(project.ManifestPath))!;
        Check(json["version"]!.GetValue<int>() == 6 && json["objectGroups"]!.AsArray().Single()!["members"]!.AsArray().Count == 2,
            "A group did not save as version 6 with its members.");
        var reopened = StudioProject.Open(project.DirectoryPath);
        var saved = reopened.ObjectGroups.Single();
        Check(saved.Name == "Trees" && saved.Members.SequenceEqual([tree, canopy]),
            "A group did not survive a reopen with its trimmed name and whole targets.");
        Reject(() => reopened.SetObjectGroup(new("c", " ", [spawn])));
        Reject(() => reopened.SetObjectGroup(new("c", "two\nlines", [spawn])));
        Reject(() => reopened.SetObjectGroup(new("c", new string('a', 201), [spawn])));
        Reject(() => reopened.SetObjectGroup(new("c", "Empty", [])));
        Reject(() => reopened.SetObjectGroup(new("c", "Twice", [spawn, spawn])));
        Reject(() => reopened.SetObjectGroup(new("c", "Taken", [tree])));
        Reject(() => reopened.SetObjectGroup(new("c", "Trigger", [Target(0x80A00016, ObjectEditLayout.TriggerVolume, 0x60, 0x120, -1)])));
        Reject(() => reopened.SetObjectGroup(new("c", "Hash", [spawn with { OriginalSha256 = "bad" }])));
        Reject(() => reopened.SetObjectGroup(new(" ", "Id", [spawn])));
        Check(reopened.ObjectGroups.Count == 1, "A refused group changed the project.");

        // Replacing by id moves a member between groups; names and groups save as version 6.
        reopened.SetObjectGroup(saved with { Members = [tree] });
        reopened.SetObjectGroup(new("a", "Canopy and spawn", [canopy, spawn]));
        reopened.SetObjectName("edit:" + tree.Key, "Big tree");
        json = JsonNode.Parse(File.ReadAllText(reopened.ManifestPath))!;
        Check(json["version"]!.GetValue<int>() == 6 && reopened.ObjectGroups.Select(group => group.Id).SequenceEqual(["a", "b"]),
            "Groups with names did not save as version 6 in id order.");
        reopened.RemoveObjectGroup("a");
        reopened.RemoveObjectGroup("b");
        reopened.SetObjectName("edit:" + tree.Key, null);
        json = JsonNode.Parse(File.ReadAllText(reopened.ManifestPath))!;
        Check(json["version"]!.GetValue<int>() == 1 && json["objectGroups"] is null,
            "Removing every group did not return the project to version 1.");

        // A version 5 manifest cannot hold groups; a version 5 project with names still opens.
        var older = JsonNode.Parse(File.ReadAllText(reopened.ManifestPath))!;
        older["version"] = 5;
        older["objectNames"] = new JsonArray(new JsonObject { ["key"] = "row:a", ["name"] = "A" });
        File.WriteAllText(reopened.ManifestPath, older.ToJsonString());
        Check(StudioProject.Open(reopened.DirectoryPath).ObjectGroups.Count == 0, "A version 5 project no longer opens.");
        older["objectGroups"] = new JsonArray(new JsonObject
        {
            ["id"] = "x", ["name"] = "X",
            ["members"] = new JsonArray(new JsonObject
            {
                ["sourceTag"] = spawn.SourceTag, ["layout"] = "SpawnPoint", ["offset"] = spawn.Offset,
                ["length"] = spawn.Length, ["index"] = spawn.Index, ["identity"] = spawn.Identity, ["originalSha256"] = Hash
            })
        });
        File.WriteAllText(reopened.ManifestPath, older.ToJsonString());
        Reject(() => StudioProject.Open(reopened.DirectoryPath));
        older["version"] = 6;
        File.WriteAllText(reopened.ManifestPath, older.ToJsonString());
        Check(StudioProject.Open(reopened.DirectoryPath).ObjectGroups.Single().Members.Single() == spawn,
            "A hand-written version 6 group did not open.");
        Console.WriteLine("object_groups version=6 reopen=pass trim=pass refused=9 removal=version1 v5_names=open v5_groups=refused");
        CollisionParts(parent);
    }

    /** Group collision parts need version 7; a part is in one group; an empty list is not written. */
    private static void CollisionParts(string parent)
    {
        var project = StudioProject.Create(Path.Combine(parent, "collision-part-groups"), "Collision part groups");
        var spawn = Target(0x80A00011, ObjectEditLayout.SpawnPoint, 0x30, 48, -1, 500);
        ProjectCollisionPart leaf = new(0x80FAA028, 0x80FAA027, 3, 0x80FAA37D, 12, 0x80B91745, 0x40, -1, 0, 0x112),
            trunk = leaf with { ShapeKey = 0x200 }, body = leaf with { Bone = 4, Ordinal = 1, ShapeKey = 0xFFFFFFFF };
        project.SetObjectGroup(new("tree", "Tree", []) { CollisionParts = [leaf, trunk] });
        project.SetObjectGroup(new("mixed", "Mixed", [spawn]) { CollisionParts = [body] });
        var json = JsonNode.Parse(File.ReadAllText(project.ManifestPath))!;
        var reopened = StudioProject.Open(project.DirectoryPath);
        Check(json["version"]!.GetValue<int>() == 7 && reopened.FindObjectGroup("tree")!.CollisionParts!.SequenceEqual([leaf, trunk])
            && reopened.FindObjectGroup("mixed")!.Members.Single() == spawn, "Collision part groups did not save as version 7 and reopen.");
        Check(json["objectGroups"]!.AsArray().Single(group => group!["id"]!.GetValue<string>() == "tree")!["collisionParts"]![0]!
            .GetValue<string>() == leaf.Key, "A collision part did not save as its key.");
        // A full Select connected result fits one group and the manifest; one part more is refused.
        var many = Enumerable.Range(0, ProjectObjectGroup.MaximumCollisionParts)
            .Select(i => leaf with { EntityTag = 0x80FAB000, ShapeKey = (uint)i }).ToArray();
        reopened.SetObjectGroup(new("many", "Many", []) { CollisionParts = many });
        long bytes = new FileInfo(reopened.ManifestPath).Length;
        Check(StudioProject.Open(reopened.DirectoryPath).FindObjectGroup("many")!.CollisionParts!.Count == many.Length,
            "A group of the most collision parts did not reopen.");
        Reject(() => reopened.SetObjectGroup(new("many", "Many", []) { CollisionParts = [.. many, leaf with { EntityTag = 0x80FAB001 }] }));
        reopened.RemoveObjectGroup("many");
        Reject(() => reopened.SetObjectGroup(new("other", "Other", []) { CollisionParts = [trunk] }));
        Reject(() => reopened.SetObjectGroup(new("twice", "Twice", []) { CollisionParts = [body with { Bone = 5 }, body with { Bone = 5 }] }));
        Reject(() => reopened.SetObjectGroup(new("bad", "Bad", []) { CollisionParts = [leaf with { Ordinal = -1, ShapeKey = 1 }] }));
        Reject(() => reopened.SetObjectGroup(new("none", "None", []) { CollisionParts = [] }));
        reopened.SetObjectGroup(reopened.FindObjectGroup("mixed")! with { CollisionParts = [] });
        reopened.RemoveObjectGroup("tree");
        json = JsonNode.Parse(File.ReadAllText(reopened.ManifestPath))!;
        Check(json["version"]!.GetValue<int>() == 6 && json["objectGroups"]![0]!["collisionParts"] is null,
            "Removing every collision part did not return the project to version 6.");
        // A version 6 manifest cannot hold collision parts.
        json["objectGroups"]![0]!["collisionParts"] = JsonNode.Parse(JsonSerializer.Serialize(new[] { leaf },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        File.WriteAllText(reopened.ManifestPath, json.ToJsonString());
        Reject(() => StudioProject.Open(reopened.DirectoryPath));
        json["version"] = 7;
        File.WriteAllText(reopened.ManifestPath, json.ToJsonString());
        Check(StudioProject.Open(reopened.DirectoryPath).ObjectGroups.Single().CollisionParts!.Single() == leaf,
            "A hand-written version 7 collision part did not open.");
        Console.WriteLine($"object_group_parts version=7 reopen=pass parts_only=pass saved=key most={many.Length} most_bytes={bytes} "
            + "refused=5 removal=version6 v6_parts=refused");
    }

    private static ObjectEditTarget Target(uint tag, ObjectEditLayout layout, int offset, int length, int index, ulong identity = 0) =>
        new(tag, layout, offset, length, index, identity, Hash);

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("An invalid object group was accepted.");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
