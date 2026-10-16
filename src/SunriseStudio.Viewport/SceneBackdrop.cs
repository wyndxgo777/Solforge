using System.Diagnostics;
using System.Numerics;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** How backdrop bubbles are shown: following the camera, fixed or all. */
public enum SceneBackdropMode { FollowCamera, Fixed, All }

/** One bubble supplies the sky; layer-based backdrop detection is a viewer heuristic. */
public sealed class SceneBackdrop
{
    private readonly Dictionary<uint, uint> aliases = [];
    private readonly HashSet<MeshInstance> backdrop = [];
    private readonly List<(MeshInstance Instance, SelectionBounds Bounds)> anchors = [];
    // Each anchor's selection index, in anchor order.
    private readonly List<int> anchorIndices = [];
    // The anchors' sphere centres, radii and canonical bubbles, in anchor order.
    private Vector3[] anchorCenters = [];
    private float[] anchorRadii = [];
    private uint[] anchorBubbles = [];
    // Each anchor's prepared visibility, valid for shownRevision while the filters are cached.
    private bool[] anchorShown = [];
    private long shownRevision = -1;
    // Anchors per parallel task of a cached scan.
    private const int AnchorsPerTask = 65536;
    private uint defaultBubble;
    private long nextUpdate;
    private bool scanned;
    private Vector3 scannedPivot;
    private long scannedRevision;

    public SceneBackdropMode Mode { get; set; } = SceneBackdropMode.FollowCamera;
    public uint BubbleHash { get; set; }
    public uint ActiveBubbleHash { get; private set; }

    /** A map layer belongs to the backdrop when most of its parts are sky or large scenery. */
    internal void SetScene(SceneDocument scene, SceneSelection selection)
    {
        aliases.Clear(); backdrop.Clear(); anchors.Clear(); anchorIndices.Clear(); nextUpdate = 0; scanned = false;
        foreach (var bubble in scene.Bubbles)
        {
            uint canonical = bubble.NameHash == 0 ? bubble.TagHash : bubble.NameHash;
            aliases[bubble.TagHash] = canonical;
            aliases[canonical] = canonical;
        }
        var parts = new Dictionary<int, int>();
        var largeParts = new Dictionary<int, int>();
        foreach (var item in scene.Instances.Where(x => x.Mesh.LodLevel == 0))
        {
            bool large = item.Layer is SceneLayer.Skybox or SceneLayer.Distant || selection.GetBounds(item).Radius > 2000;
            foreach (int layer in item.MapContainers.SelectMany(x => x.MapGlobalIndices))
            {
                parts[layer] = parts.GetValueOrDefault(layer) + 1;
                if (large) largeParts[layer] = largeParts.GetValueOrDefault(layer) + 1;
            }
        }
        var layers = parts.Where(x => largeParts.GetValueOrDefault(x.Key) * 2 > x.Value).Select(x => x.Key).ToHashSet();
        foreach (var item in scene.Instances)
        {
            bool isBackdrop = item.Layer is SceneLayer.Skybox or SceneLayer.Distant || item.MapContainers.Count > 0 &&
                item.MapContainers.All(x => x.MapGlobalIndices.Count > 0 && x.MapGlobalIndices.All(layers.Contains));
            if (isBackdrop) backdrop.Add(item);
            else if (item.BubbleHash != 0 && selection.GetBounds(item) is { IsFinite: true } bounds)
            { anchors.Add((item, bounds)); anchorIndices.Add(selection.IndexOf(item)); }
        }
        anchorCenters = anchors.Select(anchor => anchor.Bounds.Center).ToArray();
        anchorRadii = anchors.Select(anchor => anchor.Bounds.Radius).ToArray();
        anchorBubbles = anchors.Select(anchor => CanonicalBubble(anchor.Instance.BubbleHash)).ToArray();
        anchorShown = new bool[anchors.Count];
        shownRevision = -1;
        defaultBubble = scene.Instances.Where(x => x.BubbleHash != 0 && x.Mesh.LodLevel == 0)
            .GroupBy(x => CanonicalBubble(x.BubbleHash)).OrderByDescending(x => x.Count())
            .Select(x => x.Key).FirstOrDefault();
        ActiveBubbleHash = defaultBubble;
    }

    /** Canonical hash for a bubble alias. */
    public uint CanonicalBubble(uint hash) => aliases.GetValueOrDefault(hash, hash);
    /** True when the item is a backdrop mesh. */
    public bool IsBackdrop(object item) => item is MeshInstance mesh && backdrop.Contains(mesh);

    /** False only for backdrop meshes of a bubble other than the active one. */
    public bool IsVisible(object item) => Mode == SceneBackdropMode.All || ActiveBubbleHash == 0 ||
        item is not MeshInstance mesh || !backdrop.Contains(mesh) || mesh.BubbleHash == 0 ||
        CanonicalBubble(mesh.BubbleHash) == ActiveBubbleHash;

    /**
     * Follow mode uses the smallest containing object, then the nearest object outside the pivot.
     * Call after visibility.Prepare for the current frame.
     * @param contextShowsAnchors True when visibility.ContextFilter is null or passes every anchor.
     */
    internal void Update(Vector3 pivot, SceneVisibility visibility, bool contextShowsAnchors)
    {
        if (Mode != SceneBackdropMode.FollowCamera) scanned = false;
        if (Mode == SceneBackdropMode.All) return;
        if (Mode == SceneBackdropMode.Fixed)
        {
            ActiveBubbleHash = BubbleHash == 0 ? defaultBubble : CanonicalBubble(BubbleHash);
            return;
        }
        bool cached = contextShowsAnchors && visibility.FiltersCached;
        // The same pivot and hide state choose the same bubble.
        if (cached && scanned && pivot == scannedPivot && visibility.Revision == scannedRevision) return;
        long now = Stopwatch.GetTimestamp();
        if (now < nextUpdate) return;
        nextUpdate = now + Stopwatch.Frequency / 2;
        scanned = cached; scannedPivot = pivot; scannedRevision = visibility.Revision;
        Nearest best;
        if (cached)
        {
            // Prepared visibility changes only with the revision, so it is read once per revision.
            if (shownRevision != visibility.Revision)
            {
                for (int index = 0; index < anchors.Count; index++)
                    anchorShown[index] = visibility.PreparedContentVisible(anchorIndices[index], anchors[index].Instance);
                shownRevision = visibility.Revision;
            }
            if (anchors.Count <= AnchorsPerTask) best = Scan(pivot, null, 0, anchors.Count);
            else
            {
                // Each range keeps its first best anchor; merging ranges in order keeps the first overall.
                var ranges = new Nearest[(anchors.Count + AnchorsPerTask - 1) / AnchorsPerTask];
                Parallel.For(0, ranges.Length, range => ranges[range] = Scan(pivot, null, range * AnchorsPerTask,
                    Math.Min(anchors.Count, (range + 1) * AnchorsPerTask)));
                best = ranges[0];
                foreach (var next in ranges.AsSpan(1)) best = best.Merge(next);
            }
        }
        else best = Scan(pivot, visibility, 0, anchors.Count);
        uint insideBubble = best.Inside < 0 ? 0 : anchorBubbles[best.Inside];
        uint outsideBubble = best.Outside < 0 ? 0 : anchorBubbles[best.Outside];
        uint bubble = insideBubble != 0 ? insideBubble : outsideBubble;
        ActiveBubbleHash = bubble == 0 ? defaultBubble : bubble;
    }

    /** The smallest anchor around the pivot and the nearest one outside it; -1 when there is none. */
    private readonly record struct Nearest(float InsideRadius, int Inside, float OutsideGap, int Outside)
    {
        /** Takes a later range's anchor only when it is strictly better, so ties keep the earlier one. */
        public Nearest Merge(Nearest next) => new(
            next.InsideRadius < InsideRadius ? next.InsideRadius : InsideRadius, next.InsideRadius < InsideRadius ? next.Inside : Inside,
            next.OutsideGap < OutsideGap ? next.OutsideGap : OutsideGap, next.OutsideGap < OutsideGap ? next.Outside : Outside);
    }

    /**
     * Scans anchors from to to in order.
     * @param live Filters each anchor live when set; null reads anchorShown.
     */
    private Nearest Scan(Vector3 pivot, SceneVisibility? live, int from, int to)
    {
        float insideRadius = float.PositiveInfinity, outsideGap = float.PositiveInfinity;
        int inside = -1, outside = -1;
        for (int index = from; index < to; index++)
        {
            if (live is null ? !anchorShown[index] : !live.IsVisible(anchors[index].Instance)) continue;
            float radius = anchorRadii[index];
            float gap = Vector3.Distance(pivot, anchorCenters[index]) - radius;
            if (gap <= 0 && radius < insideRadius) { insideRadius = radius; inside = index; }
            else if (gap > 0 && gap < outsideGap) { outsideGap = gap; outside = index; }
        }
        return new(insideRadius, inside, outsideGap, outside);
    }
}
