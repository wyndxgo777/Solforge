using System.Numerics;

namespace SunriseStudio.Rendering;

/** One TFX output lane as a function of simulation seconds; operation names follow DXBC. */
public sealed record TfxTerm(string Operation, TfxTerm[] Inputs, float Value = 0)
{
    public static TfxTerm Time { get; } = new("time", []);
    public bool IsConstant => Operation == "constant";

    /** A constant term. */
    public static TfxTerm Constant(float value) => new("constant", [], value);

    /** Folds an operation whose inputs are all constant. */
    public static TfxTerm Op(string operation, params TfxTerm[] inputs) => inputs.All(input => input.IsConstant)
        ? Constant(Apply(operation, inputs.Select(input => input.Value).ToArray())) : new(operation, inputs);

    /** Adds, folding constants. */
    public static TfxTerm Add(TfxTerm a, TfxTerm b) => Op("add", a, b);
    /** Multiplies, folding constants. */
    public static TfxTerm Mul(TfxTerm a, TfxTerm b) => Op("mul", a, b);
    /** Subtracts, folding constants. */
    public static TfxTerm Sub(TfxTerm a, TfxTerm b) => Add(a, Op("neg", b));
    /** Absolute value, folding constants. */
    public static TfxTerm Abs(TfxTerm a) => Op("abs", a);
    /** Clamps to 0..1. */
    public static TfxTerm Saturate(TfxTerm a) => Op("min", Op("max", a, Constant(0)), Constant(1));

    /** The native wave in turns: nearest-integer wrap, then two parabola stages. */
    public static TfxTerm Wave(TfxTerm phase)
    {
        var wrapped = Wrap(phase);
        var wave = Mul(Add(Mul(Abs(wrapped), Constant(-16)), Constant(8)), wrapped);
        return Mul(Add(Mul(Abs(wave), Constant(0.225f)), Constant(0.775f)), wave);
    }

    // Inputs at or above 2^23 wrap to zero natively; the term form leaves them unwrapped.
    public static TfxTerm Wrap(TfxTerm value) => Sub(value, Op("round_ne", value));

    /** Evaluates the term tree at the given simulation time. */
    public float Evaluate(float seconds)
    {
        if (Operation == "time") return seconds;
        return Operation == "constant" ? Value : Apply(Operation, Inputs.Select(input => input.Evaluate(seconds)).ToArray());
    }

    private static float Apply(string operation, float[] v) => operation switch
    {
        "add" => v[0] + v[1], "mul" => v[0] * v[1], "div" => v[0] / v[1],
        "min" => MathF.Min(v[0], v[1]), "max" => MathF.Max(v[0], v[1]), "abs" => MathF.Abs(v[0]),
        "neg" => -v[0], "frc" => v[0] - MathF.Floor(v[0]), "round_ni" => MathF.Floor(v[0]),
        "round_ne" => MathF.Round(v[0], MidpointRounding.ToEven), "round_pi" => MathF.Ceiling(v[0]),
        "ge" => v[0] >= v[1] ? 1 : 0, "lt" => v[0] < v[1] ? 1 : 0, "movc" => v[0] != 0 ? v[1] : v[2],
        "exp" => MathF.Pow(2, v[0]), "log" => MathF.Log2(v[0]), "sqrt" => MathF.Sqrt(v[0]),
        _ => throw new InvalidOperationException($"TFX term operation {operation} is not defined.")
    };
}

/** Four-lane term forms of the TFX vector operations, in the native operand order. */
internal static class TfxTermMath
{
    private static TfxTerm[] Lanes(Func<int, TfxTerm> lane) => [lane(0), lane(1), lane(2), lane(3)];
    private static TfxTerm C(float value) => TfxTerm.Constant(value);

    /** Lane-wise add. */
    public static TfxTerm[] Add(TfxTerm[] a, TfxTerm[] b) => Lanes(i => TfxTerm.Add(a[i], b[i]));
    /** Lane-wise subtract. */
    public static TfxTerm[] Sub(TfxTerm[] a, TfxTerm[] b) => Lanes(i => TfxTerm.Sub(a[i], b[i]));
    /** Lane-wise multiply. */
    public static TfxTerm[] Mul(TfxTerm[] a, TfxTerm[] b) => Lanes(i => TfxTerm.Mul(a[i], b[i]));
    /** Lane-wise divide. */
    public static TfxTerm[] Div(TfxTerm[] a, TfxTerm[] b) => Lanes(i => TfxTerm.Op("div", a[i], b[i]));
    /** Lane-wise maximum. */
    public static TfxTerm[] Max(TfxTerm[] a, TfxTerm[] b) => Lanes(i => TfxTerm.Op("max", a[i], b[i]));
    /** Applies a one-input operation to each lane. */
    public static TfxTerm[] Unary(string operation, TfxTerm[] a) => Lanes(i => TfxTerm.Op(operation, a[i]));
    /** Clamps each lane to 0..1. */
    public static TfxTerm[] Saturate(TfxTerm[] a) => Lanes(i => TfxTerm.Saturate(a[i]));
    /** Repeats one term in all four lanes. */
    public static TfxTerm[] Splat(TfxTerm value) => [value, value, value, value];

    /** Paired reduction: (p1 + p3) + (p2 + p0) in every lane. */
    public static TfxTerm[] Dot4(TfxTerm[] a, TfxTerm[] b) => Splat(Sum4(Mul(a, b)));

    /** Evaluates the cubic with coefficients c on each lane. */
    public static TfxTerm[] Cubic(TfxTerm[] a, TfxTerm[] c) => Lanes(i => TfxTerm.Add(
        TfxTerm.Mul(TfxTerm.Add(TfxTerm.Mul(c[0], a[i]), c[1]), TfxTerm.Mul(a[i], a[i])),
        TfxTerm.Add(TfxTerm.Mul(c[2], a[i]), c[3])));

    /** Lane-wise a + (b - a) * t. */
    public static TfxTerm[] Lerp(TfxTerm[] first, TfxTerm[] second, TfxTerm[] amount) =>
        Lanes(i => TfxTerm.Add(TfxTerm.Mul(TfxTerm.Sub(second[i], first[i]), amount[i]), first[i]));

    /** Lane-wise first * second + top. */
    public static TfxTerm[] MultiplyAdd(TfxTerm[] first, TfxTerm[] second, TfxTerm[] top) =>
        Lanes(i => TfxTerm.Add(top[i], TfxTerm.Mul(second[i], first[i])));

    /** Cosine in turns, via the native wave. */
    public static TfxTerm[] Cosine(TfxTerm[] a) => Lanes(i => TfxTerm.Wave(TfxTerm.Add(a[i], C(0.25f))));

    /** Sine and cosine of two phases, as the native SineCosine form. */
    public static TfxTerm[] SineCosine(TfxTerm[] a) =>
        [TfxTerm.Wave(a[0]), TfxTerm.Wave(TfxTerm.Add(a[0], C(0.25f))), TfxTerm.Wave(a[1]), TfxTerm.Wave(TfxTerm.Add(a[1], C(0.25f)))];

    /** Triangle wave in turns. */
    public static TfxTerm[] Triangle(TfxTerm[] a) => Lanes(i => TfxTerm.Mul(TfxTerm.Abs(TfxTerm.Wrap(a[i])), C(2)));

    /** Lane-wise clamp to [minimum, maximum]. */
    public static TfxTerm[] Clamp(TfxTerm[] value, TfxTerm[] minimum, TfxTerm[] maximum) =>
        Lanes(i => TfxTerm.Op("min", TfxTerm.Op("max", value[i], minimum[i]), maximum[i]));

    /** One where top is not below previous. */
    public static TfxTerm[] StepNotBelow(TfxTerm[] previous, TfxTerm[] top) => Lanes(i => TfxTerm.Op("ge", top[i], previous[i]));

    /** Multiplies v by the matrix rows r0..r3. */
    public static TfxTerm[] Transform(TfxTerm[] r0, TfxTerm[] r1, TfxTerm[] r2, TfxTerm[] r3, TfxTerm[] v) => Lanes(i =>
        TfxTerm.Add(TfxTerm.Add(TfxTerm.Mul(v[0], r0[i]), TfxTerm.Mul(v[1], r1[i])),
            TfxTerm.Add(TfxTerm.Mul(v[2], r2[i]), TfxTerm.Mul(v[3], r3[i]))));

    /** Four-component length, repeated in all lanes. */
    public static TfxTerm[] Length4(TfxTerm[] a) => Splat(TfxTerm.Op("sqrt", Sum4(Mul(a, a))));

    /** Native polynomial 2^x on each lane. */
    public static TfxTerm[] Exp2(TfxTerm[] a) => Lanes(i =>
    {
        var whole = TfxTerm.Op("round_ne", a[i]);
        var fraction = TfxTerm.Sub(a[i], whole);
        var linear = TfxTerm.Add(TfxTerm.Mul(fraction, C(TfxWaveMath.Exp2Linear)), C(1));
        var square = TfxTerm.Mul(TfxTerm.Mul(fraction, fraction), C(TfxWaveMath.Exp2Square));
        return TfxTerm.Mul(TfxTerm.Add(square, linear), TfxTerm.Op("exp", whole));
    });

    /** The native form reads the float bits, so the sign is ignored; zero has no term value. */
    public static TfxTerm[] Log2(TfxTerm[] a) => Lanes(i =>
    {
        var magnitude = TfxTerm.Abs(a[i]);
        var exponent = TfxTerm.Op("round_ni", TfxTerm.Op("log", magnitude));
        var mantissa = TfxTerm.Sub(TfxTerm.Mul(magnitude, TfxTerm.Op("exp", TfxTerm.Op("neg", exponent))), C(1));
        var p = TfxWaveMath.Log2Polynomial;
        var poly = TfxTerm.Add(TfxTerm.Mul(TfxTerm.Add(C(p.Y), TfxTerm.Mul(C(p.Z), mantissa)), TfxTerm.Mul(mantissa, mantissa)),
            TfxTerm.Mul(C(p.X), mantissa));
        return TfxTerm.Add(poly, exponent);
    });

    /** Smoothed sum of three phase-shifted waves of a[0]. */
    public static TfxTerm[] Jitter(TfxTerm[] a)
    {
        var frequency = TfxWaveMath.JitterFrequency; var offset = TfxWaveMath.JitterOffset;
        var lanes = Lanes(i =>
        {
            var wrapped = TfxTerm.Wrap(TfxTerm.Add(TfxTerm.Mul(a[0], C(frequency[i])), C(offset[i])));
            return TfxTerm.Mul(TfxTerm.Add(TfxTerm.Mul(TfxTerm.Abs(wrapped), C(-16)), C(8)), TfxTerm.Mul(C(0.25f), wrapped));
        });
        var value = TfxTerm.Add(Sum4(lanes), C(0.5f));
        return Splat(TfxTerm.Mul(TfxTerm.Add(TfxTerm.Mul(C(-2), value), C(3)), TfxTerm.Mul(value, value)));
    }

    /** Weighted product of two waves of a[0], summed over lanes. */
    public static TfxTerm[] DoubleWave(TfxTerm[] a)
    {
        var lanes = Lanes(i =>
        {
            var first = TfxTerm.Wrap(TfxTerm.Add(TfxTerm.Mul(C(TfxWaveMath.DoubleWaveFirstScale[i]), a[0]), C(TfxWaveMath.DoubleWaveFirstOffset[i])));
            var second = TfxTerm.Wrap(TfxTerm.Add(TfxTerm.Mul(C(TfxWaveMath.DoubleWaveSecondScale[i]), a[0]), C(TfxWaveMath.DoubleWaveSecondOffset[i])));
            var weighted = TfxTerm.Mul(TfxTerm.Mul(TfxTerm.Add(TfxTerm.Mul(TfxTerm.Abs(first), C(-16)), C(8)), first),
                C(TfxWaveMath.DoubleWaveWeight[i]));
            return TfxTerm.Mul(weighted, TfxTerm.Mul(TfxTerm.Add(TfxTerm.Mul(TfxTerm.Abs(second), C(-16)), C(8)), second));
        });
        return Splat(TfxTerm.Add(Sum4(lanes), C(0.5f)));
    }

    /** Fractional hash of floor(a[0]). */
    public static TfxTerm[] FloorHash(TfxTerm[] a)
    {
        var floor = TfxTerm.Op("round_ni", a[0]);
        var fraction = TfxTerm.Op("frc", Sum4(Lanes(i => TfxTerm.Mul(floor, C(TfxWaveMath.FloorHashWeight[i])))));
        return Splat(TfxTerm.Op("frc", TfxTerm.Mul(TfxTerm.Mul(fraction, fraction), C(251))));
    }

    /** Four-segment spline of x[0], repeated in all lanes. */
    public static TfxTerm[] Spline4(TfxTerm[] x, Vector4[] c) => Splat(Segments(x, c[0], c[1], c[2], c[3], c[4]));

    /** Eight-segment spline of x[0], picking the upper half at its split. */
    public static TfxTerm[] Spline8(TfxTerm[] x, Vector4[] c) => Splat(TfxTerm.Op("movc", TfxTerm.Op("ge", x[0], C(c[9].X)),
        Segments(x, c[4], c[5], c[6], c[7], c[9]), Segments(x, c[0], c[1], c[2], c[3], c[8])));

    /** Eight-segment spline that keeps the previous value below the first knot. */
    public static TfxTerm[] Spline8OrPrevious(TfxTerm[] previous, TfxTerm[] x, Vector4[] c)
    {
        var lower = Segments(x, c[0], c[1], c[2], c[3], c[8]);
        var upper = Segments(x, c[4], c[5], c[6], c[7], c[9]);
        var first = TfxTerm.Op("ge", x[0], C(c[8].X));
        var second = TfxTerm.Op("ge", x[0], C(c[9].X));
        return Lanes(i => TfxTerm.Op("movc", second, upper, TfxTerm.Op("movc", first, lower, previous[i])));
    }

    /** Four-knot gradient of x, giving one channel per lane. */
    public static TfxTerm[] Gradient(TfxTerm[] x, Vector4[] c)
    {
        var knots = c[5];
        var next = new Vector4(knots.Y, knots.Z, knots.W, 1);
        var amount = Lanes(i => Ramp(TfxTerm.Sub(x[i], C(knots[i])), next[i] - knots[i]));
        return Lanes(channel => TfxTerm.Add(Sum4(Lanes(i => TfxTerm.Mul(C(c[channel + 1][i]), amount[i]))), C(c[0][channel])));
    }

    /** Eight-knot gradient of x. */
    public static TfxTerm[] Gradient8(TfxTerm[] x, Vector4[] c)
    {
        Vector4 lowKnots = c[9], highKnots = c[10];
        var lowNext = new Vector4(lowKnots.Y, lowKnots.Z, lowKnots.W, highKnots.X);
        var highNext = new Vector4(highKnots.Y, highKnots.Z, highKnots.W, 1);
        var low = Lanes(i => Ramp(TfxTerm.Sub(x[i], C(lowKnots[i])), lowNext[i] - lowKnots[i]));
        var high = Lanes(i => Ramp(TfxTerm.Sub(x[i], C(highKnots[i])), highNext[i] - highKnots[i]));
        return Lanes(channel => TfxTerm.Add(Sum4(Lanes(i => TfxTerm.Add(TfxTerm.Mul(C(c[channel + 1][i]), low[i]),
            TfxTerm.Mul(C(c[channel + 5][i]), high[i])))), C(c[0][channel])));
    }

    /** Paired reduction: (p1 + p3) + (p2 + p0). */
    private static TfxTerm Sum4(TfxTerm[] p) => TfxTerm.Add(TfxTerm.Add(p[1], p[3]), TfxTerm.Add(p[2], p[0]));

    /** The cubic of the lane whose knot is the last one at or below its input; zero below the first knot. */
    private static TfxTerm Segments(TfxTerm[] x, Vector4 a, Vector4 b, Vector4 c, Vector4 d, Vector4 knots)
    {
        var reached = Lanes(i => TfxTerm.Op("ge", x[i], C(knots[i])));
        return Sum4(Lanes(i =>
        {
            var value = TfxTerm.Add(TfxTerm.Mul(TfxTerm.Add(TfxTerm.Mul(C(a[i]), x[i]), C(b[i])), TfxTerm.Mul(x[i], x[i])),
                TfxTerm.Add(TfxTerm.Mul(C(c[i]), x[i]), C(d[i])));
            var select = i == 3 ? reached[3] : TfxTerm.Abs(TfxTerm.Sub(reached[i], reached[i + 1]));
            return TfxTerm.Mul(value, select);
        }));
    }

    /** Position in a segment, clamped to zero..one; a segment narrower than the minimum steps at its knot. */
    private static TfxTerm Ramp(TfxTerm offset, float width) => MathF.Abs(width) > TfxWaveMath.RampMinimumWidth
        ? TfxTerm.Saturate(TfxTerm.Op("div", offset, C(width))) : TfxTerm.Op("ge", offset, C(0));
}
