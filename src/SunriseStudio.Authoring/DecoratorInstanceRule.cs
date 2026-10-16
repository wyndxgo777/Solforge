using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

/**
 * Writes each edited record into the block, which the CPU reads, and the vertex buffer, which the
 * GPU draws. A value outside the stored ranges re-quantizes the whole block. Then the boxes follow.
 */
internal sealed class DecoratorInstanceRule : IObjectEditRule
{
    // Tags that more than one instance header names, per catalog.
    private static readonly ConditionalWeakTable<PackageCatalog, HashSet<uint>> shared = [];

    public ObjectEditLayout Layout => ObjectEditLayout.DecoratorInstance;

    /** Rewrites the edited decorator records in the CPU block and the vertex buffer, then their boxes. */
    public void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits)
    {
        foreach (var group in edits.GroupBy(edit => edit.Target.Identity).OrderBy(group => group.Key))
        {
            var tags = DecoratorTags.Read(context.Catalog, checked((uint)group.Key));
            if (group.Any(edit => edit.Target.SourceTag != tags.Block))
                throw new InvalidDataException("A decorator edit names a block its payload does not use.");
            var sharing = Shared(context.Catalog);
            if (sharing.Contains(tags.Block) || sharing.Contains(tags.BufferHeader) || sharing.Contains(tags.Quantization))
                throw new NotSupportedException("This decorator payload shares its instance data with another payload; an edit would move both.");
            Write(context, tags, group.ToArray());
        }
    }

    private static void Write(ObjectEditContext context, DecoratorTags tags, ObjectEdit[] edits)
    {
        byte[] block = context.Current(tags.Block), buffer = context.Current(tags.Buffer), quantBytes = context.Current(tags.Quantization);
        var originalBlock = context.Original(tags.Block);
        var quant = DecoratorQuantization.Read(quantBytes);
        var originalQuant = DecoratorQuantization.Read(context.Original(tags.Quantization));
        int[] rows = new NativeRows(block).Rows(0x08, DecoratorTags.RecordClass, DecoratorRecords.Size);
        int[] originalRows = new NativeRows(originalBlock).Rows(0x08, DecoratorTags.RecordClass, DecoratorRecords.Size);
        if (rows.Length != originalRows.Length || buffer.Length != rows.Length * DecoratorRecords.Size)
            throw new InvalidDataException("The decorator block and its vertex buffer disagree on the instance count.");
        var values = rows.Select(row => DecoratorRecords.Decode(block.AsSpan(row, DecoratorRecords.Size), quant)).ToArray();
        foreach (var edit in edits)
        {
            int index = edit.Target.Index;
            if (index < 0 || index >= rows.Length || rows[index] != edit.Target.Offset)
                throw new InvalidDataException("The selected decorator record is missing.");
            var value = values[index];
            var position = edit.Position is { } moved ? ObjectEditCompiler.Vector(moved) : value.Position;
            float scale = edit.Scale ?? value.Scale;
            // An unset rotation keeps the stored axes rather than their nearest rotation.
            values[index] = edit.Rotation is { } turn
                ? DecoratorInstance.Create(position, new Quaternion(turn[0], turn[1], turn[2], turn[3]), scale)
                : value with { Position = position, Scale = scale };
        }
        byte[] probe = new byte[DecoratorRecords.Size], gpu = new byte[DecoratorRecords.Size];
        bool fits = edits.All(edit => DecoratorRecords.TryEncode(values[edit.Target.Index], quant, probe, gpu));
        var written = fits ? quant : DecoratorRecords.Fit(values);
        var indices = fits ? edits.Select(edit => edit.Target.Index).Distinct() : Enumerable.Range(0, rows.Length);
        foreach (int index in indices)
            if (!DecoratorRecords.TryEncode(values[index], written, block.AsSpan(rows[index], DecoratorRecords.Size),
                buffer.AsSpan(index * DecoratorRecords.Size, DecoratorRecords.Size)))
                throw new InvalidDataException("A decorator value does not fit its own quantization.");
        if (!fits) written.Write(quantBytes);
        UpdateBoxes(context, tags, edits, originalBlock, originalRows, originalQuant, block, rows, written);
        foreach (var edit in edits)
        {
            context.Produced(edit, tags.Block);
            context.Produced(edit, tags.Buffer);
            if (!fits) context.Produced(edit, tags.Quantization);
        }
    }

    /** The installed cell box moves from the installed record to the edited one; the payload box grows. */
    private static void UpdateBoxes(ObjectEditContext context, DecoratorTags tags, ObjectEdit[] edits, byte[] originalBlock,
        int[] originalRows, DecoratorQuantization originalQuant, byte[] block, int[] rows, DecoratorQuantization quant)
    {
        byte[] payloadBytes = context.Current(tags.Payload), cellBytes = context.Current(tags.Cells);
        var payload = new NativeRows(payloadBytes);
        uint[] starts = payload.Rows(0x38, 0x80800007, 4).Select(payload.U32).ToArray();
        var originalCells = new NativeRows(context.Original(tags.Cells));
        int[] originalBoxes = originalCells.Rows(0x08, 0x80809673, 48);
        var cells = new NativeRows(cellBytes);
        int[] boxes = cells.Rows(0x08, 0x80809673, 48);
        if (starts.Length != boxes.Length + 1 || originalBoxes.Length != boxes.Length || starts[^1] != rows.Length)
            throw new InvalidDataException("The decorator cells do not cover the instance block.");
        Vector3 payloadMin = payload.V3(0x70), payloadMax = payload.V3(0x80), min = payloadMin, max = payloadMax;
        foreach (var edit in edits)
        {
            int index = edit.Target.Index;
            int cell = Array.FindLastIndex(starts, start => start <= index);
            var before = DecoratorRecords.Decode(originalBlock.AsSpan(originalRows[index], DecoratorRecords.Size), originalQuant).Transform;
            var after = DecoratorRecords.Decode(block.AsSpan(rows[index], DecoratorRecords.Size), quant).Transform;
            if (!Matrix4x4.Invert(before, out var inverse))
                throw new InvalidDataException("The installed decorator transform cannot be inverted.");
            var moved = InstanceBoundsRule.TransformBox(originalCells.V3(originalBoxes[cell]),
                originalCells.V3(originalBoxes[cell] + 16), inverse * after);
            // A one-instance cell takes the moved box like a static bounds row; shared cells grow.
            bool alone = starts[cell + 1] - starts[cell] == 1;
            Vector3 low = alone ? moved.Min : Vector3.Min(cells.V3(boxes[cell]), moved.Min);
            Vector3 high = alone ? moved.Max : Vector3.Max(cells.V3(boxes[cell] + 16), moved.Max);
            // Both W lanes are 1.0, as in every installed bounds row.
            if (low != cells.V3(boxes[cell]) || high != cells.V3(boxes[cell] + 16))
            {
                NativeRows.Write(cellBytes, boxes[cell], low.X, low.Y, low.Z, 1);
                NativeRows.Write(cellBytes, boxes[cell] + 16, high.X, high.Y, high.Z, 1);
                context.Produced(edit, tags.Cells);
            }
            min = Vector3.Min(min, low); max = Vector3.Max(max, high);
        }
        if (min != payloadMin) NativeRows.Write(payloadBytes, 0x70, min.X, min.Y, min.Z, 1);
        if (max != payloadMax) NativeRows.Write(payloadBytes, 0x80, max.X, max.Y, max.Z, 1);
        if (min != payloadMin || max != payloadMax)
            foreach (var edit in edits) context.Produced(edit, tags.Payload);
    }

    private static HashSet<uint> Shared(PackageCatalog catalog) => shared.GetValue(catalog, owner =>
    {
        var counts = new Dictionary<uint, int>();
        foreach (var entry in owner.Entries.Where(entry => entry.Reference == DecoratorTags.HeaderClass).DistinctBy(entry => entry.TagHash))
        {
            var header = new NativeRows(owner.ReadOriginal(entry.TagHash));
            foreach (uint tag in new[] { header.U32(0x14), header.U32(0x18), header.U32(0x1C) })
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
        }
        return counts.Where(pair => pair.Value > 1).Select(pair => pair.Key).ToHashSet();
    });
}
