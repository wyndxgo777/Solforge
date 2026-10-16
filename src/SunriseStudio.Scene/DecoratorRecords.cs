using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/**
 * Rows 0 to 5 of a class-0x8080716B record, the decorator vertex shader's cb0[0] to cb0[5].
 * Half and Middle unpack position xyz and scale w; the axis rows unpack the X and Z axes.
 */
public sealed record DecoratorQuantization(Vector4 Half, Vector4 Middle,
    Vector3 XScale, Vector3 XOffset, Vector3 ZScale, Vector3 ZOffset)
{
    public const int Size = 0x60;

    /** Reads a quantization record; throws if it is shorter than Size. */
    public static DecoratorQuantization Read(ReadOnlySpan<byte> record)
    {
        if (record.Length < Size) throw new InvalidDataException("Decorator quantization record is truncated.");
        var value = new DecoratorQuantization(Row(record, 0), Row(record, 1), Xyz(Row(record, 2)), Xyz(Row(record, 3)),
            Xyz(Row(record, 4)), Xyz(Row(record, 5)));
        if (!value.Finite) throw new InvalidDataException("Decorator quantization record is not finite.");
        return value;
    }

    /** The w lanes of rows 2 to 5 are not axis data and keep their stored bytes. */
    public void Write(Span<byte> record)
    {
        if (record.Length < Size) throw new InvalidDataException("Decorator quantization record is truncated.");
        Put(record, 0, Half, 4); Put(record, 1, Middle, 4);
        Put(record, 2, new(XScale, 0), 3); Put(record, 3, new(XOffset, 0), 3);
        Put(record, 4, new(ZScale, 0), 3); Put(record, 5, new(ZOffset, 0), 3);
    }

    private static Vector4 Row(ReadOnlySpan<byte> record, int index) => new(F32(record, index * 16),
        F32(record, index * 16 + 4), F32(record, index * 16 + 8), F32(record, index * 16 + 12));

    private static void Put(Span<byte> record, int index, Vector4 value, int lanes)
    {
        for (int lane = 0; lane < lanes; lane++)
            BinaryPrimitives.WriteSingleLittleEndian(record[(index * 16 + lane * 4)..], value[lane]);
    }

    private bool Finite => new[] { Half, Middle }.All(v => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W))
        && new[] { XScale, XOffset, ZScale, ZOffset }.All(v => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z));

    private static float F32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);
}

/** One decorator instance in payload space. The game uses the axes as stored; Y is Z cross X. */
public readonly record struct DecoratorInstance(Vector3 Position, float Scale, Vector3 XAxis, Vector3 ZAxis)
{
    public Matrix4x4 Transform
    {
        get
        {
            var y = Vector3.Cross(ZAxis, XAxis);
            return new(XAxis.X * Scale, XAxis.Y * Scale, XAxis.Z * Scale, 0, y.X * Scale, y.Y * Scale, y.Z * Scale, 0,
                ZAxis.X * Scale, ZAxis.Y * Scale, ZAxis.Z * Scale, 0, Position.X, Position.Y, Position.Z, 1);
        }
    }

    /** The nearest rotation to the stored axes. */
    public Quaternion Rotation
    {
        get
        {
            var z = Vector3.Normalize(ZAxis);
            var x = Vector3.Normalize(XAxis - Vector3.Dot(XAxis, z) * z);
            var y = Vector3.Cross(z, x);
            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(
                new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)));
        }
    }

    /** Builds an instance from a pose; its axes come from the rotation. */
    public static DecoratorInstance Create(Vector3 position, Quaternion rotation, float scale) =>
        new(position, scale, Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(Vector3.UnitZ, rotation));
}

/** The tags one decorator payload reaches, each checked for its class. */
public sealed record DecoratorTags(uint Payload, uint Header, uint Quantization, uint BufferHeader, uint Buffer,
    uint Block, uint Cells)
{
    public const uint PayloadClass = 0x80807164, HeaderClass = 0x80807170, QuantizationClass = 0x8080716B,
        BlockClass = 0x80807173, RecordClass = 0x80807175, CellsClass = 0x80809671;

    /**
     * Payload +0x48 is the header and +0x4C the cells.
     * Header +0x14 is the quantization, +0x18 the vertex buffer header and +0x1C the block.
     */
    public static DecoratorTags Read(PackageCatalog catalog, uint payload)
    {
        uint Checked(uint tag, uint expected) => catalog.TryGetEntry(tag, out var entry) && entry.Reference == expected ? tag
            : throw new InvalidDataException($"Tag 0x{tag:X8} is not class 0x{expected:X8}.");
        byte[] data = catalog.Read(Checked(payload, PayloadClass));
        if (data.Length < 0x90) throw new InvalidDataException("Decorator payload is truncated.");
        uint header = Checked(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x48)), HeaderClass);
        uint cells = Checked(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x4C)), CellsClass);
        byte[] fields = catalog.Read(header);
        if (fields.Length < 0x20) throw new InvalidDataException("Decorator instance header is truncated.");
        uint bufferHeader = BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(0x18));
        if (!catalog.TryGetEntry(bufferHeader, out var buffer) || buffer is not { Type: 32, SubType: 4 }
            || !catalog.TryGetEntry(buffer.Reference, out _))
            throw new InvalidDataException("Decorator vertex buffer header is missing.");
        return new(payload, header, Checked(BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(0x14)), QuantizationClass),
            bufferHeader, buffer.Reference, Checked(BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(0x1C)), BlockClass), cells);
    }
}

/** The 16-byte instance record of a class-0x80807173 block and of its vertex buffer. */
public static class DecoratorRecords
{
    public const int Size = 16;

    /** Bytes 0-7 snorm16 position and scale, 8-10 unorm8 X axis, 12-14 unorm8 Z axis. */
    public static DecoratorInstance Decode(ReadOnlySpan<byte> record, DecoratorQuantization quant)
    {
        if (record.Length < Size) throw new InvalidDataException("Decorator record is truncated.");
        var packed = new Vector4(Snorm(record, 0), Snorm(record, 2), Snorm(record, 4), Snorm(record, 6));
        var value = quant.Middle + packed * quant.Half;
        return new(new(value.X, value.Y, value.Z), value.W,
            quant.XOffset + Unorm(record, 8) * quant.XScale, quant.ZOffset + Unorm(record, 12) * quant.ZScale);
    }

    /** Signed-normalised 16-bit value at the offset, clamped to -1..1. */
    internal static float Snorm(ReadOnlySpan<byte> record, int offset) =>
        Math.Max(BinaryPrimitives.ReadInt16LittleEndian(record[offset..]) / 32767f, -1f);

    private static Vector3 Unorm(ReadOnlySpan<byte> record, int offset) =>
        new Vector3(record[offset], record[offset + 1], record[offset + 2]) / 255f;

    /**
     * The block copy truncates toward zero and the vertex buffer copy rounds to nearest, as installed
     * pairs do. Bytes 11 and 15 keep their stored values.
     * @return False when a value leaves the quantization range; neither record is written then.
     */
    public static bool TryEncode(DecoratorInstance value, DecoratorQuantization quant, Span<byte> block, Span<byte> buffer)
    {
        Span<float> snorm = stackalloc float[4];
        Span<float> unorm = stackalloc float[6];
        Vector4 packed = new(value.Position, value.Scale);
        for (int lane = 0; lane < 4; lane++)
            if (!Ratio(packed[lane] - quant.Middle[lane], quant.Half[lane], -1, out snorm[lane])) return false;
        for (int lane = 0; lane < 3; lane++)
        {
            if (!Ratio(value.XAxis[lane] - quant.XOffset[lane], quant.XScale[lane], 0, out unorm[lane])) return false;
            if (!Ratio(value.ZAxis[lane] - quant.ZOffset[lane], quant.ZScale[lane], 0, out unorm[lane + 3])) return false;
        }
        for (int lane = 0; lane < 4; lane++)
        {
            float scaled = snorm[lane] * 32767;
            BinaryPrimitives.WriteInt16LittleEndian(block[(lane * 2)..], (short)MathF.Truncate(scaled));
            BinaryPrimitives.WriteInt16LittleEndian(buffer[(lane * 2)..], (short)MathF.Round(scaled, MidpointRounding.AwayFromZero));
        }
        for (int lane = 0; lane < 6; lane++)
        {
            int at = lane < 3 ? 8 + lane : 12 + lane - 3;
            float scaled = unorm[lane] * 255;
            block[at] = (byte)MathF.Truncate(scaled);
            buffer[at] = (byte)MathF.Round(scaled, MidpointRounding.AwayFromZero);
        }
        return true;
    }

    /** The narrowest rows that hold every value; lanes with one value keep a zero scale. */
    public static DecoratorQuantization Fit(IReadOnlyCollection<DecoratorInstance> values)
    {
        if (values.Count == 0) throw new ArgumentException("A decorator block needs an instance.", nameof(values));
        Vector4 low = new(float.MaxValue), high = new(float.MinValue);
        Vector3 xLow = new(float.MaxValue), xHigh = new(float.MinValue), zLow = new(float.MaxValue), zHigh = new(float.MinValue);
        foreach (var value in values)
        {
            Vector4 packed = new(value.Position, value.Scale);
            low = Vector4.Min(low, packed); high = Vector4.Max(high, packed);
            xLow = Vector3.Min(xLow, value.XAxis); xHigh = Vector3.Max(xHigh, value.XAxis);
            zLow = Vector3.Min(zLow, value.ZAxis); zHigh = Vector3.Max(zHigh, value.ZAxis);
        }
        // The half range keeps a margin so the extreme values stay clear of the snorm limits.
        return new((high - low) / 2 * 1.0001f, (high + low) / 2, xHigh - xLow, xLow, zHigh - zLow, zLow);
    }

    /** @param floor -1 for snorm, 0 for unorm; the upper limit is 1. */
    private static bool Ratio(float offset, float range, float floor, out float ratio)
    {
        ratio = range == 0 ? 0 : offset / range;
        if (range == 0) return MathF.Abs(offset) <= 1e-6f;
        if (!float.IsFinite(ratio) || ratio < floor - 1e-6f || ratio > 1 + 1e-6f) return false;
        ratio = Math.Clamp(ratio, floor, 1);
        return true;
    }
}
