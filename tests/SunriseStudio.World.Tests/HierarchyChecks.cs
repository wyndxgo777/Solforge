using System.Numerics;
using SunriseStudio.Scene;

/** Checks the world hierarchy: canonical placement owners, reused sources and hide state. */
internal static class HierarchyChecks
{
    /** Reused sources stay separate while parts and their exact owner marker share one node. */
    internal static void Synthetic()
    {
        var scene = Scene();
        var world = WorldDocument.FromScene(scene);
        var tree = SceneHierarchy.Build(world);
        var first = tree.Find(scene.Instances[0])!;
        var repeated = tree.Find(scene.Instances[2])!;
        var other = tree.Find(scene.Instances[3])!;
        Check(ReferenceEquals(first, tree.Find(scene.Instances[1]))
            && ReferenceEquals(first, tree.Find(scene.Markers[0])) && first.Members.Count == 3,
            "Placement parts with different transforms lost their canonical owner.");
        Check(!ReferenceEquals(first, repeated) && first.Id != repeated.Id,
            "Reused source placements merged into one tree node.");
        Check(repeated.Parent?.Kind == SceneHierarchyKind.SharedContainers
            && repeated.ContainerTags.SequenceEqual(new uint[] { 10, 11 }),
            "Multiple container routes were assigned a false single owner.");
        var bubble = tree.PathTo(first).Single(x => x.Kind == SceneHierarchyKind.Bubble);
        Check(tree.SetHidden(first.Id, true) && tree.SetHidden(bubble.Id, true), "Hide state was not applied.");
        Check(!tree.IsVisible(scene.Instances[0]) && !tree.IsVisible(scene.Instances[2])
            && tree.IsVisible(scene.Instances[3]), "Ancestor visibility escaped its subtree.");
        long revision = tree.Revision;
        Check(!tree.SetHidden(bubble.Id, true) && tree.Revision == revision,
            "Unchanged visibility invalidated the tree.");
        tree.SetHidden(bubble.Id, false);
        Check(!first.Visible && repeated.Visible, "Showing an ancestor erased child hide state.");
        Check(tree.Find(new Proxy(scene.Instances[0])) == first
            && tree.Find(world.Find(scene.Instances[0])!.Id) == first,
            "Viewport proxy or world-row selection did not map to its placement.");
        var secondScene = Scene(shift: 50, extra: true);
        var secondWorld = WorldDocument.FromScene(secondScene);
        var rebuilt = SceneHierarchy.Build(secondWorld, tree.HiddenNodeIds);
        var restored = rebuilt.Find(secondScene.Instances[1])!;
        Check(restored.Id == first.Id && !restored.Visible
            && rebuilt.Find(secondScene.Instances[3])!.Id == repeated.Id,
            "Labels, edits, or unrelated insertion changed a stable placement hide key.");
        Check(rebuilt.Reveal(restored) && restored.Visible && !rebuilt.IsHidden(restored.Id),
            "Reveal failed to restore the selected branch.");
        Check(tree.DescendantMembers(bubble).Count() == 4
            && tree.Nodes.Select(x => x.Id).Distinct().Count() == tree.Nodes.Count,
            "Hierarchy duplicated sources or node identities.");
        Check(other.Parent is not null && other.Depth == tree.PathTo(other).Count - 1,
            "Hierarchy depth and ancestor path disagree.");
        Console.WriteLine("PASS hierarchy placements=canonical reused=distinct ownership=explicit hidden=recursive reload=stable selection=mapped");
    }

    /** State definitions and exact light ownership remain available even without mesh geometry. */
    internal static void Mission(WorldDocument world)
    {
        var tree = SceneHierarchy.Build(world);
        Check(tree.Nodes.Count(x => x.Kind == SceneHierarchyKind.State) == world.AllStates.Count,
            "Hierarchy discarded authored state definitions.");
        if (world.Map.Tag == WorldFixture.Base)
        {
            var volume = world.Scene.Markers.Single(x => x.Layer == SceneLayer.Volumes);
            Check(tree.Find(volume)?.Parent?.Kind == SceneHierarchyKind.AuthoredObject,
                "Exact decoded slot identity lost its authored object parent.");
        }
        foreach (var light in world.Objects.Where(x => x.Source is WorldLight))
        {
            var source = (WorldLight)light.Source;
            if (source.Owner.OwnerMarker is not { } marker) continue;
            var node = tree.Find(source)!;
            Check(ReferenceEquals(node.Parent, tree.Find(marker)), "Light lost its exact placement owner.");
            tree.SetHidden(node.Parent!.Id, true);
            Check(!tree.IsVisible(source), "Placement hide did not hide its light children.");
            tree.SetHidden(node.Parent.Id, false);
        }
        var state = tree.Nodes.First(x => x.Kind == SceneHierarchyKind.State && x.Children.Count > 0);
        tree.SetHidden(state.Id, true);
        Check(tree.DescendantMembers(state).All(x => !tree.IsVisible(x)),
            "State hide left descendants visible.");
        var members = world.Objects.Where(x => x.Source is MeshInstance or SceneMarker or WorldLight)
            .Select(x => x.Source).ToArray();
        Check(members.All(x => tree.Find(x) is not null), "Selectable scene source is absent from hierarchy.");
        Console.WriteLine($"PASS mission-hierarchy nodes={tree.Nodes.Count} states={world.AllStates.Count} members={members.Length}");
    }

    private static SceneDocument Scene(float shift = 0, bool extra = false)
    {
        var scene = new SceneDocument { TagHash = 0x80001000, Name = "World" };
        scene.Bubbles.AddRange([new(1, 101, "First"), new(2, 102, "Second")]);
        var placement = new SceneObjectPlacement(20, 3, Matrix4x4.CreateTranslation(shift, 0, 0));
        var reused = new SceneObjectPlacement(20, 3, Matrix4x4.CreateTranslation(100 + shift, 0, 0));
        var container = new MapContainerInfo(10, new byte[32], [0]);
        var alternate = new MapContainerInfo(11, new byte[32], [1]);
        if (extra) scene.Instances.Add(new() { Mesh = new() { TagHash = 999 },
            ObjectPlacement = new(21, 0, Matrix4x4.Identity), Name = "New unrelated", BubbleHash = 1 });
        scene.Instances.Add(new() { Mesh = new() { TagHash = 30, PartIndex = 0 },
            ObjectPlacement = placement, Name = shift == 0 ? "Object" : "New label", BubbleHash = 1,
            Transform = Matrix4x4.CreateTranslation(shift, 1, 0), MapContainers = [container] });
        scene.Instances.Add(new() { Mesh = new() { TagHash = 31, PartIndex = 1 },
            ObjectPlacement = placement, Name = "Other part", BubbleHash = 1,
            Transform = Matrix4x4.CreateTranslation(shift, 2, 0), MapContainers = [container] });
        scene.Instances.Add(new() { Mesh = new() { TagHash = 30, PartIndex = 0 },
            ObjectPlacement = reused, Name = "Reused source", BubbleHash = 1,
            Transform = Matrix4x4.CreateTranslation(100 + shift, 1, 0), MapContainers = [container, alternate] });
        scene.Instances.Add(new() { Mesh = new() { TagHash = 30, PartIndex = 0 },
            ObjectPlacement = placement, Name = "Other bubble", BubbleHash = 2, MapContainers = [container] });
        scene.Markers.Add(new() { ObjectPlacement = placement, SourceTag = 20, SourceOffset = 3,
            Name = "Exact owner", BubbleHash = 1, MapContainers = [container] });
        return scene;
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidDataException(message); }

    private sealed record Proxy(object Key) : ISceneSelectionProxy;
}
