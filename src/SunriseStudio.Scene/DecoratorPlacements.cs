using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/**
 * Decorator part of AssetSceneLoader: expands decorator payloads (speed trees and ground decorators)
 * into placed instances.
 */
public sealed partial class AssetSceneLoader
{
    // Placement resource whose +0x10 names a class-0x80807164 decorator payload.
    internal const uint DecoratorResourceClass = 0x808071AD;
    private const uint DecoratorPayloadClass = DecoratorTags.PayloadClass;

    /** A payload that does not decode keeps its placement marker. */
    private bool TryDecorators(uint payload, uint bubble, Matrix4x4 placement)
    {
        try { return Decorators(payload, bubble, placement); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException or IOException)
        {
            scene.DecodeErrorCount++;
            scene.Diagnostics.Add($"Cannot decode decorator payload {Name(payload)}: {error.Message}");
            return false;
        }
    }

    /**
     * Places the models of a SpeedTree payload, one mesh instance per part and instance record.
     * Ground decorator payloads place one instance per cell and LOD mesh; see GroundDecorators.
     * @return False for a payload that mixes SpeedTree and ground types.
     */
    private bool Decorators(uint payload, uint bubble, Matrix4x4 placement)
    {
        var data = Read(payload);
        var types = data.Rows(0x08, 0x8080717D, 4).Select(data.U32).ToArray();
        var groups = data.Rows(0x18, 0x80800007, 4).Select(data.U32).ToArray();
        if (types.Length != 0 && types.All(type => Read(type).U32(0x0C) != 0))
            return GroundDecorators(payload, bubble, placement, data, types, groups);
        // Type +0x0C is 0 for a SpeedTree; such a payload holds one instance group per type.
        if (types.Length == 0 || groups.Length != types.Length + 1 || types.Any(type => Read(type).U32(0x0C) != 0))
            return false;
        var tags = DecoratorTags.Read(catalog, payload);
        var quantization = DecoratorQuantization.Read(Read(tags.Quantization).Bytes);
        var block = Read(tags.Block);
        var rows = block.Rows(0x08, DecoratorTags.RecordClass, DecoratorRecords.Size);
        if (groups[0] != 0 || groups[^1] != rows.Length || groups.Zip(groups.Skip(1)).Any(pair => pair.First > pair.Second))
            throw new InvalidDataException("Decorator instance groups do not cover the instance block.");
        var models = types.Select(type => Checked(Read(type).U32(0x08), 0x808073A5)).ToArray();
        // Every LOD part and the far billboard mesh are placed; the native draw picks one by distance.
        var meshes = types.Select((type, index) => SpeedTreeParts(type, models[index])).ToArray();
        for (var type = 0; type < types.Length; ++type)
        {
            var model = models[type];
            var parts = meshes[type];
            for (var i = (int)groups[type]; i < groups[type + 1]; ++i)
            {
                var record = block.Bytes.AsMemory(rows[i], DecoratorRecords.Size);
                var transform = DecoratorRecords.Decode(record.Span, quantization).Transform * placement;
                var objectPlacement = new SceneObjectPlacement(payload, i, transform) { EntryIndex = i };
                foreach (var part in parts)
                    scene.Instances.Add(new MeshInstance
                    {
                        Mesh = part,
                        ObjectPlacement = objectPlacement,
                        Transform = transform,
                        InstanceParent = placement,
                        Name = Name(model),
                        BubbleHash = bubble,
                        NativeInstanceData = record
                    });
            }
        }
        return true;
    }

    private readonly Dictionary<uint, MeshData[]> speedTreeParts = [];

    /**
     * A SpeedTree type's parts placed as its vertex shader places them: each model mesh scales and offsets
     * its positions by scope 8 rows 0 and 1 from type +0x14, not by the model's +0x50 and +0x60.
     * @return The model's own parts when the rows do not read.
     */
    private MeshData[] SpeedTreeParts(uint type, uint model)
    {
        if (speedTreeParts.TryGetValue(type, out var known)) return known;
        var parts = EntityMesh(model);
        try
        {
            var modelData = Read(model);
            Vector3 scale = modelData.V3(0x50), offset = modelData.V3(0x60);
            if (scale.X == 0 || scale.Y == 0 || scale.Z == 0) return speedTreeParts[type] = parts;
            var rows = Read(Read(type).U32(0x14));
            var meshRows = rows.RowsOfAnyClass(0x08, 80);
            var meshOf = new Dictionary<int, int>();
            var modelMeshes = modelData.RowsOfAnyClass(0x10, 0x88);
            for (var m = 0; m < modelMeshes.Length; ++m)
                foreach (var row in modelData.RowsOfAnyClass(modelMeshes[m] + 0x18, 0x20)) meshOf[row] = m;
            return speedTreeParts[type] = parts.Select(part =>
            {
                if (!meshOf.TryGetValue(part.PartIndex, out var m) || m >= meshRows.Length) return part;
                Vector3 rowScale = rows.V3(meshRows[m]), rowOffset = rows.V3(meshRows[m] + 16);
                var vertices = part.Vertices.ToArray();
                for (var v = 0; v < vertices.Length; ++v)
                    vertices[v].Position = (vertices[v].Position - offset) / scale * rowScale + rowOffset;
                return new MeshData
                {
                    TagHash = part.TagHash, Vertices = vertices, Indices = part.Indices, MaterialTag = part.MaterialTag,
                    PartIndex = part.PartIndex, LogicalPartIndex = part.LogicalPartIndex, PartFlags = part.PartFlags,
                    LodLevel = part.LodLevel, SourceVertexIndices = part.SourceVertexIndices, Native = part.Native
                };
            }).ToArray();
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException or IOException)
        {
            return speedTreeParts[type] = parts;
        }
    }

    /**
     * Ground decorators: +0x18 splits the records into bands and +0x38 into cells. 0x7FF742E030E0 draws a
     * band's cells with the parts whose logical index is the band, one model mesh per LOD level. Each
     * instance holds one cell's records; InstanceSourceTag is the payload, InstanceSourceIndex the first record.
     */
    private bool GroundDecorators(uint payload, uint bubble, Matrix4x4 placement, TagData data, uint[] types, uint[] bands)
    {
        var cells = data.Rows(0x38, 0x80800007, 4).Select(data.U32).ToArray();
        var tags = DecoratorTags.Read(catalog, payload);
        var quantization = Read(tags.Quantization).Bytes;
        var block = Read(tags.Block);
        var rows = block.Rows(0x08, DecoratorTags.RecordClass, DecoratorRecords.Size);
        if (bands.Length < 2 || bands[0] != 0 || bands[^1] != rows.Length || cells.Length == 0 || cells[0] != 0
            || bands.Zip(bands.Skip(1)).Any(pair => pair.First > pair.Second) || cells.Zip(cells.Skip(1)).Any(pair => pair.First > pair.Second))
            throw new InvalidDataException("Ground decorator bands or cells do not cover the instance block.");
        int recordStart = rows.Length == 0 ? 0 : rows[0];
        var cellData = Read(tags.Cells);
        var boxes = cellData.RowsOfAnyClass(0x08, 48);
        // +0x38 ends with the record count, so it holds one start more than there are cells.
        if (boxes.Length < cells.Length - 1) throw new InvalidDataException("Ground decorator cells have no bounds.");
        int cellCount = Math.Min(boxes.Length, cells.Length);
        // The largest record scale of each cell; it does not depend on the type.
        var cellScale = new float[cellCount];
        for (var cell = 0; cell < cellCount; ++cell)
        {
            var (first, end, band) = GroundCell(cells, bands, cell, rows.Length);
            if (band < 0) continue;
            for (var r = first; r < end; ++r)
                cellScale[cell] = MathF.Max(cellScale[cell], MathF.Abs(GroundScale(
                    block.Bytes.AsSpan(recordStart + r * DecoratorRecords.Size, DecoratorRecords.Size), quantization)));
        }
        foreach (var type in types)
        {
            var model = Checked(Read(type).U32(0x08), 0x808073A5);
            var parts = EntityMesh(model);
            var reach = parts.Select(part => part.Vertices.Length == 0 ? 0 : part.Vertices.Max(v => v.Position.Length())).ToArray();
            for (var cell = 0; cell < cellCount; ++cell)
            {
                var (first, end, band) = GroundCell(cells, bands, cell, rows.Length);
                if (band < 0) continue;
                var records = block.Bytes.AsMemory(recordStart + first * DecoratorRecords.Size, (end - first) * DecoratorRecords.Size);
                var transform = GroundTransform(records.Span[..DecoratorRecords.Size], quantization) * placement;
                for (var i = 0; i < parts.Length; ++i)
                {
                    var part = parts[i];
                    if (part.LogicalPartIndex != band) continue;
                    scene.Instances.Add(new MeshInstance
                    {
                        Mesh = part,
                        Transform = transform,
                        CellBounds = CellBox(cellData, boxes[cell], placement, cellScale[cell] * reach[i]),
                        InstanceParent = placement,
                        Name = Name(model),
                        BubbleHash = bubble,
                        InstanceSourceTag = payload,
                        InstanceSourceIndex = first,
                        NativeInstanceData = records
                    });
                }
            }
        }
        return true;
    }

    /**
     * Record range and band of one ground cell.
     * @return Band -1 for an empty cell; throws when the cell crosses its band.
     */
    private static (int First, int End, int Band) GroundCell(uint[] cells, uint[] bands, int cell, int recordCount)
    {
        int first = (int)cells[cell], end = cell + 1 < cells.Length ? (int)cells[cell + 1] : recordCount;
        if (first >= end || first >= recordCount) return (first, end, -1);
        int band = Array.FindLastIndex(bands, start => start <= first);
        if (band < 0 || band >= bands.Length - 1 || end > bands[band + 1])
            throw new InvalidDataException("A ground decorator cell crosses its band.");
        return (first, end, band);
    }

    /**
     * A ground record as its vertex shader reads it: snorm position and scale on rows 0 and 1, and a
     * quaternion from bytes 8 to 11 on all four lanes of rows 2 and 3. The shader does not normalize it.
     */
    public static Matrix4x4 GroundTransform(ReadOnlySpan<byte> record, byte[] quantization)
    {
        Vector4 Row(int index) => new(BitConverter.ToSingle(quantization, 16 * index), BitConverter.ToSingle(quantization, 16 * index + 4),
            BitConverter.ToSingle(quantization, 16 * index + 8), BitConverter.ToSingle(quantization, 16 * index + 12));
        var snorm = new Vector4(DecoratorRecords.Snorm(record, 0), DecoratorRecords.Snorm(record, 2),
            DecoratorRecords.Snorm(record, 4), DecoratorRecords.Snorm(record, 6));
        var position = Row(1) + snorm * Row(0);
        var bytes = new Vector4(record[8], record[9], record[10], record[11]) / 255f;
        var q = Row(3) + bytes * Row(2);
        var rotation = q.LengthSquared() > 0 ? Quaternion.Normalize(new Quaternion(q.X, q.Y, q.Z, q.W)) : Quaternion.Identity;
        return Matrix4x4.CreateScale(position.W) * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(position.X, position.Y, position.Z);
    }

    private static float GroundScale(ReadOnlySpan<byte> record, byte[] quantization) =>
        BitConverter.ToSingle(quantization, 28) + DecoratorRecords.Snorm(record, 6) * BitConverter.ToSingle(quantization, 12);

    /** A cell's authored box (min at +0, max at +16), grown by the reach of its largest plant, then placed. */
    private static (Vector3 Min, Vector3 Max) CellBox(TagData cells, int row, Matrix4x4 placement, float reach)
    {
        var grow = new Vector3(reach);
        Vector3 low = cells.V3(row) - grow, high = cells.V3(row + 16) + grow;
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        for (var corner = 0; corner < 8; ++corner)
        {
            var point = Vector3.Transform(new Vector3((corner & 1) != 0 ? high.X : low.X, (corner & 2) != 0 ? high.Y : low.Y,
                (corner & 4) != 0 ? high.Z : low.Z), placement);
            min = Vector3.Min(min, point); max = Vector3.Max(max, point);
        }
        return (min, max);
    }

    private uint Checked(uint tag, uint expectedClass) => Class(tag) == expectedClass ? tag
        : throw new InvalidDataException($"Tag 0x{tag:X8} is not class 0x{expectedClass:X8}.");
}

/** The map roots (class 0x808091DE) whose bubbles, groups, containers and placement lists place each decorator payload. */
public static class DecoratorMapIndex
{
    private const uint MapRoot = 0x808091DE, Bubble = 0x80807DAE, Group = 0x808091E0, Container = 0x80808A54,
        PlacementList = 0x808099D6;

    /** Payload tag to the maps that place it; a payload no map reaches is absent. */
    public static IReadOnlyDictionary<uint, uint[]> Build(PackageCatalog catalog, CancellationToken token = default)
    {
        var maps = new Dictionary<uint, HashSet<uint>>();
        foreach (var entry in catalog.Entries)
        {
            if (entry.Reference != MapRoot) continue;
            token.ThrowIfCancellationRequested();
            foreach (uint payload in Payloads(catalog, entry.TagHash))
            {
                if (!maps.TryGetValue(payload, out var set)) maps.Add(payload, set = []);
                set.Add(entry.TagHash);
            }
        }
        return maps.ToDictionary(pair => pair.Key, pair => pair.Value.Order().ToArray());
    }

    private static HashSet<uint> Payloads(PackageCatalog catalog, uint map)
    {
        var payloads = new HashSet<uint>();
        var seen = new HashSet<uint>();
        var pending = new Stack<uint>([map]);
        while (pending.TryPop(out uint tag))
        {
            if (tag == 0 || !seen.Add(tag) || !catalog.TryGetEntry(tag, out var entry)) continue;
            switch (entry.Reference)
            {
                case MapRoot:
                    var root = new TagData(catalog.Read(tag));
                    foreach (int row in root.Rows(0x10, 0x80807D53, 16)) Push(root, row);
                    break;
                case Bubble:
                    pending.Push(new TagData(catalog.Read(tag)).U32(8));
                    break;
                case Group:
                    var group = new TagData(catalog.Read(tag));
                    foreach (int row in group.Rows(8, 0x808084C1, 16)) Push(group, row);
                    break;
                case Container:
                    var container = new TagData(catalog.Read(tag));
                    foreach (int row in container.Rows(0x28, 0x80808BB0, 4)) pending.Push(container.U32(row));
                    break;
                // The links AssetSceneLoader.Visit follows between these lists.
                case 0x8080925B:
                    pending.Push(new TagData(catalog.Read(tag)).U32(20));
                    break;
                case 0x8080925E:
                    var links = new TagData(catalog.Read(tag));
                    foreach (int descriptor in new[] { 8, 24, 40 })
                        foreach (int row in links.Rows(descriptor, 0x80809260, 4)) pending.Push(links.U32(row));
                    break;
                case 0x80806EF4:
                    pending.Push(new TagData(catalog.Read(tag)).U32(8));
                    break;
                case PlacementList:
                    var list = new TagData(catalog.Read(tag));
                    foreach (int row in list.Rows(8, 0x808099D8, 144))
                    {
                        var (resource, resourceClass) = AssetSceneLoader.PlacementResource(list, row);
                        if (resourceClass == AssetSceneLoader.DecoratorResourceClass && list.Has(resource, 0x14))
                            payloads.Add(list.U32(resource + 0x10));
                        // Rows that place another placement list or entity, as Placements visits them.
                        else if (resourceClass is 0x80809160 or 0x80806F91 or 0x808071B3 && list.Has(resource, 0x14))
                            pending.Push(list.U32(resource + 0x10));
                        else if (resourceClass == 0x8080714B && list.Has(resource, 0x20))
                            pending.Push(list.U32(resource + 0x18));
                    }
                    break;
            }
        }
        return payloads;

        // A reference of 0xFFFFFFFF carries a 64-bit hash in the next 8 bytes.
        void Push(TagData data, int row) =>
            pending.Push(data.U32(row) == uint.MaxValue ? catalog.Resolve64(data.U64(row + 8)) ?? 0 : data.U32(row));
    }
}
