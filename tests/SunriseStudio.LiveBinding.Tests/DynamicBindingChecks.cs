using System.Numerics;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Live;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Checks dynamic live instances: unavailable registry rows are skipped, signed scale is kept, and a failed read keeps the last fresh scale. */
internal static class DynamicBindingChecks
{
    private const ulong Now = 10000;
    private const uint First = 0x80A00000;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static int Run(AssetViewport viewport)
    {
        string folder = Path.Combine(Path.GetTempPath(), "sunrise-dynamic-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "packages"));
        try
        {
            PackageWriter.WriteNew(Path.Combine(folder, "packages", "w64_live_0100_0.pkg"), 0x100,
                [new(0x80809C0F, 8 << 9, new byte[32])]);
            using var workspace = new StudioWorkspace();
            typeof(StudioWorkspace).GetProperty("Catalog")!.SetValue(workspace, PackageCatalog.Open(folder));
            FreshnessAndGenerations(workspace, viewport);
            Cancellation(workspace, viewport);
            RegistrationAndBudget(workspace, viewport);
            RevisionReset(workspace, viewport);
            DrawnModels(workspace, viewport);
            Markers(workspace, viewport);
            Console.WriteLine("live_dynamic cases=6 generations=checked freshness=checked single_worker=checked budgets=checked revision=checked drawn_models=checked markers=checked");
            return 6;
        }
        finally
        {
            if (Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(folder, true);
        }
    }

    /** Missing rows need a complete fresh section; handle reuse invalidates an old generation immediately. */
    private static void FreshnessAndGenerations(StudioWorkspace workspace, AssetViewport viewport)
    {
        viewport.SetScene(new());
        using var binding = new LiveDynamicBinding((_, tag, _) => Prototype(tag));
        var row = Row(7, 1, First); var fresh = Snapshot([row]);
        binding.Poll(workspace, viewport, fresh with { Available = false }, Now);
        Check(binding.ObservedCount == 0 && viewport.LivePrototypeCount == 0, "Unavailable registry rows entered the queue.");
        Pump(binding, workspace, viewport, fresh, Now, () => viewport.LiveInstanceCount == 1);
        string first = LiveDynamicBinding.Key(row);
        foreach (float scale in new[] { -2f, 0f, 3f })
        {
            binding.Poll(workspace, viewport, Snapshot([row with { Scale = scale }]), Now);
            var mesh = viewport.TryGetLiveInstance(first)!.Meshes[0];
            Check(mesh.RenderTransform.M11 == scale && mesh.RenderTransform.M22 == scale && mesh.RenderTransform.M33 == scale,
                "Dynamic instance lost signed world scale.");
            binding.Poll(workspace, viewport, Snapshot([row with { Scale = null, TransformAvailable = false, Time = 0 }]), Now + 10);
            Check(mesh.RenderTransform.M11 == scale, "A failed dynamic read replaced the last fresh scale.");
        }
        foreach (var invalid in new[] { Snapshot([]) with { Complete = false }, Snapshot([]) with { Truncated = true },
            Snapshot([]) with { Overflow = 1 }, Snapshot([]) with { Total = 1 }, Snapshot([]) with { Available = false },
            Snapshot([]) with { Time = Now - LiveWorldProtocol.StaleAfterMilliseconds - 1 } })
        {
            binding.Poll(workspace, viewport, invalid, Now);
            Check(viewport.TryGetLiveInstance(first) is not null, "An incomplete or stale section implied a dynamic despawn.");
        }
        var moved = row with { Position = new(50, 0, 0) };
        binding.Poll(workspace, viewport, Snapshot([moved]) with { Time = 1 }, Now);
        Check(viewport.TryGetLiveInstance(first)!.Meshes[0].RenderTransform.Translation == Vector3.Zero,
            "Fresh-looking rows escaped their stale section.");
        var unknownNext = row with { Generation = 2, TransformAvailable = false, Time = 0 };
        binding.Poll(workspace, viewport, Snapshot([unknownNext]) with { Complete = false }, Now);
        Check(viewport.LiveInstanceCount == 0 && binding.ObservedCount == 0, "A reused handle retained its older generation's mesh.");
        var next = row with { Generation = 2, Position = Vector3.UnitX };
        binding.Poll(workspace, viewport, Snapshot([next]), Now);
        Check(viewport.LiveInstanceCount == 1 && viewport.LiveSlotCount == 1
            && viewport.TryGetLiveInstance(first) is null, "Fresh generation did not reuse its class slot.");
        var alias = next with { Handle = next.Handle | (2u << 13), Generation = 3 };
        binding.Poll(workspace, viewport, Snapshot([alias]) with { Complete = false }, Now);
        Check(binding.Actor(next.Handle) is null && binding.Actor(alias.Handle) == alias
            && viewport.TryGetLiveInstance(LiveDynamicBinding.Key(next)) is null,
            "A replacement with different handle bits retained or aliased the old slot occupant.");
        var conflict = Snapshot([next, next with { Generation = 3 }]);
        binding.Poll(workspace, viewport, conflict, Now);
        Check(viewport.LiveInstanceCount == 0, "A duplicate handle guessed which generation to draw.");
        var again = Snapshot([next]); binding.Poll(workspace, viewport, again, Now);
        binding.Poll(workspace, viewport, again, Now + LiveWorldProtocol.StaleAfterMilliseconds);
        Check(viewport.LiveInstanceCount == 1, "Dynamic freshness expired at its inclusive boundary.");
        binding.Poll(workspace, viewport, again, Now + LiveWorldProtocol.StaleAfterMilliseconds + 1);
        Check(viewport.LiveInstanceCount == 0, "Dynamic pose remained visible after its timestamp expired.");
        binding.Poll(workspace, viewport, fresh, Now);
        binding.Poll(workspace, viewport, Snapshot([]), Now);
        Check(viewport.LiveInstanceCount == 0, "Complete fresh omission did not remove a dynamic entity.");
    }

    /** A slow canceled decoder must exit before another decoder starts, and its result cannot publish. */
    private static void Cancellation(StudioWorkspace workspace, AssetViewport viewport)
    {
        viewport.SetScene(new());
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int running = 0, maximum = 0, calls = 0;
        using var binding = new LiveDynamicBinding((_, tag, token) =>
        {
            int concurrent = Interlocked.Increment(ref running);
            maximum = Math.Max(maximum, concurrent);
            try
            {
                if (Interlocked.Increment(ref calls) == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }
                return Prototype(tag);
            }
            finally { Interlocked.Decrement(ref running); }
        });
        var snapshot = Snapshot([Row(1, 1, First)]);
        binding.Poll(workspace, viewport, snapshot, Now);
        Check(entered.Wait(TimeSpan.FromSeconds(2)), "Prototype worker did not start.");
        try
        {
            binding.CancelPreparation();
            for (int i = 0; i < 20; i++) { binding.Poll(workspace, viewport, snapshot, Now); Thread.Sleep(1); }
            Check(calls == 1 && viewport.LivePrototypeCount == 0, "Cancel allowed a second decoder or adopted an abandoned result.");
        }
        finally { release.Set(); }
        Pump(binding, workspace, viewport, snapshot, Now, () => viewport.LiveInstanceCount == 1);
        Check(calls == 2 && maximum == 1, "Canceled work was duplicated or never resumed for the same snapshot.");
        binding.Clear(viewport);
        Check(viewport.LiveInstanceCount == 0 && viewport.SelectedObject is null, "Clear retained a runtime instance.");
    }

    /** Rejected registrations remain diagnostic notes; class and render limits do not break the connection owner. */
    private static void RegistrationAndBudget(StudioWorkspace workspace, AssetViewport viewport)
    {
        viewport.SetScene(new());
        int calls = 0;
        using (var failed = new LiveDynamicBinding((_, tag, _) =>
        { Interlocked.Increment(ref calls); return new(tag, new SceneDocument(), null, []); }))
        {
            var snapshot = Snapshot([Row(1, 1, First)]);
            Pump(failed, workspace, viewport, snapshot, Now, () => failed.Errors.Count == 1);
            for (int i = 0; i < 10; i++) failed.Poll(workspace, viewport, snapshot, Now);
            Check(calls == 1 && viewport.LivePrototypeCount == 0 && viewport.LiveInstanceCount == 0,
                "A registration error was retried every frame or partly adopted.");
        }
        viewport.SetScene(new());
        using var bounded = new LiveDynamicBinding((_, tag, _) => Prototype(tag));
        const int limit = AssetViewport.MaximumLivePrototypes;
        var rows = Enumerable.Range(0, limit + 1).Select(index => Row((uint)index + 1, 1, First + (uint)index)).ToArray();
        var many = Snapshot(rows);
        Pump(bounded, workspace, viewport, many, Now, () => viewport.LivePrototypeCount == limit);
        for (int i = 0; i < 10; i++) bounded.Poll(workspace, viewport, many, Now);
        Check(viewport.LivePrototypeCount == limit && viewport.LiveInstanceCount == limit
            && bounded.Status.Contains("limit", StringComparison.OrdinalIgnoreCase),
            "Class limit was exceeded or hidden from the user.");
    }

    /**
     * A drawn model shows only when no class prototype already shows it. Of the unlisted owners only
     * the player shows, under a stand-in row; objects attached to the player are not shown.
     */
    private static void DrawnModels(StudioWorkspace workspace, AssetViewport viewport)
    {
        viewport.SetScene(new());
        const uint Model = 0x80B00000, Other = 0x80B00001, Stray = 0x80B00002, Player = 0x00AA0009;
        using var binding = new LiveDynamicBinding((_, tag, _) => Prototype(tag));
        var row = Row(7, 1, First);
        var weapon = Row(12, 1, First + 1) with { PlayerAttached = true };
        var snapshot = Snapshot([row, weapon]);
        binding.SetPlayer(Player);
        binding.SetDrawn(new Dictionary<uint, DrawnOwner>
        {
            [7] = new(Vector3.Zero, [First, Model], 1), [Player] = new(new(5, 0, 0), [Other], 8),
            [11] = new(Vector3.Zero, [Stray], 1), [12] = new(Vector3.Zero, [Stray], 1)
        });
        Pump(binding, workspace, viewport, snapshot, Now, () => viewport.LiveInstanceCount == 3);
        for (int i = 0; i < 20; i++) { binding.Poll(workspace, viewport, snapshot, Now); Thread.Sleep(1); }
        Check(viewport.LiveInstanceCount == 3 && viewport.TryGetLiveInstance(LiveDynamicBinding.Key(row)) is not null
            && viewport.TryGetLiveInstance(LiveDynamicBinding.ModelKey(row, 7, Model, 1)) is not null
            && viewport.TryGetLiveInstance(LiveDynamicBinding.ModelKey(row, 7, First, 1)) is null,
            "A drawn model duplicated its class prototype, was not shown, or a stray owner was shown.");
        Check(viewport.TryGetLiveInstance(LiveDynamicBinding.Key(weapon)) is null && binding.Owners.All(owner => owner.Handle != 12),
            "An object attached to the player was shown.");
        var standIn = binding.Owners.Single(owner => owner.Handle == Player);
        string key = LiveDynamicBinding.ModelKey(null, Player, Other, 8);
        Check(standIn.ClassTag == 0 && key.StartsWith(LiveDynamicBinding.Key(standIn) + "/", StringComparison.Ordinal)
            && viewport.TryGetLiveInstance(key)?.Meshes[0].RenderTransform.Translation == new Vector3(5, 0, 0),
            "The player lost its generation-qualified stand-in row or its anchor.");
        var replacement = row with { Generation = 2 };
        binding.Poll(workspace, viewport, Snapshot([replacement, weapon]), Now);
        Check(viewport.TryGetLiveInstance(LiveDynamicBinding.ModelKey(replacement, 7, Model, 2)) is null,
            "An old captured generation created a drawn model for a replacement object.");
        binding.SetDrawn(new Dictionary<uint, DrawnOwner>()); binding.Poll(workspace, viewport, snapshot, Now);
        Check(viewport.LiveInstanceCount == 1 && binding.Owners.Count == 1, "Drawn models outlived their draws.");
    }

    /** A row the game reports as not placed or hidden loads no class; it is drawn as a marker. */
    private static void Markers(StudioWorkspace workspace, AssetViewport viewport)
    {
        viewport.SetScene(new());
        int calls = 0;
        using var binding = new LiveDynamicBinding((_, tag, _) => { Interlocked.Increment(ref calls); return Prototype(tag); });
        var row = Row(7, 1, First) with { PlacedNotHidden = false, Position = new(1, 2, 3) };
        var placed = Row(8, 1, 0) with { Dynamic = false, PlacedNotHidden = false, ObjectListTag = 0x80A00100, EntryIndex = 4 };
        var snapshot = Snapshot([row, placed]);
        Pump(binding, workspace, viewport, snapshot, Now, () => viewport.LiveInstanceCount == 2);
        for (int i = 0; i < 10; i++) { binding.Poll(workspace, viewport, snapshot, Now); Thread.Sleep(1); }
        Check(calls == 0 && viewport.LivePrototypeCount == 1 && viewport.HasLivePrototype(LiveDynamicBinding.MarkerTag)
            && viewport.TryGetLiveInstance(LiveDynamicBinding.MarkerKey(row))?.Meshes[0].RenderTransform.Translation == new Vector3(1, 2, 3)
            && viewport.TryGetLiveInstance(LiveDynamicBinding.MarkerKey(placed)) is not null
            && viewport.TryGetLiveInstance(LiveDynamicBinding.Key(row)) is null,
            "An inactive row loaded its class or lost its marker.");
        var active = Snapshot([row with { PlacedNotHidden = true }]);
        Pump(binding, workspace, viewport, active, Now, () => viewport.TryGetLiveInstance(LiveDynamicBinding.Key(row)) is not null);
        Check(calls == 1 && viewport.TryGetLiveInstance(LiveDynamicBinding.MarkerKey(row)) is null
            && viewport.TryGetLiveInstance(LiveDynamicBinding.MarkerKey(placed)) is null,
            "An active row kept its marker, or a placed row outlived a complete omission.");
    }

    private static void RevisionReset(StudioWorkspace workspace, AssetViewport viewport)
    {
        viewport.SetScene(new());
        int calls = 0;
        using var binding = new LiveDynamicBinding((_, tag, _) => { Interlocked.Increment(ref calls); return Prototype(tag); });
        var snapshot = Snapshot([Row(1, 1, First)]);
        Pump(binding, workspace, viewport, snapshot, Now, () => viewport.LiveInstanceCount == 1);
        int revision = viewport.LivePrototypeRevision;
        viewport.SetScene(new());
        Check(viewport.LivePrototypeRevision != revision, "Scene reset did not publish a prototype epoch.");
        Pump(binding, workspace, viewport, snapshot, Now, () => viewport.LiveInstanceCount == 1);
        Check(calls == 2 && viewport.LivePrototypeCount == 1 && binding.Errors.Count == 0,
            "Scene revision retained old prototype readiness or lost the same live snapshot.");
        var sameScene = viewport.Scene;
        typeof(AssetViewport).GetMethod("ReleaseRuntimeClasses", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewport, null);
        Check(ReferenceEquals(viewport.Scene, sameScene), "Renderer epoch fixture replaced the authored scene.");
        Pump(binding, workspace, viewport, snapshot, Now, () => viewport.LiveInstanceCount == 1);
        Check(calls == 3 && viewport.LivePrototypeCount == 1, "Native renderer epoch did not clear prototype readiness.");
    }

    private static void Pump(LiveDynamicBinding binding, StudioWorkspace workspace, AssetViewport viewport,
        LivePlacements snapshot, ulong now, Func<bool> done)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && timer.Elapsed < TimeSpan.FromSeconds(5))
        { binding.Poll(workspace, viewport, snapshot, now); Thread.Sleep(1); }
        Check(done(), "Prototype queue did not reach the expected state: " + binding.Status);
    }

    private static LiveScenePrototype Prototype(uint tag)
    {
        var mesh = new MeshData { TagHash = tag, Vertices = [new(new(-1, 0, -1), -Vector3.UnitY, Vector2.Zero, Vector4.One),
            new(new(0, 0, 1), -Vector3.UnitY, Vector2.Zero, Vector4.One),
            new(new(1, 0, -1), -Vector3.UnitY, Vector2.Zero, Vector4.One)], Indices = [0, 1, 2] };
        var scene = new SceneDocument { Name = "Runtime fixture" }; scene.Instances.Add(new() { Mesh = mesh });
        return new(tag, scene, null, []);
    }
    private static LivePlacement Row(uint handle, uint generation, uint tag) => new(0, 0, 0, handle, generation,
        0, Vector3.Zero, Quaternion.Identity, Now, true, tag, true);
    private static LivePlacements Snapshot(IReadOnlyList<LivePlacement> rows) =>
        new(Now, true, true, false, (uint)rows.Count, 0, 1, Now, rows);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
