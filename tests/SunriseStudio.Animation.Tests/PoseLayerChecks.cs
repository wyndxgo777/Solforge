using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Packages;

/** Pose-space selection, aim frames and blend-entry merging. */
static class PoseLayerChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run()
    {
        // Two triangles over a unit square; input inside the first picks its three corners.
        var plane = new AnimationPoseSpace(AnimationPoseSpaceKind.Plane,
            [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [[0, 1, 2, -1, -1, 1], [0, 2, 3, 0, -1, -1]]);
        var inside = plane.Select(.75f, .25f);
        Require(inside.Count == 3 && MathF.Abs(inside.Sum(s => s.Weight) - 1) < 1e-5f && inside[0].Frame == 1,
            "Plane selection did not return the containing triangle by weight.");
        var outside = plane.Select(2, .5f);
        Require(outside.Where(s => s.Weight > 0).Select(s => s.Frame).Order().SequenceEqual([1, 2])
            && MathF.Abs(outside[0].Weight - .5f) < 1e-5f, "Plane input outside did not project onto the nearest edge.");
        var line = new AnimationPoseSpace(AnimationPoseSpaceKind.Line,
            [new(0, -1), new(0, 0), new(0, 1)], [[0, 1, -1, 1], [1, 2, 0, -1]]);
        var half = line.Select(0, .5f);
        Require(half.Count == 2 && MathF.Abs(half[0].Weight - .5f) < 1e-5f, "Line selection did not split the segment.");
        Require(line.Select(0, 9)[0] is { Frame: 2, Weight: 1 }, "Line selection did not clamp past the end.");
        var disk = new AnimationPoseSpace(AnimationPoseSpaceKind.Disk,
            [new(-MathF.PI / 2, 0), new(0, 0), new(MathF.PI / 2, 0), new(MathF.PI, 0)],
            [[0, 1, 3, 1], [1, 2, 0, 2], [2, 3, 1, 3], [3, 0, 2, 0]]);
        var wrapped = disk.Select(-3 * MathF.PI / 4, 0);
        Require(wrapped.Select(s => s.Frame).Order().SequenceEqual([0, 3]) && MathF.Abs(wrapped[0].Weight - .5f) < 1e-4f,
            "Disk selection did not use the wrap segment.");
        CheckAimFrame();
        CheckMerge();
        Console.WriteLine("animation_pose_space synthetic=pass aim=pass merge=pass");
    }

    /** Every authored point must select its own frame at full weight. */
    public static void Owned(PackageCatalog catalog, uint entity)
    {
        var rig = new SkeletonReader(catalog).Read(entity);
        var binding = new AnimationRigReader(catalog).Read(rig);
        var reader = new AnimationControlReader(catalog);
        var kinds = new Dictionary<AnimationPoseSpaceKind, int>();
        int points = 0, misses = 0, unorderedDisks = 0;
        foreach (uint tag in new EntityAnimationCatalog(catalog).Read(entity).Select(link => link.ClipTag).Distinct())
        {
            var clip = reader.Read(tag);
            if (clip.PoseSpace is not { } space) continue;
            kinds[space.Kind] = kinds.GetValueOrDefault(space.Kind) + 1;
            // A disk whose ring starts mid-range blends only its wrap pair, as the game does.
            if (space.Kind == AnimationPoseSpaceKind.Disk
                && space.Points[space.Simplices[0][0]].X > space.Points[space.Simplices[^1][0]].X)
            {
                unorderedDisks++;
                continue;
            }
            for (int frame = 0; frame < space.Points.Count; frame++)
            {
                var picked = space.Select(space.Points[frame].X, space.Points[frame].Y);
                points++;
                if (picked.Count == 0 || picked[0].Frame != frame || picked[0].Weight < .999f) misses++;
            }
            var center = (space.Bounds.Minimum + space.Bounds.Maximum) / 2;
            binding.ApplyStack([new(1, 1, AnimationLayerStack.PoseSamples(clip, center.X, center.Y))], rig.CreateBindPose());
        }
        var layers = new EntityAnimationCatalog(catalog).Read(entity).Select(link => link.DefinitionTag).Distinct()
            .Select(definition => new EntityAnimationCatalog(catalog).ReadLayerNames(definition)).ToArray();
        Require(layers.All(list => list.Count > 0 && AnimationLayerNames.Resolve(list[0]) == "base"),
            "Animation definition layer list does not start with base.");
        Require(points > 0 && misses == 0, $"Pose space points selected another frame: {misses} of {points}.");
        Console.WriteLine($"animation_pose_space entity={entity:X8} spaces={string.Join(",", kinds.Select(k => $"{k.Key}:{k.Value}"))} points={points} misses={misses} unordered_disks={unorderedDisks} layers={layers.Sum(list => list.Count)}");
    }

    /** Half weight turns +X halfway toward +Y about +Z. */
    private static void CheckAimFrame()
    {
        var bone = new SkeletonBone(0, 0, "root", -1, -1, -1,
            BoneTransform.Identity, BoneTransform.Identity, BoneTransform.Identity);
        var rig = new SkeletonRig(0, 0, [], [bone], [0], 0, []);
        var nested = new AnimationFrameReference(-1, -1, 2, null, 0, -1, 0);
        var aim = new AnimationFrameReference(-1, -1, 6, nested, .5f, -1, 0);
        var control = new AnimationRigControl(0, 0, 0, -1, 0, 255, aim, nested, BoneTransform.Identity);
        var binding = new AnimationRigBinding(rig, 0, [control], [1], [new(1, 1)], [], true, [])
            { AimDirection = Vector3.UnitY };
        var turned = Vector3.Transform(Vector3.UnitX, binding.Evaluate([BoneTransform.Identity])[0].Rotation);
        Require(Vector3.Distance(turned, Vector3.Normalize(new(1, 1, 0))) < 1e-5f, "Aim frame did not follow its weight.");
        binding.AimDirection = Vector3.UnitX;
        Require(binding.Evaluate([BoneTransform.Identity])[0].Rotation == Quaternion.Identity,
            "Rest aim did not give the identity rotation.");
    }

    /** A partial later entry on the same layer composites over the earlier one. */
    private static void CheckMerge()
    {
        var clip = new AnimationControlClip(1, 2, 1, 1, 0, [], [], [new(1, 1)]);
        var merged = AnimationLayerStack.Prepare([new(2, .5f, [new(clip, 0, 2)]), new(1, 1, [new(clip, 0, 1)]),
            new(2, .5f, [new(clip, 1, 1)])]);
        Require(merged.Count == 2 && merged[0].LayerIndex == 1 && MathF.Abs(merged[1].Weight - .75f) < 1e-6f
            && MathF.Abs(merged[1].Samples[1].Weight - 2f / 3) < 1e-6f, "Equal layers did not composite in order.");
        Require(AnimationLayerStack.SequentialFactors([.5f, .25f, .25f]).SequenceEqual([1, 1f / 3, .25f]),
            "Sequential blend factors are wrong.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidDataException(message);
    }
}
