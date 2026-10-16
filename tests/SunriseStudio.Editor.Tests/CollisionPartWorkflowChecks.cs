using System.Diagnostics;
using System.Numerics;
using SunriseStudio.Editor;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;

/** Parts chosen in two meshes move in one undo step, and a group saves them by key. */
internal static class CollisionPartWorkflowChecks
{
    // The Leviathan static compound; its meshes are per-mesh packfiles.
    private const uint Compound = 0x80EDC2D0;

    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(string gameRoot, string root)
    {
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(gameRoot); Wait(workspace);
        workspace.Select(Compound); Wait(workspace);
        workspace.CreateProject(Path.Combine(root, "collision-part-project"), "Collision part workflow"); Wait(workspace);
        var catalog = workspace.Catalog!;
        var project = workspace.Project!;
        var collision = new CollisionSceneLoader(catalog).Load(new SceneDocument { TagHash = Compound });
        bool Editable(CollisionPiece piece) => piece.Shape.Class == CollisionShapes.CompressedMesh && piece.Shape.TriangleCount > 0
            && HavokPackfile.Parse(catalog.ReadOriginal(piece.Tag)) is { UserTag: 1 } file && file.RootOffset == piece.Offset;
        int[] chosen = Enumerable.Range(0, collision.Placements.Count).Where(i => collision.Placements[i].Bone < 0)
            .DistinctBy(i => collision.Placements[i].Piece).Where(i => Editable(collision.Pieces[collision.Placements[i].Piece])).Take(2).ToArray();
        Require(chosen.Length == 2, "The compound has no two editable meshes.");
        uint[] tags = chosen.Select(i => collision.Pieces[collision.Placements[i].Piece].Tag).ToArray();
        byte[][] before = tags.Select(tag => catalog.Read(tag)).ToArray();
        CollisionPartRef[] parts = [new(chosen[0], 0), new(chosen[1], 0)];
        workspace.EditCollisionParts(collision, parts, Matrix4x4.CreateTranslation(0, 0, .25f)); Wait(workspace);
        Require(tags.All(tag => project.Changes.Any(change => change.TagHash == tag)), "The part edit did not save both meshes.");
        workspace.Undo(); Wait(workspace);
        Require(!workspace.CanUndo && tags.Select((tag, i) => catalog.Read(tag).AsSpan().SequenceEqual(before[i])).All(same => same),
            "One undo did not restore both meshes.");

        // A group holds the parts by key; a collision part sits in one group only.
        var keys = CollisionPartKey.Of(collision, parts).Select(key => new ProjectCollisionPart(key.EntityTag, key.ListTag, key.ListRow,
            key.CompoundTag, key.Instance, key.ShapeTag, key.ShapeOffset, key.Bone, key.Ordinal, key.ShapeKey)).ToArray();
        string id = workspace.CreateObjectGroup("Parts", [], keys);
        Require(project.FindObjectGroup(id)!.CollisionParts!.SequenceEqual(keys) && !workspace.CanUndo,
            "A collision part group was not saved, or it made an undo step.");
        Throws(() => workspace.CreateObjectGroup("Again", [], [keys[0]]));
        workspace.SetObjectGroupCollisionParts(id, [keys[1]]);
        var (found, missing) = CollisionPartKey.Resolve(new CollisionSceneLoader(catalog).Load(new SceneDocument { TagHash = Compound }),
            project.FindObjectGroup(id)!.CollisionParts!.Select(part => new CollisionPartKey(part.EntityTag, part.ListTag, part.ListRow,
                part.CompoundTag, part.Instance, part.ShapeTag, part.ShapeOffset, part.Bone, part.Ordinal, part.ShapeKey)));
        Require(found.SequenceEqual([parts[1]]) && missing.Count == 0, "The group's part key did not find its part again.");
        float error = GroupChange(workspace, collision, parts[1], before[1]);
        Console.WriteLine($"editor_collision_parts meshes={tags[0]:X8},{tags[1]:X8} undo=one-step group=keys second-group=refused resolve=pass "
            + $"group_change_error={error * 1000:0.#}mm");
    }

    /** A group change turns, scales and moves its part about the part's own centre, as one undo step. */
    private static float GroupChange(StudioWorkspace workspace, CollisionScene collision, CollisionPartRef part, byte[] before)
    {
        var catalog = workspace.Catalog!;
        var placement = collision.Placements[part.Placement];
        var shape = collision.Pieces[placement.Piece].Shape;
        var vertices = CollisionParts.Of(shape).Vertices(shape, [part.Part]).ToArray();
        var (low, high) = CollisionParts.Of(shape).Bounds(shape, [part.Part], placement.World);
        var centre = (low + high) / 2;
        var change = new TransformChange(new(0, 0, .25f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f), new(1.5f));
        var matrix = change.Matrix(centre);
        float gap = Vector3.Distance(Vector3.Transform(low, matrix), change.Apply(low, centre));
        // Map coordinates reach hundreds of metres, so float rounding scales with the point.
        Require(gap < 1e-5f * MathF.Max(1, low.Length()), $"The change matrix is {gap} m from Apply.");
        // An affine change maps the vertex mean exactly, so the mean shows where the part went.
        Vector3 Mean(CollisionShape from) =>
            vertices.Aggregate(Vector3.Zero, (sum, vertex) => sum + Vector3.Transform(from.Vertices[vertex], placement.World)) / vertices.Length;
        var expected = change.Apply(Mean(shape), centre);
        workspace.TransformGroup([], collision, [part], matrix, "Transformed Parts"); Wait(workspace);
        var edited = new CollisionSceneLoader(catalog).Load(new SceneDocument { TagHash = Compound });
        var piece = edited.Pieces[edited.Placements[part.Placement].Piece];
        Require(piece.Tag == collision.Pieces[placement.Piece].Tag, "The edited compound changed its placement order.");
        float error = Vector3.Distance(Mean(piece.Shape), expected);
        // Re-packing rounds the part, so it lands within a few centimetres; the origin would be metres away.
        Require(error < .05f, $"The group change put the part {error:0.###} m from its expected centre.");
        workspace.Undo(); Wait(workspace);
        Require(catalog.Read(piece.Tag).AsSpan().SequenceEqual(before), "One undo did not restore the group-changed mesh.");
        return error;
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(120)) { workspace.Poll(); Thread.Sleep(2); }
        Require(!workspace.IsBusy && workspace.Error.Length == 0, "Collision part workflow failed: " + workspace.Error);
    }

    private static void Throws(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("A collision part was saved in two groups.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
