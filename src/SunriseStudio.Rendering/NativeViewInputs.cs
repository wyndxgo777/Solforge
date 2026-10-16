using System.Numerics;

namespace SunriseStudio.Rendering;

/** The two scalar fields and source flags must be supplied for the selected native pass. */
public sealed record NativeViewParameters(ushort Width, ushort Height, Matrix4x4 WorldToView,
    Matrix4x4 Projection, bool ReversePerspectiveDepth, uint SourceFlags100,
    float SourceScalar0C, float SourceScalar10);

/** Evaluated vertex and pixel constants of the view scope. */
public sealed record NativeViewScopeConstants(TfxEvaluation Vertex, TfxEvaluation Pixel);
/** Camera scope constants for the vertex and pixel stages; the pixel buffer has one extra row (PixelPass). */
public sealed record NativeCameraScopeConstants(Vector4[] Vertex, Vector4[] Pixel)
{
    /** PS row 13; only lanes X and Y are known, see NativeViewInputs.UnknownPixelWords. */
    public Vector4 PixelPass { get; init; }
    /** The fourteen PS rows in buffer order. */
    public Vector4[] PixelRows => [.. Pixel, PixelPass];
}

/** Builds the view and camera scope inputs and constants from a camera description. */
public static class NativeViewInputs
{
    public static IReadOnlyDictionary<uint, uint> UnknownVertexWords { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<uint, uint>(
            new Dictionary<uint, uint> { [36] = 0, [37] = 0, [38] = 0, [39] = 0 });
    /** PS row 13 lanes Z and W come from view fields with no known writer. */
    public static IReadOnlyDictionary<uint, uint> UnknownPixelWords { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<uint, uint>(
            new Dictionary<uint, uint> { [54] = 0, [55] = 0 });
    /** Lane Y of extern 2 vector 1: 1 for camera modes 0 and 2. Studio draws as mode 0. */
    public const float CameraModeZeroLaneY = 1;
    /** Matrices use row-vector storage; scope bytecode chooses the distinct VS and PS layouts. */
    public static TfxInputs Create(NativeViewParameters parameters)
    {
        var inputs = new TfxInputs();
        Apply(inputs, parameters);
        return inputs;
    }

    /** Only fields produced from these explicit camera values enter extern 2. */
    public static void Apply(TfxInputs inputs, NativeViewParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!float.IsFinite(parameters.SourceScalar0C) || !float.IsFinite(parameters.SourceScalar10))
            throw new ArgumentException("Native view input contains a non-finite value.");
        bool reversed = ApplyCameraMatrices(inputs, parameters.Width, parameters.Height,
            parameters.WorldToView, parameters.Projection, parameters.ReversePerspectiveDepth);
        inputs.Vectors[new(2, 1)] = new(reversed ? 0 : 1,
            (parameters.SourceFlags100 & ~2u) == 0 ? 1 : 0,
            parameters.SourceScalar0C, parameters.SourceScalar10);
    }

    /** Camera matrices only; extern 2 vector 1 stays unsupplied. */
    public static TfxInputs CreateCameraInputs(ushort width, ushort height, Matrix4x4 worldToView,
        Matrix4x4 projection, bool reversePerspectiveDepth = false)
    {
        var inputs = new TfxInputs();
        ApplyCameraMatrices(inputs, width, height, worldToView, projection, reversePerspectiveDepth);
        return inputs;
    }

    /** Camera-only inputs deliberately omit native pass flags and scalar fields. */
    private static bool ApplyCameraMatrices(TfxInputs inputs, ushort width, ushort height,
        Matrix4x4 view, Matrix4x4 projection, bool reversePerspectiveDepth)
    {
        if (width == 0 || height == 0) throw new ArgumentException("Native view dimensions must be positive.");
        if (!Finite(view) || !Finite(projection)) throw new ArgumentException("Native view matrix is not finite.");
        bool reversed = projection.M44 == 0 && reversePerspectiveDepth;
        if (reversed)
            projection = NativeMatrixMath.Multiply(projection, new Matrix4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, 0, 0, 1, 1));
        var inverseView = Invert(view);
        var inverseProjection = Invert(projection);
        var viewProjection = NativeMatrixMath.Multiply(view, projection);
        var inverseViewProjection = Invert(viewProjection);
        var viewRotation = WithoutTranslation(view);
        var inverseViewRotation = WithoutTranslation(inverseView);
        var pixelToClip = new Matrix4x4(2f / width, 0, 0, 0,
            0, -2f / height, 0, 0, 0, 0, 1, 0, -1, 1, 0, 1);
        var pixelToWorld = NativeMatrixMath.Multiply(pixelToClip, inverseViewProjection);
        inputs.Floats[new(2, 0)] = width;
        inputs.Floats[new(2, 1)] = height;
        inputs.Matrices[new(2, 2)] = view;
        inputs.Matrices[new(2, 6)] = projection;
        inputs.Matrices[new(2, 10)] = inverseView;
        inputs.Matrices[new(2, 14)] = inverseProjection;
        inputs.Matrices[new(2, 18)] = viewProjection;
        inputs.Matrices[new(2, 22)] = inverseViewProjection;
        inputs.Matrices[new(2, 26)] = pixelToWorld;
        inputs.Matrices[new(2, 30)] = NativeMatrixMath.Multiply(pixelToWorld, view);
        var uvToClip = new Matrix4x4(2, 0, 0, 0, 0, -2, 0, 0, 0, 0, 1, 0, -1, 1, 0, 1);
        inputs.Matrices[new(2, 34)] = NativeMatrixMath.Multiply(uvToClip, inverseViewProjection);
        inputs.Matrices[new(2, 38)] = NativeMatrixMath.Multiply(pixelToClip,
            NativeMatrixMath.Multiply(inverseProjection, inverseViewRotation));
        inputs.Matrices[new(2, 50)] = viewRotation;
        return reversed;
    }

    /** The authored view scope must retain its native name, index, and constant slots. */
    public static NativeViewScopeConstants EvaluateScope(GameMaterialReader reader,
        NativeViewParameters parameters, uint scopeTag = 0x80B356A5) =>
        EvaluateScope(reader.ReadScopeExecution(scopeTag), parameters);

    /** Runs the scope's vertex and pixel TFX programs; throws if an input they read is missing. */
    public static NativeViewScopeConstants EvaluateScope(GameScopeExecution scope, NativeViewParameters parameters)
    {
        ValidateScope(scope);
        var inputs = Create(parameters);
        var vertex = TfxEvaluator.Evaluate(scope.Vertex.Data, inputs);
        var pixel = TfxEvaluator.Evaluate(scope.Pixel.Data, inputs);
        vertex.RequireComplete(); pixel.RequireComplete();
        return new(vertex, pixel);
    }

    /** VS row 9 stays unknown; PS row 13 carries only lanes X and Y of extern 2 vector 1. */
    public static NativeCameraScopeConstants EvaluateCameraScope(GameScopeExecution scope,
        ushort width, ushort height, Matrix4x4 worldToView, Matrix4x4 projection, bool reversePerspectiveDepth = false)
    {
        ValidateScope(scope);
        var inputs = new TfxInputs();
        bool reversed = ApplyCameraMatrices(inputs, width, height, worldToView, projection, reversePerspectiveDepth);
        Vector4[] KnownRows(MaterialStageData stage, string expectedProgram, byte unknownRow)
        {
            byte[] expected = Convert.FromHexString(expectedProgram);
            if (!stage.TfxBytecode.AsSpan().SequenceEqual(expected) || stage.ConstantTemplate.Length != 14)
                throw new NotSupportedException("Partial view scope program changed.");
            byte[] copy = [0x3D, 2, 1, 0x43, unknownRow];
            int offset = expected.AsSpan().IndexOf(copy);
            var reduced = stage with { TfxBytecode = [.. expected.AsSpan(0, offset), .. expected.AsSpan(offset + copy.Length)] };
            var result = TfxEvaluator.Evaluate(reduced, inputs);
            result.RequireComplete();
            result.Constants[unknownRow] = default;
            return result.Constants;
        }
        var vertex = KnownRows(scope.Vertex.Data,
            "3E021244003E020A44043C02003C02010C34003C02003C02010C040D43083D020143093E0206440A", 9);
        var pixel = KnownRows(scope.Pixel.Data,
            "3E021244003E020A44043E021E44083C02003C02010C34003C02003C02010C040D430C3D0201430D", 13);
        return new(vertex, pixel[..13]) { PixelPass = new(reversed ? 0 : 1, CameraModeZeroLaneY, 0, 0) };
    }

    private static void ValidateScope(GameScopeExecution scope)
    {
        if (scope.Name != "view" || scope.ScopeIndex != 1
            || scope.Vertex.ConstantBufferSlot != 12 || scope.Pixel.ConstantBufferSlot != 12)
            throw new NotSupportedException("View scope identity or constant slots changed.");
    }

    private static Matrix4x4 WithoutTranslation(Matrix4x4 matrix)
    { matrix.M41 = matrix.M42 = matrix.M43 = 0; matrix.M44 = 1; return matrix; }

    private static Matrix4x4 Invert(Matrix4x4 matrix)
    {
        if (!Matrix4x4.Invert(matrix, out var inverse) || !Finite(inverse))
            throw new ArgumentException("Native view matrix is singular.");
        return inverse;
    }

    private static bool Finite(Matrix4x4 matrix) =>
        float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) && float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14)
        && float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) && float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24)
        && float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) && float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34)
        && float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) && float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);
}
