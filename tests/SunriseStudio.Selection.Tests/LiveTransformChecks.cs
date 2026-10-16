using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Checks that live-transformed meshes leave their authored grid cell for culling, camera, outline and framing. */
internal static partial class Program
{
    /** Live snapshots move visible geometry and hit tests while authored fields remain byte-stable. */
    private static void LiveTransforms()
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, true);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        var mesh = new MeshInstance { Mesh = Triangle(), Transform = Matrix4x4.CreateTranslation(100, 0, 0) };
        var marker = new SceneMarker { Position = new(100, 0, 0), Extents = new(.1f), Layer = SceneLayer.Spawns };
        var scene = new SceneDocument(); scene.Instances.Add(mesh); scene.Markers.Add(marker);
        var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
        string original = JsonSerializer.Serialize(scene, jsonOptions);
        var authoredBounds = scene.GetBounds();
        viewport.SetScene(scene); viewport.Camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        viewport.RenderImage(320, 240); int builds = viewport.SceneGridBuildCount;
        Require(viewport.Pick(Size / 2, Size) is null, "Authored distant mesh entered the live camera.");
        viewport.SetLiveTransforms(new Dictionary<MeshInstance, Matrix4x4> { [mesh] = Matrix4x4.Identity },
            new Dictionary<SceneMarker, (Vector3, Quaternion)>());
        Require(ReferenceEquals(viewport.Pick(Size / 2, Size), mesh), "Live mesh stayed in its authored grid cell.");
        var image = viewport.RenderImage(320, 240);
        Require(renderer.ReadTexturePixel(image, 160, 120).X > .2f && viewport.VisibleObjectCount == 1,
            "Live mesh was culled at its authored position.");
        viewport.SetSelection(mesh);
        var selected = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(selected.Count(Orange) > 20 && viewport.SelectedBounds.Center.X == 0, "Live outline or framing stayed authored.");
        for (int i = 0; i < 8; i++)
        {
            viewport.SetLiveTransforms(new Dictionary<MeshInstance, Matrix4x4> { [mesh] = Matrix4x4.CreateTranslation(i / 20f, 0, 0) },
                new Dictionary<SceneMarker, (Vector3, Quaternion)>());
            viewport.RenderImage(320, 240); viewport.Pick(Size / 2, Size);
        }
        Require(viewport.SceneGridBuildCount == builds, "Live snapshots rebuilt the full scene grid.");
        viewport.SetLiveTransforms(new Dictionary<MeshInstance, Matrix4x4>(),
            new Dictionary<SceneMarker, (Vector3, Quaternion)> { [marker] = (Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .5f)) });
        viewport.SetSelection(marker); image = viewport.RenderImage(320, 240);
        Require(ReferenceEquals(viewport.Pick(Size / 2, Size), marker) && viewport.SelectedBounds.Center.Length() < .001,
            "Live marker bounds or hit test stayed authored.");
        Require(renderer.ReadTexturePixels(image, Pixels).Count(Orange) > 5, "Live marker lines or outline stayed authored.");
        Require(mesh.Transform.Translation.X == 100 && marker.Position.X == 100 && scene.GetBounds() == authoredBounds
            && JsonSerializer.Serialize(scene, jsonOptions) == original, "Live preview changed exported scene fields.");
        var prior = marker.PreviewPose;
        try
        {
            viewport.SetLiveTransforms(new Dictionary<MeshInstance, Matrix4x4> { [new() { Mesh = Triangle() }] = Matrix4x4.Identity },
                new Dictionary<SceneMarker, (Vector3, Quaternion)>());
            throw new InvalidOperationException("Foreign scene member was accepted.");
        }
        catch (ArgumentException) { }
        Require(marker.PreviewPose == prior && viewport.LiveTransformCount == 1, "Rejected snapshot partly cleared live state.");
        viewport.ClearLiveTransforms(); viewport.SetSelection(null); viewport.RenderImage(320, 240);
        Require(mesh.PreviewTransform is null && marker.PreviewPose is null && viewport.VisibleObjectCount == 0
            && viewport.Pick(Size / 2, Size) is null && viewport.SceneGridBuildCount == builds,
            "Clearing live data did not restore authored placement.");
        viewport.SetLiveTransforms(new Dictionary<MeshInstance, Matrix4x4> { [mesh] = Matrix4x4.Identity },
            new Dictionary<SceneMarker, (Vector3, Quaternion)>());
        viewport.SetScene(new SceneDocument());
        Require(mesh.PreviewTransform is null && viewport.LiveTransformCount == 0, "Scene replacement retained old live fields.");
        LivePaletteConstants();
        Console.WriteLine("selection_live grid=stable geometry=pass markers=pass outline=pass authored=unchanged clear=pass");
    }

    private static void LivePaletteConstants()
    {
        byte[] header = new byte[0x80]; BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(0x6C), 1);
        var native = new NativeMeshData { Kind = NativeMeshKind.Entity, VertexStreams = [], IndexData = [], SourceHeader = header };
        var mesh = new MeshInstance { Mesh = new MeshData { Native = native } };
        var current = Matrix4x4.CreateRotationZ(.5f) * Matrix4x4.CreateTranslation(4, 5, 6);
        mesh.PreviewTransform = current;
        Require(AssetSceneLoader.ReadNativeEntityConstants(mesh)[3] == new Vector4(4, 5, 6, 1), "Rigid CB11 ignored live placement.");
        var skin = Matrix4x4.CreateTranslation(1, 2, 3);
        var palette = NativeEntityPalettes.BuildSkinned(mesh, NativeEntityPalette.Matrix, [skin]);
        var expected = skin * current;
        Require(palette[8] == new Vector4(expected.M11, expected.M21, expected.M31, expected.M41)
            && palette[9] == new Vector4(expected.M12, expected.M22, expected.M32, expected.M42), "Bone and live transforms were not composed.");
        _ = NativeEntityPalettes.BuildSkinned(mesh, NativeEntityPalette.DualQuaternion, [skin]);
        foreach (float scale in new[] { 0f, -2f, 3f })
        {
            mesh.PreviewTransform = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(4, 5, 6);
            var matrixRows = NativeEntityPalettes.BuildSkinned(mesh, NativeEntityPalette.Matrix, [skin]);
            var composed = skin * mesh.RenderTransform;
            Require(matrixRows[8] == new Vector4(composed.M11, composed.M21, composed.M31, composed.M41)
                && matrixRows[10] == new Vector4(composed.M13, composed.M23, composed.M33, composed.M43),
                "Matrix palette changed signed or zero scale.");
            Require(!NativeEntityPalettes.RequiresDiagnosticPlacement(mesh.RenderTransform, NativeEntityPalette.Matrix)
                && NativeEntityPalettes.RequiresDiagnosticPlacement(mesh.RenderTransform, NativeEntityPalette.DualQuaternion) == (scale <= 0),
                "Representable matrix placement was sent to a diagnostic shader.");
        }
        Require(mesh.Transform == Matrix4x4.Identity, "Palette generation mutated authored placement.");
    }

    /** Installed entity shaders must move with the live placement while the animated pose stays active. */
    private static void LiveNative(PackageCatalog catalog)
    {
        var scene = new AssetSceneLoader(catalog).Load(0x80C0FA98);
        var source = AssetAnimationSource.Prepare(catalog, 0x80C0FA98);
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, true);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(scene); viewport.SetAnimation(source);
        var plan = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 128L * 1024 * 1024);
        viewport.SetNativeScene(plan);
        viewport.NativeRenderer!.AutoExposure = false; viewport.NativeRenderer.ViewerStops = 0;
        viewport.Animation!.SetClip(source.ReadClip(0x80FE2C08), false); viewport.Animation.Seek(.333333f);
        viewport.SetSelection(FindPick(viewport));
        var before = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        var beforeConstants = LiveConstants(viewport.NativeRenderer!);
        var beforeGbuffer = viewport.NativeRenderer!.SamplePixels(Pixels);
        int recordings = viewport.NativeRenderer!.OpaqueRecordings, builds = viewport.SceneGridBuildCount;
        var shift = viewport.Camera.Right * viewport.SelectedBounds.Radius;
        var originals = scene.Instances.ToDictionary(item => item, item => item.Transform);
        var live = originals.ToDictionary(pair => pair.Key, pair => pair.Value * Matrix4x4.CreateTranslation(shift));
        viewport.SetLiveTransforms(live, new Dictionary<SceneMarker, (Vector3, Quaternion)>());
        var after = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(viewport.UseGameShaders && viewport.NativeRenderer.OpaqueRecordings > recordings
            && before.Where((pixel, i) => Vector4.Distance(pixel, after[i]) > .05f).Count() > 100,
            "Native entity constants did not move the posed model.");
        Require(viewport.SceneGridBuildCount == builds && scene.Instances.All(item => item.Transform == originals[item]),
            "Native live update changed source fields or rebuilt the static grid.");
        viewport.ClearLiveTransforms();
        var restored = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        var restoredConstants = LiveConstants(viewport.NativeRenderer!);
        Require(beforeConstants.SequenceEqual(restoredConstants), "Clearing native placement changed palette vectors.");
        var restoredGbuffer = viewport.NativeRenderer!.SamplePixels(Pixels);
        Require(beforeGbuffer.Zip(restoredGbuffer).All(pair => Vector4.Distance(pair.First.Albedo, pair.Second.Albedo) < .001f
            && Vector4.Distance(pair.First.Third, pair.Second.Third) < .001f)
            && before.Select(Orange).SequenceEqual(restored.Select(Orange)),
            "Clearing native live placement did not preserve the active pose.");
        foreach (float scale in new[] { -1f, 0f })
        {
            viewport.SetLiveTransforms(originals.ToDictionary(pair => pair.Key,
                pair => pair.Value * Matrix4x4.CreateScale(scale)), new Dictionary<SceneMarker, (Vector3, Quaternion)>());
            foreach (var group in plan.Groups.Where(group => group.Material.Palette == NativeEntityPalette.Matrix))
                Require(group.Parts.All(part => viewport.NativeRenderer.SupportsLiveTransform(part.Instance)),
                    "A native matrix source unnecessarily lost its shader under signed scale.");
            viewport.SetSelection(scene.Instances.First());
            Require(viewport.SelectedBounds.IsFinite && (scale != 0 || viewport.SelectedBounds.Radius == 0),
                "Native scale left stale selected bounds.");
            viewport.SetSelection(null);
            if (scale != 0) viewport.Camera.Frame(scene.Instances.SelectMany(instance => instance.Mesh.Vertices
                    .Select(vertex => Vector3.Transform(vertex.Position, instance.RenderTransform)))
                .Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min),
                scene.Instances.SelectMany(instance => instance.Mesh.Vertices.Select(vertex => Vector3.Transform(vertex.Position, instance.RenderTransform)))
                .Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max));
            var scaledImage = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
            if (scale == 0)
                Require(viewport.NativeRenderer.SamplePixels(Pixels).All(pixel => pixel.Albedo.LengthSquared() < 1e-10f)
                    && viewport.Pick(Size / 2, Size) is null, "Collapsed native geometry still rendered or picked.");
            else Require(FindPick(viewport) is not null && scaledImage.Any(pixel => pixel.X > .2f),
                "Reflected installed geometry was replaced or disappeared.");
        }
        viewport.ClearLiveTransforms();
        Require(scene.Instances.All(item => item.Transform == originals[item]), "Signed native preview changed source transforms.");
        Console.WriteLine("selection_live_native pose=preserved cb11=updated source=unchanged restore=pass");
    }

    private static Vector4[] LiveConstants(NativeSceneRenderer renderer)
    {
        var groups = (System.Collections.IEnumerable)typeof(NativeSceneRenderer).GetField("groups",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(renderer)!;
        var values = new List<Vector4>();
        foreach (object group in groups) values.AddRange((Vector4[])group.GetType().GetProperty("Instances")!.GetValue(group)!);
        return values.ToArray();
    }
}
