using System.Numerics;
using SunriseStudio.Live;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Models the game draws for one owner, and where its first bone stands. */
internal sealed record DrawnOwner(Vector3 Anchor, IReadOnlyList<uint> Models, uint Generation);

/**
 * Prototypes load once per scene; native snapshots update only transient instance slots. Only rows
 * that show a model load a class; the others, spawned or placed, are drawn as markers.
 */
internal sealed class LiveDynamicBinding(
    Func<PackageCatalog, uint, CancellationToken, LiveScenePrototype>? prepare = null) : IDisposable
{
    /** Prototype key of the shared marker; no package tag uses it. */
    public const uint MarkerTag = 0xFFFFFFFE;
    // A display choice: visible at a distance, small enough not to cover its neighbors.
    private const float MarkerRadius = .35f;
    private sealed record Prepared(uint Tag, LiveScenePrototype? Prototype, string Error);
    private readonly Func<PackageCatalog, uint, CancellationToken, LiveScenePrototype> preparePrototype =
        prepare ?? ((catalog, tag, token) => LiveScenePrototype.Prepare(catalog, tag, token));
    private readonly Dictionary<string, LivePlacement> actors = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> actorKeys = [];
    private readonly HashSet<uint> ready = [];
    /** Mesh model tags each ready prototype already shows. */
    private readonly Dictionary<uint, HashSet<uint>> prototypeModels = [];
    private IReadOnlyDictionary<uint, DrawnOwner> drawn = new Dictionary<uint, DrawnOwner>();
    private readonly Dictionary<uint, string> errors = [];
    private readonly HashSet<uint> requested = [];
    private readonly Queue<uint> pending = [];
    private Task<Prepared>? work;
    private CancellationTokenSource? workCancellation;
    private uint workTag;
    private bool discardWork, disposed, queueDirty;
    private SceneDocument? source;
    private LivePlacements? applied;
    private bool dirty;
    private int prototypeRevision = -1;
    private string loading = "", note = "", previewError = "";
    public string Status => previewError.Length > 0 ? previewError : loading.Length > 0 ? loading : note;
    public IReadOnlyDictionary<uint, string> Errors => errors;
    public int ObservedCount => actors.Count;
    public IReadOnlyCollection<LivePlacement> ObservedRows => actors.Values;
    public long Revision { get; private set; }
    /** Stable key of a live row: handle plus generation, so a reused slot is a new object. */
    public static string Key(LivePlacement row) => $"{row.Handle:X8}:{row.Generation:X8}";
    /** A row the game reports as not placed in the world or hidden, keyed under its row. */
    public static string MarkerKey(LivePlacement row) => Key(row) + "/marker";
    /** Owners whose model Studio shows; only their draws need the game's palettes. */
    public IEnumerable<uint> ModelOwners => actors.Values.Where(row => row.ShowsModel && ShowsClass(row)).Select(row => row.Handle)
        .Concat(drawn.Keys.Where(IsPlayer));
    /** A drawn model shown apart from its owner's class, keyed under the owner's row or stand-in row. */
    public static string ModelKey(LivePlacement? row, uint owner, uint model, uint generation) =>
        $"{(row is null ? $"{owner:X8}:{generation:X8}" : Key(row))}/{model:X8}";
    /** Spawned rows, plus a stand-in row for each drawn owner the registry does not list. */
    public IReadOnlyCollection<LivePlacement> Owners { get; private set; } = [];
    /** Full handles must match; a reused pool slot never aliases its previous occupant. */
    public LivePlacement? Actor(uint handle) => actorKeys.TryGetValue(handle, out var key) ? actors.GetValueOrDefault(key) : null;
    /** Changes only when the set of shown instances changes, not when they move. */
    public long InstanceRevision { get; private set; }
    private HashSet<string> instanceKeys = new(StringComparer.Ordinal);
    private uint? player;

    /** Handle bits that pick the object's slot; a slot holds one live object at a time. */
    public const uint HandleIndexMask = 0x1FFF;
    /** True when both handles name the same live slot; the invalid handle matches nothing. */
    public static bool SameObject(uint left, uint right) => left != uint.MaxValue && left == right;
    /** Whether the handle is the player's. */
    public bool IsPlayer(uint handle) => player is { } value && SameObject(handle, value);

    /** Replaces the models the game draws per owner handle. Authored owners must be left out. */
    public void SetDrawn(IReadOnlyDictionary<uint, DrawnOwner> value)
    {
        drawn = value;
        foreach (var (owner, draw) in value)
            if (IsPlayer(owner) && Actor(owner) is { } row && row.Generation != draw.Generation) Remove(Key(row));
        dirty = queueDirty = true;
    }

    /** The local player shows only what the game draws for it; objects attached to it are not shown. */
    public void SetPlayer(uint? handle)
    {
        if (player == handle) return;
        player = handle; dirty = queueDirty = true;
    }

    private bool Shows(LivePlacement row) => !row.PlayerAttached || player is null;
    private bool ShowsClass(LivePlacement row) =>
        Shows(row) && !(IsPlayer(row.Handle) && drawn.Keys.Any(owner => SameObject(owner, row.Handle)));

    /** A canceled worker keeps its slot until it exits, so replacement work cannot overlap it. */
    public void CancelPreparation()
    {
        if (disposed) return;
        workCancellation?.Cancel(); discardWork = work is not null;
        if (work?.IsCompleted == true) FinishWorker(null, null);
        pending.Clear(); requested.Clear(); requested.UnionWith(ready); requested.UnionWith(errors.Keys);
        loading = ""; queueDirty = true;
    }

    /** Cancels preparation and forgets all spawned actors. */
    public void Clear(AssetViewport viewport)
    {
        CancelPreparation(); actors.Clear(); actorKeys.Clear(); applied = null; dirty = false;
        drawn = new Dictionary<uint, DrawnOwner>(); Owners = [];
        previewError = note = ""; viewport.ClearLiveInstances();
        Revision++;
    }

    /** Unknown and incomplete observations never imply an entity was destroyed. */
    public void Poll(StudioWorkspace workspace, AssetViewport viewport, LivePlacements snapshot, ulong now)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ReferenceEquals(source, viewport.Scene) || prototypeRevision != viewport.LivePrototypeRevision)
        {
            CancelPreparation(); source = viewport.Scene; ready.Clear(); prototypeModels.Clear(); errors.Clear(); requested.Clear();
            prototypeRevision = viewport.LivePrototypeRevision;
            actors.Clear(); actorKeys.Clear(); applied = null; dirty = queueDirty = true;
            previewError = note = "";
        }
        if (work is { IsCompleted: true } && (discardWork || !workspace.IsBusy)) FinishWorker(workspace, viewport);
        if (!ReferenceEquals(applied, snapshot))
        {
            applied = snapshot;
            if (snapshot.Available && LiveWorldProtocol.Fresh(snapshot.Time, now))
                ReadSnapshot(snapshot, now);
        }
        foreach (string key in actors.Where(pair => !LiveWorldProtocol.Fresh(pair.Value.Time, now)).Select(pair => pair.Key).ToArray())
        { Remove(key); dirty = queueDirty = true; }
        if (queueDirty) ReconcileQueue();
        if (dirty)
        {
            var shown = actors.Values.Where(ShowsClass).OrderBy(row => row.Handle).ThenBy(row => row.Generation).ToArray();
            bool markers = shown.Any(row => !row.ShowsModel) && MarkerReady(viewport);
            var instances = shown.Where(row => row.ShowsModel && ready.Contains(row.ClassTag))
                .Select(row => new LiveSceneInstance(Key(row), row.ClassTag, Placement(row), workspace.DisplayName(row.ClassTag)))
                .Concat(shown.Where(row => markers && !row.ShowsModel).Select(row => new LiveSceneInstance(MarkerKey(row), MarkerTag,
                    Matrix4x4.CreateFromQuaternion(row.Orientation) * Matrix4x4.CreateTranslation(row.Position), Name(workspace, row))))
                .Concat(NeededModels().Where(item => ready.Contains(item.Model)).Select(item => new LiveSceneInstance(
                    ModelKey(item.Row, item.Owner, item.Model, drawn[item.Owner].Generation), item.Model,
                    item.Row is { } row ? Placement(row) : Matrix4x4.CreateTranslation(drawn[item.Owner].Anchor),
                    workspace.DisplayName(item.Row?.ClassTag is > 0 and < uint.MaxValue ? item.Row.ClassTag : item.Model))))
                .Take(2048).ToArray();
            // A stand-in row retains the captured lifetime until the registry supplies its class.
            Owners = actors.Values.Where(Shows).Concat(drawn.Where(pair => Actor(pair.Key) is null && IsPlayer(pair.Key))
                .Select(pair => new LivePlacement(0, uint.MaxValue, uint.MaxValue, pair.Key, pair.Value.Generation, 0,
                    pair.Value.Anchor, Quaternion.Identity, now, true, 0, true))).ToArray();
            try { viewport.SetLiveInstances(instances); previewError = ""; }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException)
            { viewport.ClearLiveInstances(); previewError = error.Message; }
            var keys = instances.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            if (!keys.SetEquals(instanceKeys)) { instanceKeys = keys; InstanceRevision++; }
            dirty = false;
            Revision++;
        }
        if (work is null && source is not null && !workspace.IsBusy && workspace.Catalog is { } catalog
            && pending.TryDequeue(out uint tag))
        {
            loading = "Preparing " + workspace.DisplayName(tag); workTag = tag;
            workCancellation = new(); discardWork = false; var token = workCancellation.Token;
            work = Task.Run(() =>
            {
                try
                {
                    var result = preparePrototype(catalog, tag, token);
                    token.ThrowIfCancellationRequested();
                    return new Prepared(tag, result, "");
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                { return new Prepared(tag, null, error.Message); }
            }, token);
        }
    }

    /**
     * A reused handle invalidates its old generation even when the new pose is unavailable. Spawned
     * rows and marker rows are kept; a placed row that shows its model belongs to the authored scene.
     */
    private void ReadSnapshot(LivePlacements snapshot, ulong now)
    {
        dirty = queueDirty = true; note = "";
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in snapshot.Rows.GroupBy(row => row.Handle & HandleIndexMask))
        {
            var rows = group.ToArray();
            foreach (var row in rows) observed.Add(Key(row));
            foreach (var retired in actors.Values.Where(row => (row.Handle & HandleIndexMask) == group.Key
                && (rows.Length != 1 || Key(row) != Key(rows[0]))).ToArray()) Remove(Key(retired));
            if (rows.Length != 1)
            {
                note = "Conflicting runtime handles"; continue;
            }
            var item = rows[0]; string key = Key(item);
            if (actorKeys.TryGetValue(item.Handle, out var old) && (old != key || !item.Dynamic && item.ShowsModel)) Remove(old);
            if (!item.Dynamic && item.ShowsModel || !item.TransformAvailable || !LiveWorldProtocol.Fresh(item.Time, now)
                || item.ShowsModel && item.ClassTag is 0 or uint.MaxValue) continue;
            if (actors.TryGetValue(key, out var previous) && previous.ClassTag != item.ClassTag)
            { Remove(key); note = "Conflicting runtime class"; continue; }
            actors[key] = item; actorKeys[item.Handle] = key;
        }
        if (snapshot.Exhaustive)
            foreach (string key in actors.Keys.Where(key => !observed.Contains(key)).ToArray()) Remove(key);
    }

    /** Unneeded queued classes are dropped; failures remain known for this scene revision. */
    private void ReconcileQueue()
    {
        queueDirty = false;
        if (note is "Live class limit reached" or "Showing at most 2048 runtime entities") note = "";
        var wanted = actors.Values.Where(row => row.ShowsModel && ShowsClass(row)).Select(row => row.ClassTag).ToHashSet();
        wanted.UnionWith(NeededModels().Select(item => item.Model));
        int count = pending.Count;
        for (int index = 0; index < count; index++)
        {
            uint tag = pending.Dequeue();
            if (wanted.Contains(tag)) pending.Enqueue(tag);
            else requested.Remove(tag);
        }
        if (work is not null && !wanted.Contains(workTag))
        { workCancellation?.Cancel(); discardWork = true; loading = ""; }
        foreach (uint tag in wanted.Order())
        {
            if (requested.Contains(tag)) continue;
            if (ready.Count + pending.Count + (work is null ? 0 : 1) >= AssetViewport.MaximumLivePrototypes)
            { if (note.Length == 0) note = "Live class limit reached"; break; }
            requested.Add(tag); pending.Enqueue(tag);
        }
        if (actors.Count > 2048 && note.Length == 0) note = "Showing at most 2048 runtime entities";
    }

    /** Completed data is adopted only by the scene that still owns the uncanceled worker. */
    private void FinishWorker(StudioWorkspace? workspace, AssetViewport? viewport)
    {
        var completed = work!; work = null; loading = "";
        bool discard = discardWork; discardWork = false;
        workCancellation?.Dispose(); workCancellation = null;
        try
        {
            var result = completed.GetAwaiter().GetResult();
            if (discard || workspace is null || viewport is null)
            { ReleaseRequest(workTag); return; }
            if (result.Prototype is { } prototype)
            {
                if (prototype.ClassTag != result.Tag) throw new InvalidDataException("Prepared class identity differs from its request.");
                workspace.Names?.NameScene(prototype.Scene); viewport.RegisterLivePrototype(prototype);
                ready.Add(result.Tag); dirty = true;
                prototypeModels[result.Tag] = prototype.Scene.Instances.Select(instance => instance.Mesh.TagHash).ToHashSet();
            }
            else errors[result.Tag] = result.Error;
        }
        catch (OperationCanceledException) { ReleaseRequest(workTag); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!discard) errors[workTag] = error.GetBaseException().Message; }
        finally { queueDirty = true; }
    }

    /** Cancels preparation; safe to call twice. */
    public void Dispose()
    {
        if (disposed) return;
        CancelPreparation(); disposed = true;
        var cancellation = workCancellation; workCancellation = null;
        if (work is { } abandoned)
            _ = abandoned.ContinueWith(result => { _ = result.Exception; cancellation?.Dispose(); }, TaskScheduler.Default);
        else cancellation?.Dispose();
        work = null;
    }

    private static Matrix4x4 Placement(LivePlacement row) => Matrix4x4.CreateScale(row.Scale ?? 1)
        * Matrix4x4.CreateFromQuaternion(row.Orientation) * Matrix4x4.CreateTranslation(row.Position);

    private static string Name(StudioWorkspace workspace, LivePlacement row) => row.ClassTag is not (0 or uint.MaxValue)
        ? workspace.DisplayName(row.ClassTag) : $"{workspace.DisplayName(row.ObjectListTag)} / {row.EntryIndex}";

    /** Registers the shared marker once per prototype epoch; false leaves marker rows undrawn. */
    private bool MarkerReady(AssetViewport viewport)
    {
        if (viewport.HasLivePrototype(MarkerTag)) return true;
        try { viewport.RegisterLivePrototype(MarkerPrototype()); return true; }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or InvalidDataException)
        { previewError = error.Message; return false; }
    }

    /** An octahedron: a point that stays readable from any side. */
    private static LiveScenePrototype MarkerPrototype()
    {
        Vector3[] corners = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        var vertices = corners.Select(corner => new SceneVertex(corner * MarkerRadius, corner, Vector2.Zero, Vector4.One)).ToArray();
        uint[] indices = [0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4, 2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5];
        var scene = new SceneDocument { Name = "Live marker", TagHash = MarkerTag };
        scene.Instances.Add(new() { Mesh = new MeshData { TagHash = MarkerTag, Vertices = vertices, Indices = indices }, Layer = SceneLayer.Objects });
        return new(MarkerTag, scene, null, []);
    }

    /**
     * Drawn models that no class prototype shows. An owner's class is waited for first, so a class
     * that loads its own models is never drawn twice. Of the owners the registry does not list, only
     * the player is shown; the player shows every drawn model and no class model.
     */
    private IEnumerable<(uint Owner, LivePlacement? Row, uint Model)> NeededModels()
    {
        foreach (var (owner, item) in drawn)
        {
            var row = Actor(owner);
            if (row is not null && row.Generation != item.Generation) continue;
            bool isPlayer = IsPlayer(owner);
            if (!isPlayer && (row is null || !row.ShowsModel || !Shows(row))) continue;
            uint type = row?.ClassTag ?? 0;
            if (!isPlayer && !ready.Contains(type) && !errors.ContainsKey(type)) continue;
            var shown = isPlayer ? null : prototypeModels.GetValueOrDefault(type);
            foreach (uint model in item.Models)
                if (shown?.Contains(model) != true) yield return (owner, row, model);
        }
    }

    private void Remove(string key)
    {
        if (actors.Remove(key, out var row) && actorKeys.GetValueOrDefault(row.Handle) == key)
            actorKeys.Remove(row.Handle);
    }

    private void ReleaseRequest(uint tag)
    {
        if (!ready.Contains(tag) && !errors.ContainsKey(tag) && !pending.Contains(tag)) requested.Remove(tag);
    }
}
