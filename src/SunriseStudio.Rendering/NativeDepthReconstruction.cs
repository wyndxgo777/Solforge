using System.Numerics;

namespace SunriseStudio.Rendering;

/** Depth-coefficient maths shared by TFX extern 3 and the frame scope constants. */
public static class NativeDepthReconstruction
{
    /** Extern 3 vector 0 and frame row 12 share this native depth-coefficient calculation. */
    public static Vector4 Create(NativeFrameDepthInputs source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!float.IsFinite(source.Source100) || !float.IsFinite(source.Source104))
            throw new ArgumentException("Depth reconstruction input is not finite.");
        float first = MathF.Max(0.0001f, source.SourceFlag623 ? source.Source104 : source.Source100);
        float second = MathF.Max(0.0001f, source.SourceFlag623 ? source.Source100 : source.Source104);
        return new(1 / first, (first - second) / (first * second), 0, 0);
    }

    /** Stores the coefficients in extern 3 vector 0. */
    public static void Apply(TfxInputs inputs, NativeFrameDepthInputs source) =>
        inputs.Vectors[new(3, 0)] = Create(source);
}
