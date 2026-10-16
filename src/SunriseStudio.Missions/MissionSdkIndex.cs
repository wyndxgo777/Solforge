using System.Runtime.CompilerServices;

namespace SunriseStudio.Missions;

/** Lookups over one script's SDK entries; built once per list. */
internal sealed class MissionSdkIndex
{
    private static readonly ConditionalWeakTable<IReadOnlyList<MissionSdkReference>, MissionSdkIndex> Cache = new();
    private readonly IReadOnlyList<MissionSdkReference> list;
    // Each value keeps list order, so the first index is the first match.
    private readonly Dictionary<string, List<int>> byExpression = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> byIdentity = new(StringComparer.Ordinal);

    private MissionSdkIndex(IReadOnlyList<MissionSdkReference> list)
    {
        this.list = list;
        for (int i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            if (!byExpression.TryGetValue(entry.Expression, out var indexes)) byExpression.Add(entry.Expression, indexes = []);
            indexes.Add(i);
            if (entry.Identity is { } identity) byIdentity.TryAdd(identity, i);
        }
    }

    /** Index for a reference list, built once per list. */
    public static MissionSdkIndex For(IReadOnlyList<MissionSdkReference> list) => Cache.GetValue(list, value => new(value));

    /** First reference with this expression, or null. */
    public MissionSdkReference? First(string expression) => byExpression.TryGetValue(expression, out var indexes) ? list[indexes[0]] : null;

    /** The first entry with this expression or this identity, in list order; a null identity matches nothing. */
    public MissionSdkReference? Resolve(string expression, string? identity)
    {
        int found = byExpression.TryGetValue(expression, out var indexes) ? indexes[0] : int.MaxValue;
        if (identity is not null && byIdentity.TryGetValue(identity, out int other)) found = Math.Min(found, other);
        return found == int.MaxValue ? null : list[found];
    }

    /** True when this reference is in the list. */
    public bool Contains(MissionSdkReference value) =>
        byExpression.TryGetValue(value.Expression, out var indexes) && indexes.Any(index => list[index].Equals(value));
}
