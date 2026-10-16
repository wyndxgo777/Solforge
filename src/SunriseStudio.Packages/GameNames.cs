using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace SunriseStudio.Packages;

/** Names a scenario carries: its dev name, its activity name hash and its destination text hash. */
public sealed record ScenarioNames(uint Tag, string? DevName, uint StringsTag, uint ActivityHash,
    string? Activity, uint DestinationHash, string? Destination);

/** Readable names for tags, classes and hashes; installed tag data comes before any fallback. */
public sealed partial class GameNames
{
    private const uint MapRootClass = 0x808091DE, LocationNamesClass = 0x80809962, ScenarioClass = 0x80809994;
    private const uint StringsClass = 0x80809A88, InlineStringClass = 0x80800065, BubbleParentClass = 0x80807DAE;

    private readonly PackageCatalog catalog;
    private readonly Func<uint, string?>? hashFallback;
    private readonly Dictionary<uint, ScenarioNames> scenarios = [];
    private readonly Dictionary<uint, string> mapDevNames = [];
    private readonly Dictionary<uint, string> mapTitles = [];
    private readonly Dictionary<string, uint[]> stringsByDevName = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> tagTitles = [];
    // Bubble parent tag -> bubble name hash at +0x18.
    private readonly Dictionary<uint, uint> bubbleParents = [];

    public LocalizedStrings Strings { get; private set; }
    public TimeSpan BuildTime { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    /**
     * Reads every string container, scenario and map root once; later reads are lock-free.
     * @param catalog Installed packages; it must outlive this object.
     * @param hashFallback Thread-safe resolver for hashes no localized string names.
     * @param token Cancels the scan.
     */
    public GameNames(PackageCatalog catalog, Func<uint, string?>? hashFallback = null, CancellationToken token = default)
    {
        var timer = Stopwatch.StartNew();
        this.catalog = catalog;
        this.hashFallback = hashFallback;
        Strings = LocalizedStrings.Load(catalog, token);
        var diagnostics = new List<string>(Strings.Diagnostics);
        foreach (var entry in catalog.Entries)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (entry.Reference == ScenarioClass)
                    scenarios.Add(entry.TagHash, ReadScenario(catalog, entry.TagHash, Strings));
                else if (entry.Reference == MapRootClass && ReadMapDevName(catalog, entry.TagHash) is { } map)
                {
                    mapDevNames.Add(entry.TagHash, map.DevName);
                    tagTitles.TryAdd(map.NamesTag, $"{map.DevName} location names");
                }
                else if (entry.Reference == BubbleParentClass)
                    bubbleParents.Add(entry.TagHash, U32(catalog.Read(entry.TagHash), 0x18));
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException
                                          or IndexOutOfRangeException or KeyNotFoundException or OverflowException)
            {
                diagnostics.Add($"Names of 0x{entry.TagHash:X8}: {error.Message}");
            }
        }
        foreach (var group in scenarios.Values.Where(s => s.DevName != null).GroupBy(s => s.DevName!, StringComparer.Ordinal))
            stringsByDevName.Add(group.Key, group.Select(s => s.StringsTag).Where(Strings.IsContainer).Distinct().ToArray());
        foreach (var (map, dev) in mapDevNames)
        {
            var destination = scenarios.Values.Where(s => s.DevName == dev && s.Destination != null)
                .GroupBy(s => s.Destination!, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault()?.Key;
            mapTitles.Add(map, destination == null ? dev : $"{destination} ({dev})");
        }
        Diagnostics = diagnostics.AsReadOnly();
        BuildTime = timer.Elapsed;
    }

    private volatile ComponentIndex? components;

    /** Null until LoadComponents finishes; then entity kinds and prompts for the whole catalog. */
    public ComponentIndex? Components => components;

    /** Component facts are complete before the worker publishes their index. */
    public ComponentIndex LoadComponents(CancellationToken token = default, Action<int, int>? progress = null) =>
        components ??= ComponentIndex.Build(catalog, Strings, token, progress);

    /** Overlay edits publish localization and component facts together after dependency refresh. */
    public GameNames RefreshComponents(IEnumerable<uint> changedTags, out IReadOnlySet<uint> affected)
    {
        var changes = changedTags.ToHashSet();
        if (changes.Count == 0) { affected = new HashSet<uint>(); return this; }
        var strings = Strings.Refresh(catalog, changes, out var banks, out var hashes);
        affected = new HashSet<uint>();
        var next = components is null ? null : components.Refresh(catalog, strings, changes.Concat(banks), hashes, out affected);
        if (ReferenceEquals(strings, Strings) && ReferenceEquals(next, components)) return this;
        var result = (GameNames)MemberwiseClone();
        result.Strings = strings; result.components = next;
        return result;
    }

    /** A wwise event's name candidate from the hash resolver; needs LoadComponents for the event id. */
    public string? SoundName(uint eventTag) =>
        components?.SoundId(eventTag) is { } id ? hashFallback?.Invoke(id) : null;

    public int ScenarioCount => scenarios.Count;
    public int MapCount => mapDevNames.Count;

    /** Localized text for a string hash, if any. */
    public string? Localized(uint hash) => Strings.Get(hash);

    /** Known class name for a class hash. */
    public string? ClassName(uint reference) => ClassNames.Get(reference);

    /** Names of a scenario tag, if known. */
    public ScenarioNames? Scenario(uint scenarioTag) => scenarios.GetValueOrDefault(scenarioTag);

    /** Developer name of a map root tag, if known. */
    public string? MapDevName(uint mapRootTag) => mapDevNames.GetValueOrDefault(mapRootTag);
    /** Name hash of the bubble below a parent tag, if any. */
    public uint? BubbleNameHash(uint parentTag) => bubbleParents.TryGetValue(parentTag, out var hash) ? hash : null;

    /** Destination text shared by the map's scenarios, then the map's dev name. */
    public string? MapTitle(uint mapRootTag) => mapTitles.GetValueOrDefault(mapRootTag);

    /**
     * Display text of a bubble name hash.
     * @param nameHash Bubble name hash from a map, scenario or bubble parent.
     * @param mapRootTag Optional map whose string containers settle a hash used with several texts.
     * @return Localized text, then the fallback resolver's name, or null.
     */
    public string? BubbleName(uint nameHash, uint mapRootTag = 0)
    {
        if (Strings.Get(nameHash) is { } text) return text;
        string? dev = mapDevNames.GetValueOrDefault(mapRootTag) ?? scenarios.GetValueOrDefault(mapRootTag)?.DevName;
        if (dev is not null && stringsByDevName.TryGetValue(dev, out var containers))
        {
            var scoped = containers.Select(c => Strings.Get(c, nameHash)).OfType<string>().Distinct().ToArray();
            if (scoped.Length == 1) return scoped[0];
        }
        return hashFallback?.Invoke(nameHash);
    }

    /** Localized bubble text with the authored name beside it when the two differ. */
    public string BubbleTitle(uint nameHash, string authoredName, uint mapRootTag = 0)
    {
        var text = BubbleName(nameHash, mapRootTag);
        if (text == null || authoredName.StartsWith(text, StringComparison.Ordinal)) return authoredName;
        return IsPlaceholder(authoredName) ? text : $"{text} -- {authoredName}";
    }

    /** Localized activity text, else the scenario's package name, with its destination. */
    public string? ActivityTitle(uint scenarioTag)
    {
        if (!scenarios.TryGetValue(scenarioTag, out var names)) return null;
        var packageName = catalog.GetName(scenarioTag);
        var stem = packageName?.Split(':')[0] ?? names.DevName ?? $"0x{scenarioTag:X8}";
        var title = names.Activity is { } activity && activity != stem ? $"{activity} ({stem})" : stem;
        return names.Destination is { } destination && destination != names.Activity ? $"{title} -- {destination}" : title;
    }

    /** A name read from the tag, then its package name, then its class label and hash. */
    public string Describe(uint tag) =>
        catalog.TryGetEntry(tag, out var entry) ? Describe(entry) : $"0x{tag:X8}";

    /** Lock-free lookups only, so a browser can name every catalog entry on demand. */
    public string Describe(PackageEntry entry)
    {
        var tag = entry.TagHash;
        if (!AssetCategories.IsStructured(entry))
            return catalog.GetName(tag) ?? $"{AssetCategories.KindLabel(entry)} 0x{tag:X8}";
        if (entry.Reference == MapRootClass && mapTitles.TryGetValue(tag, out var map)) return map;
        if (entry.Reference == ScenarioClass && ActivityTitle(tag) is { } activity) return activity;
        if (tagTitles.TryGetValue(tag, out var title)) return title;
        if (catalog.GetName(tag) is { } name) return name;
        if (bubbleParents.TryGetValue(tag, out var bubble) && BubbleName(bubble) is { } bubbleName)
            return $"bubble {bubbleName}";
        if (components is { } index && entry.Reference == 0x80809C0F)
        {
            if (index.CombatantName(tag) is { } combatant) return $"{combatant} (combatant)";
            if (index.Prompt(tag) is { } prompt) return $"{prompt} (prompt)";
            if (index.Pattern(tag) is { } pattern) return $"{pattern} (pattern)";
            if (index.InternalNameHash(tag) is { } nameHash && hashFallback?.Invoke(nameHash) is { } internalName)
                return internalName;
            var kind = index.EntityKind(tag) ?? "Other";
            if (kind == "Audio emitter" && index.EntitySound(tag) is { } eventTag && SoundName(eventTag) is { } sound)
                return sound;
            // Derived: the kind and the package that holds the entity; not an authored name.
            return $"{(kind == "Other" ? "Entity" : kind)} -- {AssetCategories.PackageStems(entry.PackageName)[^1]}";
        }
        if (components?.Prompt(tag) is { } configPrompt) return $"{configPrompt} (prompt)";
        if (entry.Reference == 0x80809802 && SoundName(tag) is { } eventName) return eventName;
        return $"{ClassNames.Describe(entry.Reference)} 0x{tag:X8}";
    }

    /**
     * Reads the name fields of one scenario.
     * @param catalog Installed packages.
     * @param tag A class-0x80809994 scenario.
     * @param strings Optional table that turns the hashes into text.
     */
    public static ScenarioNames ReadScenario(PackageCatalog catalog, uint tag, LocalizedStrings? strings)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != ScenarioClass)
            throw new InvalidDataException($"Tag 0x{tag:X8} is not a scenario.");
        var data = catalog.Read(tag);
        var stringsTag = U32(data, 0x28);
        if (!catalog.TryGetEntry(stringsTag, out var stringsEntry) || stringsEntry.Reference != StringsClass)
            stringsTag = 0;
        var activity = U32(data, 0x08);
        var destination = U32(data, 0x20);
        string? Text(uint hash) => strings == null ? null : strings.Get(stringsTag, hash) ?? strings.Get(hash);
        return new ScenarioNames(tag, InlineString(data, 0x30), stringsTag, activity, Text(activity),
            destination, Text(destination));
    }

    /** The map root's location-names tag holds the map dev name at +0x08. */
    private static (uint NamesTag, string DevName)? ReadMapDevName(PackageCatalog catalog, uint mapRootTag)
    {
        var root = catalog.Read(mapRootTag);
        var names = U32(root, 0x08);
        if (!catalog.TryGetEntry(names, out var entry) || entry.Reference != LocationNamesClass) return null;
        return InlineString(catalog.Read(names), 0x08) is { } dev ? (names, dev) : null;
    }

    /** A string pointer targets text after a class-0x80800065 marker and its u64 length. */
    private static string? InlineString(byte[] data, int field)
    {
        var relative = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(field));
        if (relative == 0) return null;
        var start = field + relative;
        if (start < 12 || start >= data.Length || U32(data, (int)start - 12) != InlineStringClass)
            throw new InvalidDataException($"String pointer at 0x{field:X} has no inline string block.");
        var length = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)start - 8));
        if (length <= 0 || start + length > data.Length)
            throw new InvalidDataException($"String block at 0x{start:X} runs past its tag.");
        var bytes = data.AsSpan((int)start, (int)length);
        var end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        return bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
    }

    /** A hex hash or an empty name carries no authored text. */
    private static bool IsPlaceholder(string name) =>
        name.Length == 0 || name.StartsWith("0x", StringComparison.Ordinal);

    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
}
