using System.Numerics;

namespace SunriseStudio.Animation;

/** Two-segment limbs bend around an authored pole axis or in the plane of the upper segment. */
internal static class AnimationIkSolver
{
    // Native plane solves stop short of full extension and full folding.
    private const float MaximumReach = 0.98f, MinimumReach = 1.02f;

    // Native plane solves never reach a straight elbow.
    private const float MinimumSineSquared = 1e-8f;

    /** Each solve reads the original control pose and writes its output and child transforms. */
    internal static void Apply(SkeletonRig rig, IReadOnlyList<AnimationRigControl> controls,
        IReadOnlyList<int> indices, bool usePoleAxes, BoneTransform[] output)
    {
        var original = output.ToArray();
        foreach (int index in indices)
        {
            var control = controls[index];
            int end = control.BoneIndex;
            int middle = rig.Bones[end].ParentIndex;
            int root = middle < 0 ? -1 : rig.Bones[middle].ParentIndex;
            if (root < 0) throw new NotSupportedException("IK control has no two-segment parent chain.");
            var target = original[end];
            output[middle] = Compose(original[root],
                Compose(Inverse(rig.Bones[root].BindObject), rig.Bones[middle].BindObject));
            output[end] = Compose(output[middle],
                Compose(Inverse(rig.Bones[middle].BindObject), rig.Bones[end].BindObject));
            var rootValue = output[root];
            if (!usePoleAxes || control.ChainMode != 0)
                SolveInPlane(target, ref rootValue, ref output[middle], ref output[end]);
            else if (control.PoleAxis is >= 1 and <= 6)
            {
                var axis = ((control.PoleAxis - 1) % 3) switch
                { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
                if (control.PoleAxis > 3) axis = -axis;
                axis = Vector3.Transform(axis, output[root].Rotation);
                Solve(target, axis, ref rootValue, ref output[middle], ref output[end]);
            }
            // Other pole codes keep the reset chain and skip the solve and child carry.
            else continue;
            if (root != 0) output[root] = rootValue;
            var excluded = new HashSet<int> { root, middle, end };
            foreach (int ancestor in new[] { root, middle, end })
            {
                var delta = Compose(output[ancestor], Inverse(original[ancestor]));
                foreach (int child in Descendants(rig, ancestor, excluded))
                    output[child] = Compose(delta, original[child]);
            }
        }
    }

    /** Native child traversal stops at other joints in the solved chain. */
    private static IEnumerable<int> Descendants(SkeletonRig rig, int ancestor, HashSet<int> excluded)
    {
        var pending = new Stack<int>();
        int first = rig.Bones[ancestor].FirstChildIndex;
        if (first >= 0) pending.Push(first);
        while (pending.TryPop(out int bone))
        {
            var row = rig.Bones[bone];
            if (row.NextSiblingIndex >= 0) pending.Push(row.NextSiblingIndex);
            if (excluded.Contains(bone)) continue;
            yield return bone;
            if (row.FirstChildIndex >= 0) pending.Push(row.FirstChildIndex);
        }
    }

    /** Reach is clamped to the authored segment lengths before choosing the elbow position. */
    private static void Solve(BoneTransform target, Vector3 pole, ref BoneTransform root,
        ref BoneTransform middle, ref BoneTransform end)
    {
        Vector3 a = root.Translation, ab = middle.Translation - a, bc = end.Translation - middle.Translation;
        float first = ab.Length(), second = bc.Length();
        if (first < 0.0001f || second < 0.0001f) return;
        Vector3 aim = target.Translation - a;
        float distance = Math.Clamp(aim.Length(), MathF.Abs(first - second), first + second);
        Vector3 x = aim.LengthSquared() >= 0.0001f ? Vector3.Normalize(aim)
            : Vector3.Transform(Vector3.UnitX, root.Rotation);
        // The authored pole is the local Z axis of the limb solve frame.
        Vector3 y = Vector3.Cross(pole, x);
        if (y.LengthSquared() < 1e-12f)
            throw new NotSupportedException("IK pole and target direction are parallel.");
        y = Vector3.Normalize(y);
        float denominator = 2 * first * distance;
        float cosine = denominator <= 0.0001f ? 0
            : (first * first + distance * distance - second * second) / denominator;
        float sine = -MathF.Sqrt(MathF.Max(1 - cosine * cosine, 0));
        Vector3 elbow = a + x * (first * cosine) + y * (first * sine);
        Vector3 wrist = a + x * distance;
        root = root with
        {
            Rotation = Quaternion.Normalize(FromTo(Vector3.Normalize(ab),
            Vector3.Normalize(elbow - a), pole) * root.Rotation),
            Scale = 1
        };
        middle = middle with
        {
            Translation = elbow,
            Rotation = Quaternion.Normalize(
            FromTo(Vector3.Normalize(bc), Vector3.Normalize(wrist - elbow), pole) * middle.Rotation),
            Scale = 1
        };
        end = target with { Translation = wrist, Scale = 1 };
    }

    /** Without a pole the elbow bends toward the upper segment and each bone keeps its Z axis. */
    private static void SolveInPlane(BoneTransform target, ref BoneTransform root,
        ref BoneTransform middle, ref BoneTransform end)
    {
        Vector3 a = root.Translation, ab = middle.Translation - a, bc = end.Translation - middle.Translation;
        float first = ab.Length(), second = bc.Length();
        if (first < 0.0001f || second < 0.0001f) return;
        var rootAim = AimRotation(root.Rotation, ab);
        var middleAim = AimRotation(middle.Rotation, bc);
        Vector3 aim = target.Translation - a;
        float maximum = MaximumReach * (first + second);
        float minimum = MathF.Min(maximum, MinimumReach * MathF.Abs(first - second));
        float distance = Math.Clamp(aim.Length(), minimum, maximum);
        Vector3 wrist = a + NormalizeOrZero(aim) * distance;
        float denominator = 2 * first * distance;
        float cosine = denominator <= 0.0001f ? 0
            : (first * first + distance * distance - second * second) / denominator;
        float sine = MathF.Sqrt(MathF.Max(1 - cosine * cosine, MinimumSineSquared));
        var frame = LookRotation(aim, ab, Vector3.UnitY);
        Vector3 elbow = a + Vector3.Transform(new Vector3(first * cosine, first * sine, 0), frame);
        root = new(Reaim(rootAim, root.Rotation, elbow - a), a, 1);
        middle = new(Reaim(middleAim, middle.Rotation, wrist - elbow), elbow, 1);
        end = target with { Translation = wrist, Scale = 1 };
    }

    /** A bone already aimed down its segment is its own aim frame. */
    private static Quaternion AimRotation(Quaternion bone, Vector3 segment)
    {
        Vector3 axis = NormalizeOrZero(Vector3.Transform(Vector3.UnitX, bone));
        if (Vector3.Dot(NormalizeOrZero(segment), axis) >= 1) return bone;
        return LookRotation(segment, Vector3.Transform(Vector3.UnitZ, bone), Vector3.UnitZ);
    }

    /** The bone keeps its offset from an aim frame that turns to the new direction. */
    private static Quaternion Reaim(Quaternion aim, Quaternion bone, Vector3 direction)
    {
        var turned = LookRotation(direction, Vector3.Transform(Vector3.UnitZ, aim), Vector3.UnitZ);
        return Quaternion.Normalize(turned * Quaternion.Conjugate(aim) * bone);
    }

    /** Turns +X to the direction, then the given axis toward the reference. */
    private static Quaternion LookRotation(Vector3 direction, Vector3 reference, Vector3 axis)
    {
        Vector3 x = NormalizeOrZero(direction);
        Vector3 side = NormalizeOrZero(Vector3.Cross(Vector3.Cross(direction, reference), direction));
        var first = FromTo(Vector3.UnitX, x, side);
        var second = FromTo(Vector3.Transform(axis, first), side, x);
        return Quaternion.Normalize(second * first);
    }

    /** Native normalization returns zero for a zero vector. */
    internal static Vector3 NormalizeOrZero(Vector3 value)
    {
        float length = value.Length();
        return length > 0 ? value / length : Vector3.Zero;
    }

    /** Opposite vectors use the supplied pole to choose the half-turn axis. */
    internal static Quaternion FromTo(Vector3 from, Vector3 to, Vector3 pole)
    {
        if (Vector3.Dot(from, to) < -0.9999f)
        {
            Vector3 side = Vector3.Normalize(Vector3.Cross(from, pole));
            Vector3 axis = Vector3.Normalize(Vector3.Cross(from, side));
            return new Quaternion(axis, 0);
        }
        Vector3 half = Vector3.Normalize(from + to);
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, half), Vector3.Dot(from, half)));
    }

    /** Parent-then-child transform composition. */
    internal static BoneTransform Compose(BoneTransform parent, BoneTransform child) => new(
        Quaternion.Normalize(parent.Rotation * child.Rotation),
        Vector3.Transform(child.Translation, parent.Rotation) * parent.Scale + parent.Translation,
        parent.Scale * child.Scale);

    /** Inverse of a uniform-scale rigid transform. */
    internal static BoneTransform Inverse(BoneTransform value)
    {
        var rotation = Quaternion.Conjugate(Quaternion.Normalize(value.Rotation));
        return new(rotation, -Vector3.Transform(value.Translation, rotation) / value.Scale, 1 / value.Scale);
    }
}
