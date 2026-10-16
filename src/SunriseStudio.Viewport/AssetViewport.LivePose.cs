using System.Numerics;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** Live pose half of AssetViewport: posed vertex geometry that replaces a mesh's authored vertices. */
public sealed partial class AssetViewport
{
    private sealed class LivePoseMesh(GpuMesh active)
    {
        public GpuMesh Active = active;
        public GpuMesh? Spare;
        /** Releases both vertex buffers. */
        public void Dispose() { Active.Dispose(); Spare?.Dispose(); }
    }
    private readonly Dictionary<MeshInstance, LivePoseMesh> livePoseMeshes = [];
    private readonly HashSet<int> livePoseIndices = [];
    public int LivePoseGeometryCount => livePoseMeshes.Count;

    /** Validate and stage the whole snapshot before replacing any visible per-instance geometry. */
    public void SetLivePoseGeometry(IReadOnlyDictionary<MeshInstance, RenderVertex[]> worldVertices)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (worldVertices.Count > 4096) throw new NotSupportedException("Live pose geometry exceeds its part budget.");
        long total = 0;
        var snapshots = new Dictionary<MeshInstance, RenderVertex[]>();
        foreach (var (instance, input) in worldVertices)
        {
            if (selection.IndexOf(instance) < 0 || runtimeObjects.Contains(instance) && !activeRuntimeObjects.Contains(instance))
                throw new ArgumentException("Live pose geometry is outside the active scene.", nameof(worldVertices));
            if (input is null || input.Length != instance.Mesh.Vertices.Length || input.Length == 0 || instance.Mesh.Indices.Length == 0)
                throw new ArgumentException("Live pose geometry must retain its vertex and index counts.", nameof(worldVertices));
            total += input.LongLength * 32 + instance.Mesh.Indices.LongLength * 4;
            if (total > 128L * 1024 * 1024) throw new NotSupportedException("Live pose geometry exceeds its buffer budget.");
            var vertices = input.ToArray();
            for (int i = 0; i < vertices.Length; i++)
                if (!Finite(vertices[i].Position) || !Finite(vertices[i].Normal)
                    || vertices[i].TexCoord != instance.Mesh.Vertices[i].TexCoord
                    || !float.IsFinite(vertices[i].TexCoord.X) || !float.IsFinite(vertices[i].TexCoord.Y))
                    throw new ArgumentException("Live pose vertices must be finite and preserve source UVs.", nameof(worldVertices));
            snapshots.Add(instance, vertices);
        }
        var staged = new Dictionary<MeshInstance, GpuMesh>();
        var created = new List<GpuMesh>();
        try
        {
            foreach (var (instance, vertices) in snapshots)
            {
                GpuMesh mesh;
                if (livePoseMeshes.TryGetValue(instance, out var old) && old.Spare is { } spare)
                { mesh = spare; renderer.UpdateMeshVertices(mesh, vertices); }
                else { mesh = renderer.CreateMesh(vertices, instance.Mesh.Indices); created.Add(mesh); }
                staged.Add(instance, mesh);
            }
        }
        catch { foreach (var mesh in created) mesh.Dispose(); throw; }
        RemoveLivePoseGeometry(livePoseMeshes.Keys.Where(instance => !snapshots.ContainsKey(instance)).ToArray());
        foreach (var (instance, mesh) in staged)
        {
            if (livePoseMeshes.TryGetValue(instance, out var old)) { old.Spare = old.Active; old.Active = mesh; }
            else livePoseMeshes.Add(instance, new(mesh));
            selection.SetInstancePose(instance, snapshots[instance].Select(vertex => vertex.Position).ToArray());
        }
        ReindexLivePoseGeometry(); HoveredObject = null;
    }

    /** Drops all live pose geometry. */
    public void ClearLivePoseGeometry() => SetLivePoseGeometry(new Dictionary<MeshInstance, RenderVertex[]>());

    private void RemoveLivePoseGeometry(IEnumerable<MeshInstance> instances)
    {
        foreach (var instance in instances)
            if (livePoseMeshes.Remove(instance, out var mesh))
            {
                mesh.Dispose();
                if (selection.IndexOf(instance) >= 0) selection.SetInstancePose(instance, null);
            }
        ReindexLivePoseGeometry();
    }

    private void ReindexLivePoseGeometry()
    {
        livePoseIndices.Clear();
        foreach (var instance in livePoseMeshes.Keys) livePoseIndices.Add(selection.IndexOf(instance));
    }

    /** Moving bones use a small transient query instead of rebuilding the authored scene grid. */
    private void QueryLivePose(Vector4[] planes, Func<int, bool> shown, List<int> result)
    {
        foreach (int index in livePoseIndices)
        {
            var box = selection.GetBounds(selection.Objects[index]);
            if (shown(index) && SceneGrid.SphereInFrustum(box.Center, box.Radius, planes)) result.Add(index);
        }
    }

    private bool DiagnosticMesh(MeshInstance instance, out GpuMesh mesh, out Matrix4x4 transform)
    {
        if (livePoseMeshes.TryGetValue(instance, out var pose))
        { mesh = pose.Active; transform = Matrix4x4.Identity; return true; }
        transform = instance.RenderTransform;
        return meshes.TryGetValue(instance.Mesh, out mesh!);
    }
}
