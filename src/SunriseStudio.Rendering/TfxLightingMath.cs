using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SunriseStudio.Rendering;

/** SSE maximum and negate with the native operand order, for lighting maths that must match the game. */
internal static class TfxLightingMath
{
    /** SSE max with the native operand order (which operand wins when a lane is NaN). */
    public static Vector4 Maximum(Vector4 previous, Vector4 top) =>
        From(Sse.Max(To(previous), To(top)));

    /** The native dot product retains paired lane reduction and operand order. */
    public static Vector4 Dot4(Vector4 previous, Vector4 top)
    {
        var product = Sse.Multiply(To(previous), To(top));
        var pairs = Sse.Add(Sse.Shuffle(product, product, 0x4E), product);
        return From(Sse.Add(Sse.Shuffle(pairs, pairs, 0x93), pairs));
    }

    /** The append operation adds masked vectors, including its signed-zero behavior. */
    public static Vector4 AppendScalar(Vector4 previous, Vector4 top)
    {
        var value = To(top);
        var w = Sse.And(Sse.Shuffle(value, value, 0), Vector128.Create(0u, 0u, 0u, uint.MaxValue).AsSingle());
        var xyz = Sse.And(To(previous), Vector128.Create(uint.MaxValue, uint.MaxValue, uint.MaxValue, 0u).AsSingle());
        return From(Sse.Add(w, xyz));
    }

    /** Negates by subtracting from zero, as the native code does. */
    public static Vector4 Negate(Vector4 value) => From(Sse.Subtract(Vector128<float>.Zero, To(value)));
    private static Vector128<float> To(Vector4 value) => Vector128.Create(value.X, value.Y, value.Z, value.W);
    private static Vector4 From(Vector128<float> value) => new(value.GetElement(0), value.GetElement(1),
        value.GetElement(2), value.GetElement(3));
}
