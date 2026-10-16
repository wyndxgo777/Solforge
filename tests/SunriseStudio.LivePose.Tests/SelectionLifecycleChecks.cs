using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SunriseStudio.Animation;
using SunriseStudio.Editor;
using SunriseStudio.Live;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Checks live selection lifecycle on installed models: selecting an object publishes both geometry and native state, and deselecting or losing the object cleans up. */
internal static class SelectionLifecycleChecks
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private const uint Entity = 0x80C0FA98, Model = 0x80FEE881, List = 0x80A01000, Owner = 42;

    /** Owned shader inputs run in a synthetic world; the connection belongs only to this test PID. */
    public static void Run(PackageCatalog catalog)
    {
        var loaded = new AssetSceneLoader(catalog) { IncludeLowerLods = false }.Load(Entity);
        var inspected = AssetViewport.PrepareNativeScene(catalog, loaded, textureBudgetBytes: long.MaxValue, assetPreview: true);
        var selected = inspected.Groups.Where(group => group.RenderStage == 0
                && group.Material.Palette == NativeEntityPalette.DualQuaternion && !group.Material.ReadsWaterObject)
            .SelectMany(group => group.Parts).Select(part => part.Instance)
            .Where(instance => instance.Mesh.TagHash == Model).Distinct().OrderByDescending(instance => instance.Mesh.Indices.Length)
            .Take(2).ToArray();
        Check(selected.Length == 2, "Owned lifecycle fixture needs two primary model parts.");
        var placement = new SceneObjectPlacement(List, 0, Matrix4x4.Identity) { EntryIndex = 0 };
        var scene = new SceneDocument { Name = "Pose lifecycle fixture" };
        foreach (var part in selected) scene.Instances.Add(new()
        {
            Mesh = part.Mesh, Transform = part.Transform, ObjectPlacement = placement, ObjectClassTag = Entity,
            ModelSourceResourceClass = part.ModelSourceResourceClass, Name = part.Name
        });
        var first = scene.Instances[0]; var second = scene.Instances[1];
        var world = WorldDocument.FromScene(scene);
        var prepared = LivePoseModelBinding.Prepare(catalog, Entity, Model);
        var rig = new SkeletonReader(catalog).Read(Entity);
        byte[] header = first.Mesh.Native!.SourceHeader;
        Vector4[] fixedRows = [Vector(header, 0x60), Vector(header, 0x70), Vector4.One];
        var rows = Enumerable.Range(0, rig.Bones.Count).SelectMany(_ => new Vector4[] { new(0, 0, 0, 1), new(1.5f, -.25f, .5f, 0) }).ToArray();
        using var renderer = new Dx11Renderer(IntPtr.Zero, 160, 120, true);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(scene);
        viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: long.MaxValue, assetPreview: true));
        Check(scene.Instances.All(instance => viewport.NativeRenderer!.CanApplyLivePalette(instance, NativeEntityPalette.DualQuaternion)),
            "Owned lifecycle fixture has an unadmitted material.");
        using var workspace = new StudioWorkspace();
        using var process = Process.GetCurrentProcess();
        var connection = LiveConnection.Attach(new(process.Id, process.MainModule!.FileName));
        using var page = new LivePage { AnimationEnabled = true };
        Set(page, "connection", connection);
        Set(page, "scene", world);
        ulong now = (ulong)Environment.TickCount64;
        Set(page, "world", new LiveWorld(now, true, true, world.Map.DefinitionTag, 0, 0, world.Map.Tag, null, "fixture", []));
        Set(page, "snapshot", new LiveSnapshot(now, (uint)process.Id, true, null, null, false, null));
        var placementRow = new LivePlacement(1, List, 0, Owner, 7, 0, Vector3.Zero,
            Quaternion.Identity, now, true, Entity, false);
        var placements = new LivePlacements(now, true, true, false, 1, 0, 1, now, [placementRow]);
        Property(Field<object>(connection, "world"), "Placements", placements);
        var bindingType = typeof(LivePage).Assembly.GetType("SunriseStudio.Editor.LiveSceneBinding", true)!;
        var binding = Activator.CreateInstance(bindingType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [world], null)!;
        Set(page, "binding", binding);
        Call(binding, "Apply", placements, viewport, now);
        Set(page, "transformsApplied", true);
        Set(page, "byHandle", new Dictionary<uint, LivePlacement> { [Owner] = placementRow });
        Set(page, "byIndex", new Dictionary<uint, LivePlacement> { [Owner] = placementRow });
        Set(page, "filteredViewport", viewport);
        Property(workspace, "Catalog", catalog);
        Invoke(page, "PollSelectedPose", workspace, viewport);
        Field<Dictionary<(uint, uint), LivePoseModelBinding>>(page, "poseBindings")[(Entity, Model)] = prepared;
        viewport.SetSelection(first);
        Invoke(page, "MergeDraws");
        var poseConnection = Field<object>(connection, "pose");
        Property(poseConnection, "Draws", new LiveDraws((ulong)Environment.TickCount64, [new LiveDraw((ulong)Environment.TickCount64,
            Owner, 91, Model, LivePaletteKind.DualQuaternion, rig.Bones.Count, rig.Bones.Count, fixedRows, rows, 7, 11)], 0, 0));
        Invoke(page, "MergeDraws");
        Apply();
        Check(viewport.LivePoseGeometryCount == 2 && viewport.NativeRenderer!.LivePaletteCount == 2,
            "Object selection did not publish both geometry and native palettes.");
        viewport.SelectionMode = SceneSelectionMode.MeshPart;
        Check(ReferenceEquals(viewport.SelectedObject, first), "Fixture changed the representative while switching selection mode.");
        Apply();
        Check(viewport.LivePoseGeometryCount == 1 && viewport.NativeRenderer!.LivePaletteCount == 2 && PoseParts().Single() == first,
            "Part mode did not narrow posed geometry to the part, or dropped another part's palette.");
        viewport.SetSelection(second); Apply();
        Check(viewport.LivePoseGeometryCount == 1 && PoseParts().Single() == second,
            "Same-model part selection kept the previous part's geometry.");
        viewport.SelectionMode = SceneSelectionMode.Object; Apply();
        Check(viewport.LivePoseGeometryCount == 2 && viewport.NativeRenderer!.LivePaletteCount == 2,
            "Returning to object mode did not restore the complete selected part set.");

        var oldRenderer = viewport.NativeRenderer;
        viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: long.MaxValue, assetPreview: true));
        Check(!ReferenceEquals(viewport.NativeRenderer, oldRenderer) && viewport.LivePoseGeometryCount == 0
            && viewport.NativeRenderer!.LivePaletteCount == 0, "Renderer replacement kept pick geometry or palettes.");
        Apply();
        Check(viewport.LivePoseGeometryCount == 2 && viewport.NativeRenderer!.LivePaletteCount == 2,
            "The held capture did not reapply to the replacement renderer.");

        var oldDraw = connection.Draws!.Draws.Single();
        var replacement = placementRow with { Generation = 8 };
        PublishRows([replacement]); Invoke(page, "MergeDraws"); Apply();
        Check(viewport.LivePoseGeometryCount == 0 && viewport.NativeRenderer!.LivePaletteCount == 0,
            "A replacement generation received the previous object's held palette or pick geometry.");
        Property(poseConnection, "Draws", new LiveDraws((ulong)Environment.TickCount64, [oldDraw], 0, 0));
        Invoke(page, "MergeDraws"); Apply();
        Check(viewport.NativeRenderer!.LivePaletteCount == 0, "A late native publication restored a retired generation.");
        var nextDraw = new LiveDraw((ulong)Environment.TickCount64, Owner, 91, Model,
            LivePaletteKind.DualQuaternion, rig.Bones.Count, rig.Bones.Count, fixedRows, rows, 8, 12);
        Property(poseConnection, "Draws", new LiveDraws(nextDraw.Time, [nextDraw], 0, 0));
        Invoke(page, "MergeDraws"); Apply();
        Check(viewport.LivePoseGeometryCount == 2 && viewport.NativeRenderer!.LivePaletteCount == 2,
            "The replacement's own generation did not recover both pose paths.");
        PublishRows([replacement with { Handle = Owner | (2u << 13) }]); Invoke(page, "MergeDraws"); Apply();
        Check(viewport.LivePoseGeometryCount == 0 && viewport.NativeRenderer!.LivePaletteCount == 0,
            "A different full handle in the same slot received the previous palette.");
        PublishRows([replacement, replacement with { Generation = 9 }]); Invoke(page, "MergeDraws"); Apply();
        Check(viewport.NativeRenderer!.LivePaletteCount == 0, "Ambiguous rows admitted a captured owner.");
        Set(page, "snapshot", Field<LiveSnapshot>(page, "snapshot") with { PlayerHandle = Owner });
        PublishRows([]);
        Property(poseConnection, "Draws", new LiveDraws(nextDraw.Time, [nextDraw], 0, 0));
        Invoke(page, "MergeDraws"); Apply();
        Check(viewport.NativeRenderer!.LivePaletteCount == 0 && viewport.LivePoseGeometryCount == 0,
            "A player handle without a registry lifetime admitted an unverified generation.");
        Property(poseConnection, "Draws", new LiveDraws(nextDraw.Time, [nextDraw], 0, 0, new(Owner, 8)));
        Invoke(page, "MergeDraws");
        Check(Field<Dictionary<uint, LiveDraw>>(page, "drawsByComponent").Count == 1,
            "An independently observed player lifetime could not animate without a registry row.");
        Property(poseConnection, "Draws", new LiveDraws(nextDraw.Time, [], 0, 0, new(Owner, 9)));
        Invoke(page, "MergeDraws"); Apply();
        Check(Field<Dictionary<uint, LiveDraw>>(page, "drawsByComponent").Count == 0 && viewport.LivePoseGeometryCount == 0,
            "A new unregistered player generation retained its predecessor's draw.");
        Property(poseConnection, "Draws", new LiveDraws(nextDraw.Time, [nextDraw], 0, 0, new(Owner, 8)));
        Invoke(page, "MergeDraws");
        Property(poseConnection, "Draws", new LiveDraws(nextDraw.Time, [], 0, 0));
        Invoke(page, "MergeDraws");
        Check(Field<Dictionary<uint, LiveDraw>>(page, "drawsByComponent").Count == 0,
            "A failed current-player read retained an unverifiable held draw.");
        Property(poseConnection, "Draws", new LiveDraws((ulong)Environment.TickCount64 - LivePoseProtocol.StaleAfterMilliseconds - 1,
            [nextDraw], 0, 0, new(Owner, 8)));
        Invoke(page, "MergeDraws");
        Check(Field<Dictionary<uint, LiveDraw>>(page, "drawsByComponent").Count == 0,
            "A stale observation admitted the player's current-looking draw.");
        Set(page, "snapshot", Field<LiveSnapshot>(page, "snapshot") with { PlayerHandle = null });
        PublishRows([replacement]);
        Property(poseConnection, "Draws", new LiveDraws(nextDraw.Time, [nextDraw], 0, 0));
        Invoke(page, "ResetDraws", true); Invoke(page, "MergeDraws"); Apply();
        Check(viewport.NativeRenderer!.LivePaletteCount == 0, "A world reset readopted the preceding capture.");

        Set(page, "syncAnimation", false); Invoke(page, "StopAnimation"); Apply();
        Check(viewport.LivePoseGeometryCount == 0 && viewport.NativeRenderer!.LivePaletteCount == 0,
            "Turning animation off kept a palette or posed geometry.");

        var pending = new TaskCompletionSource<(LivePoseModelBinding? Binding, string Error)>();
        var cancellation = new CancellationTokenSource();
        Field<Dictionary<(uint, uint), LivePoseModelBinding>>(page, "poseBindings").Clear();
        Set(page, "poseCancellation", cancellation); Set(page, "poseWork", pending.Task);
        Set(page, "poseWorkKey", (Entity, Model));
        page.CancelPreparation(); pending.SetResult((prepared, "")); Invoke(page, "FinishPosePreparation");
        Check(Field<object?>(page, "poseWork") is null
            && Field<Dictionary<(uint, uint), LivePoseModelBinding>>(page, "poseBindings").Count == 0,
            "A canceled preparation adopted its result.");
        Property(workspace, "Catalog", null);
        Console.WriteLine("live_pose_selection palettes=every_animated_part geometry=selection_only part_mode=refresh renderer_replacement=reapplied disable=cleared canceled_result=rejected adapter=warp");

        void Apply() => Invoke(page, "ApplyAnimation", workspace, viewport);
        void PublishRows(LivePlacement[] published)
        {
            var changed = placements with { Rows = published, Total = (uint)published.Length, Complete = false };
            Property(Field<object>(connection, "world"), "Placements", changed);
            Invoke(page, "IndexRows", changed);
        }
        IEnumerable<MeshInstance> PoseParts() => Field<System.Collections.IDictionary>(viewport, "livePoseMeshes").Keys.Cast<MeshInstance>();
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Fields)!.GetValue(value)!;
    private static void Set(object value, string name, object? item) => value.GetType().GetField(name, Fields)!.SetValue(value, item);
    private static void Property(object value, string name, object? item) => value.GetType().GetProperty(name)!.SetValue(value, item);
    private static void Invoke(object value, string name, params object[] args) => Call(value, name, args, Fields);
    private static void Call(object value, string name, params object[] args) =>
        Call(value, name, args, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static void Call(object value, string name, object[] args, BindingFlags flags)
    {
        try { value.GetType().GetMethod(name, flags)!.Invoke(value, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
    private static Vector4 Vector(byte[] bytes, int at) => new(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at)),
        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 4)), BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 8)),
        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 12)));
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
