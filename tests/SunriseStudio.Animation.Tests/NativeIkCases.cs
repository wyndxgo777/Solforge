using System.Numerics;
using System.Text.Json;
using SunriseStudio.Animation;
using SunriseStudio.Packages;

/** Diagnostic tool that dumps native IK solver inputs for fixed installed rigs so the managed solver can be compared against native output offline. */
internal static class NativeIkCases
{
    /** Native and managed solvers receive the same pre-solve transforms; pole cases add the authored pole. */
    internal static void Write(string game, string path)
    {
        using var catalog = PackageCatalog.Open(game);
        var cases = new List<object>();
        foreach (var pair in new[] { (0x80B82FE3u, 0x80BFBEB8u), (0x80C0FA98u, 0x80FE2C08u), (0x80BAB778u, 0x80BC9FDBu) })
        {
            var rig = new SkeletonReader(catalog).Read(pair.Item1);
            var binding = new AnimationRigReader(catalog).Read(rig);
            var clip = new AnimationControlReader(catalog).Read(pair.Item2);
            foreach (float phase in new[] { 0f, .25f, .5f, .75f, 1f })
            {
                var values = binding.Controls.Select(control => control.DefaultValue).ToArray();
                int count = binding.CompatibleControlCount(clip);
                foreach (var sample in clip.Sample(clip.DurationSeconds * phase))
                {
                    if (sample.ControlIndex >= count) continue;
                    var value = values[sample.ControlIndex];
                    values[sample.ControlIndex] = new(sample.HasRotation ? sample.Rotation : value.Rotation,
                        sample.HasTranslation ? sample.Translation : value.Translation,
                        sample.HasScale ? sample.Scale : value.Scale);
                }
                var original = binding.Evaluate(values).ToArray();
                foreach (int index in binding.IkControlIndices)
                {
                    var control = binding.Controls[index];
                    int end = control.BoneIndex, middle = rig.Bones[end].ParentIndex, root = rig.Bones[middle].ParentIndex;
                    var middlePose = AnimationIkSolver.Compose(original[root], AnimationIkSolver.Compose(
                        AnimationIkSolver.Inverse(rig.Bones[root].BindObject), rig.Bones[middle].BindObject));
                    var endPose = AnimationIkSolver.Compose(middlePose, AnimationIkSolver.Compose(
                        AnimationIkSolver.Inverse(rig.Bones[middle].BindObject), rig.Bones[end].BindObject));
                    var plane = original.ToArray();
                    AnimationIkSolver.Apply(rig, binding.Controls, [index], false, plane);
                    cases.Add(new { solver = "plane", entity = $"{pair.Item1:X8}", clip = $"{clip.TagHash:X8}", phase, index,
                        target = Pack(original[end]), pole = new float[4],
                        root = Pack(original[root]), middle = Pack(middlePose), end = Pack(endPose),
                        managed = new[] { Pack(plane[root]), Pack(plane[middle]), Pack(plane[end]) } });
                    if (control.ChainMode != 0 || control.PoleAxis is < 1 or > 6) continue;
                    var axis = ((control.PoleAxis - 1) % 3) switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
                    if (control.PoleAxis > 3) axis = -axis;
                    axis = Vector3.Transform(axis, original[root].Rotation);
                    var output = original.ToArray();
                    AnimationIkSolver.Apply(rig, binding.Controls, [index], true, output);
                    cases.Add(new { solver = "pole", entity = $"{pair.Item1:X8}", clip = $"{clip.TagHash:X8}", phase, index,
                        target = Pack(original[end]), pole = new[] { axis.X, axis.Y, axis.Z, 0 },
                        root = Pack(original[root]), middle = Pack(middlePose), end = Pack(endPose),
                        managed = new[] { Pack(output[root]), Pack(output[middle]), Pack(output[end]) } });
                }
            }
        }
        File.WriteAllText(path, JsonSerializer.Serialize(cases));
        Console.WriteLine($"native_ik_cases count={cases.Count}");
    }

    private static float[] Pack(BoneTransform value) => [value.Rotation.X, value.Rotation.Y, value.Rotation.Z,
        value.Rotation.W, value.Translation.X, value.Translation.Y, value.Translation.Z, value.Scale];
}
