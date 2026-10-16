using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using SunriseStudio.Packages;

namespace SunriseStudio.Animation;

/** Cloth indices are accepted only when their authored inverse binds match the owning skeleton. */
internal sealed class HavokSkinBinding
{
    internal uint RawTag { get; }
    internal int PaletteSize { get; }
    internal float MaximumError { get; }
    internal HashSet<int> VerifiedBones { get; } = [];

    /** Local and global fixups resolve package pointers without loading the Havok runtime. */
    internal HavokSkinBinding(PackageCatalog catalog, uint assetTag, SkeletonRig rig)
    {
        if (!catalog.TryGetEntry(assetTag, out var asset) || asset.Reference != 0x8080727A)
            throw new InvalidDataException("Cloth asset has an unexpected class.");
        var resource = new AnimationControlReader.Data(catalog.Read(assetTag));
        RawTag = resource.U32(0x690);
        var file = new Packfile(catalog.Read(RawTag));
        var transformSets = file.Objects.Where(o => o.Name == "hclTransformSetDefinition").ToArray();
        if (transformSets.Length != 1)
            throw new NotSupportedException("Cloth input needs exactly one authored transform set.");
        file.RequireDataRange(transformSets[0].Offset, 32);
        PaletteSize = checked((int)file.U32(transformSets[0].Offset + 0x1C));
        if (PaletteSize != rig.Bones.Count)
            throw new NotSupportedException("Cloth transform count differs from its entity skeleton.");
        foreach (var item in file.Objects.Where(o => o.Name.Contains("Skin", StringComparison.Ordinal)))
        {
            if (item.Name != "hclObjectSpaceSkinPNTOperator")
                throw new NotSupportedException($"Cloth skin operator {item.Name} is not decoded.");
            int row = item.Offset;
            file.RequireDataRange(row, 192);
            if (file.U32(row + 0x44) != 0)
                throw new NotSupportedException("Cloth skin operator selects another transform set.");
            int count = checked((int)file.U32(row + 0x28));
            if (count == 0 || count > rig.Bones.Count || count != file.U32(row + 0x38))
                throw new InvalidDataException("Cloth transform subset and inverse-bind counts disagree.");
            int matrices = file.Pointer(row + 0x20), indices = file.Pointer(row + 0x30);
            file.RequireDataRange(matrices, checked(count * 64));
            file.RequireDataRange(indices, checked(count * 2));
            for (int i = 0; i < count; i++)
            {
                int bone = file.U16(indices + i * 2);
                if (bone >= rig.Bones.Count) throw new InvalidDataException("Cloth transform index is outside the rig.");
                var actual = file.Matrix(matrices + i * 64);
                var expected = rig.Bones[bone].InverseBindObject.Matrix;
                float[] differences = [actual.M11 - expected.M11, actual.M12 - expected.M12,
                    actual.M13 - expected.M13, actual.M14 - expected.M14, actual.M21 - expected.M21,
                    actual.M22 - expected.M22, actual.M23 - expected.M23, actual.M24 - expected.M24,
                    actual.M31 - expected.M31, actual.M32 - expected.M32, actual.M33 - expected.M33,
                    actual.M34 - expected.M34, actual.M41 - expected.M41, actual.M42 - expected.M42,
                    actual.M43 - expected.M43, actual.M44 - expected.M44];
                float error = differences.Max(MathF.Abs);
                if (error > 0.0002f)
                    throw new NotSupportedException("Cloth inverse bind differs from its skeleton bone.");
                MaximumError = MathF.Max(MaximumError, error);
                VerifiedBones.Add(bone);
            }
        }
        if (VerifiedBones.Count == 0) throw new NotSupportedException("Cloth has no supported skin operators.");
    }

    private sealed record Section(int Start, int Local, int Global, int Virtual, int Exports, int End);
    private sealed record ObjectRow(int Offset, string Name);

    /** Only the installed 64-bit little-endian Havok packfile layout is supported. */
    private sealed class Packfile
    {
        private readonly byte[] bytes;
        private readonly Section[] sections;
        private readonly Dictionary<int, int> pointers = [];
        internal List<ObjectRow> Objects { get; } = [];

        /** Fixup tables must point within bounded sections before their pointers are exposed. */
        internal Packfile(byte[] bytes)
        {
            this.bytes = bytes;
            if (U32(0) != 0x57E0E057 || U32(4) != 0x10C0C010 || U32(12) != 9
                || !Range(0x10, 4).SequenceEqual(new byte[] { 8, 1, 0, 1 })
                || Encoding.ASCII.GetString(Range(0x28, 14)) != "hk_2012.2.0-r1")
                throw new NotSupportedException("Cloth packfile version or pointer layout is unsupported.");
            int count = checked((int)U32(0x14));
            if (count is < 1 or > 16) throw new InvalidDataException("Invalid Havok section count.");
            sections = new Section[count];
            for (int i = 0; i < count; i++)
            {
                int p = 0x40 + i * 48 + 20;
                int[] fields = Enumerable.Range(0, 7).Select(j => checked((int)U32(p + j * 4))).ToArray();
                if (fields.Skip(1).Zip(fields.Skip(2)).Any(pair => pair.First > pair.Second))
                    throw new InvalidDataException("Havok fixup sections are not ordered.");
                _ = Range(fields[0], fields[6]);
                sections[i] = new(fields[0], fields[1], fields[2], fields[3], fields[4], fields[6]);
            }
            foreach (var section in sections)
            {
                if ((section.Global - section.Local) % 8 != 0)
                    throw new InvalidDataException("Havok local fixup table is misaligned.");
                for (int p = section.Start + section.Local; p + 8 <= section.Start + section.Global; p += 8)
                {
                    uint source = U32(p);
                    if (source == uint.MaxValue) continue;
                    AddPointer(section, source, section, U32(p + 4));
                }
                for (int p = section.Start + section.Global; p + 12 <= section.Start + section.Virtual; p += 12)
                {
                    uint source = U32(p);
                    if (source == uint.MaxValue) continue;
                    uint targetSection = U32(p + 4);
                    if (targetSection >= sections.Length) throw new InvalidDataException("Havok target section is absent.");
                    AddPointer(section, source, sections[targetSection], U32(p + 8));
                }
                for (int p = section.Start + section.Virtual; p + 12 <= section.Start + section.Exports; p += 12)
                {
                    uint source = U32(p);
                    if (source == uint.MaxValue) continue;
                    uint nameSection = U32(p + 4), nameOffset = U32(p + 8);
                    if (source >= section.Local || nameSection >= sections.Length
                        || nameOffset >= sections[nameSection].Local)
                        throw new InvalidDataException("Havok object fixup exceeds its section.");
                    int nameStart = checked(sections[nameSection].Start + (int)nameOffset);
                    int nameEnd = Array.IndexOf(bytes, (byte)0, nameStart,
                        sections[nameSection].Local - (int)nameOffset);
                    if (nameEnd < 0) throw new InvalidDataException("Havok class name is unterminated.");
                    Objects.Add(new(checked(section.Start + (int)source),
                        Encoding.ASCII.GetString(bytes, nameStart, nameEnd - nameStart)));
                }
            }
        }

        private void AddPointer(Section sourceSection, uint source, Section targetSection, uint target)
        {
            if ((ulong)source + 8 > (ulong)sourceSection.Local || target >= targetSection.Local)
                throw new InvalidDataException("Havok pointer fixup exceeds its data section.");
            if (!pointers.TryAdd(checked(sourceSection.Start + (int)source), checked(targetSection.Start + (int)target)))
                throw new InvalidDataException("Havok pointer has more than one fixup.");
        }

        /** Absolute offset for a pointer field, from the packfile's fixups. */
        internal int Pointer(int field) => pointers.TryGetValue(field, out int value)
            ? value : throw new InvalidDataException("Havok array has no pointer fixup.");
        /** Throws unless the range lies inside one data section. */
        internal void RequireDataRange(int offset, int count)
        {
            if (count < 0 || !sections.Any(s => offset >= s.Start && (long)offset + count <= (long)s.Start + s.Local))
                throw new InvalidDataException("Havok object or array exceeds its data section.");
        }
        /** Reads a u32 at a data offset. */
        internal uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Range(offset, 4));
        /** Reads a u16 at a data offset. */
        internal ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Range(offset, 2));
        private float F32(int offset)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(Range(offset, 4));
            return float.IsFinite(value) ? value : throw new InvalidDataException("Havok matrix is not finite.");
        }
        /** Reads a row-major 4x4 matrix of floats. */
        internal Matrix4x4 Matrix(int offset) => new(F32(offset), F32(offset + 4), F32(offset + 8), F32(offset + 12),
            F32(offset + 16), F32(offset + 20), F32(offset + 24), F32(offset + 28),
            F32(offset + 32), F32(offset + 36), F32(offset + 40), F32(offset + 44),
            F32(offset + 48), F32(offset + 52), F32(offset + 56), F32(offset + 60));
        private ReadOnlySpan<byte> Range(int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > bytes.Length - count)
                throw new InvalidDataException("Havok range exceeds its package payload.");
            return bytes.AsSpan(offset, count);
        }
    }
}
