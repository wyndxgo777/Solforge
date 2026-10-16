using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SunriseStudio.Rendering;

/** Matrix helpers that reproduce the native SSE operation order so results match bit for bit. */
internal static class NativeMatrixMath
{
    /** Native products add XY and ZW pairs before the final sum, without fused arithmetic. */
    public static Matrix4x4 Multiply(Matrix4x4 a, Matrix4x4 b)
    {
        var b0 = Vector128.Create(b.M11, b.M12, b.M13, b.M14);
        var b1 = Vector128.Create(b.M21, b.M22, b.M23, b.M24);
        var b2 = Vector128.Create(b.M31, b.M32, b.M33, b.M34);
        var b3 = Vector128.Create(b.M41, b.M42, b.M43, b.M44);
        Vector128<float> Row(float x, float y, float z, float w) => Sse.Add(
            Sse.Add(Sse.Multiply(Vector128.Create(x), b0), Sse.Multiply(Vector128.Create(y), b1)),
            Sse.Add(Sse.Multiply(Vector128.Create(z), b2), Sse.Multiply(Vector128.Create(w), b3)));
        return FromRows(Row(a.M11, a.M12, a.M13, a.M14), Row(a.M21, a.M22, a.M23, a.M24),
            Row(a.M31, a.M32, a.M33, a.M34), Row(a.M41, a.M42, a.M43, a.M44));
    }

    private static Matrix4x4 FromRows(Vector128<float> a, Vector128<float> b,
        Vector128<float> c, Vector128<float> d) => new(
            a.GetElement(0), a.GetElement(1), a.GetElement(2), a.GetElement(3),
            b.GetElement(0), b.GetElement(1), b.GetElement(2), b.GetElement(3),
            c.GetElement(0), c.GetElement(1), c.GetElement(2), c.GetElement(3),
            d.GetElement(0), d.GetElement(1), d.GetElement(2), d.GetElement(3));
}
