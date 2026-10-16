using System.Numerics;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Rendering regression checks for interaction: opaque replay and cached draws must match reference pixels. */
internal static partial class Program
{
    /** Draw reuse must keep live shader buffers and obey visibility, stage, and pose changes. */
    private static void OpaqueCache(Dx11Renderer renderer, AssetViewport viewport, object selected)
    {
        var native = viewport.NativeRenderer!;
        bool auto = native.AutoExposure;
        float stops = native.ViewerStops;
        native.ViewerStops = native.AppliedViewerStops;
        native.AutoExposure = false;
        native.CacheOpaqueDraws = false;
        var reference = Read(.25);
        int recordings = native.OpaqueRecordings;
        native.CacheOpaqueDraws = true;
        var recordedImage = Read(.25);
        Require(recordedImage.SequenceEqual(reference) && native.OpaqueRecordings == recordings,
            $"Opaque replay changed pixels={recordedImage.Zip(reference).Count(pair => pair.First != pair.Second)} "
            + $"opaque={recordings}->{native.OpaqueRecordings} failed={native.LastFrame.FailedParts}.");
        var later = Read(.75);
        Require(native.OpaqueRecordings == recordings && native.LastFrame.MaterialEvaluations > 0,
            "Cached draws stopped frame-dependent material updates.");
        native.CacheOpaqueDraws = false;
        Require(Read(.75).SequenceEqual(later), "Live material buffers differ between cached and rebuilt draws.");
        native.CacheOpaqueDraws = true;
        viewport.Camera.Orbit(8, 2);
        var orbit = Read(.75);
        native.CacheOpaqueDraws = false;
        Require(Read(.75).SequenceEqual(orbit), "Orbit changed cached camera or transparent draw ordering.");
        native.CacheOpaqueDraws = true;
        recordings = native.OpaqueRecordings;
        viewport.Visibility.Filter = item => !ReferenceEquals(item, selected);
        Read(.75);
        Require(native.OpaqueRecordings > recordings, "Visibility did not invalidate cached draws.");
        viewport.Visibility.Filter = null;
        recordings = native.OpaqueRecordings;
        native.EnabledStages.Remove(0); Read(.75);
        Require(native.OpaqueRecordings > recordings, "Stage removal did not invalidate cached draws.");
        native.EnabledStages.Add(0); Read(.75);
        var resized = Read(.75, 376, 264);
        native.CacheOpaqueDraws = false;
        Require(Read(.75, 376, 264).SequenceEqual(resized), "Cached descriptors changed resized pixels.");
        native.CacheOpaqueDraws = true; Read(.75);
        native.AutoExposure = auto; native.ViewerStops = stops;
        Console.WriteLine("selection_opaque_cache image=pass time=live orbit=pass visibility=pass stages=pass resize=pass");

        Vector4[] Read(double time, int width = 320, int height = 240)
        {
            native.RequestExposureWarmup();
            return renderer.ReadTexturePixels(viewport.RenderImage(width, height, time), Pixels);
        }
    }

    /** Hidden overlays cannot alter scene pixels; only selection and the cursor bypass depth. */
    private static void OverlayInteraction(bool warp)
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, warp);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        var foreground = new MeshInstance { Mesh = Triangle(), Layer = SceneLayer.Objects,
            Transform = Matrix4x4.CreateScale(4) * Matrix4x4.CreateTranslation(0, -1, 0) };
        var scene = new SceneDocument(); scene.Instances.Add(foreground);
        viewport.SetScene(scene); viewport.Camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        var baseline = Read();
        var marker = new SceneMarker { Name = "Placement", Position = Vector3.Zero,
            Extents = new(.3f), Layer = SceneLayer.Objects, Color = new(1, 0, 1, 1) };
        var volume = new MeshInstance { Mesh = Triangle(), Layer = SceneLayer.Volumes,
            Transform = Matrix4x4.CreateScale(.4f) };
        var point = new ScenePointProxy("point", "Light", new(.5f, 0, 0), new(.2f));
        scene.Markers.Add(marker); scene.Instances.Add(volume);
        viewport.SetScene(scene); viewport.AddPointProxy(point);
        viewport.Camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        Require(Read().SequenceEqual(baseline), "Occluded placement, volume, or point changed scene pixels.");
        Require(ReferenceEquals(viewport.HoverAt(Size / 2, Size), foreground),
            "A hidden placement stole the foreground hover name.");
        viewport.OccludeOverlays = false;
        Require(Changed(baseline, Read()) > 60, "Overlay depth toggle did not expose hidden lines.");
        viewport.OccludeOverlays = true;
        foreach (object item in new object[] { marker, volume, point })
        {
            viewport.SetSelection(item);
            var selected = Read();
            Require(selected.Count(Orange) > 20 && Changed(baseline, selected) > 30,
                "A selected overlay disappeared behind scene geometry.");
            var edge = item == marker ? new Vector2(145, 120) : item == volume
                ? new Vector2(170, 120) : new Vector2(186, 120);
            Require(ReferenceEquals(viewport.Pick(edge, Size), item),
                "A selected overlay edge could not be picked through geometry.");
        }
        viewport.SetSelection(null);
        viewport.Visibility.HiddenOverlayLayers.UnionWith([SceneLayer.Objects, SceneLayer.Volumes]);
        viewport.Visibility.HiddenGeometryLayers.Add(SceneLayer.Volumes);
        Require(Read().SequenceEqual(baseline) && viewport.VisibleObjectCount == 1,
            "Box and volume filters hid object geometry or retained an overlay.");
        viewport.Visibility.HiddenOverlayLayers.Clear(); viewport.Visibility.HiddenGeometryLayers.Clear();
        viewport.Visibility.HiddenGeometryLayers.Add(SceneLayer.Objects);
        Read(); Require(viewport.VisibleObjectCount == 3, "Object geometry filter also hid placement boxes.");
        viewport.Visibility.HiddenGeometryLayers.Clear();
        viewport.Visibility.HiddenOverlayLayers.Add(SceneLayer.Objects);
        Read(); Require(viewport.VisibleObjectCount == 2, "Placement filter also hid a volume mesh.");
        viewport.Visibility.HiddenOverlayLayers.Clear(); viewport.Visibility.HiddenOverlayLayers.Add(SceneLayer.Volumes);
        Read(); Require(viewport.VisibleObjectCount == 4,
            "Same-size box filter replacement retained stale visibility, or a volume box hid its shape.");
        viewport.Visibility.HiddenOverlayLayers.Clear();

        var front = new SceneMarker { Name = "Front placement", Position = new(0, -2, 0),
            Extents = new(.3f), Layer = SceneLayer.Objects, Color = new(1, 0, 1, 1) };
        scene.Markers.Clear(); scene.Markers.Add(front); scene.Instances.Remove(volume);
        viewport.SetScene(scene); viewport.Camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        Require(Changed(baseline, Read()) > 30, "Visible placement lines were depth-culled.");
        var edgePixel = new Vector2(128, 120);
        Require(ReferenceEquals(viewport.Pick(edgePixel, Size), front), "Front placement edge was not pickable.");
        for (int y = 20; y < 240; y += 19)
            for (int x = 20; x < 320; x += 23)
                Require(ReferenceEquals(viewport.Pick(new(x, y), Size),
                    viewport.Selection.Pick(viewport.Camera, new(x, y), Size, viewport.Visibility)),
                    "Spatial picking differs from the full nearest-hit test.");
        viewport.SelectAt(edgePixel, Size);
        Require(ReferenceEquals(viewport.SelectedObject, front), "First click did not select a placement.");
        viewport.SelectAt(edgePixel, Size);
        Require(viewport.SelectedObject is null, "Second click did not deselect the same placement.");
        viewport.SelectAt(edgePixel, Size, focus: true);
        Require(ReferenceEquals(viewport.SelectedObject, front) && viewport.Camera.Pivot == front.Position,
            "Double-click did not restore and focus the selection.");

        var containing = new SceneMarker { Extents = new(20), Layer = SceneLayer.Volumes };
        scene.Markers.Clear(); scene.Markers.Add(containing);
        viewport.SetScene(scene); viewport.Camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        Require(ReferenceEquals(viewport.Pick(Size / 2, Size), foreground),
            "A volume containing the camera stole the geometry hit.");
        viewport.SetSelection(containing);
        Require(ReferenceEquals(viewport.Pick(Size / 2, Size), foreground),
            "A selected containing volume intercepted its empty interior.");

        scene.Markers.Clear(); viewport.SetScene(scene);
        viewport.Camera.LookFrom(new(3, -4, 2), Vector3.Zero);
        var noCursor = Read(); viewport.ShowCursor = true;
        var cursor = Read();
        Require(Changed(noCursor, cursor) > 60, "Depth hid the 3D cursor.");
        Require(cursor.Count(p => p.X > .95f && p.Y < .3f && p.Z < .3f) > 8 &&
            cursor.Count(p => p.Y > .95f && p.X < .35f && p.Z < .45f) > 8 &&
            cursor.Count(p => p.Z > .95f && p.X < .4f && p.Y < .7f) > 8,
            "Cursor did not render all three world axes.");
        viewport.ShowCursor = false;
        Require(Read().SequenceEqual(noCursor), "Cursor toggle left pixels in the scene image.");
        viewport.Camera.Orbit(50, 0); var orbitBase = Read(); viewport.ShowCursor = true;
        var orbitCursor = Read();
        Require(!cursor.Select((p, i) => p != noCursor[i]).SequenceEqual(
            orbitCursor.Select((p, i) => p != orbitBase[i])), "Cursor remained a flat reticle while orbiting.");
        Console.WriteLine($"selection_interaction adapter={(warp ? "warp" : "hardware")} depth=pass layers=pass hover=pass toggle=pass cursor_3d=pass");

        Vector4[] Read() => renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        static int Changed(Vector4[] before, Vector4[] after) => before.Where((p, i) => p != after[i]).Count();
    }
}
