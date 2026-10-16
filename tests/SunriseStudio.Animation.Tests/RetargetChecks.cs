using SunriseStudio.Animation;
using SunriseStudio.Packages;

/** Clips authored on another body play at the rig's size, and clips from a larger rig still apply. */
internal static class RetargetChecks
{
    /** Runs the same kind of checks against the real packages of the owned installation. */
    internal static void Owned(PackageCatalog catalog)
    {
        // Legionary and Centurion share clips; their tables scale them by 1.35 and 1.25.
        foreach (var (entity, scale) in new[] { (0x80B83C48u, 1.35f), (0x80B82711u, 1.25f) })
        {
            var rig = new SkeletonReader(catalog).Read(entity);
            var binding = new AnimationRigReader(catalog).Read(rig);
            var clip = new AnimationControlReader(catalog).Read(0x80C19B62);
            Require(clip.RetargetKeys.Contains(0x879752D7) && binding.TranslationScale(clip) == scale,
                $"Entity {entity:X8} did not pick its retarget scale for clip 80C19B62.");
            var pose = rig.CreateBindPose();
            binding.Apply(clip, 0, pose);
            // b_spine_2 is placed by the clip's translation; unscaled it sits at 0.741 of its bind length.
            var spine = rig.Bones.Single(bone => bone.Name.StartsWith("b_spine_2", StringComparison.Ordinal));
            var parent = rig.Bones[spine.ParentIndex];
            float bind = (spine.BindObject.Translation - parent.BindObject.Translation).Length();
            float posed = (pose.ObjectTransforms[spine.Index].Translation - pose.ObjectTransforms[parent.Index].Translation).Length();
            Require(MathF.Abs(posed / bind - 1) < .01f, $"Entity {entity:X8} spine is {posed / bind:0.###} of its bind length.");
        }
        // Keys are packed u32s; a wider stride reads the next array's 0x80809FBD marker as a key.
        var twoKeys = new AnimationControlReader(catalog).Read(0x80BB826E).RetargetKeys;
        Require(twoKeys.SequenceEqual([0x07A8E7FEu, 0xEE40BBAEu]),
            $"Clip 80BB826E retarget keys are {string.Join(",", twoKeys.Select(key => key.ToString("X8")))}.");
        // Psion clips come from a 77-node, 69-control rig; the leading group still drives its 44 controls.
        var psion = new SkeletonReader(catalog).Read(0x80C00974);
        var psionBinding = new AnimationRigReader(catalog).Read(psion);
        var psionClip = new AnimationControlReader(catalog).Read(0x80C1A913);
        Require(psionClip.ControlCount != psionBinding.Controls.Count && psionBinding.TranslationScale(psionClip) == .87f,
            "The Psion fixture no longer covers a clip from a larger rig with a retarget scale.");
        psionBinding.Apply(psionClip, 0, psion.CreateBindPose());
        Console.WriteLine("animation_retarget legionary=1.35 centurion=1.25 spine=bind_length two_keys=packed psion_larger_rig=applies scale=0.87");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
