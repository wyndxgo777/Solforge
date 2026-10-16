using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace SunriseStudio.Packages;

/** What an entity's component classes say it is; shared by the scene loader and the asset browser. */
public static class ComponentRoles
{
    // Component classes whose role this viewer's readers establish.
    public const uint ModelComponent = 0x808072B8, ClothModelComponent = 0x80807273;
    public const uint CombatantComponent = 0x80807EE1;
    public const uint AnimationComponent = 0x808036E2, LightComponent = 0x80807132;
    // Its configs reference expensive-light tags, like LightComponent.
    public const uint SecondLightComponent = 0x80806F4E;
    public const uint LensFlareComponent = 0x80806CBD, TriggerShapeComponent = 0x808099CF;

    // Payload-carrier components: the placements of their entities have a payload that targets this tag kind.
    private static readonly Dictionary<uint, (string Label, string Kind)> PayloadComponents = new()
    {
        [0x80806B58] = ("audio", "Audio emitter"),
        [0x80806B5D] = ("audio", "Audio emitter"),
        [0x80806FAB] = ("volume", "Trigger volume"),
        [0x80806F59] = ("light collection", "Light"),
        [0x80806DE5] = ("model reference", "Prop"),
        [0x80806E64] = ("decal", "Decal"),
        [0x80806F37] = ("ambient occlusion", "Ambient occlusion"),
    };

    public static IReadOnlyList<string> EntityKinds { get; } =
    [
        "Spawner", "Combatant", "Interactable", "Animated model", "Prop", "Trigger volume", "Light", "Animated", "Effect",
        "Audio emitter", "Decal", "Ambient occlusion", "Device", "Activity logic", "Other"
    ];

    /** Component facts about one entity or config; flags come from tags the configs reference. */
    public readonly record struct Facts(IReadOnlyCollection<uint> Primaries, bool HasPrompt,
        bool HasParticles, bool HasAudio, bool HasCombatantName = false);

    /** The first rule that matches, in EntityKinds order; rules read component classes and references only. */
    public static string EntityKind(Facts facts, ComponentSlotCatalog? slots = null)
    {
        var p = facts.Primaries;
        if (p.Contains(0x80809A3BU) || p.Contains(0x808094CFU) || p.Contains(0x808094D1U)) return "Spawner";
        if (facts.HasCombatantName) return "Combatant";
        if (facts.HasPrompt) return "Interactable";
        var model = p.Contains(ModelComponent) || p.Contains(ClothModelComponent);
        if (model && p.Contains(AnimationComponent)) return "Animated model";
        if (model) return "Prop";
        string? Payload(string kind) => p.Any(c => PayloadComponents.TryGetValue(c, out var r) && r.Kind == kind) ? kind : null;
        if (Payload("Prop") is { } prop) return prop;
        if (p.Contains(TriggerShapeComponent) || p.Contains(0x80809522U) || Payload("Trigger volume") != null)
            return "Trigger volume";
        if (p.Contains(LightComponent) || p.Contains(SecondLightComponent) || Payload("Light") != null) return "Light";
        if (p.Contains(AnimationComponent)) return "Animated";
        if (p.Contains(LensFlareComponent) || facts.HasParticles) return "Effect";
        if (facts.HasAudio || Payload("Audio emitter") != null) return "Audio emitter";
        if (Payload("Decal") is { } decal) return decal;
        if (Payload("Ambient occlusion") is { } occlusion) return occlusion;
        if (p.Contains(0x80804F45U)) return "Device";
        if (slots is not null && p.Any(component => slots.TryGetSlotType(component, out _))) return "Activity logic";
        return "Other";
    }

    /** Short role label of a component class (model, animation and so on), or null. */
    public static string? Label(uint component) => component switch
    {
        ModelComponent => "model",
        ClothModelComponent => "cloth model",
        AnimationComponent => "animation",
        LightComponent or SecondLightComponent => "light",
        LensFlareComponent => "lens flare",
        TriggerShapeComponent => "trigger shape",
        CombatantComponent => "combatant",
        _ when PayloadComponents.TryGetValue(component, out var payload) => payload.Label,
        _ => null
    };

    /**
     * Reads the facts of one config: its primary class, a prompt and resource references.
     * A prompt is a key of a referenced string container, stored 0x10 bytes before that reference.
     */
    public static (uint Primary, string? Prompt, bool Particles, bool Audio) ReadConfig(
        PackageCatalog catalog, byte[] data, LocalizedStrings? strings, HashSet<uint>? referencedClasses = null,
        List<(uint Tag, uint Class)>? namedLinks = null)
    {
        uint primary = 0;
        if (data.Length < 0x20) return (0, null, false, false);
        var relative = I64(data, 0x10);
        if (relative != 0 && 0x10 + relative >= 4 && 0x10 + relative <= data.Length)
            primary = U32(data, (int)(0x10 + relative) - 4);
        string? prompt = null;
        bool particles = false, audio = false;
        for (var offset = 0x20; offset <= data.Length - 4; offset += 4)
        {
            var value = U32(data, offset);
            if (value < 0x80000000 || value == uint.MaxValue || !catalog.TryGetEntry(value, out var entry)) continue;
            if (AssetCategories.IsStructured(entry)) referencedClasses?.Add(entry.Reference);
            switch (entry.Reference)
            {
                case 0x80806E28: particles = true; break;
                case 0x80809802: audio = true; namedLinks?.Add((value, entry.Reference)); break;
                case 0x8080941E or CombatantNameRecord: namedLinks?.Add((value, entry.Reference)); break;
                case 0x80809A88 when prompt == null && strings != null && offset >= 0x10:
                    prompt = strings.Get(value, U32(data, offset - 0x10));
                    break;
            }
        }
        return (primary, prompt, particles, audio);
    }

    // A combatant component (primary 0x80807EE1) references a record of this class; its +0x08 is a
    // localized display-name hash such as "Legionary".
    public const uint CombatantNameRecord = 0x8080815F;

    /** The display name a class-0x8080815F record names, when the string table resolves it uniquely. */
    public static string? CombatantName(PackageCatalog catalog, uint record, LocalizedStrings? strings)
    {
        if (strings == null || !catalog.TryGetEntry(record, out var entry) || entry.Reference != CombatantNameRecord)
            return null;
        var data = catalog.Read(record);
        return data.Length >= 12 ? strings.Get(U32(data, 8))?.Trim() : null;
    }

    /** Entity definition byte +150, or null for a definition too short to hold it. */
    public static byte? ObjectTypeIndex(ReadOnlySpan<byte> definition) =>
        definition.Length > 150 ? definition[150] : null;

    /** Object type name of an entity definition, or null when it has none. */
    public static string? ObjectType(ReadOnlySpan<byte> definition) =>
        ObjectTypeIndex(definition) is { } type ? ObjectTypeNames.Name(type) ?? $"Object type {type}" : null;

    /** Entity definition +0x08 is FNV-1 of the internal name; 0x811C9DC5 means none was hashed. */
    public static uint? InternalNameHash(byte[] definition)
    {
        if (definition.Length < 12) return null;
        var hash = U32(definition, 8);
        return hash is 0x811C9DC5 or 0 ? null : hash;
    }

    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static long I64(byte[] data, int offset) => BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset));
}

/** Immutable component facts and dependency links for the current catalog overlay. */
public sealed partial class ComponentIndex
{
    private const uint EntityClass = 0x80809C0F, ConfigClass = 0x80809C36;
    private readonly Dictionary<uint, uint> primaries = [];
    private readonly Dictionary<uint, string> prompts = [];
    private readonly Dictionary<uint, string> entityKinds = [];

    public TimeSpan BuildTime { get; private set; }
    public int ConfigCount => primaries.Count;
    public int EntityCount => entityKinds.Count;
    public int PromptCount => prompts.Count;

    private ComponentSlotCatalog slots = ComponentSlotCatalog.Empty;
    private ComponentSlotBuilder? slotBuilder;
    private Dictionary<uint, ComponentSlotConfigSeed>? slotUpdates;

    private ComponentIndex() { }

    public ComponentSlotCatalog SlotMappings => slots;
    public IReadOnlyList<string> NameDiagnostics => slots.Diagnostics;

    private const uint SoundClass = 0x80809802;
    private readonly Dictionary<uint, string> referenceLabels = [];
    private readonly Dictionary<uint, uint> soundIds = [];
    private readonly Dictionary<uint, uint> entitySounds = [];
    private readonly Dictionary<uint, string> patterns = [];

    /** The event id at +0x08 of a class-0x80809802 wwise event tag; it is FNV-1 of the event name. */
    public uint? SoundId(uint eventTag) => soundIds.TryGetValue(eventTag, out var id) ? id : null;

    /** The one wwise event tag an entity's configs reference, if exactly one. */
    public uint? EntitySound(uint entity) => entitySounds.TryGetValue(entity, out var tag) ? tag : null;

    /** The leaf of the one pattern path an entity's behaviours carry, if exactly one. */
    public string? Pattern(uint entity) => patterns.GetValueOrDefault(entity);

    private readonly Dictionary<uint, string> combatants = [];
    private readonly Dictionary<uint, byte> objectTypes = [];
    private readonly Dictionary<uint, uint> nameHashes = [];
    private readonly Dictionary<uint, uint> modelTags = [];

    /** A cached zero means the config had no validated secondary model link. */
    public bool TryGetModelTag(uint configTag, out uint modelTag) => modelTags.TryGetValue(configTag, out modelTag);

    /** The localized display name of an entity's combatant record, e.g. "Legionary". */
    public string? CombatantName(uint entity) => combatants.GetValueOrDefault(entity);

    /** Entity definition byte +150 by name; null when the definition is too short to hold it. */
    public string? ObjectType(uint entity) => objectTypes.TryGetValue(entity, out byte type)
        ? ObjectTypeNames.Name(type) ?? $"Object type {type}" : null;

    /** Name of a slot type number. */
    public string? SlotTypeName(int type) => SlotTypeNames.Name(type);
    /** True when the component class is a slot component. */
    public bool IsSlotComponent(uint component) => slots.TryGetSlotType(component, out _);

    /** FNV-1 of the entity's internal name, when the package hashed one. */
    public uint? InternalNameHash(uint entity) => nameHashes.TryGetValue(entity, out var hash) ? hash : null;

    public int CombatantCount => combatants.Count;

    /** Primary class of a config, or 0. */
    public uint PrimaryClass(uint config) => primaries.GetValueOrDefault(config);

    /** A role label, else "uses X" from a class most of the component's configs reference, else null. */
    public string? ComponentLabel(uint component) => slots.TryGetSlotType(component, out ushort type)
        ? SlotTypeNames.Name(type) ?? $"slot type {type}"
        : ComponentRoles.Label(component) ?? referenceLabels.GetValueOrDefault(component);
    /** Prompt text of an entity or config, if any. */
    public string? Prompt(uint entityOrConfig) => prompts.GetValueOrDefault(entityOrConfig);
    /** Kind of an entity, if known. */
    public string? EntityKind(uint entity) => entityKinds.GetValueOrDefault(entity);

    /**
     * Reads every class-0x80809C36 config and class-0x80809C0F entity.
     * @param strings Resolves component prompts; null skips them.
     */
    public static ComponentIndex Build(PackageCatalog catalog, LocalizedStrings? strings, CancellationToken token = default,
        Action<int, int>? progress = null)
    {
        var timer = Stopwatch.StartNew();
        var index = new ComponentIndex { building = new(), slotBuilder = new(catalog) };
        var entries = catalog.Entries.Where(entry => AssetCategories.IsStructured(entry)
                && entry.Reference is SoundClass or ConfigClass or EntityClass)
            .Select(entry => catalog.TryGetEntry(entry.TagHash, out var current) ? current : entry)
            .Concat(catalog.GetOverlayEntries().Select(row => row.Entry)).DistinctBy(entry => entry.TagHash)
            .Where(entry => AssetCategories.IsStructured(entry)).ToArray();
        var configsToRead = entries.Where(entry => entry.Reference is SoundClass or ConfigClass).ToArray();
        var entitiesToRead = entries.Where(entry => entry.Reference == EntityClass).ToArray();
        var behaviourPatterns = new Dictionary<uint, string?>();
        int totalWork = configsToRead.Length + entitiesToRead.Length;
        for (int i = 0; i < configsToRead.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if ((i & 511) == 0) progress?.Invoke(i, totalWork);
            var entry = configsToRead[i];
            if (entry.Reference == SoundClass) index.ReadSound(catalog, entry.TagHash);
            else index.ReadConfig(catalog, entry.TagHash, strings, behaviourPatterns);
        }
        index.slots = index.slotBuilder.Build(token);
        index.slotBuilder = null;
        for (int i = 0; i < entitiesToRead.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if ((i & 511) == 0) progress?.Invoke(configsToRead.Length + i, totalWork);
            index.ReadEntity(catalog, entitiesToRead[i].TagHash);
        }
        index.FinishBuild();
        index.RebuildReferenceLabels();
        progress?.Invoke(totalWork, totalWork);
        index.BuildTime = timer.Elapsed;
        return index;
    }

    /** Model ownership comes from the typed secondary resource and its repeated config tag. */
    private static bool TryReadModelTag(uint configTag, byte[] data, out uint modelTag)
    {
        modelTag = 0;
        if (data.Length < 0x20) return true;
        long relative = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(0x18));
        if (relative == 0) return true;
        if (relative > int.MaxValue - 0x18L || relative < int.MinValue + 0x18L) return false;
        int resource = (int)(relative + 0x18);
        if (resource < 4 || resource > data.Length - 0x1E0) return true;
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(resource - 4));
        if (type is not (0x808072BD or 0x80807286)
            || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(resource)) != configTag) return true;
        modelTag = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(resource + 0x1DC));
        return true;
    }

    /** The leaf of the single ".pattern.tft" content path among a behaviour's inline strings. */
    private static string? PatternName(byte[] data)
    {
        string? found = null;
        for (var offset = 0; offset <= data.Length - 12; offset += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) != 0x80800065) continue;
            var length = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset + 4));
            if (length == 0 || length > (ulong)(data.Length - offset - 12)) continue;
            foreach (var part in Encoding.UTF8.GetString(data, offset + 12, (int)length).Split('\0'))
            {
                if (!part.EndsWith(".pattern.tft", StringComparison.Ordinal)) continue;
                var leaf = part.Replace('\\', '/')[(part.Replace('\\', '/').LastIndexOf('/') + 1)..^".pattern.tft".Length];
                if (found != null && found != leaf) return null;
                found = leaf;
            }
        }
        return found;
    }

    /** Entity definitions list their configs as 12-byte rows of the +0x10 array. */
    public static uint[] EntityConfigs(byte[] data)
    {
        if (data.Length < 0x20) return [];
        var count = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(0x10));
        if (count <= 0 || count > 4096) return [];
        var header = 0x18 + BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(0x18));
        if (header < 4 || header + 16 + count * 12 > data.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)header - 4)) != 0x80809FBD
            || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)header + 8)) != 0x80809C04) return [];
        return Enumerable.Range(0, (int)count)
            .Select(i => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)header + 16 + i * 12))).ToArray();
    }
}
