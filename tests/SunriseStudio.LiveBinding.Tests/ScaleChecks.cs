using System.Numerics;
using System.Text.Json;
using SunriseStudio.Editor;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Checks observed scale handling: observed scale replaces (not multiplies) authored scale and reaches markers, bounds, geometry and picking. */
internal static partial class Program
{
    /** World scale replaces authored scale while child offsets and source fields stay intact. */
    private static void ObservedScale(AssetViewport viewport, Dx11Renderer renderer)
    {
        var authored = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(100, 0, 0);
        var local = Matrix4x4.CreateTranslation(1, 0, 0);
        var placement = new SceneObjectPlacement(List, 0, authored) { EntryIndex = 0 };
        var mesh = new MeshInstance { Mesh = Triangle(), Transform = local * authored, ObjectPlacement = placement };
        var marker = new SceneMarker { Position = new(102, 0, 0), Extents = new(2, 4, 6), ObjectPlacement = placement,
            Layer = SceneLayer.Spawns };
        var scene = new SceneDocument(); scene.Instances.Add(mesh); scene.Markers.Add(marker);
        string original = JsonSerializer.Serialize(scene, new JsonSerializerOptions { IncludeFields = true });
        viewport.SetScene(scene); viewport.Visibility.HiddenOverlayLayers.Add(SceneLayer.Spawns);
        var binding = new LiveSceneBinding(WorldDocument.FromScene(scene));
        foreach (float scale in new[] { 3f, -2f, 0f })
        {
            var row = Row(0) with { Scale = scale };
            binding.Apply(Snapshot([row]), viewport, Now);
            Check(Near(mesh.RenderTransform, local * Matrix4x4.CreateScale(scale)), "Observed scale was multiplied by authored scale.");
            Check(Vector3.Distance(marker.RenderPosition, new(scale, 0, 0)) < 1e-5f
                && Vector3.Distance(marker.RenderExtents, new Vector3(1, 2, 3) * MathF.Abs(scale)) < 1e-5f,
                "Marker position or extents did not follow observed world scale.");
            viewport.SetSelection(mesh);
            Check(MathF.Abs(viewport.SelectedBounds.Radius - MathF.Sqrt(2) * MathF.Abs(scale)) < 1e-4f,
                "Selected bounds ignored zero or negative scale.");
            viewport.SetSelection(null);
            viewport.Camera.LookFrom(new(scale, -8, 0), new(scale, 0, 0));
            var image = viewport.RenderImage(320, 240);
            bool visible = renderer.ReadTexturePixel(image, 160, 120).X > .2f;
            Check(visible == (scale != 0) && (viewport.Pick(new(160, 120), new(320, 240)) is MeshInstance) == (scale != 0),
                "Signed scale did not reach actual GPU geometry and picking.");
            binding.Apply(Snapshot([row with { TransformAvailable = false, Scale = null, Time = 0 }]), viewport, Now + 10);
            Check(Near(mesh.RenderTransform, local * Matrix4x4.CreateScale(scale)), "Failed read lost the retained scale.");
        }
        viewport.ClearLiveTransforms(); viewport.Visibility.HiddenOverlayLayers.Clear();
        Check(mesh.PreviewTransform is null && marker.PreviewExtents is null && marker.RenderExtents == marker.Extents
            && original == JsonSerializer.Serialize(scene, new JsonSerializerOptions { IncludeFields = true }),
            "Scale preview changed authored fields or survived clearing.");
        cases++;
    }
}
