using System.Numerics;

namespace SunriseStudio.Scene;

/** A mission with its bubbles and map tags. This file holds the world catalog and object records. */
public sealed record WorldMissionEntry(uint Tag, string Name, IReadOnlyList<uint> BubbleHashes,
    IReadOnlyList<uint> MapTags);

/** The maps and missions available to load, with any diagnostics. */
public sealed record WorldCatalog(IReadOnlyList<MapEntry> Maps, IReadOnlyList<WorldMissionEntry> Missions,
    IReadOnlyList<string> Diagnostics)
{
    /** Missions that run on the map. */
    public IReadOnlyList<WorldMissionEntry> MissionsFor(uint mapTag) =>
        Missions.Where(x => x.MapTags.Contains(mapTag)).ToArray();
}

/** Options for loading a world: scenario states, bubbles, map indices, LODs and mission map layers. */
public sealed record WorldLoadOptions
{
    public ScenarioStateMode ScenarioStates { get; init; } = ScenarioStateMode.All;
    public IReadOnlyDictionary<uint, int> StateOrdinals { get; init; } = new Dictionary<uint, int>();
    public IReadOnlySet<uint>? BubbleNameHashes { get; init; }
    public IReadOnlySet<int>? MapGlobalIndices { get; init; }
    public bool IncludeLowerLods { get; init; } = true;
    /** A mission shows the map layers it loads, so an edit targets the copy the game draws. */
    public bool UseMissionMapLayers { get; init; } = true;
}

/** Kind of world object. */
public enum WorldObjectKind { Mesh, Marker, Light, AuthoredObject, Squad, State }
/** Identifies a scenario state by mission, bubble and ordinal. */
public sealed record WorldStateKey(uint MissionTag, uint BubbleHash, int Ordinal);
/** A named, grouped property of an inspected object. */
public sealed record WorldProperty(string Group, string Name, string Value);
/** A reference from an inspected object to a tag. */
public sealed record WorldReference(string Name, uint TagHash, int Offset = -1);
/** A light row with the table that owns it. */
public sealed record WorldLight(PlacedLightTable Owner, AuthoredLightRow Row);
/** Lets a wrapper stand in for the object that is selected; Key is that object. */
public interface ISceneSelectionProxy { object Key { get; } }

/** An object of the world: id, name, kind, source and its bubble, mission and state. */
public sealed record WorldObject(string Id, string Name, WorldObjectKind Kind, object Source,
    uint AssetTag, uint BubbleHash, string BubbleName, uint MissionTag, int StateOrdinal,
    string StateName, Vector3? Position, Vector3? Extents,
    IReadOnlyList<WorldStateKey> Owners);

/** A world object with its properties, references and slots. */
public sealed record WorldObjectInspection(WorldObject Object, IReadOnlyList<WorldProperty> Properties,
    IReadOnlyList<WorldReference> References, IReadOnlyList<AuthoredSlotInfo> Slots,
    IReadOnlyList<object> RelatedObjects);

/** The loaded map stays separate from its mission overlay while both share decoded geometry. */
public sealed class WorldDocument
{
    private Dictionary<object, WorldObject>? bySource;
    private Dictionary<string, WorldObject>? byId;
    private readonly Dictionary<string, WorldObjectInspection> inspections = [];
    private WorldObject[] objects = [];
    public bool HasMapContext { get; init; } = true;
    public required MapEntry Map { get; init; }
    public required IReadOnlyList<WorldMissionEntry> Activities { get; init; }
    public required SceneDocument MapScene { get; init; }
    public SceneDocument? MissionScene { get; init; }
    public required SceneDocument Scene { get; init; }
    public required WorldLoadOptions Options { get; init; }
    public required IReadOnlyDictionary<uint, uint> BubbleAliases { get; init; }
    public required IReadOnlyList<WorldObject> Objects { get => objects; init => objects = value.ToArray(); }
    public IReadOnlyList<ScenarioStateInfo> AllStates => MissionScene?.Scenario?.Bubbles
        .SelectMany(x => x.States).ToArray() ?? [];
    public uint? MissionTag => MissionScene?.TagHash;
    public string Name => MissionScene?.Name ?? MapScene.Name;
    /** Maps a bubble hash to its canonical alias. */
    public uint CanonicalBubble(uint value) => BubbleAliases.GetValueOrDefault(value, value);

    /** Proxy keys and stable metadata identities survive name-only relabeling. */
    public WorldObject? Find(object source)
    {
        if (source is ISceneSelectionProxy proxy) source = proxy.Key;
        byId ??= Objects.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());
        if (source is string id) return byId.GetValueOrDefault(id);
        if (source is WorldObject wrapper)
        {
            if (byId.TryGetValue(wrapper.Id, out var current)) return current;
            source = wrapper.Source;
        }
        // Built whole before it is published, so the AI tools may read it off the UI thread.
        var sources = bySource;
        if (sources is null)
        {
            sources = new(ReferenceEqualityComparer.Instance);
            foreach (var item in Objects) sources.TryAdd(item.Source, item);
            bySource = sources;
        }
        if (sources.TryGetValue(source, out var row)) return row;
        return source switch
        {
            WorldLight light => Objects.FirstOrDefault(x => x.Source is WorldLight current
                && current.Owner.Table.TagHash == light.Owner.Table.TagHash && current.Row.Index == light.Row.Index
                && current.Owner.PlacementSourceTag == light.Owner.PlacementSourceTag
                && current.Owner.PlacementSourceOffset == light.Owner.PlacementSourceOffset
                && current.Owner.BubbleHash == light.Owner.BubbleHash),
            AuthoredObjectInfo authored => Objects.FirstOrDefault(x => x.Source is AuthoredObjectInfo current
                && current.TagHash == authored.TagHash && current.RegistryKey == authored.RegistryKey),
            ScenarioStateInfo state => Objects.FirstOrDefault(x => x.Source is ScenarioStateInfo current
                && current.BubbleNameHash == state.BubbleNameHash && current.Ordinal == state.Ordinal && current.EntryTag == state.EntryTag),
            _ => null
        };
    }

    /** Inspect without the cache, for readers off the UI thread (the cache is not thread-safe). */
    public WorldObjectInspection? Describe(object source) => Find(source) is { } row ? WorldInspection.Describe(this, row) : null;

    /**
     * Describes a scene object's properties and references, cached per row; null when it is not part of
     * this world.
     */
    public WorldObjectInspection? Inspect(object source)
    {
        if (Find(source) is not { } row) return null;
        if (!inspections.TryGetValue(row.Id, out var value)) inspections[row.Id] = value = WorldInspection.Describe(this, row);
        return value;
    }

    /**
     * Writes the placement of each moved source into its row; identities and order stay.
     * @return The rows that changed.
     */
    internal IReadOnlyList<WorldObject> RefreshPlacements(IReadOnlySet<object> moved)
    {
        var changed = new List<WorldObject>();
        for (int index = 0; index < objects.Length; index++)
        {
            var row = objects[index];
            object key = row.Source is WorldLight light ? light.Row : row.Source;
            if (!moved.Contains(key)) continue;
            var (position, extents) = WorldInspection.Placement(row.Source);
            if (row.Position == position && row.Extents == extents) continue;
            objects[index] = row with { Position = position, Extents = extents };
            changed.Add(objects[index]);
        }
        bySource = null; byId = null;
        inspections.Clear();
        return changed;
    }

    /** New names replace labels without replacing decoded geometry or viewport object identities. */
    public WorldDocument Relabel(StudioNameIndex names)
    {
        names.NameScene(MapScene);
        if (MissionScene is not null) names.NameScene(MissionScene);
        if (!ReferenceEquals(Scene, MapScene) && !ReferenceEquals(Scene, MissionScene)) names.NameScene(Scene);
        var owners = Objects.Where(x => x.Source is AuthoredObjectInfo)
            .GroupBy(x => ((AuthoredObjectInfo)x.Source).TagHash)
            .ToDictionary(x => x.Key, x => x.First().Owners);
        var oldLights = Objects.Where(x => x.Source is WorldLight).GroupBy(x => x.Id)
            .ToDictionary(x => x.Key, x => x.First().Source);
        var updated = WorldInspection.CreateObjects(Scene, BubbleAliases, names.Describe, owners)
            .Select(row => row.Source is WorldLight && oldLights.TryGetValue(row.Id, out var old)
                ? row with { Source = old } : row).ToArray();
        return new()
        {
            HasMapContext = HasMapContext, Map = Map with { Name = names.Describe(Map.Tag) },
            Activities = Activities.Select(x => x with { Name = names.Describe(x.Tag) }).ToArray(),
            MapScene = MapScene, MissionScene = MissionScene, Scene = Scene, Options = Options,
            BubbleAliases = BubbleAliases, Objects = updated
        };
    }

    /** Standalone previews share the world inspector without acquiring a map identity. */
    public static WorldDocument FromScene(SceneDocument scene, StudioNameIndex? names = null)
    {
        var aliases = new Dictionary<uint, uint>();
        foreach (var bubble in scene.Bubbles)
        { aliases[bubble.TagHash] = bubble.NameHash; aliases[bubble.NameHash] = bubble.NameHash; }
        return new()
        {
            HasMapContext = false, Map = new(scene.TagHash, scene.Name, scene.Bubbles.Select(x => x.NameHash).Distinct().ToArray()),
            Activities = [], MapScene = scene, MissionScene = scene.Scenario is null ? null : scene,
            Scene = scene, Options = new(), BubbleAliases = aliases,
            Objects = WorldInspection.CreateObjects(scene, aliases,
                tag => names?.Describe(tag) ?? $"0x{tag:X8}", new Dictionary<uint, IReadOnlyList<WorldStateKey>>())
        };
    }
}
