using System.Diagnostics;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Editor;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Workspace edits move the open world in place, and the result matches a full reload of the same state. */
internal static class PlacementUpdateWorkflowChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(string root)
    {
        string install = Path.Combine(root, "placement-workflow-install");
        WorldFixture.Create(install);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace);
        workspace.OpenMission(WorldFixture.Base, WorldFixture.Base + 4); Wait(workspace);
        workspace.CreateProject(Path.Combine(root, "placement-workflow-project"), "Placement workflow"); Wait(workspace);
        workspace.Select(WorldFixture.Base + 21); Wait(workspace);
        var catalog = workspace.Catalog!;
        var lightTable = workspace.Document!;

        // A spawn edit moves the marker in place and keeps the unrelated asset tab document.
        var spawn = workspace.World!.Scene.Markers.First(marker => marker.Layer == SceneLayer.Spawns);
        workspace.TakeCollisionChanges();
        workspace.ApplyObjectEdit(SceneEdits.CreateTransformEdit(catalog, spawn, spawn.Position + new Vector3(3, 0, 0), null, 1,
            workspace.World, "Spawn")); Wait(workspace);
        Require(workspace.LastWorldRefresh is { Incremental: true }, "A spawn edit reloaded the world: " + workspace.LastWorldRefresh?.Reason);
        // The collision layer gets the tags the edit rewrote, once.
        Require(workspace.TakeCollisionChanges() is { } collisionChanges && collisionChanges.Contains(spawn.SourceTag)
            && workspace.TakeCollisionChanges() is { Count: 0 }, "The collision layer did not get the spawn edit's tags once.");
        Require(ReferenceEquals(workspace.Document, lightTable), "A spawn edit read the unrelated asset tab again.");
        Compare(workspace, "spawn");

        // Undo moves it back in place.
        workspace.Undo(); Wait(workspace);
        Require(workspace.LastWorldRefresh is { Incremental: true }, "Undo reloaded the world: " + workspace.LastWorldRefresh?.Reason);
        Compare(workspace, "undo");

        // A light edit changes the open light table, so the asset tab reads it again.
        var light = workspace.World!.Objects.Single(row => row.Kind == WorldObjectKind.Light).Source;
        workspace.ApplyObjectEdit(SceneEdits.CreateTransformEdit(catalog, light, new(7, 8, 9), null, 2, workspace.World, "Light")); Wait(workspace);
        Require(workspace.LastWorldRefresh is { Incremental: true }, "A light edit reloaded the world: " + workspace.LastWorldRefresh?.Reason);
        Require(!ReferenceEquals(workspace.Document, lightTable) && workspace.Document?.Entry.TagHash == WorldFixture.Base + 21,
            "The edited light table was not read again.");
        Compare(workspace, "light");

        // A byte outside the placement fields reloads the world.
        byte[] trigger = catalog.Read(WorldFixture.Base + 22);
        trigger[0x30] ^= 1;
        workspace.Select(WorldFixture.Base + 22); Wait(workspace);
        workspace.ApplyBytes(trigger); Wait(workspace);
        Require(workspace.LastWorldRefresh is { Incremental: false, Reason.Length: > 0 }, "A structural raw edit moved the world in place.");
        Console.WriteLine($"editor_placement_updates spawn=in-place undo=in-place light=in-place kept-document=pass "
            + $"reread-document=pass raw-structure=\"{workspace.LastWorldRefresh!.Reason}\"");
    }

    /** Moves one static instance of an installed map through the workspace and times both paths. */
    internal static void Owned(string gameRoot, string root, uint map, uint mission)
    {
        using var workspace = new StudioWorkspace();
        var watch = Stopwatch.StartNew();
        workspace.OpenInstallation(gameRoot); Wait(workspace, 1200);
        workspace.OpenMission(map, mission); Wait(workspace, 1200);
        double open = watch.Elapsed.TotalMilliseconds;
        workspace.CreateProject(Path.Combine(root, "owned-placement-workflow"), "Owned placement workflow"); Wait(workspace, 1200);
        var world = workspace.World!;
        var catalog = workspace.Catalog!;
        var drawn = workspace.NativePlan!.Groups.SelectMany(group => group.Parts).Select(part => part.Instance).ToHashSet();
        var instance = world.Scene.Instances.First(item => item.InstanceSourceTag != 0 && drawn.Contains(item)
            && SceneEdits.Describe(catalog, item, world).CanTransform);
        var info = SceneEdits.Describe(catalog, instance, world);
        var edit = SceneEdits.CreateTransformEdit(catalog, instance, info.Position + new Vector3(1, 0, .5f), null, info.Scale, world, "Instance");
        watch.Restart();
        workspace.ApplyObjectEdit(edit); Wait(workspace, 1200);
        double incremental = watch.Elapsed.TotalMilliseconds;
        double isolation = 0;
        bool isolated = workspace.CompiledObjects.GeneratedAssets.Count != 0;
        if (isolated)
        {
            isolation = incremental;
            // The first isolation adds generated world membership in place; a full reload must match it.
            Require(workspace.LastWorldRefresh is { Incremental: true } && ReferenceEquals(workspace.World, world),
                "First isolation reloaded the world: " + workspace.LastWorldRefresh?.Reason);
            Compare(workspace, "owned-first-isolation", 1200, unordered: true);
            var original = instance;
            world = workspace.World!;
            instance = world.Scene.Instances.First(item => item.InstanceSourceTag == original.InstanceSourceTag
                && item.InstanceSourceIndex == original.InstanceSourceIndex && item.Mesh.TagHash == original.Mesh.TagHash
                && item.Mesh.PartIndex == original.Mesh.PartIndex && item.Mesh.LodLevel == original.Mesh.LodLevel
                && item.PlacementSourceTag == original.PlacementSourceTag && item.PlacementSourceOffset == original.PlacementSourceOffset
                && item.BubbleHash == original.BubbleHash && item.ActivityTag == original.ActivityTag
                && item.StateOrdinal == original.StateOrdinal);
            var current = SceneEdits.Describe(catalog, instance, world);
            Require(current.Target == edit!.Target, "Isolation changed the owned editable identity.");
            var again = SceneEdits.CreateTransformEdit(catalog, instance, current.Position + new Vector3(0, .5f, 0),
                null, current.Scale, world, "Instance");
            watch.Restart();
            workspace.ApplyObjectEdit(again); Wait(workspace, 1200);
            incremental = watch.Elapsed.TotalMilliseconds;
        }
        Require(workspace.LastWorldRefresh is { Incremental: true }, "An owned instance edit reloaded the world: " + workspace.LastWorldRefresh?.Reason);
        double update = workspace.LastWorldRefresh!.Milliseconds;
        Require(instance.Transform.Translation != info.Position && ReferenceEquals(workspace.World, world), "The instance did not move in place.");
        double reload = Compare(workspace, "owned-instance", 1200);
        watch.Restart();
        workspace.Undo(); Wait(workspace, 1200);
        double undo = watch.Elapsed.TotalMilliseconds;
        Require(workspace.LastWorldRefresh is { Incremental: true }, "Owned undo reloaded the world: " + workspace.LastWorldRefresh?.Reason);
        Compare(workspace, "owned-undo", 1200);
        if (isolated)
        {
            workspace.Undo(); Wait(workspace, 1200);
            Require(workspace.LastWorldRefresh is { Incremental: true }
                && workspace.CompiledObjects.GeneratedAssets.Count == 0 && workspace.Project!.ObjectEdits.Count == 0,
                "Removing the last isolated edit reloaded or retained generated membership: " + workspace.LastWorldRefresh?.Reason);
            Compare(workspace, "owned-last-isolation-undo", 1200, unordered: true);
        }
        Console.WriteLine($"editor_placement_timing map={map:X8} mission={mission:X8} instances={world.Scene.Instances.Count} "
            + $"open_ms={open:F0} first_isolation_ms={isolation:F0} edit_in_place_ms={incremental:F1} update_ms={update:F1} "
            + $"undo_in_place_ms={undo:F1} full_reload_ms={reload:F0}");
    }

    /** A SpeedTree canopy edit that requantizes its payload moves every record of it in place. */
    internal static void OwnedDecorator(string gameRoot, string root)
    {
        // pvp_sabre map root; canopy payload 80FAA33A record 42 sits on the tree at 80FAA28E row 4544.
        const uint Map = 0x80FAA003, Payload = 0x80FAA33A;
        using var workspace = new StudioWorkspace();
        workspace.OpenInstallation(gameRoot); Wait(workspace, 1200);
        workspace.OpenMap(Map); Wait(workspace, 1200);
        workspace.CreateProject(Path.Combine(root, "owned-decorator-workflow"), "Owned decorator workflow"); Wait(workspace, 1200);
        var world = workspace.World!;
        var catalog = workspace.Catalog!;
        var canopy = world.Scene.Instances.First(item => item.ObjectPlacement is { SourceTag: Payload, SourceIndex: 42 });
        var info = SceneEdits.Describe(catalog, canopy, world);
        // A tenth of the scale is outside the installed range, so every record is encoded again.
        workspace.ApplyObjectEdit(SceneEdits.CreateTransformEdit(catalog, canopy, info.Position + new Vector3(.5f, 0, 0), null,
            info.Scale * .1f, world, "Canopy")); Wait(workspace, 1200);
        Require(workspace.LastWorldRefresh is { Incremental: true } && ReferenceEquals(workspace.World, world),
            "A decorator edit reloaded the world: " + workspace.LastWorldRefresh?.Reason);
        Compare(workspace, "owned-decorator", 1200);
        workspace.Undo(); Wait(workspace, 1200);
        Require(workspace.LastWorldRefresh is { Incremental: true }, "Decorator undo reloaded the world: " + workspace.LastWorldRefresh?.Reason);
        Compare(workspace, "owned-decorator-undo", 1200);
        // A handle drag lands where it said: the tree row exactly, the canopy record within its snorm16 step.
        // Compare reloaded the world, so the objects come from the current one.
        var current = workspace.World!.Scene.Instances;
        var tree = current.First(item => item.InstanceSourceTag == 0x80FAA28E && item.InstanceSourceIndex == 4544);
        var record = current.First(item => item.ObjectPlacement is { SourceTag: Payload, SourceIndex: 42 });
        var delta = new Vector3(.5f, -.25f, 1);
        foreach (var (target, tolerance) in new[] { (tree, 1e-4f), (record, .005f) })
        {
            var frame = TransformHandles.Read(catalog, target, workspace.World, out string reason);
            Require(frame is not null, "The handles refused an editable selection: " + reason);
            var before = target.Transform.Translation;
            workspace.ApplyObjectEdit(TransformHandles.CreateEdit(catalog, target, frame!, frame!.Pivot,
                new TransformChange(delta, Quaternion.Identity, new Vector3(1.25f)), workspace.World, "Handle")); Wait(workspace, 1200);
            Require(workspace.LastWorldRefresh is { Incremental: true }
                && (target.Transform.Translation - before - delta).Length() <= tolerance,
                $"A handle drag did not move {target.Name} by its delta in place.");
            workspace.Undo(); Wait(workspace, 1200);
        }
        Console.WriteLine("editor_placement_decorator payload=80FAA33A record=42 requantized=in-place undo=in-place handles=pass");
    }

    /**
     * Fingerprints the updated world and plan, reloads the same state, and requires equal fingerprints.
     * @param unordered Compares instances as a set; a reload lists an isolated instance after the others.
     */
    internal static double Compare(StudioWorkspace workspace, string label, int timeoutSeconds = 30, bool unordered = false)
    {
        var world = Canonical(WorldFingerprint.Of(workspace.World!), unordered);
        var plan = Canonical(PlanPrint(workspace.NativePlan, workspace.World!.Scene, unordered), unordered);
        var watch = Stopwatch.StartNew();
        workspace.ReloadWorld(); Wait(workspace, timeoutSeconds);
        double reload = watch.Elapsed.TotalMilliseconds;
        Require(workspace.LastWorldRefresh is { Incremental: false }, "The comparison reload did not run.");
        string difference = WorldFingerprint.Compare(world, Canonical(WorldFingerprint.Of(workspace.World!), unordered))
            + WorldFingerprint.Compare(plan, Canonical(PlanPrint(workspace.NativePlan, workspace.World!.Scene, unordered), unordered));
        Require(difference.Length == 0, $"{label}: the moved world differs from a full reload:{difference}");
        Console.WriteLine($"PLACEMENT_WORKFLOW step={label} world_lines={world.Count} plan_lines={plan.Count} equal=true reload_ms={reload:F0}");
        return reload;
    }

    /** Drops scene indices and sorts, so the same instances in another order compare equal. */
    private static List<string> Canonical(List<string> lines, bool unordered) => !unordered ? lines
        : lines.Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"^(\S+ (instance|marker)) \d+ ", "$1 "))
            .Order(StringComparer.Ordinal).ToList();

    /** Each drawn part's CB11 rows and bounds, keyed by its instance's scene index, or by its identity when unordered. */
    private static List<string> PlanPrint(NativeScenePlan? plan, SceneDocument scene, bool byIdentity = false)
    {
        if (plan is null) return ["no plan"];
        var index = new Dictionary<MeshInstance, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < scene.Instances.Count; i++) index[scene.Instances[i]] = i;
        var lines = new List<string> { $"plan groups={plan.Groups.Count} parts={plan.PreparedPartCount} {V(plan.Minimum)} {V(plan.Maximum)}" };
        foreach (var group in plan.Groups)
            foreach (var part in group.Parts)
                lines.Add($"{group.PipelineKey}|{group.GeometryKey}|{(byIdentity ? Identity(part.Instance) : index.GetValueOrDefault(part.Instance, -1))}|"
                    + string.Join(",", part.Constants.Select(V4)) + $"|{V(part.Center)}|{F(part.Radius)}");
        return lines;
    }

    private static string Identity(MeshInstance item) => $"{item.InstanceSourceTag:X8}:{item.InstanceSourceIndex}:{item.PlacementSourceTag:X8}:"
        + $"{item.PlacementSourceOffset}:{item.Mesh.TagHash:X8}:{item.Mesh.PartIndex}:{item.Mesh.LodLevel}:{item.BubbleHash:X8}";

    private static string F(float value) => BitConverter.SingleToUInt32Bits(value).ToString("X8");
    private static string V(Vector3 value) => $"{F(value.X)},{F(value.Y)},{F(value.Z)}";
    private static string V4(Vector4 value) => $"{F(value.X)},{F(value.Y)},{F(value.Z)},{F(value.W)}";

    private static void Wait(StudioWorkspace workspace, int timeoutSeconds = 30)
    {
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        { workspace.Poll(); Thread.Sleep(1); }
        Require(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error);
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
