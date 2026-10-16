/** File: Splits root-bone motion out of a pose delta according to the clip's extraction mode. */
using System.Numerics;

namespace SunriseStudio.Animation;

/** Frame the extracted root translation is expressed in. */
public enum RootMotionTranslationSpace { None, ClipMotion, SampledStart }

/** Rotation is an axis multiplied by radians, with magnitude no greater than pi. */
public readonly record struct ExtractedRootMotionDelta(Vector3 Translation,
    Vector3 RotationVectorRadians, RootMotionTranslationSpace TranslationSpace);

/** Static helpers for root-motion extraction. */
internal static class RootMotionExtraction
{
    /** Mode one receives a clip-space difference; other modes receive a start-relative SRT. */
    internal static ExtractedRootMotionDelta Filter(BoneTransform delta, byte mode)
    {
        Vector3 translation = delta.Translation;
        if (mode is 1 or 2) translation.Z = 0;
        if (mode == 1)
            return new(translation, Vector3.Zero, RootMotionTranslationSpace.ClipMotion);
        var rotation = delta.Rotation;
        if (mode is 2 or 3)
        {
            Vector3 turnedAxis = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, rotation));
            if (Vector3.Dot(Vector3.UnitZ, turnedAxis) < -0.9999f)
                throw new NotSupportedException("Root-motion Z twist has an undefined opposite-axis pole.");
            var swing = AnimationIkSolver.FromTo(Vector3.UnitZ, turnedAxis, Vector3.UnitZ);
            rotation = Quaternion.Normalize(Quaternion.Conjugate(swing) * rotation);
        }
        var vector = RotationVector(rotation);
        if (mode is 2 or 3) vector = new(0, 0, MathF.Sign(vector.Z) * vector.Length());
        return new(translation, vector, RootMotionTranslationSpace.SampledStart);
    }

    /** Native axis-angle conversion folds rotations above pi onto the opposite axis. */
    private static Vector3 RotationVector(Quaternion rotation)
    {
        Vector3 vector = new(rotation.X, rotation.Y, rotation.Z);
        float length = vector.Length();
        if (length == 0) return Vector3.Zero;
        float angle = 2 * MathF.Atan2(length, rotation.W);
        var axis = vector / length;
        if (angle > MathF.PI)
        {
            angle = MathF.Tau - angle;
            axis = -axis;
        }
        return axis * angle;
    }
}
