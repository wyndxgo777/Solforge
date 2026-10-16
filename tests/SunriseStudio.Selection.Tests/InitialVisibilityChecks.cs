using System.Numerics;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Packages;
using SunriseStudio.Viewport;

/** Checks that inactive parts are hidden, unpickable and unmasked by default and restored by explicit inspection. */
internal static partial class Program
{
    /** Known inactive parts use one filter for pixels, picking and selection masks. */
    private static void InitialVisibility(bool warp)
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, warp);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false, UseGameShaders = false };
        var inactive = new MeshInstance { Mesh = Triangle(), InitiallyVisible = false, InitialVisibilityReason = "Fixture inactive" };
        var scene = new SceneDocument(); scene.Instances.Add(inactive);
        viewport.SetScene(scene); viewport.Camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        var hidden = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(viewport.VisibleObjectCount == 0 && !viewport.IsVisible(inactive), "Inactive part drew by default.");
        viewport.SelectAt(Size / 2, Size);
        Require(viewport.SelectedObject is null, "Inactive part remained pickable.");
        viewport.SetSelection(inactive);
        var selectedHidden = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(hidden.SequenceEqual(selectedHidden), "Inactive part retained a selection mask.");
        viewport.Visibility.ShowInactiveParts = true;
        var shown = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(viewport.VisibleObjectCount == 1 && shown.Count(Orange) > 100,
            "Explicit inactive inspection did not restore geometry and outline.");
        viewport.SetSelection(null); viewport.SelectAt(Size / 2, Size);
        Require(ReferenceEquals(viewport.SelectedObject, inactive), "Explicit inactive inspection remained unpickable.");
        viewport.Visibility.ShowInactiveParts = false;
        Require(renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels).SequenceEqual(hidden),
            "The prepared visibility cache did not follow the inactive switch.");
        Require(new SceneVisibility().IsVisible(new MeshInstance { Mesh = Triangle(), InitiallyVisible = null }),
            "Unresolved initial state was hidden.");
        Console.WriteLine($"selection_initial_visibility adapter={(warp ? "warp" : "hardware")} default=hidden inspect=visible picking=pass outline=pass unknown=visible");
    }

    /** The same inactive gate filters a prepared native draw and its selection outline. */
    private static void InitialNativeVisibility(PackageCatalog catalog, bool warp)
    {
        var source = new AssetSceneLoader(catalog).Load(0x80B82FE3).Instances
            .First(instance => instance.Mesh.TagHash == 0x80C4424F && instance.Mesh.PartIndex == 0x300);
        var item = new MeshInstance { Mesh = source.Mesh, InitiallyVisible = false,
            ObjectClassTag = source.ObjectClassTag, ModelSourceConfigTag = source.ModelSourceConfigTag,
            ModelSourceResourceClass = source.ModelSourceResourceClass };
        var scene = new SceneDocument { TagHash = 0x80B82FE3 }; scene.Instances.Add(item);
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, warp);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(scene);
        var minimum = item.Mesh.Vertices.Select(vertex => vertex.Position).Aggregate(Vector3.Min);
        var maximum = item.Mesh.Vertices.Select(vertex => vertex.Position).Aggregate(Vector3.Max);
        viewport.Camera.Frame(minimum, maximum, 320f / 240);
        viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 128L * 1024 * 1024));
        Require(viewport.NativePartCount > 0, "Owned inactive test did not prepare a native shader part.");
        viewport.SetSelection(item);
        var hidden = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(viewport.NativeRenderer!.LastFrame.DrawnParts == 0 && hidden.Count(Orange) == 0,
            "Known inactive native geometry or outline was submitted.");
        viewport.Visibility.ShowInactiveParts = true;
        var shown = renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels);
        Require(viewport.NativeRenderer.LastFrame.DrawnParts > 0 && shown.Count(Orange) > 5,
            "Explicit inactive inspection did not restore the native draw and outline.");
        viewport.Visibility.ShowInactiveParts = false;
        viewport.RenderImage(320, 240);
        Require(viewport.NativeRenderer.LastFrame.DrawnParts == 0,
            "Native recorded draws retained an inactive part after the toggle.");
        Console.WriteLine($"selection_initial_native adapter={(warp ? "warp" : "hardware")} hidden=pass inspect=pass outline=pass recorded_filter=pass");
    }
}
