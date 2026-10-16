using System.Numerics;

namespace SunriseStudio.Scene;

/**
 * Point part of AssetSceneLoader: reads authored slots and points from config tags, and the device and
 * volume links between them.
 */
public sealed partial class AssetSceneLoader
{
    private readonly List<(ulong Placement, AuthoredSlotIdentity Slot)> deviceLinks = [];
    private readonly List<(AuthoredSlotIdentity Target, AuthoredSlotIdentity Source, uint Tag, int Offset)> volumeNameLinks = [];

    /** Type-4 placement arrays and type-23 identities have distinct native descriptor contracts. */
    private void ReadConfigPoints(uint tag, TagData data, uint bubble)
    {
        foreach (var descriptor in ConfigDescriptors(tag, data))
        {
            if (descriptor.Slot.Type == 30 && descriptor.ClassId == 0x8080952F
                && data.U32(descriptor.Offset + 68) == 0x80809531
                && data.U32(descriptor.Offset + 72) == 0x80809532)
            {
                var target = SlotReference(data.U64(descriptor.Offset + 0x58));
                if (target.Type == 60) volumeNameLinks.Add((target, descriptor.Slot, tag, descriptor.Offset + 0x58));
            }
            if (descriptor.Slot.Type == 23 && descriptor.ClassId == 0x80804F45)
            {
                deviceLinks.Add((data.U64(descriptor.Offset + 0x58), descriptor.Slot));
                continue;
            }
            if (descriptor.Slot.Type != 4 || descriptor.ClassId != 0x80809927) continue;
            foreach (var row in data.Rows(descriptor.Offset + 0x58, 0x808099D8, 0x90))
            {
                var (position, rotation) = SlotPose(data, row);
                var identity = data.U64(row + 0x70);
                var nameHash = data.U32(row + 0x64);
                authoredPlacements.Add(new PlacementFact(identity, tag, row, data.U32(row), bubble,
                    activeEntryIndex, position, rotation, nameHash));
                var marker = new SceneMarker
                {
                    Name = $"Object {descriptor.Slot.Index}",
                    NameHash = nameHash,
                    Slot = descriptor.Slot,
                    TagHash = data.U32(row),
                    SourceTag = tag,
                    SourceOffset = row,
                    PlacementIdentity = identity,
                    BubbleHash = bubble,
                    Position = position,
                    Rotation = rotation,
                    Layer = SceneLayer.Objects,
                    Semantic = "object sensor placement",
                    Extents = new Vector3(0.25f),
                    Color = new Vector4(0.9f, 0.8f, 0.25f, 1)
                };
                scene.Markers.Add(marker);
                QueuePlacedClassModels(marker, Placement(data, row + 0x10));
            }
        }
        var triggers = ConfigSlots(tag, data, 31);
        if (triggers.Length == 1)
            foreach (var offset in TriggerLinkOffsets(data))
            {
                var target = SlotReference(data.U64(offset + 8));
                if (target.Type == 60) volumeNameLinks.Add((target, triggers[0], tag, offset + 8));
            }
    }

    /** Embedded object rows hold rotation at +0x10 and position at +0x20 in world coordinates. */
    internal static (Vector3 Position, Quaternion Rotation) SlotPose(TagData data, int row)
    {
        var position = data.V3(row + 0x20);
        var rotation = data.Q4(row + 0x10);
        if (!Finite(position) || !float.IsFinite(rotation.LengthSquared()))
            throw new InvalidDataException("Embedded object placement is not finite.");
        return (position, rotation);
    }

    /** Offsets of 0x80809C42 link records, found by scanning every word of the config. */
    internal static IEnumerable<int> TriggerLinkOffsets(TagData data)
    {
        for (var offset = 0; offset <= data.Length - 16; offset += 4)
            if (data.U32(offset) == 0x80809C42 && data.U32(offset + 4) == 0) yield return offset;
    }

    /** A device name is assigned only through an exact, unique placement-identity match. */
    private void ResolveDeviceNames()
    {
        var links = deviceLinks.Where(l => l.Placement is not (0 or ulong.MaxValue)).GroupBy(l => l.Placement)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Slot).Distinct().ToArray());
        foreach (var marker in scene.Markers.Where(m => m.Layer == SceneLayer.Objects && m.Slot == null))
        {
            if (!links.TryGetValue(marker.PlacementIdentity, out var slots)) continue;
            if (slots.Length != 1) { marker.AssociationIsAmbiguous = true; continue; }
            marker.Slot = slots[0];
            marker.Semantic = "device sensor placement";
        }
    }
}
