using System.Numerics;

namespace SunriseStudio.Scene.Collision;

/** Inline part of CollisionSceneLoader: reads trigger shapes embedded in entity configs. */
public sealed partial class CollisionSceneLoader
{
    private sealed record InlineTrigger(uint Tag, TagData Data, int Variant, Vector3 Offset, uint Attachment);
    private readonly Dictionary<uint, InlineTrigger[]> inlineTriggers = [];

    /** The embedded physics shape reads its config resource even when the row overrides its array tag. */
    private InlineTrigger[] InlineTriggers(uint classTag)
    {
        if (inlineTriggers.TryGetValue(classTag, out var found)) return found;
        var result = new List<InlineTrigger>();
        if (!catalog.TryGetEntry(classTag, out var entry) || entry.Reference != EntityClass) return [];
        var definition = new TagData(Read(classTag));
        foreach (int row in definition.Rows(0x10, 0x80809C04, 12))
        {
            uint configTag = definition.U32(row);
            if (!catalog.TryGetEntry(configTag, out var configEntry) || configEntry.Reference != 0x80809C36) continue;
            var config = new TagData(Read(configTag));
            if (config.I64(0x10) == 0) continue;
            int template = config.Relative(0x10);
            if (!config.Has(template - 4, 0x44) || config.U32(template - 4) != 0x808099CF) continue;
            uint resourceTag = config.U32(template + 0x30);
            long offset = config.I64(template + 0x38);
            var data = resourceTag == configTag ? config : new TagData(Read(resourceTag));
            if (offset < 0 || offset > data.Length - 0x50 || data.U32((int)offset + 4) != 0x808092A4)
                throw new InvalidDataException("The trigger physics shape resource is invalid.");
            int shape = (int)offset;
            if (data.I64(shape + 0x10) == 0) continue;
            result.Add(new(resourceTag, data, data.Relative(shape + 0x10), data.V3(shape + 0x40), data.U32(shape + 0x30)));
        }
        return inlineTriggers[classTag] = result.ToArray();
    }

    /** Standalone and placed trigger classes can carry their shape without a row override. */
    private void LoadInlineTriggers(uint classTag, SceneMarker? marker, Matrix4x4 transform)
    {
        var shapes = InlineTriggers(classTag);
        if (shapes.Length == 0) return;
        if (!Matrix4x4.Decompose(transform, out _, out var rotation, out var translation))
            throw new InvalidDataException("The trigger placement has a sheared transform.");
        transform = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
        var entity = AddEntity(CollisionSource.ShapeArray, classTag, null, marker, transform,
            listTag: marker?.SourceTag ?? 0, listRow: marker?.SourceOffset ?? -1, inlineTrigger: true);
        foreach (var shape in shapes) PlaceInlineTrigger(entity, shape, transform);
        Finish(entity);
    }

    /** World-aligned variants reset rotation before the resource offset is rotated into world space. */
    private void PlaceInlineTrigger(CollisionEntity entity, InlineTrigger source, Matrix4x4 owner)
    {
        int piece;
        Matrix4x4 world;
        if (source.Data.U8(source.Variant) <= 3)
        {
            var inline = InlineTriggerShape.Decode(source.Data.Bytes, source.Variant);
            world = inline.WorldTransform(owner);
            piece = Piece(source.Tag, source.Variant, inline.Shape);
        }
        else
        {
            uint tag = source.Data.U32(source.Variant + 0x28);
            if (Packfile(tag) is not { } file)
            { scene.Count("inline trigger without a shape packfile"); return; }
            piece = Piece(tag, file.RootOffset, file);
            if (piece < 0) { scene.Count("inline trigger without a shape packfile"); return; }
            world = owner;
        }
        if (source.Attachment is not (0 or 0x811C9DC5))
            scene.Notes.Add($"Trigger {source.Tag:X8} attachment {source.Attachment:X8} is not resolved; its preview uses the object frame.");
        world.Translation += Vector3.TransformNormal(source.Offset, world);
        Place(entity, piece, 0, -1, -1, QsTransform.Identity, world, CollisionRole.Trigger);
    }
}
