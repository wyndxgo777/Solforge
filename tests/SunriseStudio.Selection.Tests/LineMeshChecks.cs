using System.Numerics;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Checks line mesh drawing: edits, persistent buffers, camera-only motion and resize must not change pixels or re-upload unchanged lines. */
internal static partial class Program
{
    /** Only equal byte sequences reuse an upload; empty streams suppress the prior draw. */
    private static void LineStreamEdits(Dx11Renderer renderer)
    {
        using var mesh = renderer.CreateLineMesh();
        using var target = renderer.CreateNativeTargets(320, 240, Vortice.DXGI.Format.R8G8B8A8_UNorm);
        LineVertex[] vertices = [new(new(-.7f, 0, .3f), new(1, 0, 0, 1)), new(new(.7f, 0, .3f), new(1, 0, 0, 1)),
            new(new(0, -.6f, .4f), new(0, 1, 0, 1)), new(new(0, .6f, .4f), new(0, 1, 0, 1))];
        void Compare(LineVertex[] data, Matrix4x4 view, int uploads)
        {
            renderer.BeginOffscreen(target, Vector4.Zero); renderer.DrawLines(data, view);
            var expected = renderer.ReadTexturePixels(target.Textures[0], Pixels);
            renderer.BeginOffscreen(target, Vector4.Zero); renderer.DrawLines(mesh, data, view);
            var actual = renderer.ReadTexturePixels(target.Textures[0], Pixels);
            Require(expected.SequenceEqual(actual) && mesh.Uploads == uploads && mesh.Count == data.Length,
                "Line stream edits changed pixels, count or upload reuse.");
        }
        Compare(vertices, Matrix4x4.Identity, 1);
        Compare(vertices, Matrix4x4.CreateRotationZ(.2f), 1);
        vertices[0] = vertices[0] with { Position = new(-.5f, -.1f, .3f) };
        Compare(vertices, Matrix4x4.Identity, 2);
        vertices[1] = vertices[1] with { Color = new(0, 0, 1, 1) };
        Compare(vertices, Matrix4x4.Identity, 3);
        Array.Reverse(vertices); Compare(vertices, Matrix4x4.Identity, 4);
        var shorter = vertices[..2]; Compare(shorter, Matrix4x4.Identity, 5);
        Compare([], Matrix4x4.Identity, 5); Compare(shorter, Matrix4x4.Identity, 5);
        shorter[0] = shorter[0] with { Color = new(BitConverter.Int32BitsToSingle(int.MinValue), 1, 0, 1) };
        Compare(shorter, Matrix4x4.Identity, 6);
        renderer.RestoreMainTarget();
    }

    /** Persistent line streams preserve every overlay pixel across transform, depth, selection and layer changes. */
    private static void LineMeshes(bool warp)
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, warp);
        using var viewport = new AssetViewport(renderer) { UseGameShaders = false };
        var scene = new SceneDocument();
        scene.Instances.Add(new() { Mesh = Triangle(), Transform = Matrix4x4.CreateScale(3) * Matrix4x4.CreateTranslation(0, -.4f, 0) });
        for (int index = 0; index < 48; index++)
            scene.Instances.Add(new() { Mesh = Triangle(), Layer = SceneLayer.Volumes,
                Transform = Matrix4x4.CreateScale(index % 2 == 0 ? .18f : -.18f, .2f, .3f)
                    * Matrix4x4.CreateRotationZ(index * .07f)
                    * Matrix4x4.CreateTranslation((index % 8 - 4) * .5f, index / 8 * .3f - .9f, .15f) });
        scene.Markers.Add(new() { Position = new(-.5f, -.8f, .1f), Extents = new(.25f), Layer = SceneLayer.Objects });
        scene.Markers.Add(new() { Position = new(.7f, .8f, 0), Extents = new(.4f), Layer = SceneLayer.Volumes });
        for (int index = 0; index < 320; index++)
            scene.Markers.Add(new() { Position = new((index % 20 - 10) * .12f, (index / 20 - 8) * .12f, .2f),
                Extents = new(.015f), Layer = SceneLayer.Objects });
        viewport.SetScene(scene);
        viewport.AddPointProxy(new("diagnostic", "Point", new(.6f, -.7f, .2f), new(.2f)));
        viewport.Camera.LookFrom(new(3, -6, 3), Vector3.Zero);
        for (int index = 0; index < 16; index++)
        {
            viewport.Camera.Orbit(1, .3f);
            viewport.OccludeOverlays = (index & 1) == 0;
            viewport.ShowGrid = (index & 2) == 0;
            viewport.ShowCursor = (index & 4) == 0;
            viewport.Visibility.HiddenOverlayLayers.Clear();
            if ((index & 8) != 0) viewport.Visibility.HiddenOverlayLayers.Add(SceneLayer.Objects);
            viewport.SetSelection(index % 3 == 0 ? scene.Instances[1] : index % 3 == 1 ? scene.Markers[0] : null);
            viewport.CacheOverlayLines = false;
            int before = renderer.DrawCalls;
            var reference = renderer.ReadTexturePixels(viewport.RenderImage(320, 240, .4), Pixels);
            int draws = renderer.DrawCalls - before;
            viewport.CacheOverlayLines = true;
            before = renderer.DrawCalls;
            var pooled = renderer.ReadTexturePixels(viewport.RenderImage(320, 240, .4), Pixels);
            Require(pooled.SequenceEqual(reference) && renderer.DrawCalls - before == draws,
                "Persistent line buffers changed overlay pixels or draw count.");

        }
        viewport.ShowGrid = viewport.ShowCursor = true;
        viewport.Visibility.HiddenOverlayLayers.Clear();
        viewport.SetSelection(scene.Markers[0]);
        viewport.RenderImage(320, 240, .4);
        int uploads = viewport.OverlayLineUploads;
        viewport.Camera.Orbit(1, .2f);
        viewport.RenderImage(320, 240, .4);
        Require(viewport.OverlayLineUploads == uploads, "Camera-only motion uploaded unchanged lines.");
        var resizedPixels = Enumerable.Range(0, 264).SelectMany(y => Enumerable.Range(0, 352).Select(x => (x, y))).ToArray();
        var resized = renderer.ReadTexturePixels(viewport.RenderImage(352, 264, .4), resizedPixels);
        viewport.CacheOverlayLines = false;
        Require(renderer.ReadTexturePixels(viewport.RenderImage(352, 264, .4), resizedPixels).SequenceEqual(resized),
            "Viewport resize retained stale line data or changed pixels.");
        viewport.CacheOverlayLines = true;
        viewport.SetScene(scene);
        Require(viewport.OverlayLineUploads == 0, "Scene replacement retained line resources.");
        viewport.RenderImage(352, 264, .4);
        Require(viewport.OverlayLineUploads > 0, "A new scene did not upload its line data.");
        LineStreamEdits(renderer);
        Console.WriteLine($"line_mesh warp={warp} pixels=exact depth=pass selection=pass layers=pass transforms=pass persistent=exact draws=unchanged");
    }
}
