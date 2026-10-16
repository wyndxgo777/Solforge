using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Scene;

/**
 * Solforge Phase 11: CPU decoding of compressed vertex attributes. The game-shader path uploads the raw
 * streams (NativeMeshReader layouts) and the GPU expands them; this decodes the same formats on the CPU for
 * export, picking and checks. Missing components follow the D3D input-assembler default: 0 for Y and Z, 1 for W.
 */
public static class VertexAttributeDecoding
{
    /** Byte size of one element, or 0 for a format this decoder does not know. */
    public static int Size(NativeVertexFormat format) => format switch
    {
        NativeVertexFormat.R32G32B32A32Float => 16,
        NativeVertexFormat.R32G32B32Float => 12,
        NativeVertexFormat.R16G16B16A16UNorm or NativeVertexFormat.R16G16B16A16SNorm
            or NativeVertexFormat.R16G16B16A16SInt or NativeVertexFormat.R32G32Float => 8,
        NativeVertexFormat.R8G8B8A8UNorm or NativeVertexFormat.R8G8B8A8UInt or NativeVertexFormat.R16G16Float
            or NativeVertexFormat.R16G16SNorm or NativeVertexFormat.R16G16SInt or NativeVertexFormat.R32UInt => 4,
        NativeVertexFormat.R8UNorm => 1,
        _ => 0
    };

    /** Converts half-float bits to a float. */
    public static float Half(ushort bits) => (float)BitConverter.UInt16BitsToHalf(bits);

    /** SNORM maps -32768 and -32767 both to -1, as D3D does. */
    public static float Snorm16(short value) => Math.Max(value / 32767f, -1f);

    /** Unsigned normalised 16-bit value to 0..1. */
    public static float Unorm16(ushort value) => value / 65535f;

    /** Unsigned normalised 8-bit value to 0..1. */
    public static float Unorm8(byte value) => value / 255f;

    /** One element at the start of the span. */
    public static Vector4 Read(ReadOnlySpan<byte> data, NativeVertexFormat format)
    {
        int size = Size(format);
        if (size == 0) throw new NotSupportedException($"Vertex format {format} has no CPU decoder.");
        if (data.Length < size) throw new ArgumentException($"{format} needs {size} bytes, got {data.Length}.", nameof(data));
        return format switch
        {
            NativeVertexFormat.R32G32B32A32Float => new(F32(data, 0), F32(data, 4), F32(data, 8), F32(data, 12)),
            NativeVertexFormat.R32G32B32Float => new(F32(data, 0), F32(data, 4), F32(data, 8), 1),
            NativeVertexFormat.R32G32Float => new(F32(data, 0), F32(data, 4), 0, 1),
            NativeVertexFormat.R16G16B16A16UNorm => new(Unorm16(U16(data, 0)), Unorm16(U16(data, 2)), Unorm16(U16(data, 4)), Unorm16(U16(data, 6))),
            NativeVertexFormat.R16G16B16A16SNorm => new(Snorm16(I16(data, 0)), Snorm16(I16(data, 2)), Snorm16(I16(data, 4)), Snorm16(I16(data, 6))),
            NativeVertexFormat.R16G16B16A16SInt => new(I16(data, 0), I16(data, 2), I16(data, 4), I16(data, 6)),
            NativeVertexFormat.R16G16Float => new(Half(U16(data, 0)), Half(U16(data, 2)), 0, 1),
            NativeVertexFormat.R16G16SNorm => new(Snorm16(I16(data, 0)), Snorm16(I16(data, 2)), 0, 1),
            NativeVertexFormat.R16G16SInt => new(I16(data, 0), I16(data, 2), 0, 1),
            NativeVertexFormat.R8G8B8A8UNorm => new(Unorm8(data[0]), Unorm8(data[1]), Unorm8(data[2]), Unorm8(data[3])),
            NativeVertexFormat.R8G8B8A8UInt => new(data[0], data[1], data[2], data[3]),
            NativeVertexFormat.R32UInt => new(BinaryPrimitives.ReadUInt32LittleEndian(data), 0, 0, 1),
            NativeVertexFormat.R8UNorm => new(Unorm8(data[0]), 0, 0, 1),
            _ => throw new NotSupportedException($"Vertex format {format} has no CPU decoder.")
        };
    }

    /** Four half floats (DXGI R16G16B16A16_FLOAT), which the 86657 layouts use for some effect streams. */
    public static Vector4 ReadHalf4(ReadOnlySpan<byte> data) => new(
        Half(BinaryPrimitives.ReadUInt16LittleEndian(data)), Half(BinaryPrimitives.ReadUInt16LittleEndian(data[2..])),
        Half(BinaryPrimitives.ReadUInt16LittleEndian(data[4..])), Half(BinaryPrimitives.ReadUInt16LittleEndian(data[6..])));

    /**
     * Decodes one attribute of every vertex in an interleaved stream.
     * @param offset Byte offset of the attribute inside each vertex.
     */
    public static Vector4[] ReadStream(ReadOnlySpan<byte> stream, int stride, int offset, NativeVertexFormat format, int count)
    {
        int size = Size(format);
        if (size == 0) throw new NotSupportedException($"Vertex format {format} has no CPU decoder.");
        if (stride <= 0 || offset < 0 || offset + size > stride) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0 || (long)stride * count > stream.Length) throw new ArgumentOutOfRangeException(nameof(count));
        var output = new Vector4[count];
        for (int i = 0; i < count; i++) output[i] = Read(stream[(i * stride + offset)..], format);
        return output;
    }

    /**
     * A NORMAL/TANGENT pair as the static layouts store it (SNORM16 x4): the tangent's W carries the
     * bitangent handedness. The tangent is re-orthogonalised against the normal (Gram-Schmidt).
     */
    public static (Vector3 Normal, Vector3 Tangent, Vector3 Bitangent) TangentFrame(Vector4 normal, Vector4 tangent)
    {
        var n = SafeNormalize(new Vector3(normal.X, normal.Y, normal.Z), Vector3.UnitZ);
        var t = new Vector3(tangent.X, tangent.Y, tangent.Z);
        t = SafeNormalize(t - n * Vector3.Dot(n, t), Perpendicular(n));
        float sign = tangent.W < 0 ? -1 : 1;
        return (n, t, Vector3.Cross(n, t) * sign);
    }

    /**
     * A tangent frame packed as one quaternion (QTangent): X maps to the tangent, Z to the normal, and a
     * negative W flips the bitangent. Encoders keep |W| above zero so the sign survives SNORM rounding.
     */
    public static (Vector3 Normal, Vector3 Tangent, Vector3 Bitangent) QTangentFrame(Vector4 packed)
    {
        var q = new Quaternion(packed.X, packed.Y, packed.Z, packed.W);
        if (q.LengthSquared() < 1e-12f) return (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);
        q = Quaternion.Normalize(q);
        float sign = packed.W < 0 ? -1 : 1;
        var n = Vector3.Transform(Vector3.UnitZ, q);
        var t = Vector3.Transform(Vector3.UnitX, q);
        return (n, t, Vector3.Cross(n, t) * sign);
    }

    /** The inverse of QTangentFrame for an orthonormal frame; the result is SNORM16-safe. */
    public static Vector4 EncodeQTangent(Vector3 normal, Vector3 tangent, float sign)
    {
        var n = SafeNormalize(normal, Vector3.UnitZ);
        var t = SafeNormalize(tangent - n * Vector3.Dot(n, tangent), Perpendicular(n));
        var b = Vector3.Cross(n, t);
        var basis = new Matrix4x4(t.X, t.Y, t.Z, 0, b.X, b.Y, b.Z, 0, n.X, n.Y, n.Z, 0, 0, 0, 0, 1);
        var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));
        if (q.W < 0) q = -q;
        // One SNORM16 step keeps W non-zero so a negative sign can be stored.
        const float Bias = 1f / 32767;
        if (q.W < Bias)
        {
            float scale = MathF.Sqrt(1 - Bias * Bias);
            q = new Quaternion(q.X * scale, q.Y * scale, q.Z * scale, Bias);
        }
        return sign < 0 ? new Vector4(-q.X, -q.Y, -q.Z, -q.W) : new Vector4(q.X, q.Y, q.Z, q.W);
    }

    /** Octahedral unit-vector decode, the other common two-channel normal packing. */
    public static Vector3 OctahedralDecode(Vector2 encoded)
    {
        var n = new Vector3(encoded.X, encoded.Y, 1 - MathF.Abs(encoded.X) - MathF.Abs(encoded.Y));
        if (n.Z < 0)
        {
            float x = (1 - MathF.Abs(n.Y)) * (n.X >= 0 ? 1 : -1);
            float y = (1 - MathF.Abs(n.X)) * (n.Y >= 0 ? 1 : -1);
            n.X = x; n.Y = y;
        }
        return SafeNormalize(n, Vector3.UnitZ);
    }

    /** Encodes a unit normal as two octahedral coordinates. */
    public static Vector2 OctahedralEncode(Vector3 normal)
    {
        var n = SafeNormalize(normal, Vector3.UnitZ);
        n /= MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
        if (n.Z >= 0) return new(n.X, n.Y);
        return new((1 - MathF.Abs(n.Y)) * (n.X >= 0 ? 1 : -1), (1 - MathF.Abs(n.X)) * (n.Y >= 0 ? 1 : -1));
    }

    private static float F32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadSingleLittleEndian(data[at..]);
    private static short I16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadInt16LittleEndian(data[at..]);
    private static ushort U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-12f && float.IsFinite(value.LengthSquared()) ? Vector3.Normalize(value) : fallback;

    private static Vector3 Perpendicular(Vector3 n) =>
        Vector3.Normalize(MathF.Abs(n.X) < .9f ? Vector3.Cross(n, Vector3.UnitX) : Vector3.Cross(n, Vector3.UnitY));
}
