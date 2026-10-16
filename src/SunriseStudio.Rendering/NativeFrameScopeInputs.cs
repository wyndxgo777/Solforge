using System.Numerics;

namespace SunriseStudio.Rendering;

/**
 * The two depth source scalars and the flag that swaps them, as read from the native frame state. This
 * file also builds the frame scope constants.
 */
public sealed record NativeFrameDepthInputs(float Source100, float Source104, bool SourceFlag623);

/** Missing runtime sources stay unknown; field offsets do not imply unverified purposes. */
public sealed record NativeFrameScopeInputs
{
    public double? SimulationSeconds { get; init; }
    public float? Extern04 { get; init; }
    public float? Extern14 { get; init; }
    public float? Extern1C { get; init; }
    public float? Extern28 { get; init; }
    public bool? FrameFlag425B8 { get; init; }
    public bool? FrameFlag425B9 { get; init; }
    public float? FrameScalar425BC { get; init; }
    public int? Global743C975A8 { get; init; }
    public int? Global744BB1774 { get; init; }
    public Vector2? TargetDimensions { get; init; }
    public NativeFrameDepthInputs? Depth { get; init; }
}

/** The 13 frame-scope vectors plus a mask of which 32-bit words are actually known. */
public sealed record NativeFrameScopeConstants(Vector4[] Vectors, IReadOnlyDictionary<uint, uint> KnownWordMasks);

/** Fills the frame-scope constant buffer from NativeFrameScopeInputs. */
public static class NativeFrameScope
{
    /** The native writer fills thirteen vectors and shares that allocation between VS and PS. */
    public static NativeFrameScopeConstants Create(NativeFrameScopeInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var vectors = new Vector4[13];
        var masks = Enumerable.Range(0, 52).ToDictionary(i => (uint)i, _ => 0u);
        void Set(int row, int component, float value)
        { vectors[row][component] = value; masks[(uint)(row * 4 + component)] = uint.MaxValue; }
        void SetVector(int row, Vector4 vector)
        { for (int component = 0; component < 4; component++) Set(row, component, vector[component]); }
        void Scalar(int row, int component, float? value)
        {
            if (!value.HasValue) return;
            if (!float.IsFinite(value.Value)) throw new ArgumentException("Frame scope input is not finite.");
            Set(row, component, value.Value);
        }
        void Gate(int row, int component, bool? enabled)
        {
            if (enabled == false) Set(row, component, 0);
            else if (enabled == true) Scalar(row, component, inputs.FrameScalar425BC);
        }
        if (inputs.SimulationSeconds is { } seconds)
            Set(0, 0, NativeFrameInputs.Create(seconds).Floats[new(1, 0)]);
        Scalar(0, 1, inputs.Extern04);
        Scalar(0, 2, inputs.Extern14);
        Set(0, 3, BitConverter.UInt32BitsToSingle(0x3C888889));
        Scalar(1, 0, inputs.Extern1C);
        Scalar(1, 2, inputs.Extern1C);
        Scalar(1, 3, inputs.Extern28);
        if (inputs.Extern28 is { } ratio) Set(1, 1, ratio * 16);
        if (inputs.Extern04 is { } phase)
        {
            SetVector(2, TfxWaveMath.Multiply(TfxWaveMath.Add(new(phase), new(60, 33.75f, 60, 33.75f)),
                new(0.958312451839447f, 1.2586991786956787f, 8.789122581481934f, 2.311534881591797f)));
            float wave = MathF.Sin(phase * 6);
            Set(6, 2, wave * 0.5f + 0.5f);
        }
        SetVector(3, new(0.5f));
        SetVector(4, new(1, 1, 0, 1));
        Set(5, 0, 0);
        Set(5, 1, BitConverter.UInt32BitsToSingle(uint.MaxValue));
        Set(5, 2, 512);
        Gate(5, 3, inputs.FrameFlag425B9);
        Gate(6, 0, inputs.FrameFlag425B8);
        Set(6, 1, 1); Set(6, 3, 0);
        SetVector(7, new(0.5f, 0.5f, 180, 0));
        SetVector(8, new(-1, 0, 0, 0));
        Set(9, 0, 0); Set(9, 1, 1);
        if (inputs.Global743C975A8 is { } first) Set(9, 2, first);
        if (inputs.Global744BB1774 is { } second) Set(9, 3, second);
        SetVector(10, Vector4.Zero); SetVector(11, Vector4.Zero);
        if (inputs.TargetDimensions is { } dimensions)
        { Scalar(12, 2, dimensions.X); Scalar(12, 3, dimensions.Y); }
        if (inputs.Depth is { } depth)
        {
            var coefficients = NativeDepthReconstruction.Create(depth);
            Set(12, 0, coefficients.X); Set(12, 1, coefficients.Y);
        }
        return new(vectors, new System.Collections.ObjectModel.ReadOnlyDictionary<uint, uint>(masks));
    }
}
