/**
 * File: Records of the .merge sidecar (targets, shapes, entries, ops, sites) and a bounds-checked reader for
 * them.
 */
using System.Buffers.Binary;

namespace SunriseStudio.Projects;

/** How a sidecar entry is merged: opaque bytes, table, manifest or added entry. */
internal enum SidecarEntryKind : byte { Opaque = 0, Table = 1, Manifest = 2, Added = 3 }

/** A table the runtime merges: table and descriptor offsets plus stock and mod row counts. */
internal sealed record SidecarTarget(uint Table, uint Descriptor, uint StockCount, uint ModCount);
/** One field of a row shape: its offset, merge kind and reference. */
internal sealed record SidecarShapeField(uint RowOffset, MergeFieldKind Kind, ushort Ref);
/** Row layout of a merged table: stride and its fields. */
internal sealed record SidecarShape(uint Stride, IReadOnlyList<SidecarShapeField> Fields);
/** A byte range that differs from stock, with the original bytes. */
internal sealed record SidecarDiff(uint Offset, byte[] Old);
/** One merge operation on a table: key, parent, descriptor, shape and counts. */
internal sealed record SidecarOp(MergeOpKind Kind, byte Flags, byte KeyWidth, uint KeyOffset, uint ParentOp, uint ParentRow,
    uint Descriptor, uint Shape, uint StockCount, uint ModCount, uint Limit, uint PairGroup);
/** A pointer site inside an entry and the target it must follow. */
internal sealed record SidecarSite(uint Offset, byte Width, byte Flags, ushort Target);
/** Everything the sidecar says about one package entry. */
internal sealed record SidecarEntry(uint Index, SidecarEntryKind Kind, uint StockSize, uint BuiltSize, byte[] StockSha256,
    IReadOnlyList<SidecarDiff> Diffs, IReadOnlyList<SidecarOp> Ops, IReadOnlyList<SidecarSite> Sites);
/** A whole sidecar: package and patch ids, metadata digest, targets, shapes and entries. */
internal sealed record SidecarFile(ushort PackageId, ushort PatchId, byte[] MetadataDigest, IReadOnlyList<SidecarTarget> Targets,
    IReadOnlyList<SidecarShape> Shapes, IReadOnlyList<SidecarEntry> Entries);

/** The `.merge` sidecar format, version 2. Little-endian, packed records, nothing after the last entry. */
internal static class MergeSidecarFormat
{
    public const uint Magic = 0x47524D53;
    public const ushort Version = 2;
    /** A sidecar sits beside its package under the same stem with this extension. */
    public const string Extension = ".merge";
    public const int HeaderSize = 48, TargetSize = 16, ShapeHeaderSize = 8, ShapeFieldSize = 8, EntryHeaderSize = 64;
    public const int DiffHeaderSize = 8, OpSize = 40, SiteSize = 8, DigestSize = 20, Sha256Size = 32;
    public const uint TopLevelOp = 0xFFFFFFFF;
    public const ushort NoElementShape = 0xFFFF, OpLocalTarget = 0x8000;
    public const byte UniqueKey = 1, Ordered = 2, SignedSite = 1;
    /** The runtime refuses a larger sidecar. */
    public const int ByteLimit = 64 * 1024 * 1024;
    /** The package header digest a sidecar binds to. */
    public const int PackageDigestOffset = 0x118;
    // An owned array field is a {count, rel} descriptor; a row or object pointer is one i64.
    private const int DescriptorSize = 16, RowPointerSize = 8;
    private const long NoPointer = -1;

    /** Encodes the file, then reads it back so an encoder fault cannot ship. */
    public static byte[] Write(SidecarFile file)
    {
        var output = new List<byte>();
        if (file.MetadataDigest.Length != DigestSize) throw new ArgumentException("The package digest is 20 bytes.", nameof(file));
        U32(output, Magic); U16(output, Version); U16(output, 0);
        U16(output, file.PackageId); U16(output, file.PatchId);
        output.AddRange(file.MetadataDigest);
        U32(output, (uint)file.Targets.Count); U32(output, (uint)file.Shapes.Count); U32(output, (uint)file.Entries.Count);
        U32(output, 0);
        foreach (var target in file.Targets)
        { U32(output, target.Table); U32(output, target.Descriptor); U32(output, target.StockCount); U32(output, target.ModCount); }
        foreach (var shape in file.Shapes)
        {
            U32(output, shape.Stride); U32(output, (uint)shape.Fields.Count);
            foreach (var field in shape.Fields)
            { U32(output, field.RowOffset); output.Add((byte)field.Kind); output.Add(0); U16(output, field.Ref); }
        }
        foreach (var entry in file.Entries)
        {
            if (entry.StockSha256.Length != Sha256Size) throw new ArgumentException("The stock digest is 32 bytes.", nameof(file));
            U32(output, entry.Index); output.Add((byte)entry.Kind); output.AddRange(new byte[3]);
            U32(output, entry.StockSize); U32(output, entry.BuiltSize);
            output.AddRange(entry.StockSha256);
            U32(output, (uint)entry.Diffs.Count); U32(output, (uint)entry.Ops.Count); U32(output, (uint)entry.Sites.Count);
            U32(output, 0);
            foreach (var diff in entry.Diffs) { U32(output, diff.Offset); U32(output, (uint)diff.Old.Length); output.AddRange(diff.Old); }
            foreach (var op in entry.Ops)
            {
                output.Add((byte)op.Kind); output.Add(op.Flags); output.Add(op.KeyWidth); output.Add(0);
                U32(output, op.KeyOffset); U32(output, op.ParentOp); U32(output, op.ParentRow); U32(output, op.Descriptor);
                U32(output, op.Shape); U32(output, op.StockCount); U32(output, op.ModCount); U32(output, op.Limit);
                U32(output, op.PairGroup);
            }
            foreach (var site in entry.Sites)
            { U32(output, site.Offset); output.Add(site.Width); output.Add(site.Flags); U16(output, site.Target); }
        }
        if (output.Count > ByteLimit) throw new InvalidDataException("The merge sidecar exceeds its size limit.");
        byte[] bytes = output.ToArray();
        Read(bytes);
        return bytes;
    }

    /** Applies every format rule the runtime parser applies, but not the package binding; any break throws. */
    public static SidecarFile Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > ByteLimit) throw Bad("exceeds its size limit");
        var reader = new Cursor(bytes);
        if (reader.U32() != Magic) throw Bad("has the wrong magic");
        if (reader.U16() != Version || reader.U16() != 0) throw Bad("has an unsupported version or flags");
        ushort packageId = reader.U16(), patchId = reader.U16();
        byte[] digest = reader.Bytes(DigestSize);
        uint targetCount = reader.U32(), shapeCount = reader.U32(), entryCount = reader.U32();
        if (reader.U32() != 0) throw Bad("has a nonzero reserved header field");
        var targets = new List<SidecarTarget>();
        for (uint i = 0; i < targetCount; i++)
        {
            reader.Need(TargetSize);
            var target = new SidecarTarget(reader.U32(), reader.U32(), reader.U32(), reader.U32());
            if (target.ModCount < target.StockCount) throw Bad("has a target whose rows count down");
            if (targets.Any(other => other.Table == target.Table && other.Descriptor == target.Descriptor))
                throw Bad("repeats a target");
            targets.Add(target);
        }
        var shapes = new List<SidecarShape>();
        for (uint i = 0; i < shapeCount; i++)
        {
            reader.Need(ShapeHeaderSize);
            uint stride = reader.U32(), fieldCount = reader.U32();
            if (stride == 0) throw Bad("has a shape with no stride");
            reader.Need(checked((long)fieldCount * ShapeFieldSize));
            var fields = new List<SidecarShapeField>();
            long end = 0;
            for (uint f = 0; f < fieldCount; f++)
            {
                uint offset = reader.U32(); byte kind = reader.U8(); byte reserved = reader.U8(); ushort reference = reader.U16();
                int size = (MergeFieldKind)kind switch
                {
                    MergeFieldKind.OwnedArray => DescriptorSize,
                    MergeFieldKind.RowPointer or MergeFieldKind.ObjectPointer => RowPointerSize,
                    _ => throw Bad("has an unknown field kind")
                };
                if (reserved != 0 || offset < end || offset + (long)size > stride) throw Bad("has overlapping or oversized fields");
                if ((MergeFieldKind)kind == MergeFieldKind.OwnedArray && reference != NoElementShape && reference <= i)
                    throw Bad("has an element shape that is not later");
                if ((MergeFieldKind)kind == MergeFieldKind.ObjectPointer && reference != NoElementShape)
                    throw Bad("has an object pointer that names a shape");
                end = offset + (long)size;
                fields.Add(new(offset, (MergeFieldKind)kind, reference));
            }
            shapes.Add(new(stride, fields));
        }
        foreach (var shape in shapes)
            foreach (var field in shape.Fields)
                if (field.Kind == MergeFieldKind.OwnedArray && field.Ref != NoElementShape && field.Ref >= shapeCount)
                    throw Bad("names a missing element shape");
        // Per shape, the largest op a row pointer in it or in its elements names; element shapes come later.
        var reach = new long[shapes.Count];
        for (int index = shapes.Count - 1; index >= 0; index--)
        {
            reach[index] = NoPointer;
            foreach (var field in shapes[index].Fields)
                if (field.Kind == MergeFieldKind.RowPointer) reach[index] = Math.Max(reach[index], field.Ref);
                else if (field.Kind == MergeFieldKind.OwnedArray && field.Ref != NoElementShape)
                    reach[index] = Math.Max(reach[index], reach[field.Ref]);
        }
        var entries = new List<SidecarEntry>();
        for (uint i = 0; i < entryCount; i++)
        {
            reader.Need(EntryHeaderSize);
            uint index = reader.U32(); byte kind = reader.U8();
            if (reader.U8() != 0 || reader.U8() != 0 || reader.U8() != 0) throw Bad("has a nonzero reserved entry field");
            uint stockSize = reader.U32(), builtSize = reader.U32();
            byte[] sha = reader.Bytes(Sha256Size);
            uint diffCount = reader.U32(), opCount = reader.U32(), siteCount = reader.U32();
            if (reader.U32() != 0) throw Bad("has a nonzero reserved entry field");
            if (entries.Count > 0 && entries[^1].Index >= index) throw Bad("has entries out of order");
            if (kind > 3) throw Bad("has an unknown entry kind");
            var entryKind = (SidecarEntryKind)kind;
            var diffs = new List<SidecarDiff>();
            long diffEnd = 0;
            for (uint d = 0; d < diffCount; d++)
            {
                reader.Need(DiffHeaderSize);
                uint offset = reader.U32(), length = reader.U32();
                if (length == 0 || offset < diffEnd || offset + (long)length > stockSize) throw Bad("has a diff out of order or range");
                diffEnd = offset + (long)length;
                diffs.Add(new(offset, reader.Bytes(checked((int)length))));
            }
            var ops = new List<SidecarOp>();
            for (uint o = 0; o < opCount; o++)
            {
                reader.Need(OpSize);
                byte opKind = reader.U8(), flags = reader.U8(), keyWidth = reader.U8();
                if (reader.U8() != 0) throw Bad("has a nonzero reserved op field");
                var op = new SidecarOp((MergeOpKind)opKind, flags, keyWidth, reader.U32(), reader.U32(), reader.U32(), reader.U32(),
                    reader.U32(), reader.U32(), reader.U32(), reader.U32(), reader.U32());
                if (opKind is not (1 or 2) || keyWidth is not (0 or 4) || (flags & ~3) != 0 || op.Shape >= shapeCount
                    || op.ModCount < op.StockCount || op.ModCount > op.Limit || op.ParentOp != TopLevelOp && op.ParentOp >= o)
                    throw Bad("has an invalid op");
                uint stride = shapes[(int)op.Shape].Stride;
                bool keyed = flags != 0 || op.Kind == MergeOpKind.SortedMap;
                if (keyed && keyWidth == 0 || keyWidth != 0 && op.KeyOffset + (long)keyWidth > stride)
                    throw Bad("has an op key outside its row");
                long descriptorEnd = op.Descriptor + (long)DescriptorSize;
                bool placed = op.ParentOp == TopLevelOp
                    ? descriptorEnd <= builtSize
                    : op.ParentRow < ops[(int)op.ParentOp].ModCount
                        && descriptorEnd <= shapes[(int)ops[(int)op.ParentOp].Shape].Stride;
                if (!placed) throw Bad("has an op array outside its entry or parent row");
                if (reach[op.Shape] >= opCount) throw Bad("has a row pointer to a missing op");
                ops.Add(op);
            }
            var sites = new List<SidecarSite>();
            long siteEnd = 0;
            for (uint s = 0; s < siteCount; s++)
            {
                reader.Need(SiteSize);
                var site = new SidecarSite(reader.U32(), reader.U8(), reader.U8(), reader.U16());
                if (site.Width is not (1 or 2 or 4) || (site.Flags & ~SignedSite) != 0 || site.Offset < siteEnd
                    || site.Offset + (long)site.Width > builtSize)
                    throw Bad("has a site out of order or range");
                bool local = (site.Target & OpLocalTarget) != 0;
                if (local ? (site.Target & ~OpLocalTarget) >= opCount : site.Target >= targetCount) throw Bad("has a site with no target");
                siteEnd = site.Offset + (long)site.Width;
                sites.Add(site);
            }
            bool valid = entryKind switch
            {
                SidecarEntryKind.Opaque => diffCount == 0 && opCount == 0,
                // Only ops move pointers into added bytes, so a table without ops cannot grow.
                SidecarEntryKind.Table => stockSize > 0 && builtSize >= stockSize && (opCount != 0 || builtSize == stockSize),
                SidecarEntryKind.Manifest => diffCount == 0 && opCount == 0 && siteCount == 0,
                _ => stockSize == 0 && diffCount == 0 && opCount == 0
            };
            if (!valid) throw Bad("breaks an entry kind rule");
            entries.Add(new(index, entryKind, stockSize, builtSize, sha, diffs, ops, sites));
        }
        if (!reader.AtEnd) throw Bad("has trailing bytes");
        return new(packageId, patchId, digest, targets, shapes, entries);
    }

    private static InvalidDataException Bad(string problem) => new($"The merge sidecar {problem}.");
    private static void U16(List<byte> output, ushort value) { output.Add((byte)value); output.Add((byte)(value >> 8)); }
    private static void U32(List<byte> output, uint value) { U16(output, (ushort)value); U16(output, (ushort)(value >> 16)); }

    private ref struct Cursor(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> bytes = bytes;
        private int position;
        public readonly bool AtEnd => position == bytes.Length;
        /** Throws when fewer than count bytes remain. */
        public readonly void Need(long count)
        {
            if (count < 0 || position + count > bytes.Length) throw Bad("is truncated");
        }
        /** Reads a u8 and advances. */
        public byte U8() { Need(1); return bytes[position++]; }
        /** Reads a little-endian u16 and advances. */
        public ushort U16() { Need(2); var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes[position..]); position += 2; return value; }
        /** Reads a little-endian u32 and advances. */
        public uint U32() { Need(4); var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]); position += 4; return value; }
        /** Reads count bytes and advances. */
        public byte[] Bytes(int count) { Need(count); var value = bytes.Slice(position, count).ToArray(); position += count; return value; }
    }
}
