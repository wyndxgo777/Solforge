using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Scene;
using static Checks;

/** Phase 11: half floats, SNORM/UNORM streams and packed tangent frames decode on the CPU. */
internal static class GeometryChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(List<string> checks)
    {
        Scalars();
        checks.Add("half floats and SNORM16/UNORM16/UNORM8 scalars decode as D3D does, including -32768 and 65504");
        Stream();
        checks.Add("an interleaved stream (half2 UV, SNORM16x4 normal, UNORM8x4 colour) decodes per vertex with D3D defaults");
        TangentFrames();
        checks.Add("SNORM16 normal/tangent pairs rebuild an orthonormal frame with the handedness in tangent W");
        QTangents();
        checks.Add("quaternion tangent frames survive SNORM16 quantization with both handedness signs");
        Octahedral();
        checks.Add("octahedral normals round trip over the whole sphere");
    }

    private static void Scalars()
    {
        Require(VertexAttributeDecoding.Half(0x3C00) == 1 && VertexAttributeDecoding.Half(0xC000) == -2, "Half 1 / -2 decode.");
        Require(VertexAttributeDecoding.Half(0x7BFF) == 65504, "Half max decode.");
        Require(Near(VertexAttributeDecoding.Half(0x3555), 1 / 3f, 1e-3f), "Half one third decode.");
        Require(VertexAttributeDecoding.Snorm16(-32768) == -1 && VertexAttributeDecoding.Snorm16(-32767) == -1, "SNORM16 minimum clamps to -1.");
        Require(VertexAttributeDecoding.Snorm16(32767) == 1 && VertexAttributeDecoding.Snorm16(0) == 0, "SNORM16 maximum and zero.");
        Require(VertexAttributeDecoding.Unorm16(65535) == 1 && VertexAttributeDecoding.Unorm8(255) == 1, "UNORM maxima.");
        Require(VertexAttributeDecoding.Size(NativeVertexFormat.R16G16B16A16SNorm) == 8
            && VertexAttributeDecoding.Size(NativeVertexFormat.R16G16Float) == 4
            && VertexAttributeDecoding.Size(NativeVertexFormat.Unknown) == 0, "Element sizes.");
        var half4 = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(half4, 0x3C00);
        BinaryPrimitives.WriteUInt16LittleEndian(half4.AsSpan(2), 0x3800);
        BinaryPrimitives.WriteUInt16LittleEndian(half4.AsSpan(4), 0xBC00);
        BinaryPrimitives.WriteUInt16LittleEndian(half4.AsSpan(6), 0x0000);
        Require(VertexAttributeDecoding.ReadHalf4(half4) == new Vector4(1, .5f, -1, 0), "Half4 decode.");
    }

    private static void Vertex(byte[] data, int stride, int index, ushort u, ushort v, short[] normal, byte[] color)
    {
        var span = data.AsSpan(index * stride);
        BinaryPrimitives.WriteUInt16LittleEndian(span, u);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], v);
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteInt16LittleEndian(span[(4 + i * 2)..], normal[i]);
        color.CopyTo(span[12..]);
    }

    private static void Stream()
    {
        const int stride = 16;
        var data = new byte[stride * 2];
        Vertex(data, stride, 0, 0x3800, 0x3C00, [0, 0, 32767, 0], [255, 0, 0, 255]);
        Vertex(data, stride, 1, 0x0000, 0x3400, [-32768, 0, 0, 32767], [0, 128, 255, 0]);
        var uv = VertexAttributeDecoding.ReadStream(data, stride, 0, NativeVertexFormat.R16G16Float, 2);
        var normal = VertexAttributeDecoding.ReadStream(data, stride, 4, NativeVertexFormat.R16G16B16A16SNorm, 2);
        var color = VertexAttributeDecoding.ReadStream(data, stride, 12, NativeVertexFormat.R8G8B8A8UNorm, 2);
        Require(uv[0] == new Vector4(.5f, 1, 0, 1) && uv[1] == new Vector4(0, .25f, 0, 1), $"UV stream: {uv[0]} {uv[1]}.");
        Require(normal[0] == new Vector4(0, 0, 1, 0) && normal[1] == new Vector4(-1, 0, 0, 1), $"Normal stream: {normal[0]} {normal[1]}.");
        Require(color[0] == new Vector4(1, 0, 0, 1) && Near(color[1].Y, 128 / 255f, 1e-6f), "Colour stream.");
        bool refused = false;
        try { VertexAttributeDecoding.ReadStream(data, stride, 0, NativeVertexFormat.R16G16Float, 3); }
        catch (ArgumentOutOfRangeException) { refused = true; }
        Require(refused, "A count past the end of the stream must be refused.");
    }

    private static short Quantize(float value) => (short)Math.Clamp(MathF.Round(value * 32767), -32767, 32767);

    private static Vector4 Snorm(Vector4 value) => new(VertexAttributeDecoding.Snorm16(Quantize(value.X)),
        VertexAttributeDecoding.Snorm16(Quantize(value.Y)), VertexAttributeDecoding.Snorm16(Quantize(value.Z)),
        VertexAttributeDecoding.Snorm16(Quantize(value.W)));

    private static Vector3 Unit(Random random) => Vector3.Normalize(new Vector3((float)random.NextDouble() * 2 - 1,
        (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1) + new Vector3(1e-3f));

    private static (Vector3 Normal, Vector3 Tangent) RandomFrame(Random random)
    {
        var n = Unit(random);
        var t = Vector3.Normalize(Vector3.Cross(n, Unit(random)));
        return (n, t);
    }

    private static void TangentFrames()
    {
        var (n0, t0, b0) = VertexAttributeDecoding.TangentFrame(new(0, 0, 1, 0), new(1, 0, 0, -1));
        Require(n0 == Vector3.UnitZ && t0 == Vector3.UnitX && Near(b0, -Vector3.UnitY), $"Left-handed frame: {b0}.");
        var random = new Random(11);
        for (int i = 0; i < 2000; i++)
        {
            var (n, t) = RandomFrame(random);
            float sign = i % 2 == 0 ? 1 : -1;
            var frame = VertexAttributeDecoding.TangentFrame(Snorm(new(n, 0)), Snorm(new(t, sign)));
            Require(Near(frame.Normal, n, 2e-4f) && Near(frame.Tangent, t, 5e-4f), $"Frame {i} drifted.");
            Require(MathF.Abs(Vector3.Dot(frame.Normal, frame.Tangent)) < 1e-5f, $"Frame {i} is not orthogonal.");
            Require(Near(frame.Bitangent, Vector3.Cross(n, t) * sign, 1e-3f), $"Frame {i} lost its handedness.");
        }
    }

    private static void QTangents()
    {
        var random = new Random(23);
        for (int i = 0; i < 2000; i++)
        {
            var (n, t) = RandomFrame(random);
            float sign = i % 2 == 0 ? 1 : -1;
            var packed = Snorm(VertexAttributeDecoding.EncodeQTangent(n, t, sign));
            Require(packed.W != 0, $"QTangent {i} lost its sign bit.");
            var frame = VertexAttributeDecoding.QTangentFrame(packed);
            Require(Near(frame.Normal, n, 1e-3f) && Near(frame.Tangent, t, 1e-3f),
                $"QTangent {i}: normal {frame.Normal} vs {n}, tangent {frame.Tangent} vs {t}.");
            Require(Near(frame.Bitangent, Vector3.Cross(n, t) * sign, 2e-3f), $"QTangent {i} lost its handedness.");
        }
    }

    private static void Octahedral()
    {
        var random = new Random(5);
        foreach (var axis in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
            Require(Near(VertexAttributeDecoding.OctahedralDecode(VertexAttributeDecoding.OctahedralEncode(axis)), axis, 1e-5f),
                $"Octahedral axis {axis}.");
        for (int i = 0; i < 2000; i++)
        {
            var n = Unit(random);
            var round = VertexAttributeDecoding.OctahedralDecode(VertexAttributeDecoding.OctahedralEncode(n));
            Require(Near(round, n, 1e-4f), $"Octahedral {i}: {round} vs {n}.");
        }
    }
}
