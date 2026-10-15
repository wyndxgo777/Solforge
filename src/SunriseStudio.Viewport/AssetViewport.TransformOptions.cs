using System.Numerics;
using ImGuiNET;

namespace SunriseStudio.Viewport;

/**
 * Solforge Phase 13: local/world handle axes, grid/angle/scale snapping (toolbar toggle; Ctrl inverts it while
 * dragging) and surface snapping (Shift while moving puts the pivot on the surface under the pointer; Alt also
 * turns the selection's up axis onto the surface normal). Picking and the handles themselves are upstream.
 */
public sealed partial class AssetViewport
{
    private const float SurfaceProbePixels = 3;
    private SnapSettings snap = SnapSettings.Default;
    private Func<object, PickFaces>? surfaceFaces;

    public SnapSettings Snap { get => snap; set => snap = value; }
    /** Handles follow GizmoOrientation for Move and Rotate; Scale always uses world axes. */
    public bool GizmoLocal { get; set; }
    /** World rotation of the selection; the host sets it each frame with GizmoPivot. */
    public Quaternion GizmoOrientation { get; set; } = Quaternion.Identity;

    /** Refreshes the handle axes between drags. */
    private void UpdateGizmoAxes()
    {
        if (gizmoAxis >= 0) return;
        bool local = GizmoLocal && Tool != TransformTool.Scale;
        var rotation = local && float.IsFinite(GizmoOrientation.LengthSquared()) && GizmoOrientation.LengthSquared() > 1e-8f
            ? Quaternion.Normalize(GizmoOrientation) : Quaternion.Identity;
        GizmoAxes[0] = Vector3.Transform(Vector3.UnitX, rotation);
        GizmoAxes[1] = Vector3.Transform(Vector3.UnitY, rotation);
        GizmoAxes[2] = Vector3.Transform(Vector3.UnitZ, rotation);
    }

    /** Applies snapping to the drag in progress; called once a frame after the drag updates gizmoChange. */
    private void SnapGizmoChange(Vector3 pivot, Vector2 pixel, Vector2 size)
    {
        var io = ImGui.GetIO();
        if (Tool == TransformTool.Move && io.KeyShift && SurfaceUnder(pixel, size) is { } surface)
        {
            gizmoChange = TransformSnapping.AlignToSurface(pivot, SelectionUp(), surface.Point, surface.Normal, io.KeyAlt);
            return;
        }
        if (snap.Enabled == io.KeyCtrl || gizmoChange.IsIdentity) return;
        var axis = gizmoAxis is >= 0 and < 3 ? GizmoAxes[gizmoAxis] : Vector3.UnitZ;
        gizmoChange = TransformSnapping.Apply(gizmoChange, pivot, axis, gizmoAngle, snap);
    }

    /** The selection's own up axis in world space, whether the handles are local or world. */
    private Vector3 SelectionUp()
    {
        var rotation = GizmoOrientation;
        return float.IsFinite(rotation.LengthSquared()) && rotation.LengthSquared() > 1e-8f
            ? Vector3.Transform(Vector3.UnitZ, Quaternion.Normalize(rotation)) : Vector3.UnitZ;
    }

    /**
     * The surface under a viewport pixel and its normal, from the hit and two neighbouring hits. The selection is
     * left out of these picks: it stays where it was until the drag ends, so it would otherwise snap onto itself.
     */
    private (Vector3 Point, Vector3 Normal)? SurfaceUnder(Vector2 pixel, Vector2 size)
    {
        Func<object, PickFaces> faces = surfaceFaces ??= item => selectedMemberSet.Contains(item) ? PickFaces.None : DrawnFaces(item);
        Vector3? Hit(Vector2 at)
        {
            if (Pick(at, size, faces, out float distance) is null || !float.IsFinite(distance)) return null;
            var (origin, direction) = PickRay(at, size);
            return origin + direction * distance;
        }
        // The normal is oriented toward the eye; orthographic views have none, so they do not snap.
        if (Camera.Orthographic || Hit(pixel) is not { } point) return null;
        var right = Hit(pixel + new Vector2(SurfaceProbePixels, 0));
        var down = Hit(pixel + new Vector2(0, SurfaceProbePixels));
        var normal = right is { } r && down is { } d ? TransformSnapping.SurfaceNormal(point, r, d, Camera.Position) : null;
        return (point, normal ?? Vector3.UnitZ);
    }

    /**
     * The pick ray through a viewport pixel exactly as SceneSelection.Pick builds it: it starts on the near plane,
     * which is where its hit distance is measured from.
     */
    private (Vector3 Origin, Vector3 Direction) PickRay(Vector2 pixel, Vector2 size)
    {
        var ndc = new Vector2(pixel.X / size.X * 2 - 1, 1 - pixel.Y / size.Y * 2);
        if (!Matrix4x4.Invert(Camera.ViewProjection(size.X / size.Y), out var inverse)) return (Camera.Position, Camera.Forward);
        var near = Vector4.Transform(new Vector4(ndc, 0, 1), inverse);
        var far = Vector4.Transform(new Vector4(ndc, .5f, 1), inverse);
        var origin = new Vector3(near.X, near.Y, near.Z) / near.W;
        var direction = new Vector3(far.X, far.Y, far.Z) / far.W - origin;
        return (origin, direction.LengthSquared() > 1e-12f ? Vector3.Normalize(direction) : Camera.Forward);
    }

    /** Toolbar: world/local axes and the snap toggle with its steps. */
    private void DrawTransformOptions(Action<float> next)
    {
        float pad = ImGui.GetStyle().FramePadding.X * 2;
        string space = GizmoLocal ? "Local" : "World";
        next(ImGui.CalcTextSize("World").X + pad);
        if (ImGui.Button(space + "##gizmo-space")) GizmoLocal = !GizmoLocal;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Move and rotate along the world axes or the selection's own axes");
        next(ImGui.CalcTextSize("Snap").X + pad);
        bool enabled = snap.Enabled;
        if (enabled) ImGui.PushStyleColor(ImGuiCol.Button, SolTheme.Sun2 with { W = .55f });
        if (ImGui.Button("Snap##gizmo-snap")) snap = snap with { Enabled = !enabled };
        if (enabled) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Grid, angle and scale snapping; Ctrl inverts it while dragging. Right-click for steps.\n"
                + "Shift while moving: onto the surface under the pointer; add Alt to align to its normal.");
        if (!ImGui.BeginPopupContextItem("##gizmo-snap-steps")) return;
        float move = snap.MoveStep, angle = snap.AngleStepDegrees, scale = snap.ScaleStep;
        ImGui.SetNextItemWidth(120 * SolTheme.Scale);
        if (ImGui.DragFloat("Grid", ref move, .05f, 0, 1000, "%.2f")) snap = snap with { MoveStep = move };
        ImGui.SetNextItemWidth(120 * SolTheme.Scale);
        if (ImGui.DragFloat("Angle", ref angle, .5f, 0, 180, "%.1f deg")) snap = snap with { AngleStepDegrees = angle };
        ImGui.SetNextItemWidth(120 * SolTheme.Scale);
        if (ImGui.DragFloat("Scale", ref scale, .01f, 0, 10, "%.2f")) snap = snap with { ScaleStep = scale };
        ImGui.EndPopup();
    }
}
