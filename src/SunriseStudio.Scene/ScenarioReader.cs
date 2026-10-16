using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/**
 * Scenario part of AssetSceneLoader: describes scenarios and walks their states, bubbles and authored
 * objects.
 */
public sealed partial class AssetSceneLoader
{
    private SceneLoadOptions options = new();
    private uint activeEntryIndex = uint.MaxValue;
    private readonly Dictionary<AuthoredSlotIdentity, uint> slotNames = [];

    /** Localized and inline names; null keeps the package and hash-candidate names only. */
    public GameNames? GameNames { get; set; }

    /** Dev name, location and destination text of a scenario. */
    public ScenarioNames DescribeScenarioNames(uint tag) =>
        GameNames?.Scenario(tag) ?? GameNames.ReadScenario(catalog, tag, null);

    /** Localized bubble text first, with the authored name beside it. */
    private string BubbleDisplayName(uint hash)
    {
        var authored = ResolveHashName(hash).Name;
        return GameNames?.BubbleTitle(hash, authored) ?? authored;
    }

    /** Describes authored states without selecting runtime activity state. */
    public ScenarioInfo DescribeScenario(uint tag)
    {
        if (Class(tag) != 0x80809994) throw new InvalidDataException("Tag is not a scenario.");
        var data = new TagData(catalog.Read(tag));
        CollectInlineNames(tag, data);
        var bubbles = new List<ScenarioBubbleInfo>();
        foreach (var bubble in data.Rows(0x50, 0x8080924D, 24))
        {
            var bubbleHash = data.U32(bubble);
            var states = new List<ScenarioStateInfo>();
            var ordinal = 0;
            foreach (var state in data.Rows(bubble + 8, 0x8080924F, 76))
            {
                var entryTag = data.U32(state + 68);
                if (Class(entryTag) != 0x8080925B) throw new InvalidDataException("Scenario state has an invalid entry class.");
                var entry = new TagData(catalog.Read(entryTag));
                var stateHash = data.U32(state + 4);
                states.Add(new ScenarioStateInfo(bubbleHash, ordinal++, stateHash,
                    ResolveHashName(stateHash).Name, data.U8(state) != 0, data.U8(state + 12) != 0,
                    entryTag, entry.U32(16), entry.U32(20), data.U32(state + 28)));
            }
            bubbles.Add(new ScenarioBubbleInfo(bubbleHash, BubbleDisplayName(bubbleHash), states));
        }
        return new ScenarioInfo(tag, Name(tag), bubbles);
    }

    /** A selected state still applies each object's exact authored entry-index filter. */
    private void VisitScenario(uint tag, Matrix4x4 transform, int depth)
    {
        var described = scene.Scenario?.TagHash == tag ? scene.Scenario : DescribeScenario(tag);
        scene.Scenario = described;
        ValidateScenarioSelection(described);
        foreach (var bubble in described.Bubbles)
        {
            if (options.BubbleNameHashes != null && !options.BubbleNameHashes.Contains(bubble.NameHash)) continue;
            IEnumerable<ScenarioStateInfo> selected = options.ScenarioStates switch
            {
                ScenarioStateMode.EnabledByPackage => bubble.States.Where(s => s.PackageEnabled),
                ScenarioStateMode.Selected => options.StateOrdinals.TryGetValue(bubble.NameHash, out var ordinal)
                    ? bubble.States.Where(s => s.Ordinal == ordinal) : [],
                _ => bubble.States
            };
            var loaded = selected.ToArray();
            if (loaded.Length == 0) continue;
            scene.Bubbles.Add(new BubbleInfo(bubble.NameHash, bubble.NameHash, bubble.Name));
            foreach (var state in loaded)
            {
                scene.ScenarioStates.Add(state);
                var previous = activeEntryIndex;
                activeEntryIndex = state.EntryIndex;
                var firstInstance = scene.Instances.Count;
                var firstMarker = scene.Markers.Count;
                try { Visit(state.EntryTag, bubble.NameHash, transform, depth + 1); }
                finally
                {
                    activeEntryIndex = previous;
                    stateRanges.Add(new StateRange(tag, bubble.NameHash, state.Ordinal, state.EntryIndex,
                        firstInstance, scene.Instances.Count, firstMarker, scene.Markers.Count));
                }
            }
        }
        AddDiagnostic($"Scenario uses {options.ScenarioStates} authored states; runtime activation is not simulated.");
    }

    /** Unknown selection keys are errors; an empty explicit selection is valid. */
    private void ValidateScenarioSelection(ScenarioInfo described)
    {
        var declaredBubbles = described.Bubbles.Select(b => b.NameHash).ToHashSet();
        var selectedHashes = options.StateOrdinals.Keys.Concat(
            options.BubbleNameHashes?.AsEnumerable() ?? Enumerable.Empty<uint>());
        foreach (var hash in selectedHashes.Distinct())
            if (!declaredBubbles.Contains(hash))
                throw new ArgumentException($"Scenario 0x{described.TagHash:X8} has no authored bubble 0x{hash:X8}.");
        foreach (var bubble in described.Bubbles)
        {
            if (options.ScenarioStates == ScenarioStateMode.Selected
                && options.StateOrdinals.TryGetValue(bubble.NameHash, out var wanted)
                && bubble.States.All(s => s.Ordinal != wanted))
                throw new ArgumentException($"Bubble 0x{bubble.NameHash:X8} has no authored state {wanted}.");
        }
    }

    /** Slot ordinals belong to the object's complete slot array, not a per-type counter. */
    private void VisitAuthoredObject(uint tag, TagData data, uint bubble, Matrix4x4 transform, int depth)
    {
        var registryKey = data.U32(12);
        var slots = new List<AuthoredSlotInfo>();
        var ordinal = 0;
        foreach (var row in data.Rows(32, 0x80809B3A, 8))
        {
            var identity = new AuthoredSlotIdentity(registryKey, checked((ushort)data.U32(row)), checked((ushort)ordinal++));
            var nameHash = data.U32(row + 4);
            if (slotNames.TryGetValue(identity, out var previous) && previous != nameHash)
                throw new InvalidDataException("Authored slot identity has conflicting names.");
            slotNames[identity] = nameHash;
            slots.Add(new AuthoredSlotInfo(identity, nameHash, ResolveHashName(nameHash).Name));
        }
        if (scene.AuthoredObjects.All(o => o.TagHash != tag))
            scene.AuthoredObjects.Add(new AuthoredObjectInfo(tag, registryKey, slots));
        foreach (var row in data.Rows(56, 0x80809464, 24))
        {
            var declared = data.I32(row);
            if (activeEntryIndex != uint.MaxValue && declared != -1 && declared != activeEntryIndex) continue;
            foreach (var handle in data.Rows(row + 8, 0x80809466, 4))
                Visit(data.U32(handle), bubble, transform, depth + 1);
        }
    }
}
