using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** Live transform half of AssetViewport: per-frame transform overrides for moving meshes and markers. */
public sealed partial class AssetViewport
{
    private readonly HashSet<MeshInstance> liveMeshes = [];
    private readonly HashSet<SceneMarker> liveMarkers = [];
    private readonly List<int> liveIndices = [];
    private readonly HashSet<int> liveIndexSet = [];
    private readonly List<LineVertex> liveMarkerLines = [];
    public int LiveTransformCount => liveIndices.Count;
    public int SceneGridBuildCount { get; private set; }
    public int SceneCullQueryCount { get; private set; }

    /** A complete live snapshot replaces preview placements without changing package-backed fields. */
    public void SetLiveTransforms(IReadOnlyDictionary<MeshInstance, Matrix4x4> meshes,
        IReadOnlyDictionary<SceneMarker, (Vector3 Position, Quaternion Rotation)> markers,
        IReadOnlyDictionary<SceneMarker, Vector3>? extents = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var (instance, transform) in meshes)
        {
            if (selection.IndexOf(instance) < 0) throw new ArgumentException("Live mesh is outside this scene.", nameof(meshes));
            if (!Finite(transform) || MathF.Abs(transform.M14) + MathF.Abs(transform.M24) + MathF.Abs(transform.M34) > 1e-5f
                || MathF.Abs(transform.M44 - 1) > 1e-5f || !Matrix4x4.Invert(transform, out _) && !Collapsed(transform))
                throw new ArgumentException("Live transform must be a finite affine matrix with invertible or zero scale.", nameof(meshes));
            nativeScene?.ValidateLiveTransform(instance, transform);
        }
        foreach (var (marker, pose) in markers)
            if (selection.IndexOf(marker) < 0 || !Finite(pose.Position) || !Finite(new Vector4(pose.Rotation.X,
                pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W)) || pose.Rotation.LengthSquared() < 1e-10f)
                throw new ArgumentException("Live marker pose is invalid or outside this scene.", nameof(markers));
        foreach (var (marker, value) in extents ?? new Dictionary<SceneMarker, Vector3>())
            if (!markers.ContainsKey(marker) || !Finite(value) || value.X < 0 || value.Y < 0 || value.Z < 0)
                throw new ArgumentException("Live marker extents are invalid or have no matching pose.", nameof(extents));

        // Only instances whose preview actually changed are refreshed; a snapshot repeats most poses.
        var changed = liveMeshes.Where(instance => !meshes.ContainsKey(instance))
            .Concat(meshes.Where(pair => pair.Key.PreviewTransform != pair.Value).Select(pair => pair.Key)).ToArray();
        var changedMarkers = liveMarkers.Where(marker => !markers.ContainsKey(marker)).Concat(markers.Where(pair =>
            pair.Key.PreviewPose != (pair.Value.Position, Quaternion.Normalize(pair.Value.Rotation))
            || pair.Key.PreviewExtents != (extents?.TryGetValue(pair.Key, out var size) == true ? size : null))
            .Select(pair => pair.Key)).ToArray();
        foreach (var instance in changed)
        {
            instance.PreviewTransform = meshes.TryGetValue(instance, out var transform) ? transform : null;
            selection.RefreshBounds(instance, authored: false);
        }
        foreach (var marker in changedMarkers)
        {
            marker.PreviewPose = markers.TryGetValue(marker, out var pose)
                ? (pose.Position, Quaternion.Normalize(pose.Rotation)) : null;
            marker.PreviewExtents = extents?.TryGetValue(marker, out var size) == true ? size : null;
            selection.RefreshBounds(marker, authored: false); RefreshLiveMarker(marker);
        }
        bool membership = liveMeshes.Count != meshes.Count || liveMarkers.Count != markers.Count
            || !meshes.Keys.All(liveMeshes.Contains) || !markers.Keys.All(liveMarkers.Contains);
        if (membership)
        {
            liveMeshes.Clear(); liveMeshes.UnionWith(meshes.Keys);
            liveMarkers.Clear(); liveMarkers.UnionWith(markers.Keys);
            liveIndices.Clear(); liveIndexSet.Clear();
            foreach (object item in liveMeshes.Cast<object>().Concat(liveMarkers))
            {
                int index = selection.IndexOf(item); liveIndices.Add(index); liveIndexSet.Add(index);
            }
        }
        if (changed.Length > 0) nativeScene?.RefreshLiveTransforms(changed);
        if (changed.Length > 0 || changedMarkers.Length > 0) HoveredObject = null;
    }

    /** Drops every live transform override. */
    public void ClearLiveTransforms() => SetLiveTransforms(
        new Dictionary<MeshInstance, Matrix4x4>(),
        new Dictionary<SceneMarker, (Vector3 Position, Quaternion Rotation)>());

    /** Dynamic bounds are tested separately; the authored grid is not rebuilt for each snapshot. */
    private void QueryLive(Vector4[] planes, Func<int, bool> shown, List<int> result)
    {
        foreach (int index in liveIndices)
        {
            if (livePoseIndices.Contains(index)) continue;
            var bounds = selection.GetBounds(selection.Objects[index]);
            if (shown(index) && SceneGrid.SphereInFrustum(bounds.Center, bounds.Radius, planes)) result.Add(index);
        }
    }

    private void RefreshLiveMarker(SceneMarker marker)
    {
        int index = selection.IndexOf(marker);
        if (index < 0 || renderIndexDirty || index >= markerLineStarts.Length) return;
        liveMarkerLines.Clear(); AppendMarkerLines(marker, liveMarkerLines);
        CollectionsMarshal.AsSpan(liveMarkerLines).CopyTo(markerLines.AsSpan(markerLineStarts[index], 24));
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Collapsed(Matrix4x4 value) => value.M11 == 0 && value.M12 == 0 && value.M13 == 0
        && value.M21 == 0 && value.M22 == 0 && value.M23 == 0 && value.M31 == 0 && value.M32 == 0 && value.M33 == 0;
    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y)
        && float.IsFinite(value.Z) && float.IsFinite(value.W);
    private static bool Finite(Matrix4x4 value) => Finite(new Vector4(value.M11, value.M12, value.M13, value.M14))
        && Finite(new Vector4(value.M21, value.M22, value.M23, value.M24))
        && Finite(new Vector4(value.M31, value.M32, value.M33, value.M34))
        && Finite(new Vector4(value.M41, value.M42, value.M43, value.M44));
}
