using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SunriseStudio.Rendering;

/** Float helpers and constants that reproduce the native TFX wave and spline maths exactly (SSE order). */
internal static class TfxWaveMath
{
    /** SSE add in the native operand order. */
    public static Vector4 Add(Vector4 top, Vector4 previous) =>
        FromVector(Sse.Add(ToVector(top), ToVector(previous)));

    /** SSE multiply in the native operand order. */
    public static Vector4 Multiply(Vector4 top, Vector4 previous) =>
        FromVector(Sse.Multiply(ToVector(top), ToVector(previous)));

    /** SSE multiply-add in the native operand order. */
    public static Vector4 MultiplyAdd(Vector4 first, Vector4 second, Vector4 top) =>
        FromVector(Sse.Add(ToVector(top), Sse.Multiply(ToVector(second), ToVector(first))));

    /** SSE lerp in the native operand order. */
    public static Vector4 Lerp(Vector4 first, Vector4 second, Vector4 amount) =>
        FromVector(Sse.Add(Sse.Multiply(Sse.Subtract(ToVector(second), ToVector(first)), ToVector(amount)), ToVector(first)));

    /** The native triangle wave doubles the magnitude after nearest-integer wrapping. */
    public static Vector4 Triangle(Vector4 input)
    {
        RequireSse();
        var wrapped = Wrap(ToVector(input));
        return FromVector(Sse.Multiply(Sse.Max(Sse.Subtract(Vector128<float>.Zero, wrapped), wrapped), Vector128.Create(2f)));
    }

    /** Opcode 0x1E: the opcode 0x1F wave without its quarter-turn offset. */
    public static Vector4 Sine(Vector4 input) => Wave(ToVector(input));

    /** Keep the native multiply/add order and nearest-integer rotation wrap. */
    public static Vector4 Cosine(Vector4 input) =>
        Wave(Sse.Add(ToVector(input), Vector128.Create(0.25f)));

    /** Opcode 0x20: (sin X, cos X, sin Y, cos Y) in turns, on the same wave as opcode 0x1F. */
    public static Vector4 SineCosine(Vector4 input)
    {
        var value = ToVector(input);
        return Wave(Sse.Add(Sse.Shuffle(value, value, 0x50), Vector128.Create(0f, 0.25f, 0f, 0.25f)));
    }

    private static Vector4 Wave(Vector128<float> phase)
    {
        RequireSse();
        var wrapped = Wrap(phase);
        var absolute = Sse.Max(Sse.Subtract(Vector128<float>.Zero, wrapped), wrapped);
        var wave = Sse.Multiply(Sse.Add(Sse.Multiply(absolute, Vector128.Create(-16f)), Vector128.Create(8f)), wrapped);
        absolute = Sse.Max(Sse.Subtract(Vector128<float>.Zero, wave), wave);
        return FromVector(Sse.Multiply(Sse.Add(Sse.Multiply(absolute, Vector128.Create(0.225f)),
            Vector128.Create(0.775f)), wave));
    }

    // Opcode 0x28 phase: X times the frequency plus the offset, per lane.
    internal static readonly Vector4 JitterFrequency = new(4.67f, 2.99f, 1.08f, 1.35f);
    internal static readonly Vector4 JitterOffset = new(0.52f, 0.37f, 0.16f, 0.79f);
    // Opcode 0x29: two phases, X times scale plus offset, and the weight on the first wave.
    internal static readonly Vector4 DoubleWaveFirstScale = Floats(0x3FEA3D71, 0x4045C28F, 0x3EC7AE14, 0x3F5EB852);
    internal static readonly Vector4 DoubleWaveFirstOffset = Floats(0x3DF5C28F, 0x3EBD70A4, 0x3E23D70A, 0x3F4A3D71);
    internal static readonly Vector4 DoubleWaveSecondScale = Floats(0x40828F5C, 0x3F828F5C, 0x3F0F044B, 0x3E9ED77F);
    internal static readonly Vector4 DoubleWaveSecondOffset = Floats(0x3F6B851F, 0x3EA8F5C3, 0x3E851EB8, 0x3F0A3D71);
    internal static readonly Vector4 DoubleWaveWeight = Floats(0x3CA3D70A, 0x3CA3D70A, 0x3E8F5C29, 0x3E8F5C29);
    // Opcode 0x2A: floor(X) weights before the paired sum.
    internal static readonly Vector4 FloorHashWeight = Floats(0x3F7553F7, 0x3FA11D0E, 0x410CA03F, 0x4284710A);
    // Opcode 0x24: linear and square weights of the remainder.
    internal static readonly float Exp2Linear = BitConverter.UInt32BitsToSingle(0x3F34B4B5);
    internal static readonly float Exp2Square = BitConverter.UInt32BitsToSingle(0x3E70F0F1);
    // Opcode 0x25: mantissa polynomial (y + z m) m^2 + x m.
    internal static readonly Vector3 Log2Polynomial = new(BitConverter.UInt32BitsToSingle(0x3FB62D34),
        BitConverter.UInt32BitsToSingle(0xBF15DE28), BitConverter.UInt32BitsToSingle(0x3E260F04));
    // Gradient segments narrower than this step at their knot.
    internal static readonly float RampMinimumWidth = BitConverter.UInt32BitsToSingle(0x38D1B717);

    /** Native jitter uses only X and reduces its four frequency lanes in paired order. */
    public static Vector4 Jitter(Vector4 input)
    {
        RequireSse();
        var phase = Sse.Add(Sse.Multiply(Vector128.Create(input.X), ToVector(JitterFrequency)), ToVector(JitterOffset));
        var wrapped = Wrap(phase);
        var absolute = Sse.Max(Sse.Subtract(Vector128<float>.Zero, wrapped), wrapped);
        var value = Sse.Multiply(Sse.Add(Sse.Multiply(absolute, Vector128.Create(-16f)), Vector128.Create(8f)),
            Sse.Multiply(Vector128.Create(0.25f), wrapped));
        value = Sse.Add(Sse.Shuffle(value, value, 0x4E), value);
        value = Sse.Add(Sse.Shuffle(value, value, 0x93), value);
        value = Sse.Add(value, Vector128.Create(0.5f));
        return FromVector(Sse.Multiply(Sse.Add(Sse.Multiply(Vector128.Create(-2f), value), Vector128.Create(3f)),
            Sse.Multiply(value, value)));
    }

    /** Opcode 0x29: two four-lane wave products from X, summed in paired order, plus 0.5; helper 0x7FF742077990. */
    public static Vector4 DoubleWave(Vector4 input)
    {
        RequireSse();
        var x = Vector128.Create(input.X);
        var first = Wrap(Sse.Add(Sse.Multiply(ToVector(DoubleWaveFirstScale), x), ToVector(DoubleWaveFirstOffset)));
        var second = Wrap(Sse.Add(Sse.Multiply(ToVector(DoubleWaveSecondScale), x), ToVector(DoubleWaveSecondOffset)));
        var a = Sse.Multiply(Sse.Multiply(Sse.Add(Sse.Multiply(Absolute(first), Vector128.Create(-16f)), Vector128.Create(8f)),
            first), ToVector(DoubleWaveWeight));
        var b = Sse.Multiply(Sse.Add(Sse.Multiply(Absolute(second), Vector128.Create(-16f)), Vector128.Create(8f)), second);
        var value = Sse.Multiply(a, b);
        value = Sse.Add(Sse.Shuffle(value, value, 0x4E), value);
        value = Sse.Add(Sse.Shuffle(value, value, 0x93), value);
        return FromVector(Sse.Add(value, Vector128.Create(0.5f)));
    }

    /** Opcode 0x2A: a hash of floor(X), fract(fract(dot)^2 * 251); helper 0x7FF742077400. */
    public static Vector4 FloorHash(Vector4 input)
    {
        RequireSse();
        var value = Sse.Multiply(Floor(Vector128.Create(input.X)), ToVector(FloorHashWeight));
        value = Sse.Add(Sse.Shuffle(value, value, 0x4E), value);
        value = Sse.Add(Sse.Shuffle(value, value, 0x93), value);
        var fraction = Sse.Subtract(value, Floor(value));
        var squared = Sse.Multiply(Sse.Multiply(fraction, fraction), Vector128.Create(251f));
        return FromVector(Sse.Subtract(squared, Floor(squared)));
    }

    /** Opcode 0x0F: ((c.x * a + c.y) * (a * a)) + (c.z * a + c.w), in the native operation order. */
    public static Vector4 Cubic(Vector4 a, Vector4 c)
    {
        RequireSse();
        var x = ToVector(a);
        var high = Sse.Add(Sse.Multiply(Vector128.Create(c.X), x), Vector128.Create(c.Y));
        var low = Sse.Add(Sse.Multiply(Vector128.Create(c.Z), x), Vector128.Create(c.W));
        return FromVector(Sse.Add(Sse.Multiply(high, Sse.Multiply(x, x)), low));
    }

    /** Opcode 0x37: four cubic segments from constants c..c+3, knots c+4; helper at 0x7FF742F1E0B6. */
    public static Vector4 Spline4(Vector4 input, ReadOnlySpan<Vector4> c)
    {
        RequireSse();
        var x = ToVector(input);
        return FromVector(Segments(x, c[0], c[1], c[2], c[3], c[4]));
    }

    /** Opcode 0x38: eight segments; knot c+9 lane X picks the upper four; helper 0x7FF742077880. */
    public static Vector4 Spline8(Vector4 input, ReadOnlySpan<Vector4> c)
    {
        RequireSse();
        var x = ToVector(input);
        var lower = Segments(x, c[0], c[1], c[2], c[3], c[8]);
        var upper = Segments(x, c[4], c[5], c[6], c[7], c[9]);
        var pick = Sse.CompareLessThanOrEqual(ToVector(c[9]), x);
        pick = Sse.Shuffle(pick, pick, 0);
        return FromVector(Sse.Or(Sse.AndNot(pick, lower), Sse.And(upper, pick)));
    }

    /** Opcode 0x39: eight segments as opcode 0x38, but X below knot c+8 lane X keeps the previous value; helper 0x7FF742077750. */
    public static Vector4 Spline8OrPrevious(Vector4 previous, Vector4 input, ReadOnlySpan<Vector4> c)
    {
        RequireSse();
        var x = ToVector(input);
        var lower = Segments(x, c[0], c[1], c[2], c[3], c[8]);
        var upper = Segments(x, c[4], c[5], c[6], c[7], c[9]);
        var first = Sse.CompareLessThanOrEqual(ToVector(c[8]), x);
        first = Sse.Shuffle(first, first, 0);
        var second = Sse.CompareLessThanOrEqual(ToVector(c[9]), x);
        second = Sse.Shuffle(second, second, 0);
        var low = Sse.Or(Sse.And(lower, first), Sse.AndNot(first, ToVector(previous)));
        return FromVector(Sse.Or(Sse.AndNot(second, low), Sse.And(upper, second)));
    }

    /** Opcode 0x0A: one where top is not below previous, else zero; NaN gives one. */
    public static Vector4 StepNotBelow(Vector4 previous, Vector4 top)
    {
        RequireSse();
        return FromVector(Sse.AndNot(Sse.CompareLessThan(ToVector(top), ToVector(previous)), Vector128.Create(1f)));
    }

    /** Opcode 0x13: MAXPS of the oldest and middle, then MINPS with the top. */
    public static Vector4 Clamp(Vector4 value, Vector4 minimum, Vector4 maximum)
    {
        RequireSse();
        return FromVector(Sse.Min(Sse.Max(ToVector(value), ToVector(minimum)), ToVector(maximum)));
    }

    /** Opcode 0x2E: rows r0..r3 weighted by v, summed as (x r0 + y r1) + (z r2 + w r3). */
    public static Vector4 Transform(Vector4 r0, Vector4 r1, Vector4 r2, Vector4 r3, Vector4 v)
    {
        RequireSse();
        var a = Sse.Add(Sse.Multiply(Vector128.Create(v.X), ToVector(r0)), Sse.Multiply(Vector128.Create(v.Y), ToVector(r1)));
        var b = Sse.Add(Sse.Multiply(Vector128.Create(v.Z), ToVector(r2)), Sse.Multiply(Vector128.Create(v.W), ToVector(r3)));
        return FromVector(Sse.Add(a, b));
    }

    /** Opcode 0x24: 2^round(x) times a quadratic in the remainder; block 0x7FF742F1D3D0. */
    public static Vector4 Exp2(Vector4 input)
    {
        RequireSse();
        var x = ToVector(input);
        var whole = Sse2.ConvertToVector128Int32(x);
        var fraction = Sse.Subtract(x, Sse2.ConvertToVector128Single(whole));
        var scale = Sse2.ShiftLeftLogical(Sse2.Add(whole, Vector128.Create(0x7F)), 23).AsSingle();
        var linear = Sse.Add(Sse.Multiply(fraction, Vector128.Create(Exp2Linear)), Vector128.Create(1f));
        var square = Sse.Multiply(Sse.Multiply(fraction, fraction), Vector128.Create(Exp2Square));
        return FromVector(Sse.Multiply(Sse.Add(square, linear), scale));
    }

    /** Opcode 0x25: polynomial log2 from the exponent and mantissa bits; helper 0x7FF74207BB80. */
    public static Vector4 Log2(Vector4 input)
    {
        RequireSse();
        var bits = ToVector(input).AsInt32();
        var mantissa = Sse.Multiply(Sse2.ConvertToVector128Single(Sse2.And(bits, Vector128.Create(0x007FFFFF))),
            Bits(0x34000000, 0x34000000, 0x34000000, 0x34000000));
        var exponent = Sse2.ConvertToVector128Single(Sse2.Subtract(
            Sse2.ShiftRightLogical(Sse2.And(bits, Vector128.Create(0x7F800000)), 23), Vector128.Create(0x7F)));
        var cx = Vector128.Create(Log2Polynomial.X);
        var cy = Vector128.Create(Log2Polynomial.Y);
        var cz = Vector128.Create(Log2Polynomial.Z);
        var poly = Sse.Add(Sse.Multiply(Sse.Add(cy, Sse.Multiply(cz, mantissa)), Sse.Multiply(mantissa, mantissa)),
            Sse.Multiply(cx, mantissa));
        return FromVector(Sse.Add(poly, exponent));
    }

    /** Opcode 0x26: four-lane length through RSQRTPS and one Newton step; zero length stays zero; helper 0x7FF7420454F0. */
    public static Vector4 Length4(Vector4 input)
    {
        RequireSse();
        var v = ToVector(input);
        var sum = Sse.Multiply(v, v);
        sum = Sse.Add(Sse.Shuffle(sum, sum, 0x4E), sum);
        sum = Sse.Add(Sse.Shuffle(sum, sum, 0x93), sum);
        var half = Vector128.Create(0.5f);
        var estimate = Sse.ReciprocalSqrt(sum);
        var refined = Sse.Add(Sse.Multiply(Sse.Subtract(half, Sse.Multiply(Sse.Multiply(estimate, estimate),
            Sse.Multiply(half, sum))), estimate), estimate);
        var valid = Sse.CompareEqual(refined, refined);
        var root = Sse.Or(Sse.And(valid, refined), Sse.AndNot(valid, estimate));
        var infinite = Sse.CompareEqual(Bits(0x7F800000, 0x7F800000, 0x7F800000, 0x7F800000), root);
        return FromVector(Sse.AndNot(infinite, Sse.Multiply(root, sum)));
    }

    /** Opcode 0x3A: base c plus four ramps with knots c+5 and one row per channel; helper 0x7FF742076D20. */
    public static Vector4 Gradient(Vector4 input, ReadOnlySpan<Vector4> c)
    {
        RequireSse();
        var knots = ToVector(c[5]);
        var amount = Ramp(Sse.Subtract(ToVector(input), knots), Sse.Subtract(NextKnots(knots, One), knots));
        var result = ToVector(c[0]);
        for (int channel = 0; channel < 4; channel++)
            result = AddChannel(result, Sse.Multiply(Sse.Multiply(ToVector(c[channel + 1]), amount), One), channel);
        return FromVector(result);
    }

    /** Opcode 0x3B: base c plus eight ramps with knots c+9 and c+10; helper 0x7FF742076FA0. */
    public static Vector4 Gradient8(Vector4 input, ReadOnlySpan<Vector4> c)
    {
        RequireSse();
        var x = ToVector(input);
        var lowKnots = ToVector(c[9]);
        var highKnots = ToVector(c[10]);
        var wLane = Bits(0, 0, 0, 0xFFFFFFFF);
        var lowNext = Sse.Add(Sse.And(Sse.Shuffle(highKnots, highKnots, 0), wLane),
            Sse.And(Sse.Shuffle(lowKnots, lowKnots, 0x39), Xyz));
        var low = Ramp(Sse.Subtract(x, lowKnots), Sse.Subtract(lowNext, lowKnots));
        var high = Ramp(Sse.Subtract(x, highKnots), Sse.Subtract(NextKnots(highKnots, One), highKnots));
        var result = ToVector(c[0]);
        for (int channel = 0; channel < 4; channel++)
            result = AddChannel(result, Sse.Multiply(Sse.Add(Sse.Multiply(ToVector(c[channel + 1]), low),
                Sse.Multiply(ToVector(c[channel + 5]), high)), One), channel);
        return FromVector(result);
    }

    private static readonly Vector128<float> One = Vector128.Create(1f);
    private static readonly Vector128<float> Xyz = Bits(0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0);

    private static Vector128<float> NextKnots(Vector128<float> knots, Vector128<float> last) =>
        Sse.Or(Sse.And(Sse.Shuffle(knots, knots, 0x39), Xyz), Sse.AndNot(Xyz, last));

    /** Clamped position in each segment; segments narrower than 0x38D1B717 step at their knot. */
    private static Vector128<float> Ramp(Vector128<float> offset, Vector128<float> width)
    {
        var limit = Vector128.Create(RampMinimumWidth);
        var wide = Sse.CompareLessThan(limit, Absolute(width));
        var step = Sse.And(Sse.CompareLessThanOrEqual(Vector128<float>.Zero, offset), One);
        return Sse.Min(Sse.Max(Sse.Or(Sse.AndNot(wide, step), Sse.And(Sse.Divide(offset, width), wide)),
            Vector128<float>.Zero), One);
    }

    /** Sums the four lanes in paired order and adds the total to one channel. */
    private static Vector128<float> AddChannel(Vector128<float> result, Vector128<float> terms, int channel)
    {
        terms = Sse.Add(Sse.Shuffle(terms, terms, 0x4E), terms);
        terms = Sse.Add(Sse.Shuffle(terms, terms, 0x93), terms);
        var lane = Vector128.Create(channel == 0 ? 0xFFFFFFFF : 0, channel == 1 ? 0xFFFFFFFF : 0,
            channel == 2 ? 0xFFFFFFFF : 0, channel == 3 ? 0xFFFFFFFF : 0).AsSingle();
        return Sse.Add(Sse.And(terms, lane), result);
    }

    /** Selects the segment whose knot lane is the last one at or below X and broadcasts its value. */
    private static Vector128<float> Segments(Vector128<float> x, Vector4 a, Vector4 b, Vector4 c, Vector4 d,
        Vector4 knots)
    {
        var value = Sse.Add(Sse.Multiply(Sse.Add(Sse.Multiply(ToVector(a), x), ToVector(b)), Sse.Multiply(x, x)),
            Sse.Add(Sse.Multiply(ToVector(c), x), ToVector(d)));
        var reached = Sse.CompareLessThanOrEqual(ToVector(knots), x);
        var select = Sse.Xor(reached, Sse2.ShiftRightLogical128BitLane(reached.AsByte(), 4).AsSingle());
        value = Sse.And(value, select);
        value = Sse.Xor(Sse.Shuffle(value, value, 0x4E), value);
        return Sse.Xor(Sse.Shuffle(value, value, 0x1B), value);
    }

    /** Opcode 0x17: componentwise floor of the top vector. */
    public static Vector4 FloorVector(Vector4 value)
    {
        RequireSse();
        return FromVector(Floor(ToVector(value)));
    }

    /** Truncate, then step up for positive fractions; large magnitudes pass through unchanged. */
    public static Vector4 CeilingVector(Vector4 value)
    {
        RequireSse();
        var x = ToVector(value);
        var small = Small(x);
        var truncated = Sse2.ConvertToVector128Single(Sse2.ConvertToVector128Int32WithTruncation(x));
        var ceiling = Sse.Add(Sse.And(Sse.CompareLessThan(truncated, x), Vector128.Create(1f)), truncated);
        return FromVector(Sse.Or(Sse.And(ceiling, small), Sse.AndNot(small, x)));
    }

    /** Opcode 0x19: CVTPS2DQ under the default MXCSR, so ties go to even; large magnitudes pass through. */
    public static Vector4 RoundVector(Vector4 value)
    {
        RequireSse();
        var x = ToVector(value);
        var small = Small(x);
        var rounded = Sse2.ConvertToVector128Single(Sse2.ConvertToVector128Int32(x));
        return FromVector(Sse.Or(Sse.And(rounded, small), Sse.AndNot(small, x)));
    }

    /** Opcode 0x1A: X minus its floor. */
    public static Vector4 Fraction(Vector4 value)
    {
        RequireSse();
        var x = ToVector(value);
        return FromVector(Sse.Subtract(x, Floor(x)));
    }

    /** Opcodes 0x1B and 0x1C: RSQRTPS with one Newton step; an infinite scale gives zero. */
    public static Vector4 Normalize(Vector4 value, bool fourLanes)
    {
        RequireSse();
        var v = ToVector(value);
        var squares = Sse.Multiply(v, v);
        Vector128<float> sum;
        if (fourLanes)
        {
            sum = Sse.Add(Sse.Shuffle(squares, squares, 0x4E), squares);
            sum = Sse.Add(Sse.Shuffle(sum, sum, 0x93), sum);
        }
        else sum = Sse.Add(Sse.Add(Sse.Shuffle(squares, squares, 0x55), Sse.Shuffle(squares, squares, 0)),
            Sse.Shuffle(squares, squares, 0xAA));
        var half = Vector128.Create(0.5f);
        var estimate = Sse.ReciprocalSqrt(sum);
        var refined = Sse.Add(Sse.Multiply(Sse.Subtract(half, Sse.Multiply(Sse.Multiply(half, sum),
            Sse.Multiply(estimate, estimate))), estimate), estimate);
        var valid = Sse.CompareEqual(refined, refined);
        var scale = Sse.Or(Sse.And(valid, refined), Sse.AndNot(valid, estimate));
        var infinite = Sse.CompareEqual(Bits(0x7F800000, 0x7F800000, 0x7F800000, 0x7F800000), scale);
        return FromVector(Sse.AndNot(infinite, Sse.Multiply(scale, v)));
    }

    private static Vector128<float> Small(Vector128<float> value) =>
        Sse2.CompareGreaterThan(Vector128.Create(0x4B000000), Sse2.And(value.AsInt32(), Vector128.Create(0x7FFFFFFF))).AsSingle();

    /** Truncate, then step down for negative fractions; large magnitudes pass through unchanged. */
    private static Vector128<float> Floor(Vector128<float> value)
    {
        var small = Small(value);
        var truncated = Sse2.ConvertToVector128Single(Sse2.ConvertToVector128Int32WithTruncation(value));
        var floor = Sse.Subtract(truncated, Sse.And(Sse.CompareLessThan(value, truncated), Vector128.Create(1f)));
        return Sse.Or(Sse.And(floor, small), Sse.AndNot(small, value));
    }

    private static Vector128<float> Absolute(Vector128<float> value) => Sse.Max(Sse.Subtract(Vector128<float>.Zero, value), value);

    private static Vector128<float> Bits(uint x, uint y, uint z, uint w) => Vector128.Create(x, y, z, w).AsSingle();
    private static Vector4 Floats(uint x, uint y, uint z, uint w) => new(BitConverter.UInt32BitsToSingle(x),
        BitConverter.UInt32BitsToSingle(y), BitConverter.UInt32BitsToSingle(z), BitConverter.UInt32BitsToSingle(w));

    /** Above the float integer-precision boundary the native path subtracts the input from itself. */
    private static Vector128<float> Wrap(Vector128<float> value)
    {
        var small = Small(value);
        var rounded = Sse2.ConvertToVector128Single(Sse2.ConvertToVector128Int32(value));
        var integer = Sse.Or(Sse.And(rounded, small), Sse.AndNot(small, value));
        return Sse.Subtract(value, integer);
    }

    private static void RequireSse()
    {
        if (!Sse2.IsSupported) throw new PlatformNotSupportedException("Native TFX waves require SSE2.");
    }
    private static Vector128<float> ToVector(Vector4 value) => Vector128.Create(value.X, value.Y, value.Z, value.W);
    private static Vector4 FromVector(Vector128<float> value) =>
        new(value.GetElement(0), value.GetElement(1), value.GetElement(2), value.GetElement(3));
}
