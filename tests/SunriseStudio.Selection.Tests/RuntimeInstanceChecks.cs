using System.Numerics;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Checks runtime slot instances: creation, visibility, selectable outline and despawn cleanup. */
internal static partial class Program
{
    /** Runtime generations reuse slots while authored geometry, selection and the static index stay intact. */
    private static void RuntimeInstances()
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, true);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        var authored = new MeshInstance { Mesh = Triangle() };
        var world = new SceneDocument(); world.Instances.Add(authored);
        string original = JsonSerializer.Serialize(world, new JsonSerializerOptions { IncludeFields = true });
        viewport.SetScene(world); viewport.Camera.LookFrom(new(0, -6, 0), Vector3.Zero);
        viewport.RenderImage(320, 240); int grids = viewport.SceneGridBuildCount;
        var local = new SceneDocument { Name = "Runtime triangle" };
        local.Instances.Add(new() { Mesh = Triangle(), Layer = SceneLayer.Objects });
        viewport.RegisterLivePrototype(new(123, local, null, []));
        viewport.SetLiveInstances([new("handle:1", 123, Matrix4x4.CreateTranslation(2, 0, 0))]);
        var first = viewport.TryGetLiveInstance("handle:1")!;
        Require(viewport.LiveInstanceCount == 1 && viewport.LiveSlotCount == 1 && first.Meshes.Count == 1,
            "Runtime slot was not created.");
        viewport.RenderImage(320, 240);
        Require(viewport.VisibleObjectCount == 2 && viewport.SceneGridBuildCount == grids,
            "Runtime slot rebuilt the static grid or stayed invisible.");
        viewport.SetSelection(first.Meshes[0]);
        var outlined = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(outlined.Count(Orange) > 20, "Runtime mesh had no selectable outline.");
        viewport.ClearLiveInstances(); viewport.RenderImage(320, 240);
        Require(viewport.SelectedObject is null && viewport.LiveInstanceCount == 0 && viewport.VisibleObjectCount == 1
            && !viewport.IsVisible(first.Meshes[0]), "Despawn retained selection or visible geometry.");
        viewport.SetLiveInstances([new("handle:2", 123, Matrix4x4.CreateTranslation(0, 2, 0))]);
        var second = viewport.TryGetLiveInstance("handle:2")!;
        Require(viewport.LiveSlotCount == 1 && ReferenceEquals(first.Meshes[0], second.Meshes[0])
            && viewport.TryGetLiveInstance("handle:1") is null, "New generation leaked a slot or retained the old key.");
        viewport.RenderImage(320, 240);
        Require(ReferenceEquals(viewport.Pick(Size / 2, Size), authored), "Runtime mesh behind authored geometry ignored depth.");
        viewport.SetLiveInstances([new("handle:2", 123, Matrix4x4.CreateTranslation(0, -2, 0))]);
        viewport.RenderImage(320, 240);
        Require(ReferenceEquals(viewport.Pick(Size / 2, Size), second.Meshes[0]), "Runtime mesh in front did not share the depth query.");
        Require(world.Instances.Count == 1 && JsonSerializer.Serialize(world, new JsonSerializerOptions { IncludeFields = true }) == original
            && local.Instances[0].PreviewTransform is null && viewport.SceneGridBuildCount == grids,
            "Runtime display changed authored/exported data or its static grid.");
        viewport.SetScene(new SceneDocument());
        Require(viewport.LiveInstanceCount == 0 && viewport.LivePrototypeCount == 0 && viewport.LiveSlotCount == 0,
            "Scene replacement retained runtime resources.");
        const uint limit = AssetViewport.MaximumLivePrototypes;
        for (uint tag = 1; tag <= limit; tag++) viewport.RegisterLivePrototype(new(tag, local, null, []));
        try { viewport.RegisterLivePrototype(new(limit + 1, local, null, [])); throw new InvalidOperationException("Class budget was ignored."); }
        catch (NotSupportedException) { }
        Require(viewport.LivePrototypeCount == limit, "Rejected class partly entered the cache.");
        Console.WriteLine("selection_runtime generations=reused despawn=removed depth=shared source=unchanged grid=stable");
    }

    /** Runtime native groups share the world's buffers and depth without rerecording static opaque draws. */
    private static void RuntimeNative(PackageCatalog catalog)
    {
        Require(catalog.TryGetEntry(0x80C0FA98, out var entry) && entry.Reference == 0x80809C0F,
            "Runtime class fixture is not the native class payload.");
        var world = new AssetSceneLoader(catalog).Load(entry.TagHash);
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, true);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(world);
        viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, world, textureBudgetBytes: 128L * 1024 * 1024));
        var native = viewport.NativeRenderer!; native.AutoExposure = false;
        viewport.RenderImage(320, 240);
        var before = native.SamplePixels(Pixels);
        int staticRecordings = native.OpaqueRecordings, geometry = native.GeometryCount, textures = native.TextureCount,
            parts = native.ReadyPartCount, grids = viewport.SceneGridBuildCount;
        var prototype = LiveScenePrototype.Prepare(catalog, entry.TagHash);
        viewport.RegisterLivePrototype(prototype);
        Require(native.GeometryCount == geometry && native.TextureCount == textures,
            "Prototype registration duplicated the same installed GPU resources.");
        var shift = viewport.Camera.Right * viewport.Camera.Distance * .3f;
        viewport.SetLiveInstances([new("native:1", entry.TagHash, Matrix4x4.CreateTranslation(shift))]);
        var first = viewport.TryGetLiveInstance("native:1")!;
        viewport.SetSelection(first.Meshes.First());
        var image = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        int handles = native.ReadyPartCount;
        Require(handles > parts && native.LastFrame.DrawnParts > 0 && image.Count(Orange) > 3,
            "Runtime native mesh did not enter the shared frame or selection pass.");
        Require(native.OpaqueRecordings == staticRecordings && native.GeometryCount == geometry && native.TextureCount == textures,
            "Runtime spawn rebuilt static opaque draws or uploaded duplicate geometry/textures.");
        var changed = native.SamplePixels(Pixels);
        Require(before.Zip(changed).Any(pair => Vector4.Distance(pair.First.Albedo, pair.Second.Albedo) > .01f),
            "Runtime native mesh did not write the world's G-buffer.");
        viewport.ClearLiveInstances(); viewport.RenderImage(320, 240);
        var restored = native.SamplePixels(Pixels);
        Require(before.Zip(restored).All(pair => Vector4.Distance(pair.First.Albedo, pair.Second.Albedo) < .001f)
            && viewport.SelectedObject is null && native.OpaqueRecordings == staticRecordings,
            "Despawn changed authored G-buffer draws or retained selection.");
        viewport.SetLiveInstances([new("native:2", entry.TagHash, Matrix4x4.CreateTranslation(-shift))]);
        viewport.RenderImage(320, 240);
        Require(native.ReadyPartCount == handles && native.GeometryCount == geometry && native.TextureCount == textures
            && viewport.LiveSlotCount == 1 && viewport.SceneGridBuildCount == grids && world.Instances.All(item => item.PreviewTransform is null),
            "Generation reuse changed static resources, authored fields, or the spatial grid.");
        int revision = viewport.LivePrototypeRevision;
        viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, world, textureBudgetBytes: 128L * 1024 * 1024));
        Require(viewport.LivePrototypeCount == 0 && viewport.LiveSlotCount == 0 && viewport.LiveInstanceCount == 0
            && viewport.LivePrototypeRevision != revision && !viewport.HasLivePrototype(entry.TagHash),
            "Native renderer replacement retained stale prototype handles.");
        viewport.RegisterLivePrototype(LiveScenePrototype.Prepare(catalog, entry.TagHash));
        viewport.SetLiveInstances([new("native:3", entry.TagHash, Matrix4x4.CreateTranslation(shift))]);
        viewport.RenderImage(320, 240);
        Require(viewport.NativeRenderer!.LastFrame.DrawnParts > 0 && viewport.LiveInstanceCount == 1,
            "A fresh prototype could not be adopted after native renderer replacement.");
        Console.WriteLine($"selection_runtime_native parts={handles - parts} shared_geometry={geometry} shared_textures={textures} static_recordings=stable generation=reused gbuffer=shared");
    }
}
