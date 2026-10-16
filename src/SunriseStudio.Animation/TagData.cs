using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Animation;

/** Relative pointers and typed arrays must stay within the decoded package entry. */
internal sealed class TagData(byte[] bytes)
{
    /** True when size bytes starting at offset lie inside the buffer. */
    public bool Has(int offset, int size) => offset >= 0 && size >= 0 && offset <= bytes.Length - size;

    private ReadOnlySpan<byte> Slice(int offset, int size)
    {
        if (!Has(offset, size)) throw new InvalidDataException("Read range exceeds its tag.");
        return bytes.AsSpan(offset, size);
    }

    /** Reads a little-endian u32 at offset. */
    public uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));
    /** Reads a little-endian i32 at offset. */
    public int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Slice(offset, 4));
    /** Reads a little-endian i64 at offset. */
    public long I64(int offset) => BinaryPrimitives.ReadInt64LittleEndian(Slice(offset, 8));
    /** Reads an f32 at offset. */
    public float F32(int offset) => BinaryPrimitives.ReadSingleLittleEndian(Slice(offset, 4));
    /** Resolves a self-relative pointer to an absolute offset. */
    public int Pointer(int offset) => checked((int)(offset + I64(offset)));

    /** Row offsets of a typed array; the header sits behind the 0x80809FBD marker. */
    public int[] Rows(int offset, uint expected, int stride)
    {
        long count = I64(offset);
        if (count < 0 || count > 1_000_000) throw new InvalidDataException("Invalid array count.");
        if (count == 0) return [];
        int header = Pointer(offset + 8);
        if (U32(header - 4) != 0x80809FBD || U32(header + 8) != expected)
            throw new InvalidDataException($"Array class differs from 0x{expected:X8}.");
        int start = checked(header + 16);
        if (!Has(start, checked((int)count * stride))) throw new InvalidDataException("Array exceeds its tag.");
        return Enumerable.Range(0, (int)count).Select(i => start + i * stride).ToArray();
    }

    /** Reads a rotation, translation and scale record as a BoneTransform, rejecting non-unit rotations. */
    public BoneTransform Transform(int offset)
    {
        var transform = new BoneTransform(new Quaternion(F32(offset), F32(offset + 4),
            F32(offset + 8), F32(offset + 12)), new Vector3(F32(offset + 16),
            F32(offset + 20), F32(offset + 24)), F32(offset + 28));
        SkeletonReader.CheckTransform(transform);
        return transform;
    }
}
