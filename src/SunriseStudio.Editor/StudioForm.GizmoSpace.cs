using System.Numerics;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Solforge Phase 13: the selection's own rotation for the viewport's local handle axes. */
public sealed partial class StudioForm
{
    private static void UpdateGizmoOrientation(AssetViewport view, object? target, bool group)
    {
        var rotation = Quaternion.Identity;
        if (!group && target is MeshInstance mesh && Matrix4x4.Decompose(mesh.Transform, out _, out var meshRotation, out _))
            rotation = meshRotation;
        else if (!group && target is SceneMarker marker) rotation = marker.RenderRotation;
        view.GizmoOrientation = rotation;
    }
}
