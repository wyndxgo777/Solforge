using System.Diagnostics;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Editor;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;

/** A collision import that adds a shape saves one undo step: the compound, the owner and the new packfile. */
internal static class CollisionAddWorkflowChecks
{
    private const uint Compound = 0x80EDC2D0, Owner = 0x80EDC2D1;

    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(string gameRoot, string root)
    {
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(gameRoot); Wait(workspace);
        workspace.Select(Compound); Wait(workspace);
        workspace.CreateProject(Path.Combine(root, "collision-add-project"), "Collision add workflow"); Wait(workspace);
        var catalog = workspace.Catalog!;
        var project = workspace.Project!;
        byte[] compound = catalog.Read(Compound), owner = catalog.Read(Owner);
        var baseline = new CollisionSceneLoader(catalog).Load(new SceneDocument { TagHash = Compound });
        var corners = Enumerable.Range(0, 8).Select(i => new Vector3((i & 1) - .5f, (i >> 1 & 1) - .5f, (i >> 2 & 1) - .5f)).ToArray();
        int[] cube = [0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5];
        var at = Matrix4x4.CreateTranslation((baseline.Placements[0].BoundsMin + baseline.Placements[0].BoundsMax) / 2 + new Vector3(0, 0, 2));
        var input = new CollisionImportInput(
            [.. baseline.Placements.Select(p => new CollisionPlacementInput(p.CompoundTag, p.Instance, baseline.Pieces[p.Piece].Tag,
                baseline.Pieces[p.Piece].Offset, p.World)),
                new(0, -1, 0, 0, at) { Added = true, NewShape = new("cube", corners, cube, false, null) }],
            [], new Dictionary<uint, string>());
        var plan = CollisionImport.Plan(catalog, baseline, input, tag => project.ReadAsset(tag, catalog), CollisionImport.ShapePackage(project, catalog));
        workspace.ApplyCollisionPlan(plan); Wait(workspace);
        uint shape = plan.AddedShapes.Single().Tag;
        int Count() => StaticCompoundShape.Parse(HavokPackfile.Parse(catalog.Read(Compound))).Instances.Count;
        int installed = StaticCompoundShape.Parse(HavokPackfile.Parse(compound)).Instances.Count;
        Require(project.AddedAssets.Count == 1 && project.Changes.Count == 2 && Count() == installed + 1
            && catalog.Read(shape).AsSpan().SequenceEqual(plan.AddedShapes[0].Data), "The import did not save the shape, compound and owner.");
        workspace.Undo(); Wait(workspace);
        Require(project.AddedAssets.Count == 0 && project.Changes.Count == 0 && catalog.Read(Compound).AsSpan().SequenceEqual(compound)
            && catalog.Read(Owner).AsSpan().SequenceEqual(owner) && !catalog.TryGetEntry(shape, out _), "Undo did not remove the added collision.");
        workspace.Redo(); Wait(workspace);
        Require(project.AddedAssets.Count == 1 && project.Changes.Count == 2 && Count() == installed + 1
            && catalog.Read(shape).AsSpan().SequenceEqual(plan.AddedShapes[0].Data), "Redo did not restore the added collision.");
        Console.WriteLine($"editor_collision_add shape={shape:X8} instances={installed}->{installed + 1} changes=2 undo=removed redo=restored");
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(120)) { workspace.Poll(); Thread.Sleep(2); }
        Require(!workspace.IsBusy && workspace.Error.Length == 0, "Collision add workflow failed: " + workspace.Error);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
