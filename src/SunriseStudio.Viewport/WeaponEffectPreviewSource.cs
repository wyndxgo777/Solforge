using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** An effect spawned by a weapon clip at a frame: clip, entity, sequence, system and attachment. */
public sealed record WeaponEffectPreviewLink(string Name, string ClipName, uint ClipTag,
    int Frame, uint EffectEntityTag, uint SequenceTag, uint SystemTag, uint AttachmentHash);
/** A scene for a weapon effect preview and the tags it was built from. */
public sealed record PreparedWeaponEffectPreview(SceneDocument Scene, IReadOnlySet<uint> SourceTags);

/** Clip-spawned effects share the weapon's point provider through an explicit preview context. */
public static class WeaponEffectPreviewSource
{
    /** Only project effects in project clips, reached by typed particle events, are offered on the weapon page. */
    public static IReadOnlyList<WeaponEffectPreviewLink> Discover(PackageCatalog catalog,
        IReadOnlyDictionary<string, uint> clips)
    {
        var projectTags = catalog.GetOverlayEntries().Select(entry => entry.Entry.TagHash).ToHashSet();
        var reader = new EffectReader(catalog);
        var links = new List<WeaponEffectPreviewLink>();
        foreach (var (name, tag) in clips)
        {
            if (!projectTags.Contains(tag) || !catalog.TryGetEntry(tag, out var clip) || clip.Reference != 0x80808F49) continue;
            byte[] bytes = catalog.Read(tag);
            foreach (int row in Rows(bytes, 0x160, 0x8080902B, 8))
            {
                int at = checked((int)(row + I64(bytes, row)));
                if (at < 4 || at > bytes.Length - 32 || U32(bytes, at - 4) != 0x80809033 || bytes[at + 2] != 3) continue;
                uint entity = U32(bytes, at + 24), point = U32(bytes, at + 8);
                int frame = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at));
                if (!projectTags.Contains(entity) || frame < 0
                    || frame >= BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(0x13C))) continue;
                var effects = reader.ReadEntity(entity);
                if (effects.Count != 1 || effects[0].Systems.Count != 1) continue;
                var effect = effects[0]; var system = effect.Systems[0];
                if (system.GpuSimulated || system.Emitter is not { SpawnSlot: 0, EmitterSlot: 0, SlotCount: 1 }
                    || !effect.Locations.SelectMany(location => location).SelectMany(location => location.Sources)
                        .Any(source => source.Kind == 2 && source.Point == point)) continue;
                links.Add(new(catalog.GetName(system.Tag) ?? catalog.GetName(entity) ?? $"Particles {system.Tag:X8}",
                    name, tag, frame, entity, effect.SourceTag, system.Tag, point));
            }
        }
        return links.DistinctBy(link => (link.ClipTag, link.Frame, link.EffectEntityTag)).ToArray();
    }

    /** A private catalog composes the selected FX class node without changing any project bytes. */
    public static PreparedWeaponEffectPreview Prepare(PackageCatalog catalog, uint weaponEntity,
        WeaponEffectPreviewLink effect, IReadOnlyList<uint> artEntities, CancellationToken token = default)
    {
        if (artEntities.Count is 0 or > 32 || !catalog.TryGetEntry(weaponEntity, out var owner)
            || owner.Reference != 0x80809C0F)
            throw new InvalidDataException("Weapon particle preview needs its gameplay root and art parts.");
        var links = Discover(catalog, new Dictionary<string, uint> { [effect.ClipName] = effect.ClipTag });
        if (!links.Contains(effect)) throw new InvalidDataException("The weapon particle event changed. Open it again.");
        long revision = catalog.OverlayRevision;
        byte[] root = catalog.Read(weaponEntity).ToArray();
        int[] components = Rows(root, 0x10, 0x80809C04, 12);
        if (!components.Any(row => U32(root, row) == effect.SequenceTag))
        {
            byte[] rows = new byte[checked((components.Length + 1) * 12)];
            for (int index = 0; index < components.Length; index++)
                root.AsSpan(components[index], 12).CopyTo(rows.AsSpan(index * 12));
            Put32(rows, components.Length * 12, effect.SequenceTag);
            root = Append(root, 0x10, 0x80809C04, rows, 12);
        }
        // TODO: retire with the clip-burst preview; the overlay override needs its own catalog.
        // A throwaway catalog writes no disk cache.
        using var context = PackageCatalog.Open(catalog.GameRoot, catalog.KeySource, cacheOptions: new() { Enabled = false });
        var overlay = catalog.GetOverlayEntries().Where(entry => entry.Entry.TagHash != weaponEntity).ToList();
        overlay.Add(new(owner with { Size = root.Length }, catalog.GetName(weaponEntity)));
        context.SetOverlayEntries(overlay);
        context.ReadOverride = tag => tag == weaponEntity ? root : catalog.Read(tag);
        var scene = new AssetSceneLoader(context).Load(weaponEntity, token);
        var markers = scene.Markers.Where(marker => marker.Effect?.SourceTag == effect.SequenceTag).ToArray();
        if (markers.Length != 1 || markers[0].Effect!.SpawnMissing(0))
            throw new InvalidDataException("The weapon does not provide this particle attachment point.");
        scene.Name = effect.Name;
        scene.Markers.RemoveAll(marker => marker.Effect is not null && marker.Effect.SourceTag != effect.SequenceTag);
        var sources = scene.ReadTags.ToHashSet();
        sources.UnionWith([weaponEntity, effect.ClipTag, effect.EffectEntityTag, effect.SequenceTag, effect.SystemTag]);
        foreach (uint entity in artEntities.Distinct())
        {
            token.ThrowIfCancellationRequested();
            var part = new AssetSceneLoader(catalog).Load(entity, token);
            if (part.DecodeErrorCount != 0 || part.UnsupportedCount != 0)
                throw new NotSupportedException("A weapon art part could not be decoded.");
            scene.Instances.AddRange(part.Instances.Where(instance => instance.InitiallyVisible != false
                && !instance.Mesh.NeverDrawn && instance.Mesh.LodLevel == 0));
            sources.UnionWith(part.ReadTags); sources.Add(entity);
        }
        if (catalog.OverlayRevision != revision)
            throw new InvalidDataException("Weapon assets changed while the effect preview was prepared.");
        return new(scene, sources);
    }

    /** The preview adds only a typed construction row; original relative pointers stay at their offsets. */
    private static byte[] Append(byte[] source, int field, uint type, byte[] rows, int stride)
    {
        int header = checked((source.Length + 19) & ~15);
        byte[] result = new byte[checked(header + 16 + rows.Length)]; source.CopyTo(result, 0);
        Put64(result, field, rows.Length / stride); Put64(result, field + 8, header - field - 8);
        Put32(result, header - 4, 0x80809FBD); Put64(result, header, rows.Length / stride);
        Put32(result, header + 8, type); rows.CopyTo(result, header + 16); Put64(result, 0, result.Length);
        return result;
    }

    private static int[] Rows(byte[] bytes, int field, uint type, int stride)
    {
        if (field < 0 || field > bytes.Length - 16) throw new InvalidDataException("Weapon effect array is truncated.");
        long count = I64(bytes, field), header = checked(field + 8 + I64(bytes, field + 8));
        if (count == 0) return [];
        if (count < 0 || count > 65536 || header < 4 || header > bytes.Length - 16
            || count > (bytes.Length - header - 16) / stride || U32(bytes, (int)header - 4) != 0x80809FBD
            || I64(bytes, (int)header) != count || U32(bytes, (int)header + 8) != type)
            throw new InvalidDataException("Weapon effect array has an invalid native layout.");
        return Enumerable.Range(0, (int)count).Select(index => checked((int)header + 16 + index * stride)).ToArray();
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Put64(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
}
