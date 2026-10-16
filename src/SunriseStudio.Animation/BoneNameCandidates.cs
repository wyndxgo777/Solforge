namespace SunriseStudio.Animation;

/** FNV-1 matches are candidate names, not plaintext names carried by the skeleton. */
public static class BoneNameCandidates
{
    private static readonly IReadOnlyDictionary<uint, string> names = Build();
    /** Candidate plaintext name for a bone hash, or null when none matches. */
    public static string? Resolve(uint hash) => names.GetValueOrDefault(hash);

    /** Candidate hash collisions remain unresolved. */
    private static IReadOnlyDictionary<uint, string> Build()
    {
        string[] words = ["b_pedestal", "b_pelvis", "b_spine_1", "b_spine_2", "b_spine_3",
            "b_neck_1", "b_neck_2", "b_head"];
        var candidates = new List<string>(words);
        foreach (string side in new[] { "l", "r" })
        {
            foreach (string part in new[] { "thigh", "calf", "foot", "toe", "clav",
                "upperarm", "forearm", "hand" })
                candidates.Add($"b_{side}_{part}");
        }
        return candidates.GroupBy(AnimationClipNames.Hash).Where(g => g.Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(g => g.Key, g => g.First() + " (candidate)");
    }
}
