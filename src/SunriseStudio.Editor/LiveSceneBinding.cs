using System.Numerics;
using SunriseStudio.Live;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Native tuples join authored placements; a shared tuple is split by the placement identity. */
internal sealed class LiveSceneBinding(WorldDocument world)
{
    /** One loaded copy of a placement; a mission scene can hold the same placement once per state. */
    private sealed class Member(SceneObjectPlacement placement)
    {
        public SceneObjectPlacement Placement { get; } = placement;
        public List<MeshInstance> Meshes { get; } = [];
        public List<SceneMarker> Markers { get; } = [];
    }

    /** Copies with one tuple and one identity are one game object; a live row shows and moves all of them. */
    private sealed class Group((uint, uint) key, ulong identity, List<Member> members)
    {
        public (uint List, uint Index) Key { get; } = key;
        public ulong Identity { get; } = identity;
        public List<Member> Members { get; } = members;
    }

    private readonly Dictionary<(uint List, uint Index), List<Group>> groups = Build(world.Scene);
    private Dictionary<object, Group>? owners;
    private readonly Dictionary<MeshInstance, Matrix4x4> meshTransforms = [];
    private readonly Dictionary<SceneMarker, (Vector3 Position, Quaternion Rotation)> markerTransforms = [];
    private readonly Dictionary<SceneMarker, Vector3> markerExtents = [];
    private readonly Dictionary<Group, LivePlacement> poses = [];
    private readonly Dictionary<Group, LivePlacement> rowsByGroup = [];
    private readonly Dictionary<uint, Group> placedByHandle = [];
    private readonly HashSet<object> absent = new(ReferenceEqualityComparer.Instance);
    /** The groups `absent` was built from; it is rebuilt only when they change. */
    private HashSet<Group>? absentFrom;
    private LivePlacements? applied;
    private ulong expires;
    public WorldDocument World => world;
    public int Matched { get; private set; }
    public int Unmatched { get; private set; }
    public int Ambiguous { get; private set; }
    /** Authored placements the game has not spawned; hidden while the registry is complete. */
    public int Inactive { get; private set; }
    /** Spawned placements the game reports as not placed in the world or hidden; drawn as markers. */
    public int Dormant { get; private set; }
    /** False for authored items the registry showed to be absent from the game. */
    public bool IsVisible(object item) => !absent.Contains(item);
    /** Forgets the applied snapshot, poses and absences so the next apply starts clean. */
    public void InvalidatePreview() { applied = null; poses.Clear(); placedByHandle.Clear(); ClearAbsent(); }
    private void ClearAbsent() { absent.Clear(); absentFrom = null; Inactive = Dormant = 0; }

    /** Authored meshes the live row with this handle now moves; empty when none is placed. */
    public IEnumerable<MeshInstance> MeshesFor(uint handle) => placedByHandle.TryGetValue(handle, out var group)
        ? group.Members.SelectMany(member => member.Meshes) : [];
    /** Handles of the rows whose authored model Studio shows. */
    public IEnumerable<uint> ShownHandles => placedByHandle.Keys;

    /**
     * Applies one registry snapshot. Observed world scale replaces the authored placement scale.
     * Only a complete registry may hide an authored placement, because only it proves absence.
     */
    public void Apply(LivePlacements snapshot, AssetViewport viewport, ulong now)
    {
        if (ReferenceEquals(applied, snapshot) && now < expires) return;
        applied = snapshot; expires = 0;
        meshTransforms.Clear(); markerTransforms.Clear(); markerExtents.Clear(); rowsByGroup.Clear();
        placedByHandle.Clear();
        Matched = Unmatched = Ambiguous = 0;
        if (!snapshot.Available || !LiveWorldProtocol.Fresh(snapshot.Time, now))
        { poses.Clear(); ClearAbsent(); viewport.SetLiveTransforms(meshTransforms, markerTransforms); return; }
        expires = snapshot.Time + LiveWorldProtocol.StaleAfterMilliseconds + 1;
        bool complete = snapshot.Exhaustive;
        var slots = snapshot.Rows.GroupBy(row => row.Handle & LiveDynamicBinding.HandleIndexMask)
            .ToDictionary(group => group.Key, group => group.Count());
        var unresolved = new HashSet<(uint, uint)>();
        foreach (var byKey in snapshot.Rows.Where(row => !row.Dynamic).GroupBy(row => (row.ObjectListTag, row.EntryIndex)))
        {
            var rows = byKey.ToArray();
            if (!groups.TryGetValue(byKey.Key, out var targets)) { Unmatched += rows.Length; continue; }
            var pairs = Assign(rows, targets);
            if (pairs is null || pairs.Any(pair => slots[pair.Row.Handle & LiveDynamicBinding.HandleIndexMask] != 1))
            { unresolved.Add(byKey.Key); Ambiguous += rows.Length; continue; }
            foreach (var (row, group) in pairs) rowsByGroup[group] = row;
        }
        var dormant = rowsByGroup.Where(pair => !pair.Value.ShowsModel).Select(pair => pair.Key).ToHashSet();
        // A pose survives a failed read or a partial scan of the same lifetime until it expires. A different
        // lifetime, an ambiguity, a reused handle, a dormant row or a complete omission revokes it.
        foreach (var (group, pose) in poses.ToArray())
        {
            bool present = rowsByGroup.TryGetValue(group, out var row);
            if (!LiveWorldProtocol.Fresh(pose.Time, now) || dormant.Contains(group)
                || present && (row!.Handle != pose.Handle || row.Generation != pose.Generation)
                || !present && (complete || unresolved.Contains(group.Key)
                    || slots.ContainsKey(pose.Handle & LiveDynamicBinding.HandleIndexMask)))
                poses.Remove(group);
        }
        foreach (var (group, row) in rowsByGroup)
            if (row.ShowsModel && row.TransformAvailable && LiveWorldProtocol.Fresh(row.Time, now)
                && (!poses.TryGetValue(group, out var previous) || row.Time >= previous.Time)) poses[group] = row;
        foreach (var (group, row) in poses)
        {
            bool placed = false;
            foreach (var member in group.Members) placed |= Place(member, row);
            if (!placed) continue;
            expires = Math.Min(expires, row.Time + LiveWorldProtocol.StaleAfterMilliseconds + 1);
            placedByHandle[row.Handle] = group;
            Matched++;
        }
        // Only a complete registry proves a placement unspawned; a dormant row is a positive observation.
        var hidden = new HashSet<Group>(dormant);
        int inactive = 0;
        if (complete)
            foreach (var (key, targets) in groups)
                if (!unresolved.Contains(key))
                    foreach (var group in targets.Where(group => !rowsByGroup.ContainsKey(group))) { hidden.Add(group); inactive++; }
        if (absentFrom is null || !absentFrom.SetEquals(hidden))
        {
            absent.Clear(); absentFrom = hidden;
            foreach (var member in hidden.SelectMany(group => group.Members))
            {
                foreach (var mesh in member.Meshes) absent.Add(mesh);
                foreach (var marker in member.Markers) absent.Add(marker);
            }
        }
        Inactive = inactive; Dormant = dormant.Count;
        Unmatched += snapshot.Rows.Count(row => row.Dynamic);
        viewport.SetLiveTransforms(meshTransforms, markerTransforms, markerExtents);
    }

    /**
     * Moves one copy to the observed pose; false when its authored transform cannot be undone.
     * A copy still at its authored pose keeps its authored placement and native shading.
     */
    private bool Place(Member member, LivePlacement row)
    {
        var scale = row.Scale is { } observedScale ? new Vector3(observedScale) : Vector3.One;
        if (row.Scale is null && !TryAuthoredScale(member.Placement.WorldTransform, out scale)
            || !Matrix4x4.Invert(member.Placement.WorldTransform, out var inverse)) return false;
        var placement = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(row.Orientation)
            * Matrix4x4.CreateTranslation(row.Position);
        if (Unmoved(placement, member.Placement.WorldTransform)) return true;
        foreach (var mesh in member.Meshes) meshTransforms[mesh] = mesh.Transform * inverse * placement;
        foreach (var marker in member.Markers)
        {
            var matrix = Matrix4x4.CreateFromQuaternion(marker.Rotation)
                * Matrix4x4.CreateTranslation(marker.Position) * inverse * placement;
            if (Matrix4x4.Decompose(matrix, out var relativeScale, out var rotation, out var position))
            {
                markerTransforms[marker] = (position, rotation);
                markerExtents[marker] = Vector3.Abs(marker.Extents * relativeScale);
            }
        }
        return true;
    }

    /**
     * Pairs live rows with the game objects of one tuple.
     * @return Null when a row cannot be told apart: several objects and no identity that picks one.
     */
    private static List<(LivePlacement Row, Group Group)>? Assign(LivePlacement[] rows, List<Group> targets)
    {
        if (rows.Length == 1 && targets.Count == 1) return [(rows[0], targets[0])];
        var result = new List<(LivePlacement, Group)>();
        var taken = new HashSet<Group>();
        foreach (var row in rows)
        {
            if (row.Identity is 0 or ulong.MaxValue) return null;
            var match = targets.Where(group => group.Identity == row.Identity).ToArray();
            if (match.Length != 1 || !taken.Add(match[0])) return null;
            result.Add((row, match[0]));
        }
        return result;
    }

    /** The live row now paired with the placement that owns this mesh or marker. */
    public LivePlacement? RowFor(object source)
    {
        owners ??= groups.Values.SelectMany(list => list).SelectMany(group => group.Members.SelectMany(member =>
                member.Meshes.Cast<object>().Concat(member.Markers)).Select(item => (Item: item, Group: group)))
            .GroupBy(pair => pair.Item, ReferenceEqualityComparer.Instance)
            .ToDictionary(pair => pair.Key, pair => pair.First().Group, ReferenceEqualityComparer.Instance);
        return owners.TryGetValue(source, out var group) ? rowsByGroup.GetValueOrDefault(group) : null;
    }

    /** The authored object a live row stands for, when the pairing is unique. */
    public WorldObject? Find(LivePlacement row)
    {
        if (row.Dynamic || !groups.TryGetValue((row.ObjectListTag, row.EntryIndex), out var targets)) return null;
        var group = targets.Count == 1 ? targets[0]
            : row.Identity is 0 or ulong.MaxValue ? null : targets.SingleOrDefault(value => value.Identity == row.Identity);
        var member = group?.Members[0];
        return member is null ? null : world.Find((object?)member.Meshes.FirstOrDefault() ?? member.Markers.FirstOrDefault()!);
    }

    /** Within a centimeter and a thousandth of each basis term; read precision stays below that. */
    private static bool Unmoved(Matrix4x4 observed, Matrix4x4 authored)
    {
        const float position = .01f, basis = 1e-3f;
        return Vector3.DistanceSquared(observed.Translation, authored.Translation) <= position * position
            && MathF.Abs(observed.M11 - authored.M11) <= basis && MathF.Abs(observed.M12 - authored.M12) <= basis
            && MathF.Abs(observed.M13 - authored.M13) <= basis && MathF.Abs(observed.M21 - authored.M21) <= basis
            && MathF.Abs(observed.M22 - authored.M22) <= basis && MathF.Abs(observed.M23 - authored.M23) <= basis
            && MathF.Abs(observed.M31 - authored.M31) <= basis && MathF.Abs(observed.M32 - authored.M32) <= basis
            && MathF.Abs(observed.M33 - authored.M33) <= basis;
    }

    /** Uniform reflections retain their scalar sign; ambiguous signed axes remain authored. */
    private static bool TryAuthoredScale(Matrix4x4 authored, out Vector3 scale)
    {
        if (!Matrix4x4.Decompose(authored, out scale, out _, out _)) return false;
        if (authored.GetDeterminant() >= 0) return true;
        var magnitude = Vector3.Abs(scale);
        float tolerance = Math.Max(magnitude.X, Math.Max(magnitude.Y, magnitude.Z)) * 1e-5f;
        if (MathF.Abs(magnitude.X - magnitude.Y) > tolerance || MathF.Abs(magnitude.X - magnitude.Z) > tolerance)
            return false;
        scale = new(-magnitude.X);
        return true;
    }

    private static Dictionary<(uint, uint), List<Group>> Build(SceneDocument scene)
    {
        var byPlacement = new Dictionary<SceneObjectPlacement, Member>();
        Member? Add(SceneObjectPlacement? placement)
        {
            if (placement is not { EntryIndex: >= 0 }) return null;
            if (!byPlacement.TryGetValue(placement, out var member)) byPlacement.Add(placement, member = new(placement));
            return member;
        }
        foreach (var mesh in scene.Instances) Add(mesh.ObjectPlacement)?.Meshes.Add(mesh);
        foreach (var marker in scene.Markers) Add(marker.ObjectPlacement)?.Markers.Add(marker);
        // An effect marker shares its placement's source row; it hides and moves with that placement.
        var owners = byPlacement.Values.SelectMany(member => member.Markers.Select(marker => (Key: (marker.SourceTag, marker.SourceOffset), Member: member)))
            .GroupBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.First().Member);
        foreach (var marker in scene.Markers.Where(marker => marker.ObjectPlacement is null && marker.Effect is not null))
            if (owners.TryGetValue((marker.SourceTag, marker.SourceOffset), out var owner)) owner.Markers.Add(marker);
        // Copies without an identity cannot be shown to be one object, so each stays its own group.
        return byPlacement.Values.GroupBy(member => (member.Placement.SourceTag, (uint)member.Placement.EntryIndex))
            .ToDictionary(byKey => byKey.Key, byKey => byKey.GroupBy(member => Identity(member) is var identity && identity != 0 ? (object)identity : member)
                .Select(copies => new Group(byKey.Key, copies.Key is ulong identity ? identity : 0, copies.ToList())).ToList());
    }

    private static ulong Identity(Member member) =>
        member.Markers.Select(marker => marker.PlacementIdentity).FirstOrDefault(value => value is not (0 or ulong.MaxValue));
}
