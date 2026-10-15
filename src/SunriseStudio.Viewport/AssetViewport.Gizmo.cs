using System.Numerics;
using ImGuiNET;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

public enum TransformTool { Select, Move, Rotate, Scale }

/** World-axis move, rotate and scale handles; one drag records one edit on release. */
public sealed partial class AssetViewport
{
    // Handle length and pick distance in screen pixels.
    private const float GizmoPixels = 90, GizmoPickPixels = 9;
    private const int RingSegments = 48;
    private readonly Vector3[] GizmoAxes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
    private static readonly Vector4[] GizmoColors = [new(.93f, .3f, .3f, 1), new(.4f, .85f, .35f, 1), new(.35f, .55f, .98f, 1)];
    // -1 is no drag; 0 to 2 drag one axis; 3 drags the uniform scale centre.
    private int gizmoAxis = -1;
    private float gizmoStart, gizmoLength;
    private Vector2 gizmoPress;
    private Vector3 gizmoRingStart;
    private float gizmoAngle;
    private TransformChange gizmoChange = TransformChange.Identity;

    public TransformTool Tool { get; set; }
    /** Shows the select, move, rotate and scale buttons; editable world views set it. */
    public bool ShowTransformTools { get; set; }
    /** World pivot of a selection the host can transform; null hides the handles. Set it each frame. */
    public Vector3? GizmoPivot { get; set; }
    /** Why the selection shows no handles in a transform tool; shown as the tool's tooltip. */
    public string GizmoUnavailableReason { get; set; } = "";
    /** An axis drag in Scale scales that axis alone; otherwise every handle scales all axes. */
    public bool AxisScale { get; set; }
    /** Called once when a drag ends, with the change about GizmoPivot. */
    public Action<TransformChange>? GizmoCommitted { get; set; }
    public bool GizmoDragging => gizmoAxis >= 0;

    private bool GizmoVisible => Tool != TransformTool.Select && GizmoPivot is not null && textureData is null
        && !Camera.HasExternalPose;

    /** Starts, follows or ends a handle drag; true while the handles own the mouse. */
    private bool ProcessGizmo(bool hovered, Vector2 origin, Vector2 size)
    {
        if (!GizmoVisible) { gizmoAxis = -1; return false; }
        UpdateGizmoAxes();
        var io = ImGui.GetIO();
        var pivot = GizmoPivot!.Value;
        var pixel = io.MousePos - origin;
        if (gizmoAxis < 0)
        {
            if (!hovered || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return false;
            int axis = GizmoHit(pixel, size, pivot);
            if (axis < 0) return false;
            gizmoAxis = axis;
            gizmoPress = io.MousePos;
            gizmoLength = GizmoWorldLength(pivot, size);
            gizmoStart = axis < 3 && Tool != TransformTool.Rotate ? AxisParameter(pixel, size, pivot, GizmoAxes[axis]) ?? 0 : 0;
            gizmoRingStart = Tool == TransformTool.Rotate ? PlanePoint(pixel, size, pivot, GizmoAxes[axis]) ?? Vector3.Zero : Vector3.Zero;
            gizmoAngle = 0;
            gizmoChange = TransformChange.Identity;
            return true;
        }
        if (Tool == TransformTool.Rotate)
        {
            // The turn is the angle between the press point and the pointer on the ring's plane.
            var axis = GizmoAxes[gizmoAxis];
            if (PlanePoint(pixel, size, pivot, axis) is { } point && gizmoRingStart.LengthSquared() > 1e-8f
                && point.LengthSquared() > 1e-8f)
            {
                gizmoAngle = MathF.Atan2(Vector3.Dot(axis, Vector3.Cross(gizmoRingStart, point)), Vector3.Dot(gizmoRingStart, point));
                gizmoChange = TransformChange.Identity with { Turn = Quaternion.CreateFromAxisAngle(axis, gizmoAngle) };
            }
        }
        else if (gizmoAxis < 3 && AxisParameter(pixel, size, pivot, GizmoAxes[gizmoAxis]) is float t)
        {
            float moved = t - gizmoStart;
            if (Tool == TransformTool.Move) gizmoChange = TransformChange.Identity with { Move = GizmoAxes[gizmoAxis] * moved };
            else
            {
                float factor = MathF.Max(.01f, (gizmoLength + moved) / gizmoLength);
                var scale = Vector3.One;
                if (AxisScale) scale[gizmoAxis] = factor; else scale = new(factor);
                gizmoChange = TransformChange.Identity with { Scale = scale };
            }
        }
        else if (gizmoAxis == 3)
            // Up and right grow; one handle length of drag doubles the scale.
            gizmoChange = TransformChange.Identity with { Scale = new(MathF.Max(.01f,
                MathF.Pow(2, (io.MousePos.X - gizmoPress.X - io.MousePos.Y + gizmoPress.Y) / GizmoPixels))) };
        SnapGizmoChange(pivot, pixel, size);
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) return true;
        gizmoAxis = -1;
        if (!gizmoChange.IsIdentity) GizmoCommitted?.Invoke(gizmoChange);
        return true;
    }

    /** Handle under the pointer; the scale centre wins over the axes it sits on. */
    private int GizmoHit(Vector2 pixel, Vector2 size, Vector3 pivot)
    {
        if (Project(pivot, size) is not { } centre) return -1;
        if (Tool == TransformTool.Scale && Vector2.Distance(pixel, centre) <= GizmoPickPixels) return 3;
        float length = GizmoWorldLength(pivot, size);
        int best = -1; float nearest = GizmoPickPixels;
        for (int axis = 0; axis < 3; axis++)
        {
            float distance = float.PositiveInfinity;
            if (Tool == TransformTool.Rotate)
            {
                for (int segment = 0; segment < RingSegments; segment++)
                    if (Project(RingPoint(pivot, axis, length, segment), size) is { } a
                        && Project(RingPoint(pivot, axis, length, segment + 1), size) is { } b)
                        distance = MathF.Min(distance, SegmentDistance(pixel, a, b));
            }
            else if (Project(pivot + GizmoAxes[axis] * length, size) is { } tip) distance = SegmentDistance(pixel, centre, tip);
            if (distance < nearest) { nearest = distance; best = axis; }
        }
        return best;
    }

    /** Draws the handles at the dragged pivot and the selection box where the drag would leave it. */
    private void DrawGizmo(ImDrawListPtr draw, Vector2 origin, Vector2 size, bool hovered)
    {
        if (!GizmoVisible) return;
        var pivot = GizmoPivot!.Value + gizmoChange.Move;
        if (Project(pivot, size) is not { } centre) return;
        float length = GizmoWorldLength(GizmoPivot.Value, size);
        int hot = gizmoAxis >= 0 ? gizmoAxis : hovered ? GizmoHit(ImGui.GetIO().MousePos - origin, size, GizmoPivot.Value) : -1;
        if (gizmoAxis >= 0) DrawPreviewBox(draw, origin, size);
        for (int axis = 0; axis < 3; axis++)
        {
            uint color = ImGui.GetColorU32(axis == hot ? new Vector4(1, .85f, .3f, 1) : GizmoColors[axis]);
            if (Tool == TransformTool.Rotate)
            {
                for (int segment = 0; segment < RingSegments; segment++)
                    if (Project(RingPoint(pivot, axis, length, segment), size) is { } a
                        && Project(RingPoint(pivot, axis, length, segment + 1), size) is { } b)
                        draw.AddLine(origin + a, origin + b, color, 2.5f);
                continue;
            }
            float stretch = Tool == TransformTool.Scale && gizmoAxis >= 0 ? gizmoChange.Scale[axis] : 1;
            if (Project(pivot + GizmoAxes[axis] * length * stretch, size) is not { } tip) continue;
            Vector2 start = origin + centre, end = origin + tip;
            draw.AddLine(start, end, color, 2.5f);
            var along = end - start;
            if (along.LengthSquared() < 1) continue;
            along = Vector2.Normalize(along);
            var side = new Vector2(-along.Y, along.X);
            if (Tool == TransformTool.Move) draw.AddTriangleFilled(end + along * 12, end + side * 6, end - side * 6, color);
            else draw.AddRectFilled(end - new Vector2(5), end + new Vector2(5), color);
        }
        if (Tool == TransformTool.Scale)
            draw.AddRect(origin + centre - new Vector2(6), origin + centre + new Vector2(6),
                ImGui.GetColorU32(hot == 3 ? new Vector4(1, .85f, .3f, 1) : new Vector4(.9f, .9f, .9f, 1)), 0, ImDrawFlags.None, 2);
        if (gizmoAxis < 0) return;
        var change = gizmoChange;
        ImGui.SetTooltip(Tool switch
        {
            TransformTool.Move => $"Move {change.Move.X:0.###}, {change.Move.Y:0.###}, {change.Move.Z:0.###}",
            TransformTool.Rotate => $"Rotate {gizmoAngle * 180 / MathF.PI:0.#} degrees around {(char)('X' + gizmoAxis)}",
            _ when change.UniformScale => $"Scale x{change.Scale.X:0.###}; the game stores one uniform scale",
            _ => $"Scale x{change.Scale.X:0.###}, {change.Scale.Y:0.###}, {change.Scale.Z:0.###}; positions spread, sizes stay"
        });
    }

    /** The selection's box, moved, turned or scaled about the pivot the way the release would. */
    private void DrawPreviewBox(ImDrawListPtr draw, Vector2 origin, Vector2 size)
    {
        var box = SelectedBounds;
        if (!box.IsFinite) return;
        var pivot = GizmoPivot!.Value;
        var change = gizmoChange;
        Vector3 Corner(int i) => change.Apply(new Vector3((i & 1) != 0 ? box.Maximum.X : box.Minimum.X,
            (i & 2) != 0 ? box.Maximum.Y : box.Minimum.Y, (i & 4) != 0 ? box.Maximum.Z : box.Minimum.Z), pivot);
        uint color = ImGui.GetColorU32(new Vector4(1, .85f, .3f, .8f));
        for (int i = 0; i < 8; i++)
            for (int bit = 1; bit < 8; bit <<= 1)
                if ((i & bit) == 0 && Project(Corner(i), size) is { } a && Project(Corner(i | bit), size) is { } b)
                    draw.AddLine(origin + a, origin + b, color, 1.5f);
    }

    /** A point on the ring around an axis; segment counts round the full circle. */
    private Vector3 RingPoint(Vector3 pivot, int axis, float radius, int segment)
    {
        float angle = MathF.Tau * segment / RingSegments;
        // Solforge Phase 13: the ring follows the handle axes, which turn with the selection in local mode.
        return pivot + GizmoAxes[(axis + 1) % 3] * (MathF.Cos(angle) * radius) + GizmoAxes[(axis + 2) % 3] * (MathF.Sin(angle) * radius);
    }

    /** World length that projects to the handle length at the pivot. */
    private float GizmoWorldLength(Vector3 pivot, Vector2 size)
    {
        var right = Vector3.Normalize(Vector3.Cross(Camera.Forward, Vector3.UnitZ) is { } cross && cross.LengthSquared() > 1e-6f
            ? cross : Vector3.UnitX);
        if (Project(pivot, size) is not { } a || Project(pivot + right, size) is not { } b) return 1;
        float pixels = Vector2.Distance(a, b);
        return pixels > 1e-4f ? GizmoPixels / pixels : 1;
    }

    /** Position along the axis line through the pivot that is nearest the pointer ray; null when they are parallel. */
    private float? AxisParameter(Vector2 pixel, Vector2 size, Vector3 pivot, Vector3 axis)
    {
        if (PointerRay(pixel, size) is not { } pointer) return null;
        var (start, ray) = pointer;
        float b = Vector3.Dot(axis, ray), denominator = 1 - b * b;
        if (denominator < 1e-4f) return null;
        var w = pivot - start;
        return (b * Vector3.Dot(ray, w) - Vector3.Dot(axis, w)) / denominator;
    }

    /** Pointer hit on the plane through the pivot normal to the axis, minus the pivot. */
    private Vector3? PlanePoint(Vector2 pixel, Vector2 size, Vector3 pivot, Vector3 axis)
    {
        if (PointerRay(pixel, size) is not { } pointer) return null;
        var (start, ray) = pointer;
        float facing = Vector3.Dot(axis, ray);
        if (MathF.Abs(facing) < 1e-3f) return null;
        return start + ray * (Vector3.Dot(axis, pivot - start) / facing) - pivot;
    }

    /** World ray under a viewport pixel. */
    private (Vector3 Start, Vector3 Direction)? PointerRay(Vector2 pixel, Vector2 size)
    {
        if (!Matrix4x4.Invert(Camera.ViewProjection(size.X / size.Y), out var inverse)) return null;
        var ndc = new Vector2(pixel.X / size.X * 2 - 1, 1 - pixel.Y / size.Y * 2);
        var near = Vector4.Transform(new Vector4(ndc, 0, 1), inverse);
        var far = Vector4.Transform(new Vector4(ndc, 1, 1), inverse);
        Vector3 start = new Vector3(near.X, near.Y, near.Z) / near.W, end = new Vector3(far.X, far.Y, far.Z) / far.W;
        return (start, Vector3.Normalize(end - start));
    }

    /** Viewport pixel of a world point; null behind the camera. */
    private Vector2? Project(Vector3 point, Vector2 size)
    {
        var p = Vector4.Transform(new Vector4(point, 1), Camera.ViewProjection(size.X / size.Y));
        if (p.W <= 1e-6f) return null;
        return new Vector2(p.X / p.W * .5f + .5f, .5f - p.Y / p.W * .5f) * size;
    }

    private static float SegmentDistance(Vector2 point, Vector2 a, Vector2 b)
    {
        var along = b - a;
        float t = along.LengthSquared() < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(point - a, along) / along.LengthSquared(), 0, 1);
        return Vector2.Distance(point, a + along * t);
    }
}
