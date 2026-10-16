using System.Numerics;
using System.Text.Json;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Checks that live poses transform per-instance geometry without altering shared meshes, grid bounds or picking, and that rejected poses change nothing. */
internal static class GeometryChecks
{
    private static readonly Vector2 Size = new(320, 240);
    private static readonly (int X, int Y)[] Pixels = Enumerable.Range(0, 240)
        .SelectMany(y => Enumerable.Range(0, 320).Select(x => (x, y))).ToArray();

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run()
    {
        Cpu();
        foreach (bool warp in new[] { false, true }) Gpu(warp);
        Console.WriteLine("live_pose_geometry shared_mesh=isolated legacy_pose=retained world_bounds=pass picking=pass outline=pass grid=stable slot_reuse=cleared");
    }

    /** A live instance override takes precedence over, but never replaces, the shared asset pose. */
    private static void Cpu()
    {
        var mesh = Triangle();
        var first = new MeshInstance { Mesh = mesh, Transform = Matrix4x4.CreateTranslation(100, 0, 0) };
        var sibling = new MeshInstance { Mesh = mesh, Transform = Matrix4x4.CreateTranslation(20, 0, 0) };
        var scene = new SceneDocument(); scene.Instances.AddRange([first, sibling]);
        var index = new SceneSelection(); index.SetScene(scene);
        var animated = mesh.Vertices.Select(vertex => vertex.Position + new Vector3(3, 0, 0)).ToArray();
        index.SetPose(mesh, animated);
        var before = index.GetAuthoredBounds(first);
        var positions = mesh.Vertices.Select(vertex => vertex.Position).ToArray();
        index.SetInstancePose(first, positions);
        positions[0] += new Vector3(400, 0, 0);
        Check(index.GetBounds(first).Center == Vector3.Zero && index.GetBounds(sibling).Center.X == 23,
            "Per-instance pose changed shared-mesh bounds or retained caller-owned positions.");
        Check(index.GetAuthoredBounds(first) == before, "Live pose changed authored grid bounds.");
        var camera = new ViewerCamera(); camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        Check(ReferenceEquals(index.Pick(camera, Size / 2, Size, new()), first), "World-space posed triangle was not pickable.");
        Reject(() => index.SetInstancePose(first, [new(float.NaN), Vector3.Zero, Vector3.Zero]));
        Reject(() => index.SetInstancePose(new() { Mesh = mesh }, animated));
        Check(index.GetBounds(first).Center == Vector3.Zero, "Rejected pose changed existing bounds.");
        index.SetInstancePose(first, null);
        Check(index.GetBounds(first) == before && index.GetBounds(sibling).Center.X == 23,
            "Clearing live pose lost the active shared animation.");
        Check(mesh.Vertices[0].Position == new Vector3(-1, 0, -1) && first.Transform.Translation.X == 100,
            "Picking pose changed source geometry or placement.");
    }

    /** Posed geometry, selection masks and cull bounds must refer to the same world-space snapshot. */
    private static void Gpu(bool warp)
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, warp);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false,
            SelectionMode = SceneSelectionMode.MeshPart, UseGameShaders = false };
        var mesh = Triangle();
        var first = new MeshInstance { Mesh = mesh, Transform = Matrix4x4.CreateTranslation(100, 0, 0) };
        var sibling = new MeshInstance { Mesh = mesh, Transform = Matrix4x4.CreateTranslation(20, 0, 0) };
        var scene = new SceneDocument(); scene.Instances.AddRange([first, sibling]);
        var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
        string original = JsonSerializer.Serialize(scene, jsonOptions);
        viewport.SetScene(scene); viewport.Camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        viewport.RenderImage(320, 240); int grid = viewport.SceneGridBuildCount;
        var vertices = World(mesh, Matrix4x4.Identity);
        viewport.SetLivePoseGeometry(new Dictionary<MeshInstance, RenderVertex[]> { [first] = vertices });
        Check(ReferenceEquals(viewport.Pick(Size / 2, Size), first), "Pose movement stayed in its original pick cell.");
        var image = viewport.RenderImage(320, 240);
        Check(renderer.ReadTexturePixel(image, 160, 120).X > .2f && viewport.VisibleObjectCount == 1,
            "Diagnostic pose did not move into view or animated a sibling.");
        viewport.SetSelection(first);
        Check(renderer.ReadTexturePixels(viewport.RenderImage(320, 240), Pixels).Count(Orange) > 15,
            "Selected pose mask stayed in the source placement.");
        Check(viewport.SelectedBounds.Center == Vector3.Zero && viewport.Selection.GetBounds(sibling).Center.X == 20,
            "Framing moved the wrong placement.");
        for (int i = 0; i < 5; i++)
        {
            viewport.SetLivePoseGeometry(new Dictionary<MeshInstance, RenderVertex[]> { [first] = World(mesh, Matrix4x4.CreateTranslation(i * .05f, 0, 0)) });
            viewport.RenderImage(320, 240);
        }
        var prior = viewport.SelectedBounds;
        var invalid = vertices.ToArray(); invalid[0] = invalid[0] with { TexCoord = Vector2.One };
        Reject(() => viewport.SetLivePoseGeometry(new Dictionary<MeshInstance, RenderVertex[]> { [first] = vertices, [sibling] = invalid }));
        Check(viewport.LivePoseGeometryCount == 1 && viewport.SelectedBounds == prior, "Rejected geometry snapshot partially published.");
        viewport.SetLivePoseGeometry(new Dictionary<MeshInstance, RenderVertex[]> { [first] = World(mesh, Matrix4x4.CreateScale(-1)) });
        Check(ReferenceEquals(viewport.Pick(Size / 2, Size), first), "Reflected world-space pose was not pickable.");
        viewport.SetLivePoseGeometry(new Dictionary<MeshInstance, RenderVertex[]> { [first] = World(mesh, Matrix4x4.CreateScale(0)) });
        Check(viewport.SelectedBounds.Radius == 0 && viewport.Pick(Size / 2, Size) is null, "Collapsed pose retained an old triangle.");
        viewport.ClearLivePoseGeometry(); viewport.SetSelection(null); viewport.RenderImage(320, 240);
        Check(viewport.LivePoseGeometryCount == 0 && viewport.VisibleObjectCount == 0 && viewport.Pick(Size / 2, Size) is null
            && viewport.SceneGridBuildCount == grid, "Clearing pose changed the static grid or retained geometry.");
        Check(JsonSerializer.Serialize(scene, jsonOptions) == original, "Live GPU geometry changed exported scene fields.");

        var prototype = new SceneDocument(); prototype.Instances.Add(new() { Mesh = mesh });
        viewport.RegisterLivePrototype(new(99, prototype, null, []));
        viewport.SetLiveInstances([new("handle:1", 99, Matrix4x4.CreateTranslation(100, 0, 0))]);
        var runtime = viewport.TryGetLiveInstance("handle:1")!.Meshes.Single();
        viewport.SetLivePoseGeometry(new Dictionary<MeshInstance, RenderVertex[]> { [runtime] = vertices });
        viewport.SetSelection(runtime); viewport.RenderImage(320, 240);
        viewport.ClearLiveInstances();
        Check(viewport.LivePoseGeometryCount == 0 && viewport.SelectedObject is null, "Despawn retained pose geometry or selection.");
        Reject(() => viewport.SetLivePoseGeometry(new Dictionary<MeshInstance, RenderVertex[]> { [runtime] = vertices }));
        viewport.SetLiveInstances([new("handle:2", 99, Matrix4x4.CreateTranslation(100, 0, 0))]);
        Check(ReferenceEquals(viewport.TryGetLiveInstance("handle:2")!.Meshes.Single(), runtime), "Fixture did not reuse its runtime slot.");
        viewport.RenderImage(320, 240);
        Check(viewport.Pick(Size / 2, Size) is null && viewport.Selection.GetBounds(runtime).Center.X == 100,
            "A reused generation inherited the old pose.");
        viewport.SetLivePoseGeometry(new Dictionary<MeshInstance, RenderVertex[]> { [runtime] = vertices });
        viewport.SetScene(new SceneDocument());
        Check(viewport.LivePoseGeometryCount == 0, "Scene replacement retained per-instance GPU buffers.");
    }

    private static MeshData Triangle() => new()
    {
        Vertices = [new(new(-1, 0, -1), -Vector3.UnitY, Vector2.Zero, Vector4.One),
            new(new(0, 0, 1), -Vector3.UnitY, Vector2.Zero, Vector4.One),
            new(new(1, 0, -1), -Vector3.UnitY, Vector2.Zero, Vector4.One)], Indices = [0, 1, 2]
    };
    private static RenderVertex[] World(MeshData mesh, Matrix4x4 transform) => mesh.Vertices.Select(vertex =>
        new RenderVertex(Vector3.Transform(vertex.Position, transform), Vector3.TransformNormal(vertex.Normal, transform), vertex.TexCoord)).ToArray();
    private static bool Orange(Vector4 value) => value.X > .75f && value.Y is > .15f and < .55f && value.Z < .18f;
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Invalid live pose input was accepted."); }
}
