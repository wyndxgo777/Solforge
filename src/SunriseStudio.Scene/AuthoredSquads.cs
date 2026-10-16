using System.Numerics;

namespace SunriseStudio.Scene;

/**
 * Squad part of AssetSceneLoader: collects placement, squad and rule facts from config tags and joins
 * them into squad info.
 */
public sealed partial class AssetSceneLoader
{
    private sealed record PlacementFact(ulong Identity, uint Tag, int Offset, uint ClassTag,
        uint Bubble, uint EntryIndex, Vector3 Position, Quaternion Rotation, uint NameHash);
    private sealed record SquadFact(uint Tag, uint Bubble, uint EntryIndex, AuthoredSlotIdentity Slot,
        int Members, AuthoredSlotIdentity[] Rules);
    private sealed record RuleFact(uint Tag, uint Bubble, uint EntryIndex, AuthoredSlotIdentity Slot, ulong[] Points);
    private readonly List<PlacementFact> authoredPlacements = [];
    private readonly List<SquadFact> authoredSquads = [];
    private readonly List<RuleFact> authoredRules = [];
    // Squad config tag -> per member, the entity classes its candidate placements name.
    private readonly Dictionary<uint, List<uint[]>> squadMembers = [];
    /** A slot-bearing descriptor at a byte offset, with its class id. */
    internal sealed record DescriptorFact(AuthoredSlotIdentity Slot, int Offset, uint ClassId);

    /** Packed references retain the object's registry key, slot type and global slot ordinal. */
    private static AuthoredSlotIdentity SlotReference(ulong value) =>
        new((uint)value, (ushort)(value >> 32), (ushort)(value >> 48));

    /** Descriptor candidates must repeat the tag, marker, schemas and an existing object slot. */
    private AuthoredSlotIdentity[] ConfigSlots(uint tag, TagData data, ushort type)
        => ConfigDescriptors(tag, data).Where(d => d.Slot.Type == type).Select(d => d.Slot).Distinct().ToArray();

    private DescriptorFact[] ConfigDescriptors(uint tag, TagData data) =>
        DescriptorCandidates(tag, data).Where(d => slotNames.ContainsKey(d.Slot)).ToArray();

    /** Every word offset that matches the descriptor pattern, before the slot must exist. */
    internal static IEnumerable<DescriptorFact> DescriptorCandidates(uint tag, TagData data)
    {
        for (var offset = 0; offset <= data.Length - 128; offset += 4)
        {
            if (data.U32(offset) != tag || data.U32(offset + 8) != 0x70
                || (data.U32(offset + 4) >> 16) != 0x8080) continue;
            var sense = data.U32(offset + 68);
            var auth = data.U32(offset + 72);
            if ((sense != uint.MaxValue && (sense >> 16) != 0x8080)
                || (auth != uint.MaxValue && (auth >> 16) != 0x8080)) continue;
            var identity = new AuthoredSlotIdentity(data.U32(offset + 48), data.U16(offset + 52), data.U16(offset + 54));
            yield return new DescriptorFact(identity, offset, data.U32(offset + 4));
        }
    }

    /** Reciprocal component links distinguish squad and rule configs from unrelated classes. */
    private void ReadSquadConfig(uint tag, TagData data, uint bubble)
    {
        var primary = data.Relative(0x10);
        var secondary = data.Relative(0x18);
        if (primary < 4 || secondary < 4 || !data.Has(primary, 16) || !data.Has(secondary, 16)) return;
        var firstClass = data.U32(primary - 4);
        var secondClass = data.U32(secondary - 4);
        if (firstClass is not (0x80809A3B or 0x808094CF)) return;
        if (data.U32(primary) != tag || data.U32(secondary) != tag
            || data.U32(primary + 4) != secondClass || data.U32(secondary + 4) != firstClass
            || data.U64(primary + 8) != (ulong)secondary || data.U64(secondary + 8) != (ulong)primary)
            throw new InvalidDataException("Squad component peer links do not agree.");
        if (firstClass == 0x80809A3B && secondClass == 0x8080948F)
        {
            var members = data.Rows(secondary + 0xA8, 0x80808356, 0x68);
            ReadSquadCandidates(tag, data, bubble, members);
            var references = new[] { SlotReference(data.U64(secondary + 0x98)), SlotReference(data.U64(secondary + 0xA0)) }
                .Where(r => r.Type == 66 && r.RegistryKey != uint.MaxValue).Distinct().ToArray();
            foreach (var slot in ConfigSlots(tag, data, 1))
            {
                var targets = references;
                if (references.Length == 0 && data.Has(primary + 0x5D0, 8) && data.I64(primary + 0x5D0) != 0)
                {
                    var pointSet = data.Relative(primary + 0x5D0);
                    var placementPeer = data.Relative(secondary + 0xB8);
                    if (!data.Has(pointSet - 4, 20) || !data.Has(placementPeer - 4, 20)
                        || data.U32(pointSet - 4) != 0x80807EBE || data.U32(placementPeer - 4) != 0x80807EBF
                        || data.U32(pointSet) != tag || data.U32(placementPeer) != tag
                        || data.U32(pointSet + 4) != 0x80807EBF || data.U32(placementPeer + 4) != 0x80807EBE
                        || data.U64(pointSet + 8) != (ulong)placementPeer || data.U64(placementPeer + 8) != (ulong)pointSet)
                        throw new InvalidDataException("Inline squad point-set peers do not agree.");
                    var points = data.Rows(pointSet + 0x40, 0x80809840, 0x48).Select(p => data.U64(p)).ToArray();
                    var placement = placementPeer + 0x10;
                    var position = data.V3(placement + 0x20);
                    var rotation = data.Q4(placement + 0x10);
                    if (!Finite(position) || !float.IsFinite(rotation.LengthSquared())
                        || !float.IsFinite(data.F32(placement + 0x2C)))
                        throw new InvalidDataException("Inline squad transform is not finite.");
                    authoredPlacements.Add(new PlacementFact(data.U64(placement + 0x70), tag, placement,
                        data.U32(placement), bubble, activeEntryIndex, position, rotation, data.U32(placement + 0x64)));
                    authoredRules.Add(new RuleFact(tag, bubble, activeEntryIndex, slot, points));
                    targets = [slot];
                }
                authoredSquads.Add(new SquadFact(tag, bubble, activeEntryIndex, slot, members.Length, targets));
            }
        }
        else if (firstClass == 0x808094CF && secondClass == 0x808094D0)
        {
            var points = data.Rows(primary + 0x1A0, 0x80809840, 0x48).Select(r => data.U64(r)).ToArray();
            foreach (var slot in ConfigSlots(tag, data, 66))
                authoredRules.Add(new RuleFact(tag, bubble, activeEntryIndex, slot, points));
        }
    }

    /** All six authored candidate arrays remain distinct from runtime variant selection. */
    private void ReadSquadCandidates(uint tag, TagData data, uint bubble, int[] members)
    {
        var memberClasses = new List<uint[]>();
        foreach (var member in members)
        {
            var classes = new List<uint>();
            for (var variant = 0; variant < 6; ++variant)
                foreach (var candidate in data.Rows(member + 8 + variant * 16, 0x80808358, 0x18))
                {
                    if (data.I64(candidate) == 0) continue;
                    var placement = data.Relative(candidate);
                    if (!data.Has(placement - 4, 0x94) || data.U32(placement - 4) != 0x808099D8)
                        throw new InvalidDataException("Squad candidate has an invalid placement target.");
                    var position = data.V3(placement + 0x20);
                    var rotation = data.Q4(placement + 0x10);
                    if (!Finite(position) || !float.IsFinite(rotation.LengthSquared())
                        || !float.IsFinite(data.F32(placement + 0x2C)))
                        throw new InvalidDataException("Squad candidate transform is not finite.");
                    authoredPlacements.Add(new PlacementFact(data.U64(placement + 0x70), tag, placement,
                        data.U32(placement), bubble, activeEntryIndex, position, rotation,
                        data.U32(placement + 0x64)));
                    classes.Add(data.U32(placement));
                }
            memberClasses.Add(classes.Distinct().ToArray());
        }
        squadMembers[tag] = memberClasses;
    }

    /** "Dreg x3, Vandal": each member's candidate classes by combatant name, else by internal name. */
    private (string Summary, string[] Names)? SquadComposition(uint configTag)
    {
        if (!squadMembers.TryGetValue(configTag, out var members) || members.Count == 0) return null;
        var perMember = members.Select(classes => string.Join(" or ", classes
            .Select(c => Links(c) is var l ? l.Combatant ?? l.InternalName : null).OfType<string>().Distinct()))
            .Where(n => n.Length != 0).ToArray();
        if (perMember.Length == 0) return null;
        var summary = string.Join(", ", perMember.GroupBy(n => n)
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Key} x{g.Count()}"));
        return (summary, perMember.SelectMany(n => n.Split(" or ")).Distinct().ToArray());
    }

    /** Only an exact rule identity and placed-entry identity can supply a squad anchor. */
    private void ResolveSquadAnchors()
    {
        var rules = authoredRules.GroupBy(r => (r.Slot, r.Bubble, r.EntryIndex))
            .ToDictionary(g => g.Key, g => g.DistinctBy(r => r.Tag).ToArray());
        var placements = authoredPlacements.Where(p => p.Identity is not (0 or ulong.MaxValue))
            .GroupBy(p => (p.Identity, p.Bubble, p.EntryIndex)).ToDictionary(g => g.Key,
                g => g.DistinctBy(p => (p.Tag, p.Offset)).ToArray());
        foreach (var squad in authoredSquads.DistinctBy(s => (s.Tag, s.Slot, s.Bubble, s.EntryIndex)))
        {
            var matchedRules = squad.Rules.SelectMany(r => rules.GetValueOrDefault((r, squad.Bubble, squad.EntryIndex), []))
                .DistinctBy(r => r.Tag).ToArray();
            var ambiguous = matchedRules.Length > 1;
            var firstMarker = scene.Markers.Count;
            var anchors = 0;
            var unresolved = 0;
            foreach (var point in matchedRules.SelectMany(r => r.Points).Distinct())
            {
                if (!placements.TryGetValue((point, squad.Bubble, squad.EntryIndex), out var found))
                { unresolved++; continue; }
                foreach (var fact in found) scene.SquadSourceRows.Add((fact.Tag, fact.Offset));
                var positions = found.Select(p => p.Position).Distinct().ToArray();
                if (found.Length > 1) ambiguous = true;
                if (positions.Length != 1) { ambiguous = true; unresolved++; continue; }
                var source = found[0];
                scene.Markers.Add(new SceneMarker
                {
                    Name = $"Squad {squad.Slot.Index}",
                    NameHash = slotNames.GetValueOrDefault(squad.Slot),
                    Slot = squad.Slot,
                    TagHash = squad.Tag,
                    SourceTag = source.Tag,
                    SourceOffset = source.Offset,
                    PlacementIdentity = point,
                    BubbleHash = squad.Bubble,
                    Position = source.Position,
                    Rotation = source.Rotation,
                    Extents = new Vector3(0.35f),
                    Layer = SceneLayer.Squads,
                    Semantic = "authored squad anchor",
                    AssociationIsAmbiguous = ambiguous,
                    Color = new Vector4(1, 0.45f, 0.2f, 1)
                });
                ApplyState(scene.Markers[^1], squad.Bubble, squad.EntryIndex);
                anchors++;
            }
            if (ambiguous)
                foreach (var marker in scene.Markers.Skip(firstMarker)) marker.AssociationIsAmbiguous = true;
            scene.Squads.Add(new AuthoredSquadInfo(squad.Tag, squad.Slot, squad.Bubble, squad.EntryIndex,
                squad.Members, anchors, unresolved, ambiguous, matchedRules.Length != 0));
        }
    }
}
