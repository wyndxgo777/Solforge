using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Authoring;

/** Bounds-checked little-endian reads over one tag payload, and its typed array headers. */
internal sealed class NativeRows(byte[] bytes)
{
    internal byte[] Bytes { get; } = bytes;
    /** Reads a little-endian uint. */
    internal uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(Span(p, 4));
    /** Reads a little-endian int. */
    internal int I32(int p) => BinaryPrimitives.ReadInt32LittleEndian(Span(p, 4));
    /** Reads a little-endian ushort. */
    internal ushort U16(int p) => BinaryPrimitives.ReadUInt16LittleEndian(Span(p, 2));
    /** Reads a little-endian ulong. */
    internal ulong U64(int p) => BinaryPrimitives.ReadUInt64LittleEndian(Span(p, 8));
    /** Reads a little-endian long. */
    internal long I64(int p) => BinaryPrimitives.ReadInt64LittleEndian(Span(p, 8));
    /** Reads a little-endian float. */
    internal float F32(int p) => BinaryPrimitives.ReadSingleLittleEndian(Span(p, 4));
    /** Reads three floats as a vector. */
    internal Vector3 V3(int p) => new(F32(p), F32(p + 4), F32(p + 8));
    /** Reads four floats as a quaternion. */
    internal Quaternion Q4(int p) => new(F32(p), F32(p + 4), F32(p + 8), F32(p + 12));

    /** A near-zero stored quaternion reads as identity. */
    internal Quaternion Rotation(int p)
    {
        var rotation = Q4(p);
        return rotation.LengthSquared() > 0.0001f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
    }

    /** Bounds-checked slice; throws when the range leaves the payload. */
    internal ReadOnlySpan<byte> Span(int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > Bytes.Length - count) throw new InvalidDataException("Placement range leaves its source.");
        return Bytes.AsSpan(offset, count);
    }

    /** Row offsets of the typed array whose count and relative pointer sit at field. */
    internal int[] Rows(int field, uint type, int stride)
    {
        long count = I64(field), relative = I64(field + 8);
        if (count == 0 && relative == 0) return [];
        int header = checked((int)(field + 8 + relative));
        if (count is < 0 or > 1_000_000 || U32(header - 4) != ArrayMarker || I64(header) != count
            || U32(header + 8) != type || U32(header + 12) != 0)
            throw new InvalidDataException("Placement array layout is invalid.");
        Span(header + 16, checked((int)count * stride));
        return Enumerable.Range(0, (int)count).Select(i => checked(header + 16 + i * stride)).ToArray();
    }

    // The word just before every typed array header.
    internal const uint ArrayMarker = 0x80809FBD;

    /**
     * Row offsets of the typed array whose count and relative pointer sit at field.
     * A zero count reads as empty and its pointer is not checked.
     */
    internal static int[] Rows(byte[] bytes, int field, uint type, int stride)
    {
        if (field < 0 || field > bytes.Length - 8) throw new InvalidDataException("A typed native array is truncated.");
        long count = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field));
        if (count == 0) return [];
        if (field > bytes.Length - 16) throw new InvalidDataException("A typed native array is truncated.");
        long relative = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field + 8));
        long header = relative < -bytes.Length || relative > bytes.Length ? -1 : field + 8 + relative;
        if (count is < 0 or > 1_000_000 || header < 4 || header > bytes.Length - 16 || count > (bytes.Length - header - 16) / stride
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)header - 4)) != ArrayMarker
            || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan((int)header)) != count
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)header + 8)) != type)
            throw new InvalidDataException("A typed native array is invalid.");
        return Enumerable.Range(0, (int)count).Select(index => (int)header + 16 + index * stride).ToArray();
    }

    /**
     * Copies source with rows appended as a new typed array, pointed to from field.
     * @param first Receives the offset of the first appended row.
     */
    internal static byte[] Append(byte[] source, int field, uint type, int stride, byte[] rows, out int first)
    {
        int header = checked((source.Length + 19) & ~15); first = header + 16;
        byte[] output = new byte[checked(first + rows.Length)]; source.CopyTo(output, 0);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(field), rows.Length / stride);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(field + 8), header - field - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(header - 4), ArrayMarker);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(header), rows.Length / stride);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(header + 8), type);
        rows.CopyTo(output, first); BinaryPrimitives.WriteInt64LittleEndian(output, output.Length);
        return output;
    }

    /** Writes floats little-endian at an offset, bounds-checked. */
    internal static void Write(byte[] bytes, int offset, params float[] values)
    {
        if (offset < 0 || offset > bytes.Length - values.Length * 4) throw new InvalidDataException("Placement range leaves its source.");
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + i * 4), values[i]);
    }
}
