using System.Numerics;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** Solforge Phase 13: grid steps for the transform handles. Steps of zero or less leave that part unsnapped. */
public readonly record struct SnapSettings(bool Enabled, float MoveStep, float AngleStepDegrees, float ScaleStep)
{
    public static SnapSettings Default => new(false, 1, 15, .1f);
}

/** Solforge Phase 13: grid, angle, scale and surface-normal snapping of a handle drag. */
public static class TransformSnapping
{
    /** Rounds a value to the nearest step; unchanged when the step is not positive or the value not finite. */
    public static float Snap(float value, float step) => step > 0 && float.IsFinite(value) ? MathF.Round(value / step) * step : value;

    /**
     * A world-axis move lands the pivot on the world grid along the axes that moved; a move along any other
     * direction (a local axis) snaps its length instead, so it stays on the handle's line.
     */
    public static Vector3 SnapMove(Vector3 pivot, Vector3 move, float step)
    {
        if (step <= 0 || move == Vector3.Zero) return move;
        int moved = (move.X != 0 ? 1 : 0) + (move.Y != 0 ? 1 : 0) + (move.Z != 0 ? 1 : 0);
        if (moved > 1)
        {
            float length = move.Length();
            return move / length * Snap(length, step);
        }
        var result = move;
        for (int axis = 0; axis < 3; axis++)
            if (move[axis] != 0) result[axis] = Snap(pivot[axis] + move[axis], step) - pivot[axis];
        return result;
    }

    /** Snaps a turn about one axis to whole angle steps. */
    public static Quaternion SnapTurn(Vector3 axis, float radians, float stepDegrees) =>
        Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), Snap(radians, stepDegrees * MathF.PI / 180));

    /** Snaps each scale factor to the step, never below one step. */
    public static Vector3 SnapScale(Vector3 scale, float step)
    {
        if (step <= 0) return scale;
        var result = scale;
        for (int axis = 0; axis < 3; axis++)
            if (scale[axis] != 1) result[axis] = MathF.Max(step, Snap(scale[axis], step));
        return result;
    }

    /** The shortest rotation taking one direction onto another; opposite directions turn about a perpendicular. */
    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        var a = Vector3.Normalize(from);
        var b = Vector3.Normalize(to);
        float dot = Math.Clamp(Vector3.Dot(a, b), -1, 1);
        if (dot > 1 - 1e-6f) return Quaternion.Identity;
        if (dot < -1 + 1e-6f)
        {
            var perpendicular = MathF.Abs(a.X) < .9f ? Vector3.Cross(a, Vector3.UnitX) : Vector3.Cross(a, Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(perpendicular), MathF.PI);
        }
        var axis = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(axis, 1 + dot));
    }

    /**
     * Moves the pivot onto a surface point and, when aligning, turns the selection's up axis onto the surface normal.
     * @param up The selection's current up direction in world space.
     */
    public static TransformChange AlignToSurface(Vector3 pivot, Vector3 up, Vector3 point, Vector3 normal, bool align)
    {
        var change = TransformChange.Identity with { Move = point - pivot };
        return align && normal.LengthSquared() > 1e-12f && up.LengthSquared() > 1e-12f
            ? change with { Turn = FromTo(up, normal) } : change;
    }

    /**
     * The normal of a surface from three nearby hits (the pick point and one step right and down on screen),
     * turned to face the eye. Null when the hits do not span a plane.
     */
    public static Vector3? SurfaceNormal(Vector3 hit, Vector3 right, Vector3 down, Vector3 eye)
    {
        var normal = Vector3.Cross(right - hit, down - hit);
        if (normal.LengthSquared() < 1e-14f || !float.IsFinite(normal.LengthSquared())) return null;
        normal = Vector3.Normalize(normal);
        return Vector3.Dot(normal, eye - hit) < 0 ? -normal : normal;
    }

    /** Applies the settings to one drag; the handles call it every frame while snapping is on. */
    public static TransformChange Apply(TransformChange change, Vector3 pivot, Vector3 turnAxis, float turnRadians, SnapSettings settings)
    {
        var result = change;
        if (change.Move != Vector3.Zero) result = result with { Move = SnapMove(pivot, change.Move, settings.MoveStep) };
        if (change.Turn != Quaternion.Identity && settings.AngleStepDegrees > 0)
            result = result with { Turn = SnapTurn(turnAxis, turnRadians, settings.AngleStepDegrees) };
        if (change.Scale != Vector3.One) result = result with { Scale = SnapScale(change.Scale, settings.ScaleStep) };
        return result;
    }
}
