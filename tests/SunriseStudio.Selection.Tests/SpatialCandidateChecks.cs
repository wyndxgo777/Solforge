using System.Numerics;
using System.Reflection;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Checks the spatial candidate grid: reuse keeps membership and order, invalidation keys, and borrowed arrays are not mutated. */
internal static partial class Program
{
    /** Cached queries preserve exact traversal and invoke mutable predicates on every candidate. */
    private static void SpatialCandidates()
    {
        var random = new Random(4291);
        var centers = Enumerable.Range(0, 12000).Select(_ => new Vector3(
            random.NextSingle() * 500 - 250, random.NextSingle() * 500 - 250,
            random.NextSingle() * 100 - 50)).ToArray();
        var radii = centers.Select(_ => random.NextSingle() * 20).ToArray();
        centers[0] = new(float.NaN); radii[1] = float.PositiveInfinity;
        var grid = new SceneGrid(centers, radii);
        var camera = new ViewerCamera(); camera.LookFrom(new(0, -350, 80), Vector3.Zero);
        Vector4[] planes = SceneGrid.FrustumPlanes(camera.ViewProjection(1.6f));
        Vector3 eye = camera.Position;
        float pixels = 500, minimum = 0;
        bool ortho = false, shown = false;
        void Check(bool rebuild)
        {
            var expected = new List<int>(); var actual = new List<int>();
            var expectedVisits = new List<int>(); var actualVisits = new List<int>();
            bool Eligible(int index) => shown && index % 3 != 0;
            grid.Query(planes, eye, pixels, ortho, minimum,
                index => { expectedVisits.Add(index); return Eligible(index); }, expected);
            int before = grid.CandidateBuildCount;
            grid.QueryCached(planes, eye, pixels, ortho, minimum,
                index => { actualVisits.Add(index); return Eligible(index); }, actual);
            Require(expected.SequenceEqual(actual) && expectedVisits.SequenceEqual(actualVisits),
                "Spatial reuse changed membership, order, or predicate visits.");
            Require(grid.CandidateBuildCount == before + (rebuild ? 1 : 0), "Spatial key invalidation differs.");
        }
        Check(true);
        shown = true; Check(false); shown = false; Check(false); shown = true;
        planes = planes.ToArray(); Check(false);
        planes[0].W += 3; Check(true);
        eye += Vector3.UnitX; Check(true);
        pixels += 1; Check(true);
        minimum = 5; Check(true);
        ortho = true; Check(true); Check(false);
        camera.Orthographic = true;
        planes = SceneGrid.FrustumPlanes(camera.ViewProjection(2)); Check(true);
        var prior = new List<int>(); grid.Query(planes, eye, pixels, ortho, minimum, _ => true, prior);
        Array.Fill(centers, new Vector3(100000)); Array.Fill(radii, 0);
        var snapshot = new List<int>(); grid.QueryCached(planes, eye, pixels, ortho, minimum, _ => true, snapshot);
        Require(prior.SequenceEqual(snapshot), "Borrowed input arrays mutated the stored grid.");
        planes[0].W += 1;
        bool failed = false;
        try { grid.QueryCached(planes, eye, pixels, ortho, minimum, _ => throw new InvalidOperationException("fixture"), []); }
        catch (InvalidOperationException) { failed = true; }
        Require(failed, "The predicate failure fixture had no spatial candidate.");
        Check(true); Check(false);
        Console.WriteLine("selection_spatial_cache order=exact predicates=live keys=exact input_snapshot=stable failure=retry");
    }

    /** Authored edits rebuild the grid; transient paths and all filter closures remain live. */
    private static void SpatialVisibility()
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, true);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        var first = new MeshInstance { Mesh = Triangle(), BubbleHash = 1 };
        var second = new MeshInstance { Mesh = Triangle(), BubbleHash = 2, Layer = SceneLayer.Terrain,
            Transform = Matrix4x4.CreateTranslation(100, 0, 0) };
        var coarse = new MeshInstance { Mesh = new MeshData { TagHash = first.Mesh.TagHash,
            Vertices = first.Mesh.Vertices, Indices = first.Mesh.Indices, LodLevel = 1 },
            Transform = Matrix4x4.CreateTranslation(1.5f, 0, 0) };
        var marker = new SceneMarker { Position = new(100, 0, 0), Extents = new(.1f), Layer = SceneLayer.Spawns };
        var scene = new SceneDocument(); scene.Instances.AddRange([first, second, coarse]); scene.Markers.Add(marker);
        viewport.SetScene(scene); viewport.Backdrop.Mode = SceneBackdropMode.All;
        viewport.Camera.LookFrom(new(0, -6, 0), Vector3.Zero);
        void CheckStatic(int width = 320, int height = 240)
        {
            viewport.RenderImage(width, height);
            var bounds = viewport.Selection.Objects.Select(viewport.Selection.GetAuthoredBounds).ToArray();
            var reference = new SceneGrid(bounds.Select(value => value.Center).ToArray(), bounds.Select(value => value.Radius).ToArray());
            var expected = new List<int>();
            float aspect = (float)width / height;
            float pixels = viewport.Camera.Orthographic ? height / viewport.Camera.ViewHeight
                : height / (2 * MathF.Tan(viewport.Camera.VerticalFieldOfView(aspect) / 2));
            reference.Query(SceneGrid.FrustumPlanes(viewport.Camera.ViewProjection(aspect)), viewport.Camera.Position,
                pixels, viewport.Camera.Orthographic, 0,
                index => viewport.Visibility.IsVisible(viewport.Selection.Objects[index]), expected);
            Require(expected.SequenceEqual(viewport.VisibleIndices), "Cached viewport differs from uncached ordered candidates.");
        }
        CheckStatic();
        int shown = viewport.ShownCellBuildCount;
        viewport.Camera.Orbit(2, 0); CheckStatic(); viewport.Camera.LookFrom(new(0, -6, 0), Vector3.Zero); CheckStatic();
        Require(viewport.ShownCellBuildCount == shown, "A camera move rebuilt the shown cells.");
        int queries = viewport.SceneCullQueryCount, grids = viewport.SceneGridBuildCount;
        bool host = false, live = true, context = true;
        viewport.Visibility.Filter = _ => host;
        CheckStatic(); host = true; CheckStatic();
        viewport.Visibility.LiveFilter = _ => live;
        live = false; CheckStatic(); live = true; CheckStatic();
        var property = typeof(SceneVisibility).GetProperty("ContextFilter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalContext = property.GetValue(viewport.Visibility);
        property.SetValue(viewport.Visibility, (Predicate<object>)(_ => context));
        context = false; CheckStatic(); context = true; CheckStatic();
        property.SetValue(viewport.Visibility, originalContext);
        viewport.Visibility.HiddenLayers.Add(SceneLayer.Geometry); CheckStatic();
        viewport.Visibility.HiddenLayers.Clear(); viewport.Visibility.HiddenBubbles.Add(1); CheckStatic();
        viewport.Visibility.HiddenBubbles.Clear(); CheckStatic();
        viewport.Visibility.HiddenGeometryLayers.Add(SceneLayer.Geometry); CheckStatic();
        viewport.Visibility.HiddenGeometryLayers.Clear();
        viewport.Visibility.DetailLevel = 1; CheckStatic();
        Require(viewport.VisibleIndices.SequenceEqual(new[] { viewport.Selection.IndexOf(coarse) }), "LOD selection retained detailed parts.");
        viewport.Visibility.DetailLevel = 0; CheckStatic();
        // A live host filter leaves the shown cells; the transient path builds its candidates once.
        Require(viewport.SceneCullQueryCount == ++queries, "Filter changes rebuilt spatial candidates.");
        _ = viewport.Pick(Size / 2, Size); CheckStatic();
        Require(viewport.SceneCullQueryCount == queries, "Picking evicted the render query.");
        viewport.CacheSpatialCandidates = false; CheckStatic();
        Require(viewport.SceneCullQueryCount == ++queries, "The uncached diagnostic path did not query bounds.");
        viewport.CacheSpatialCandidates = true; CheckStatic();
        Require(viewport.SceneCullQueryCount == queries, "Uncached comparison evicted valid spatial candidates.");
        viewport.Camera.Orbit(2, 0); CheckStatic();
        Require(viewport.SceneCullQueryCount == ++queries, "Orbit reused a different frustum.");
        viewport.Camera.LookFrom(new(0, -6, 0), Vector3.Zero); CheckStatic(); queries++;
        CheckStatic(640, 480); Require(viewport.SceneCullQueryCount == ++queries, "Viewport resize reused a different pixel scale.");
        CheckStatic(); queries++;
        viewport.Camera.Orthographic = true; CheckStatic();
        Require(viewport.SceneCullQueryCount == ++queries, "Projection change retained perspective candidates.");
        viewport.Camera.Orthographic = false; CheckStatic(); queries++;

        var setter = typeof(MeshInstance).GetMethod("SetAuthored", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setter.Invoke(second, [Matrix4x4.CreateTranslation(1.5f, 0, 0), ReadOnlyMemory<byte>.Empty]);
        viewport.Selection.RefreshBounds(second); CheckStatic();
        Require(viewport.SceneGridBuildCount == ++grids && viewport.SceneCullQueryCount == ++queries
            && viewport.VisibleIndices.Contains(viewport.Selection.IndexOf(second)), "Direct authored-bound refresh kept an old grid.");
        setter.Invoke(second, [Matrix4x4.CreateTranslation(100, 0, 0), ReadOnlyMemory<byte>.Empty]);
        viewport.UpdatePlacements([second], []); CheckStatic();
        Require(viewport.SceneGridBuildCount == ++grids && viewport.SceneCullQueryCount == ++queries,
            "Placement edits kept an old spatial snapshot.");
        viewport.Selection.SetPose(first.Mesh, first.Mesh.Vertices.Select(vertex => vertex.Position + new Vector3(100, 0, 0)).ToArray());
        CheckStatic();
        Require(viewport.SceneGridBuildCount == ++grids && viewport.SceneCullQueryCount == ++queries
            && !viewport.VisibleIndices.Contains(viewport.Selection.IndexOf(first)), "Direct pose changes kept bind-pose candidates.");
        viewport.Selection.SetPose(first.Mesh, first.Mesh.Vertices.Select(vertex => vertex.Position).ToArray());
        CheckStatic(); grids++; queries++;

        viewport.SetLiveTransforms(new Dictionary<MeshInstance, Matrix4x4> { [second] = Matrix4x4.Identity },
            new Dictionary<SceneMarker, (Vector3, Quaternion)>());
        viewport.RenderImage(320, 240);
        Require(viewport.VisibleIndices.SequenceEqual(new[] { viewport.Selection.IndexOf(first), viewport.Selection.IndexOf(second) })
            && viewport.SceneGridBuildCount == grids && viewport.SceneCullQueryCount == queries,
            "Live transforms reused authored bounds or rebuilt static candidates.");
        viewport.ClearLiveTransforms(); CheckStatic();
        viewport.SetLivePoseGeometry(new Dictionary<MeshInstance, RenderVertex[]>
        { [second] = second.Mesh.Vertices.Select(vertex => new RenderVertex(vertex.Position, vertex.Normal, vertex.TexCoord)).ToArray() });
        viewport.RenderImage(320, 240);
        Require(viewport.VisibleIndices.SequenceEqual(new[] { viewport.Selection.IndexOf(first), viewport.Selection.IndexOf(second) })
            && viewport.SceneGridBuildCount == grids && viewport.SceneCullQueryCount == queries,
            "Live pose bounds were cached with the authored query.");
        viewport.ClearLivePoseGeometry(); CheckStatic();
        Require(viewport.SceneCullQueryCount == queries, "Clearing transient state invalidated authored candidates.");

        var prototype = new SceneDocument(); prototype.Instances.Add(new() { Mesh = Triangle(), Layer = SceneLayer.Objects });
        viewport.RegisterLivePrototype(new(123, prototype, null, []));
        viewport.SetLiveInstances([new("candidate:1", 123, Matrix4x4.Identity)]);
        viewport.RenderImage(320, 240);
        int runtime = viewport.Selection.IndexOf(viewport.TryGetLiveInstance("candidate:1")!.Meshes[0]);
        Require(viewport.VisibleIndices.SequenceEqual(new[] { viewport.Selection.IndexOf(first), runtime })
            && viewport.SceneGridBuildCount == grids && viewport.SceneCullQueryCount == queries,
            "Runtime admission changed authored candidates or draw order.");
        viewport.ClearLiveInstances(); viewport.RenderImage(320, 240);
        Require(viewport.VisibleIndices.SequenceEqual(new[] { viewport.Selection.IndexOf(first) })
            && viewport.SceneCullQueryCount == queries, "Runtime removal reused stale membership.");
        var proxy = new ScenePointProxy("cache", "Cached proxy", Vector3.Zero, new(.1f));
        viewport.AddPointProxy(proxy); CheckStatic();
        Require(viewport.SceneGridBuildCount == ++grids && viewport.SceneCullQueryCount == ++queries,
            "An added proxy reused prior authored candidates.");
        viewport.ReplacePointProxy(proxy, proxy with { Position = new(100, 0, 0) }); CheckStatic();
        Require(viewport.SceneGridBuildCount == ++grids && viewport.SceneCullQueryCount == ++queries,
            "A moved proxy retained prior authored candidates.");
        viewport.SetScene(new SceneDocument()); viewport.RenderImage(320, 240);
        Require(viewport.VisibleObjectCount == 0, "Scene replacement retained cached candidates.");
        Console.WriteLine("selection_spatial_viewport filters=live authored=refit pose=refit transient=fresh runtime=fresh picking=uncached");
    }
}
