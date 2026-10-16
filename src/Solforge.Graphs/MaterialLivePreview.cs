/**
 * File: LiveDebounce: rate limiter for live material preview updates while a node value is dragged (used by
 * Editor/NodeGraphLivePreview.cs).
 */
using System.Globalization;

namespace Solforge.Graphs;

/**
 * Holds back a changing value until it has been still for Delay seconds, or MaxWait seconds have passed since it
 * first changed, so a drag still updates a few times a second. Times are any increasing clock in seconds.
 */
public sealed class LiveDebounce(double delay = .05, double maxWait = .1)
{
    private string? sent, pending;
    private double firstChange, lastChange;

    public double Delay { get; } = delay;
    public double MaxWait { get; } = maxWait;
    /** The value last let through; null before the first or after Reset. */
    public string? Sent => sent;

    /** Records the current value; true when the caller should send it now. */
    public bool Update(string value, double now)
    {
        if (value == sent) { pending = null; return false; }
        if (value != pending)
        {
            if (pending is null) firstChange = now;
            pending = value;
            lastChange = now;
        }
        if (now - lastChange < Delay && now - firstChange < MaxWait) return false;
        sent = value;
        pending = null;
        return true;
    }

    /** Forgets what was sent, so the next value goes through again. */
    public void Reset() { sent = null; pending = null; }
}

/** Live preview helpers for material graphs: unsaved field edits and the identity of what a compile writes. */
public static class MaterialLivePreview
{
    /**
     * A copy of the document with in-progress field edits applied: keys are "nodeId/property" (as the details panel
     * keeps them), values the text typed so far. The document itself is not changed.
     */
    public static GraphDocument WithDrafts(GraphDocument document, IReadOnlyDictionary<string, string> drafts)
    {
        if (drafts.Count == 0) return document;
        var copy = GraphDocument.FromJson(document.ToJson());
        var nodes = copy.Nodes.Concat(copy.Functions.SelectMany(function => function.Nodes))
            .GroupBy(node => node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var (key, value) in drafts)
        {
            int slash = key.IndexOf('/');
            if (slash <= 0 || slash == key.Length - 1) continue;
            if (nodes.TryGetValue(key[..slash], out var node)) node.Properties[key[(slash + 1)..]] = value;
        }
        return copy;
    }

    /** A stable key of drafts, to tell when they changed. */
    public static string DraftKey(IReadOnlyDictionary<string, string> drafts) =>
        drafts.Count == 0 ? "" : string.Join("\n", drafts.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Key + "=" + item.Value));

    /**
     * What a compile writes to the material: target, constants and texture slots. Equal signatures preview the same,
     * so preview-only pins and node positions never send a new override.
     */
    public static string Signature(MaterialCompileResult result)
    {
        var parts = new List<string> { result.Target.Trim() };
        foreach (var write in result.Constants)
            parts.Add(string.Join("/", write.Stage, write.Bank, write.Vector.ToString(CultureInfo.InvariantCulture), write.Mask,
                string.Join(",", write.Values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)))));
        foreach (var write in result.Textures)
            parts.Add(string.Join("/", "tex", write.Stage, write.Slot.ToString(CultureInfo.InvariantCulture), write.Texture.Trim()));
        return string.Join(";", parts);
    }
}
