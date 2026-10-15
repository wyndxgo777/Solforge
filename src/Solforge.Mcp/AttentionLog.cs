using System.Text.Json.Nodes;

namespace Solforge.Mcp;

public enum AttentionKind { Hover, Select, Focus, Pin, Open }

public sealed record AttentionEvent(DateTimeOffset Time, AttentionKind Kind, string Id, string Label);

/**
 * What the user hovered, selected, focused, pinned or opened, newest last, so "the one I just clicked" and
 * "that tube" resolve. A repeat of the newest event is folded into it. Thread-safe.
 */
public sealed class AttentionLog(int capacity = 256)
{
    private readonly LinkedList<AttentionEvent> events = new();
    private readonly Lock gate = new();
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    public long Revision { get; private set; }

    public void Record(AttentionKind kind, string id, string label)
    {
        lock (gate)
        {
            if (events.Last?.Value is { } last && last.Kind == kind && last.Id == id) events.RemoveLast();
            events.AddLast(new AttentionEvent(Clock(), kind, id, label));
            while (events.Count > capacity) events.RemoveFirst();
            Revision++;
        }
    }

    /** The last n events, newest first. */
    public IReadOnlyList<AttentionEvent> Recent(int n, AttentionKind? kind = null)
    {
        lock (gate)
        {
            var result = new List<AttentionEvent>();
            for (var node = events.Last; node is not null && result.Count < n; node = node.Previous)
                if (kind is null || node.Value.Kind == kind) result.Add(node.Value);
            return result;
        }
    }

    /** Ids seen recently, weighted 1 (newest) down toward 0, for find_object ranking. */
    public IReadOnlyDictionary<string, float> Recency(int window = 32)
    {
        var weights = new Dictionary<string, float>();
        var recent = Recent(window);
        for (int i = 0; i < recent.Count; i++) weights.TryAdd(recent[i].Id, 1f - (float)i / window);
        return weights;
    }

    public JsonArray ToJson(int n)
    {
        var now = Clock();
        var array = new JsonArray();
        foreach (var item in Recent(n))
            array.Add(new JsonObject
            {
                ["time"] = item.Time.ToString("O"),
                ["secondsAgo"] = Math.Round((now - item.Time).TotalSeconds, 1),
                ["kind"] = item.Kind.ToString().ToLowerInvariant(),
                ["id"] = item.Id,
                ["label"] = item.Label
            });
        return array;
    }
}
