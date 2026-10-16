using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/**
 * One variant parameter: its key, the values a preview may pick, and the value the read used.
 * Runtime is set for the parameter the game fills from player state; its values come from part labels.
 */
public sealed record EntityVariantParameter(uint Key, IReadOnlyList<uint> Values, uint Value, bool Runtime);

/** Plain names for variant keys, values and object channels; each word's FNV-1 hash is the authored value. */
public static class EntityVariantNames
{
    private static readonly string[] Words =
    [
        "type", "faction", "unit", "unit_type", "minor", "major", "miniboss", "ultra", "taken", "taken_titan",
        "taken_hydra", "titan", "hydra", "goblin", "hobgoblin", "harpy", "minotaur", "cyclops", "sol_divisive", "past",
        "future", "titan_base", "titan_past", "titan_future", "undying_mind", "weapon_type", "damage", "destroyed", "body",
        "head", "mid_torso", "lower_torso", "top_head", "weakspot_hidden", "weakspot_active", "eye_color",
        "eye_intensity", "teleport"
    ];
    private static readonly Dictionary<uint, string> ByHash = Words.ToDictionary(StringBankWriter.Hash);

    /** The word whose hash is this value; null when none is known. */
    public static string? Name(uint hash) => ByHash.GetValueOrDefault(hash);
}

/** Stored spawn inputs and supported post-init selectors determine this preview, not live state. */
public sealed record EntityInitialState(bool[]? EnabledParts, string? Unresolved)
{
    /** Parameters of the variant component; Value is zero for one the read could not resolve. */
    public IReadOnlyList<EntityVariantParameter> Parameters { get; init; } = [];
    /** Names of the authored variant rows; a preview may request any of them. */
    public IReadOnlyList<uint> Variants { get; init; } = [];
    /** Name of the variant row whose region filters apply; zero when none applies. */
    public uint SelectedVariant { get; init; }
    /** Set when no variant has positive weight; marks the parts that every variant row disables. */
    public bool[]? DisabledByEveryVariant { get; init; }
    /** Whether the part starts enabled; null when unknown. */
    public bool? Enabled(int part) => EnabledParts is { } parts && (uint)part < parts.Length ? parts[part] : null;
    /** True when every variant disables the part. */
    public bool EveryVariantDisables(int part) =>
        DisabledByEveryVariant is { } parts && (uint)part < parts.Length && parts[part];
}

/** Resolves native part filters while keeping unsupported initializers explicit. */
public sealed class EntityInitialStateReader(PackageCatalog catalog)
{
    private const uint Empty = 0x811C9DC5, PostInitHash = 0x242D8C9A;
    private const uint Damage = 0x80804BEE, Character = 0x80806B23, Variant = 0x808038A3, RegionState = 0x80808867;
    private const uint RegionKey = 0x51E7A18D, VariantKey = 0x4C58EF8F, StateKey = 0x5266EA90;
    private const uint ShapeKey = 0x0B3B1D30, InitialShape = 0xA7E66243, RuntimeParameter = EntityPartLabels.RuntimeKey;
    // An empty spawn value for the runtime parameter is replaced with this value.
    private const uint RuntimeEmptyValue = 0x1B3DDFC5;
    // Classes whose post-init method adds one constant filter, and the health-region class.
    private const uint ConstantFilterA = 0x80803B73, ConstantFilterB = 0x808042FE, HealthRegions = 0x8080390E;
    private const uint HealthStateKey = 0x389FBDD7;
    /** The native matcher has 512 part bits and a three-bit disable counter per part. */
    private const int PartCapacity = 512, DisableCountMaximum = 7;
    public ISet<uint>? Reads { get; init; }
    private readonly Dictionary<uint, TagData> data = [];
    private readonly List<uint> variantNames = [];
    private readonly List<EntityVariantParameter> parameterInfo = [];
    private uint selectedVariant;
    /** Region filters of every row of each variant component with no positive admitted weight. */
    private readonly List<Condition[][]> unselected = [];
    private sealed record Component(uint Tag, TagData Data, int Offset, TagData Resource, int ResourceOffset);
    private sealed record Condition((uint Key, uint Value)[] Required, (uint Key, uint Value)[] Excluded);
    private sealed record Spawn(uint DamageOverride, Dictionary<int, (TagData Data, int Offset)> Overrides);

    /**
     * A zero spawn tag selects the native dynamic-class constructor defaults.
     * @param previewVariant Variant row name requested as a spawn would; zero requests none.
     * @param spawnPairs Variant parameter pairs the spawn passes; they apply after the authored spawn row's pairs.
     * @param picks Parameter values the user chose; they apply last, and a runtime pick stands in for player state.
     */
    public EntityInitialState Read(uint entityTag, uint componentConfig, uint spawnTag = 0, int spawnOffset = -1,
        uint previewVariant = 0, IReadOnlyList<(uint Key, uint Value)>? spawnPairs = null,
        IReadOnlyDictionary<uint, uint>? picks = null)
    {
        data.Clear();
        variantNames.Clear();
        parameterInfo.Clear();
        unselected.Clear();
        selectedVariant = 0;
        try
        {
            var definition = Get(entityTag);
            int[] configs = definition.Rows(0x10, 0x80809C04, 12);
            var spawn = ReadSpawn(entityTag, spawnTag, spawnOffset, configs.Length);
            var conditions = new List<Condition>();
            int[] descriptors = definition.Rows(0x58, 0x80809C22, 40);
            int[] matches = definition.Rows(0x48, 0x80809C25, 8).Where(row => definition.U32(row) == PostInitHash).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("Duplicate initial-state interface.");
            if (matches.Length == 1)
            {
                int first = definition.U16(matches[0] + 4), count = definition.U16(matches[0] + 6) & 0x7FFF;
                if (first > descriptors.Length - count) throw new InvalidDataException("Initial-state interface exceeds its rows.");
                foreach (int row in descriptors.AsSpan(first, count))
                {
                    if (definition.U16(row + 2) != 0) throw new NotSupportedException("Conditional initial-state interface.");
                    var map = Get(definition.U32(row + 32));
                    int[] methods = map.Rows(0x10, 0x80809C56, 24);
                    if (map.U32(12) != 0x80809683 || methods.Length == 0)
                        throw new InvalidDataException("Initial-state interface has another type.");
                    uint type = map.U32(methods[0]), method = map.U32(methods[0] + 4);
                    var component = Resolve(definition.U32(row + 16), definition.I64(row + 24), type);
                    int ordinal = Array.FindIndex(configs, config => definition.U32(config) == component.Tag);
                    if (ordinal < 0) throw new NotSupportedException("Initial-state component belongs to another object.");
                    spawn.Overrides.TryGetValue(ordinal, out var optional);
                    switch (type, method)
                    {
                        case (Damage, 42): ReadDamage(component, spawn.DamageOverride, conditions); break;
                        case (Character, 9): conditions.Add(new([], [(ShapeKey, InitialShape)])); break;
                        case (Variant, 3): ReadVariant(component, optional, spawnPairs ?? [], previewVariant, picks, conditions); break;
                        case (RegionState, 3): ReadRegion(component, conditions); break;
                        case (ConstantFilterA, 6): conditions.Add(new([], [(0x3692AC32, 0xA88A7C4A)])); break;
                        case (ConstantFilterB, 4): conditions.Add(new([], [(EntityPartLabels.GearAppliedKey, EntityPartLabels.False)])); break;
                        case (HealthRegions, 15): ReadHealthRegions(component, conditions); break;
                        default: throw new NotSupportedException($"Initial-state component {type:X8}, method {method}.");
                    }
                }
            }
            var target = Get(componentConfig);
            int resource = target.Relative(0x18);
            int[] parts = target.Rows(resource + 56, 0x80809C2C, 24);
            if (parts.Length > PartCapacity) throw new InvalidDataException("Part count exceeds the native matcher capacity.");
            var enabled = new bool[parts.Length];
            var everyVariant = new bool[parts.Length];
            for (int part = 0; part < parts.Length; part++)
            {
                var labels = Labels(target, parts[part] + 8);
                int disabled = conditions.Count(condition => Matches(condition, labels));
                if (disabled > DisableCountMaximum) throw new InvalidDataException("Initial filters overflow a native part counter.");
                enabled[part] = disabled == 0;
                everyVariant[part] = unselected.Any(rows => rows.All(row => row.Any(condition => Matches(condition, labels))));
            }
            return new(enabled, null)
            {
                Variants = variantNames.Distinct().ToArray(), SelectedVariant = selectedVariant,
                DisabledByEveryVariant = unselected.Count == 0 ? null : everyVariant, Parameters = ParameterList(componentConfig)
            };
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
            or IndexOutOfRangeException or OverflowException or KeyNotFoundException)
        { return new(null, error.Message) { Variants = variantNames.Distinct().ToArray(), Parameters = ParameterList(componentConfig) }; }
    }

    /** The recorded parameters; a runtime one with no authored values lists the values its part labels name. */
    private EntityVariantParameter[] ParameterList(uint componentConfig)
    {
        var labels = new List<(uint Key, uint Value)>();
        if (parameterInfo.Any(parameter => parameter is { Runtime: true, Values.Count: 0 }))
            try
            {
                var target = Get(componentConfig);
                int resource = target.Relative(0x18);
                foreach (int part in target.Rows(resource + 56, 0x80809C2C, 24)) labels.AddRange(Labels(target, part + 8));
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or IndexOutOfRangeException
                or OverflowException or KeyNotFoundException) { }
        return parameterInfo.Select(parameter => parameter is { Runtime: true, Values.Count: 0 }
            ? parameter with { Values = labels.Where(label => label.Key == parameter.Key).Select(label => label.Value).Distinct().ToArray() }
            : parameter).ToArray();
    }

    /** The export's back-reference names the component whose constructor ran. */
    private Component Resolve(uint tag, long exportOffset, uint type)
    {
        var exported = Get(tag);
        int start = Offset(exported, exportOffset, 16);
        int header = exported.Relative(start);
        if (!exported.Has(header, 16) || exported.U32(header + 4) != type)
            throw new InvalidDataException("Initial-state export has another component type.");
        uint owner = exported.U32(header);
        var component = Get(owner);
        int offset = Offset(component, exported.I64(header + 8), 48);
        var resource = Get(component.U32(offset));
        int resourceOffset = Offset(resource, component.I64(offset + 8), 16);
        if (resource.U32(resourceOffset) != owner || resource.U32(resourceOffset + 4) != type
            || resource.I64(resourceOffset + 8) != offset)
            throw new InvalidDataException("Initial-state component resource is not reciprocal.");
        return new(owner, component, offset, resource, resourceOffset);
    }

    /** Spawn overrides are keyed by the root's authored component ordinal. */
    private Spawn ReadSpawn(uint entityTag, uint tag, int offset, int componentCount)
    {
        if (tag == 0) return new(Empty, []);
        var source = Get(tag);
        if (!source.Has(offset, 144) || source.U32(offset) != entityTag)
            throw new NotSupportedException("Placed spawn inputs are unavailable.");
        // The parent at +48 and the bone at +96 place the entity; no part filter reads them.
        var overrides = new Dictionary<int, (TagData, int)>();
        var seen = new HashSet<int>();
        int link = offset + 120;
        while (source.I64(link) != 0)
        {
            int node = source.Relative(link);
            if (!source.Has(node - 4, 20) || !seen.Add(node) || seen.Count > componentCount)
                throw new InvalidDataException("Spawn override chain is invalid.");
            int ordinal = source.I32(node + 8);
            if (ordinal >= 0 && (ordinal >= componentCount || !overrides.TryAdd(ordinal, (source, node))))
                throw new InvalidDataException("Spawn override ordinal is invalid.");
            link = node;
        }
        return new(source.U32(offset + 100), overrides);
    }

    /** A named spawn override replaces the per-part damage-state filters. */
    private static void ReadDamage(Component component, uint damageOverride, List<Condition> conditions)
    {
        var resource = component.Resource;
        int r = component.ResourceOffset;
        if (damageOverride != Empty)
        {
            int[] found = resource.RowsOfAnyClass(r + 904, 60).Where(row => resource.U32(row) == damageOverride).ToArray();
            if (found.Length != 1) throw new NotSupportedException("Damage override is not one named initial state.");
            conditions.Add(ReadCondition(resource, found[0] + 4));
            return;
        }
        int[] authored = resource.Rows(r + 888, 0x80804B99, 40);
        int[] current = component.Data.Rows(component.Offset + 888, 0x80804B98, 48);
        if (authored.Length != current.Length) throw new InvalidDataException("Damage-state counts disagree.");
        for (int i = 0; i < authored.Length; i++)
        {
            int[] choices = resource.Rows(authored[i] + 24, 0x80804B9B, 60);
            int selected = component.Data.U8(current[i] + 32);
            if (selected >= choices.Length) throw new InvalidDataException("Damage-state index is outside its choices.");
            conditions.Add(ReadCondition(resource, choices[selected]));
        }
    }

    /** Parts without the selector's state label remain enabled. */
    private static void ReadRegion(Component component, List<Condition> conditions)
    {
        var resource = component.Resource;
        int[] choices = resource.Rows(component.ResourceOffset + 24, 0x8080886D, 8);
        int selected = component.Data.I32(component.Offset + 32);
        if ((uint)selected >= choices.Length) throw new NotSupportedException("Region state has no initial choice.");
        uint region = resource.U32(component.ResourceOffset + 20);
        conditions.Add(new(region == Empty ? [] : [(RegionKey, region)], [(StateKey, resource.U32(choices[selected]))]));
    }

    /**
     * Each enabled health block filters its region by its start state.
     * The constructor sets the state when the start health exceeds the float at block +312.
     */
    private static void ReadHealthRegions(Component component, List<Condition> conditions)
    {
        var resource = component.Resource;
        foreach (var (block, startHealth) in new[] { (608, 1f), (1040, 0f), (1528, 0f) })
        {
            int at = component.ResourceOffset + block;
            if (!resource.Has(at, 432)) throw new InvalidDataException("Health region block leaves its tag.");
            if (resource.U8(at + 416) == 0) continue;
            uint state = startHealth > resource.F32(at + 312) ? resource.U32(at + 424) : resource.U32(at + 428);
            conditions.Add(new([(RegionKey, resource.U32(at + 420))], [(HealthStateKey, state)]));
        }
    }

    /**
     * Stored parameters admit variants before an explicit choice or weighted fallback.
     * @param pairs Spawn pairs; each one with a key overrides its parameter like an authored pair.
     * @param preview Variant row name to request; its own labels replace the stored parameters.
     * @param picks User choices; they replace all of the above.
     */
    private void ReadVariant(Component component, (TagData Data, int Offset) optional,
        IReadOnlyList<(uint Key, uint Value)> pairs, uint preview, IReadOnlyDictionary<uint, uint>? picks,
        List<Condition> conditions)
    {
        var source = component.Resource;
        int r = component.ResourceOffset;
        int[] variants = source.Rows(r + 200, 0x808038A6, 48);
        variantNames.AddRange(variants.Select(row => source.U32(row + 4)));
        uint requested = Empty;
        var overrides = new Dictionary<uint, uint>();
        if (optional.Data is { } input)
        {
            if (input.U32(optional.Offset - 4) != 0x808038A0)
                throw new NotSupportedException("Variant spawn override has another type.");
            // A later pair for the same key wins.
            foreach (int row in input.RowsOfAnyClass(optional.Offset + 16, 8))
            {
                uint key = input.U32(row), value = input.U32(row + 4);
                overrides[key] = key == RuntimeParameter && value == Empty ? RuntimeEmptyValue : value;
            }
            requested = input.U32(optional.Offset + 32);
        }
        // 0x7FF742B14630 copies only pairs whose key is not empty.
        foreach (var (key, value) in pairs)
            if (key != Empty) overrides[key] = key == RuntimeParameter && value == Empty ? RuntimeEmptyValue : value;
        if (preview != 0)
        {
            int index = Array.FindIndex(variants, row => source.U32(row + 4) == preview);
            if (index < 0) throw new NotSupportedException("Preview variant is not an authored variant row.");
            foreach (var (key, value) in Labels(source, variants[index] + 16)) overrides[key] = value;
            requested = preview;
        }
        foreach (var (key, value) in picks ?? new Dictionary<uint, uint>()) overrides[key] = value;
        var parameters = new List<Condition>();
        int[] parameterRows = source.RowsOfAnyClass(r + 168, 24);
        int[] current = component.Data.RowsOfAnyClass(component.Offset + 56, 4);
        if (current.Length != parameterRows.Length) throw new InvalidDataException("Variant parameter counts disagree.");
        // Every row is recorded before an unresolved one stops the read, so the picker lists them all.
        Exception? unresolved = null;
        for (int i = 0; i < parameterRows.Length; i++)
        {
            int row = parameterRows[i];
            uint key = source.U32(row + 4);
            uint[] choices = source.RowsOfAnyClass(row + 8, 4).Select(source.U32).ToArray();
            uint value = overrides.GetValueOrDefault(key, component.Data.U32(current[i]));
            bool runtime = key == RuntimeParameter;
            // An empty or unlisted value takes choice 0; the runtime key reads player state unless the user picked it.
            if ((value == Empty || !choices.Contains(value)) && !(runtime && picks?.ContainsKey(key) == true))
            {
                if (runtime) unresolved ??= new NotSupportedException("Variant parameter needs player state.");
                else if (choices.Length == 0) unresolved ??= new InvalidDataException("Variant parameter has no choices.");
                value = runtime || choices.Length == 0 ? 0 : choices[0];
            }
            parameterInfo.Add(new(key, choices, value, runtime));
            parameters.Add(new([], [(key, value)]));
        }
        if (unresolved is not null) throw unresolved;
        int[] admitted = variants.Where(row => !parameters.Any(condition => Matches(condition, Labels(source, row + 16)))).ToArray();
        int[] regions = source.RowsOfAnyClass(r + 184, 4);
        Condition[] Filters(int row)
        {
            int[] values = source.RowsOfAnyClass(row + 32, 4);
            if (regions.Length != values.Length) throw new InvalidDataException("Variant region counts disagree.");
            return regions.Select((region, i) =>
            {
                uint key = source.U32(region);
                return new Condition(key == Empty ? [] : [(RegionKey, key)], [(VariantKey, source.U32(values[i]))]);
            }).ToArray();
        }
        conditions.AddRange(parameters);
        int chosen = requested == Empty ? -1 : Array.FindIndex(admitted, row => source.U32(row + 4) == requested);
        if (chosen < 0 && preview != 0) throw new NotSupportedException("Preview variant fails its parameter choices.");
        if (chosen < 0)
        {
            float weight = admitted.Sum(source.F32);
            // With no admitted weight the native selector returns -1; region filters are skipped.
            if (weight <= 0)
            {
                if (variants.Length != 0) unselected.Add(variants.Select(Filters).ToArray());
                return;
            }
            // A zero random draw can select a zero-weight row before the first positive row.
            if (admitted.Length != 1 || !float.IsFinite(weight))
                throw new NotSupportedException("Initial variant is a weighted random choice.");
            chosen = 0;
        }
        conditions.AddRange(Filters(admitted[chosen]));
        if (selectedVariant == 0) selectedVariant = source.U32(admitted[chosen] + 4);
    }

    /** Each native condition has room for three required and three excluded key/value pairs. */
    private static Condition ReadCondition(TagData source, int offset)
    {
        int required = source.I32(offset), excluded = source.I32(offset + 28);
        if (!source.Has(offset, 56) || required is < 0 or > 3 || excluded is < 0 or > 3)
            throw new InvalidDataException("Part filter count exceeds its fixed fields.");
        (uint, uint) Pair(int at) => (source.U32(at), source.U32(at + 4));
        return new(Enumerable.Range(0, required).Select(i => Pair(offset + 4 + 8 * i)).ToArray(),
            Enumerable.Range(0, excluded).Select(i => Pair(offset + 32 + 8 * i)).ToArray());
    }

    private static HashSet<(uint, uint)> Labels(TagData source, int array) =>
        source.Rows(array, 0x80809C31, 8).Select(row => (source.U32(row), source.U32(row + 4))).ToHashSet();

    private static bool Matches(Condition condition, HashSet<(uint Key, uint Value)> labels) =>
        condition.Required.All(labels.Contains) && condition.Excluded.Any(pair =>
            labels.Any(label => label.Key == pair.Key) && !labels.Contains(pair));

    private static int Offset(TagData source, long offset, int size) => offset >= 0 && offset <= source.Length - size
        ? (int)offset : throw new InvalidDataException("Initial-state pointer is outside its tag.");

    private TagData Get(uint tag)
    {
        Reads?.Add(tag);
        if (!data.TryGetValue(tag, out var value)) data[tag] = value = new(catalog.Read(tag));
        return value;
    }
}
