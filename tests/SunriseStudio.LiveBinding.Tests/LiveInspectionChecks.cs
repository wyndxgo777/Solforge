using System.Numerics;
using System.Text.Json;
using SunriseStudio.Editor;
using SunriseStudio.Live;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Checks runtime inspection: hierarchy, mesh reverse lookup, observed vs authored position, and pose updates without rebuilding the tree. */
internal static class LiveInspectionChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static int Run(AssetViewport viewport)
    {
        const uint list = 0x80A01000, tag = 0x80A00001;
        const ulong now = 10000;
        var authored = new MeshInstance { Mesh = Mesh(1), Transform = Matrix4x4.CreateTranslation(100, 0, 0),
            ObjectPlacement = new(list, 0x88, Matrix4x4.CreateTranslation(100, 0, 0)) { EntryIndex = 3 } };
        var world = new SceneDocument(); world.Instances.Add(authored);
        string original = JsonSerializer.Serialize(world, new JsonSerializerOptions { IncludeFields = true });
        viewport.SetScene(world);
        var prototype = new SceneDocument(); prototype.Instances.Add(new() { Mesh = Mesh(2) });
        prototype.Instances.Add(new() { Mesh = Mesh(3), Transform = Matrix4x4.CreateTranslation(2, 0, 0) });
        viewport.RegisterLivePrototype(new(tag, prototype, null, []));
        var row = new LivePlacement(0, 0, 0, 7, 1, 0xCAFE, Vector3.Zero,
            Quaternion.Identity, now, true, tag, true);
        string key = LiveDynamicBinding.Key(row);
        viewport.SetLiveInstances([new(key, tag, Matrix4x4.Identity)]);
        var meshes = viewport.TryGetLiveInstance(key)!.Meshes;
        var sourceRow = row with { Dynamic = false, ObjectListTag = list, EntryIndex = 3, Handle = 9, Position = new(4, 5, 6) };
        var snapshot = new LivePlacements(now, true, true, false, 2, 0, 1, now, [sourceRow, row]);
        var index = new LiveInspectionIndex();
        Check(index.Update(viewport, snapshot, [row], 1, 1, _ => "Runtime fixture"), "Runtime hierarchy did not appear.");
        var actor = index.Find(meshes[1])!;
        Check(actor.Key == key && actor.Observation.DatumFlags == 0xCAFE && actor.Meshes.Count == 2
            && index.FindNode(meshes[0], SceneSelectionMode.Object) == index.FindNode(meshes[1], SceneSelectionMode.Object),
            "Runtime mesh reverse lookup lost its whole instance or observation.");
        var binding = new LiveSceneBinding(WorldDocument.FromScene(world)); binding.Apply(snapshot, viewport, now);
        Check(binding.RowFor(authored)?.Position == new Vector3(4, 5, 6) && authored.Transform.Translation.X == 100,
            "Observed position was confused with authored position.");
        viewport.ClearLiveTransforms();
        long revision = index.TreeRevision;
        var moved = row with { Position = Vector3.UnitY, DatumFlags = 3 };
        viewport.SetLiveInstances([new(key, tag, Matrix4x4.CreateTranslation(moved.Position))]);
        Check(!index.Update(viewport, snapshot, [moved], 2, 1, _ => "Runtime fixture") && index.TreeRevision == revision
            && index.Find(meshes[0])!.Observation == moved, "Pose changes rebuilt the hierarchy or left stale inspector fields.");
        var child = index.FindNode(meshes[1], SceneSelectionMode.MeshPart)!;
        index.SetHidden(child.Id, true);
        Check(index.IsVisible(meshes[0]) && !index.IsVisible(meshes[1]), "Part hiding affected the wrong sibling.");
        index.SetHidden(actor.Node.Id, true);
        Check(!index.IsVisible(meshes[0]) && !index.IsVisible(meshes[1]), "Actor hiding did not reach all decoded parts.");
        index.SetHidden(actor.Node.Id, false);
        Check(index.IsVisible(meshes[0]) && !index.IsVisible(meshes[1]), "Parent visibility lost a child's local hide choice.");
        index.Reveal(child);
        Check(index.IsVisible(meshes[1]), "Reveal did not clear ancestor and child hiding.");
        index.SetHidden(LiveInspectionIndex.RootId, true);
        Check(meshes.All(mesh => !index.IsVisible(mesh)), "Live branch hiding did not reach its meshes.");
        index.ShowAll();
        viewport.Visibility.Filter = index.IsVisible;
        viewport.Camera.LookFrom(new(0, -6, 0), Vector3.Zero); viewport.RenderImage(320, 240);
        Check(viewport.VisibleObjectCount == 2, "Runtime visibility cache did not reach the renderer.");
        index.SetHidden(actor.Node.Id, true); viewport.RenderImage(320, 240);
        Check(viewport.VisibleObjectCount == 0, "Hidden runtime meshes remained in draw eligibility.");
        var reused = row with { Generation = 2 };
        string next = LiveDynamicBinding.Key(reused);
        viewport.SetLiveInstances([new(next, tag, Matrix4x4.Identity)]);
        index.Update(viewport, snapshot, [reused], 3, 1, _ => "Runtime fixture");
        Check(index.Find(actor.Node.Id) is null && index.Find(meshes[0])!.Key == next && index.IsVisible(meshes[0]),
            "A new generation inherited the old generation's row, inspector or hiding.");
        viewport.ClearLiveInstances(); index.Update(viewport, snapshot, [], 4, 1, _ => "Runtime fixture");
        Check(index.Objects.Count == 0 && index.Find(meshes[0]) is null && index.Root.Children.Count == 0,
            "Despawn retained runtime inspector or tree entries.");
        Check(JsonSerializer.Serialize(world, new JsonSerializerOptions { IncludeFields = true }) == original,
            "Transient inspection changed authored scene data.");
        viewport.Visibility.Filter = null;
        Console.WriteLine("live_inspection reverse=checked observed_authored=separate hierarchy=stable hiding=recursive generation=isolated despawn=cleared");
        return 1;
    }

    private static MeshData Mesh(uint tag) => new() { TagHash = tag, PartIndex = (int)tag,
        Vertices = [new(new(-1, 0, -1), -Vector3.UnitY, Vector2.Zero, Vector4.One),
            new(new(0, 0, 1), -Vector3.UnitY, Vector2.Zero, Vector4.One),
            new(new(1, 0, -1), -Vector3.UnitY, Vector2.Zero, Vector4.One)], Indices = [0, 1, 2] };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
