using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Rendering;

/**
 * One light table row that draws in stage 4, as 0x7FF742E74EF0 reads it.
 * @param Transform Light to world, from the row's world QTS.
 * @param VolumeTransform Row +0x20: the unit cube to light space.
 * @param FadeSphere Row +0x60: the sphere the distance fade measures from, centre and radius in light space.
 * @param Sphere Row +0x70: the sphere that decides whether the camera is inside the light.
 * @param Scale The QTS scale; extern 4 float 67.
 * @param Enable Row +0x8C: the parameter program whose four outputs must not all be zero or less.
 */
public sealed record NativeLocalLight(uint MaterialTag, Matrix4x4 Transform, Matrix4x4 VolumeTransform, Vector4 FadeSphere,
    Vector4 Sphere, float Scale, uint BubbleHash, NativeLightProgram? Enable);

/** A light parameter program (class 0x808071DC): its TFX bytecode, constants and four output rows. */
public sealed record NativeLightProgram(uint Tag, byte[] Bytecode, Vector4[] Constants, Vector4[] Outputs);

/**
 * Draws local lights as deferred_lights_renderer does (0x7FF742E757A0, 0x7FF742E74EF0): each row's own material on
 * the cube volume of 0x7FF742DD4EE0, added into the diffuse and specular lighting targets.
 */
public sealed class NativeLocalLights : IDisposable
{
    public const string Policy = "Local lights follow deferred_lights_renderer (0x7FF742E74EF0): every stage-4 row draws its"
        + " material on the 36-index cube of 0x7FF742DD4EE0 through the row's volume matrix, with blend 2 into the lighting"
        + " targets. A row is skipped when its +0x8C program outputs nothing above zero, and fades out between 28 and 30"
        + " radii of its +0x60 sphere. A camera inside the +0x70 sphere draws back faces (depth 16, rasterizer 3), outside"
        + " front faces (depth 15, rasterizer 2). The viewer skips the stencil volume prepass (technique 92) the game runs"
        + " within 10 radii; the light's own falloff bounds it. This path has no light shadows; the chunked_lights path"
        + " (0x7FF742E75BE0), which can bind one, is not run. A material that reads an input the pass lacks is refused.";
    // 0x7FF742EE43A0 sets the fade range (frame +0x37F50, +0x37F54) and the stencil distance (+0x37F58), in radii.
    private const float FadeEnd = 30, FadeStart = 28;
    // The lighting pass word with blend byte 0x82 (0x7FF742E75BE0); 0x7FF742EB81D0 forces depth and rasterizer.
    private const uint PassStates = 0x81819082;
    private const uint InsideStates = 0x00839000, OutsideStates = 0x00828F00;
    // xmmword_7FF743C91060: nine vertices; vertex 0 is unused.
    private static readonly float[] CubeVertices =
    [
        0, 0, 0, -1, -1, -1, -1, 1, -1, 1, 1, -1, 1, -1, -1, -1, -1, 1, 1, -1, 1, 1, 1, 1, -1, 1, 1
    ];
    // xmmword_7FF743C91010: twelve triangles.
    private static readonly short[] CubeIndices =
    [
        2, 1, 3, 4, 3, 1, 6, 5, 7, 8, 7, 5, 4, 1, 6, 5, 6, 1, 3, 4, 7, 6, 7, 4, 2, 3, 8, 7, 8, 3, 1, 2, 5, 8, 5, 2
    ];

    private sealed class Material(MaterialExecutionData execution, NativeMaterialPipeline inside, NativeMaterialPipeline outside,
        NativeConstantBuffer vertex, NativeConstantBuffer pixel)
    {
        public MaterialExecutionData Execution { get; } = execution;
        public NativeMaterialPipeline Inside { get; } = inside;
        public NativeMaterialPipeline Outside { get; } = outside;
        public NativeConstantBuffer Vertex { get; } = vertex;
        public NativeConstantBuffer Pixel { get; } = pixel;
        public NativeDrawBindings Bindings { get; } = new();
    }

    private readonly Dx11Renderer renderer;
    private readonly GameMaterialReader reader;
    private readonly NativeRenderStateCatalog states;
    private readonly TfxParameterLookup parameters;
    private readonly Func<uint, ID3D11ShaderResourceView?> textures;
    private readonly List<IDisposable> owned = [];
    private readonly Dictionary<uint, Material> materials = [];
    private readonly Dictionary<uint, string> refused = [];
    private readonly NativeGpuMesh cube;
    private readonly NativeConstantBuffer pixelView, frame;
    private readonly ID3D11SamplerState sampler;
    private readonly TfxResource thirdToken = new("gbuffer_third", ShaderResourceViewDimension.Texture2D);
    private readonly TfxResource normalToken = new("gbuffer_normal_read", ShaderResourceViewDimension.Texture2D);
    private readonly TfxResource depthToken = new("depth_stencil_read", ShaderResourceViewDimension.Texture2D);
    private double frameSeconds = double.NaN;
    private bool disposed;

    /** Lights drawn in the last frame. */
    public int LastDrawCount { get; private set; }
    /** Lights outside the view in the last frame. */
    public int LastCulledCount { get; private set; }
    /** Lights past the distance fade in the last frame. */
    public int LastFadedCount { get; private set; }
    /** Lights whose +0x8C program disabled them in the last frame. */
    public int LastDisabledCount { get; private set; }
    /** Enable programs that read an input the viewer lacks; their lights draw. */
    public IReadOnlySet<uint> UnevaluatedPrograms => unevaluated;
    private readonly HashSet<uint> unevaluated = [];
    /** Materials refused, with the reason; their lights are not drawn. */
    public IReadOnlyDictionary<uint, string> RefusedMaterials => refused;

    /** @param textures The uploaded texture of a package tag; null when it was not decoded. */
    public NativeLocalLights(Dx11Renderer renderer, GameMaterialReader reader, NativeRenderStateCatalog states,
        TfxParameterLookup parameters, Func<uint, ID3D11ShaderResourceView?> textures)
    {
        this.renderer = renderer; this.reader = reader; this.states = states; this.parameters = parameters;
        this.textures = textures;
        try
        {
            cube = Own(renderer.CreateNativeMesh([new(MemoryMarshal.AsBytes(CubeVertices.AsSpan()).ToArray(), 12)],
                MemoryMarshal.AsBytes(CubeIndices.AsSpan()), Format.R16_UInt, 0, (uint)CubeIndices.Length, PrimitiveTopology.TriangleList));
            pixelView = Own(renderer.CreateNativeConstantBuffer(new byte[13 * 16]));
            var initial = CreateFrame(0);
            frame = Own(renderer.CreateNativeConstantBuffer(MemoryMarshal.AsBytes(initial.Vectors.AsSpan()), initial.KnownWordMasks));
            frameSeconds = 0;
            // The lighting pass binds point-clamp sampler 0, as for technique 37.
            sampler = Own(renderer.Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp)));
        }
        catch { Dispose(); throw; }
    }

    private T Own<T>(T item) where T : IDisposable { owned.Add(item); return item; }

    private static NativeFrameScopeConstants CreateFrame(double seconds) => NativeFrameScope.Create(new()
    { SimulationSeconds = seconds, FrameFlag425B8 = false, FrameFlag425B9 = false });

    /** One pipeline per depth and rasterizer choice; a material whose interface differs is refused. */
    private Material? Prepare(uint tag)
    {
        if (materials.TryGetValue(tag, out var ready)) return ready;
        if (refused.ContainsKey(tag)) return null;
        try
        {
            var execution = reader.ReadExecution(tag);
            var material = execution.Material;
            if (material.VertexShader is null || material.PixelShader is null || material.ComputeShader is not null)
                throw new NotSupportedException("A light material needs a vertex and a pixel shader.");
            NativeMaterialPipeline Create(uint forced)
            {
                var resolved = states.Resolve(PassStates, material.StateOverrides, forced, NativeDepthTable.Alternate);
                return Own(renderer.CreateNativePipeline(reader, material,
                    [new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0)],
                    resolved.Rasterizer, resolved.DepthStencil, resolved.Blend));
            }
            var result = new Material(execution, Create(InsideStates), Create(OutsideStates),
                Own(renderer.CreateNativeConstantBuffer(new byte[Math.Max(1, execution.Vertex.ConstantTemplate.Length) * 16])),
                Own(renderer.CreateNativeConstantBuffer(new byte[Math.Max(1, execution.Pixel.ConstantTemplate.Length) * 16])));
            result.Bindings.ConstantBuffers[new(GameShaderStage.Vertex, 0)] = result.Vertex;
            result.Bindings.ConstantBuffers[new(GameShaderStage.Pixel, 0)] = result.Pixel;
            result.Bindings.ConstantBuffers[new(GameShaderStage.Pixel, 12)] = pixelView;
            result.Bindings.ConstantBuffers[new(GameShaderStage.Pixel, 13)] = frame;
            result.Bindings.Samplers[new(GameShaderStage.Pixel, 0)] = sampler;
            foreach (var texture in material.Textures.Where(t => t.DeclaredByShader))
                result.Bindings.Resources[new(texture.Stage, texture.Slot)] = textures(texture.TextureTag)
                    ?? throw new NotSupportedException($"Texture 0x{texture.TextureTag:X8} is not uploaded.");
            materials.Add(tag, result);
            return result;
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or ArgumentException
            or KeyNotFoundException or IOException or SharpGen.Runtime.SharpGenException)
        {
            refused[tag] = error.Message;
            return null;
        }
    }

    /**
     * Adds every light whose bounding sphere meets the view into the lighting targets.
     * @param targets The lighting targets after the global lighting pass: diffuse, then specular.
     * @param cameraPosition World position of the camera; extern 4 positions are relative to it.
     */
    public void Render(IReadOnlyList<NativeLocalLight> lights, NativeAmbientLightingInputs inputs, NativeRenderTargets targets,
        Vector3 cameraPosition, float nearPlane)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        LastDrawCount = 0; LastCulledCount = 0; LastFadedCount = 0; LastDisabledCount = 0;
        if (targets.Textures.Count < 2) throw new ArgumentException("Local lights need the diffuse and specular targets.");
        renderer.UpdateNativeConstantBuffer(pixelView, MemoryMarshal.AsBytes(inputs.Camera.Pixel.AsSpan()));
        if (frameSeconds != inputs.SimulationSeconds)
        {
            var constants = CreateFrame(inputs.SimulationSeconds);
            renderer.UpdateNativeConstantBuffer(frame, MemoryMarshal.AsBytes(constants.Vectors.AsSpan()));
            frameSeconds = inputs.SimulationSeconds;
        }
        var viewProjection = inputs.WorldToView * inputs.Projection;
        var planes = FrustumPlanes(viewProjection);
        var toCamera = Matrix4x4.CreateTranslation(-cameraPosition);
        var views = new Dictionary<TfxResource, ID3D11ShaderResourceView>
        {
            [thirdToken] = inputs.Third.View, [normalToken] = inputs.NormalRead.View, [depthToken] = inputs.DepthRead
        };
        var camera = NativeViewInputs.CreateCameraInputs(checked((ushort)targets.Width), checked((ushort)targets.Height),
            inputs.WorldToView, inputs.Projection);
        try
        {
            foreach (var light in lights)
            {
                var (center, radius) = WorldSphere(light.Sphere, light.Transform);
                if (Outside(planes, center, radius)) { LastCulledCount++; continue; }
                // 0x7FF742E86E50: the distance in radii of the +0x60 sphere, at least one.
                var (fadeCenter, fadeRadius) = WorldSphere(light.FadeSphere, light.Transform);
                float distance = Vector3.Distance(cameraPosition, fadeCenter) / MathF.Max(fadeRadius, 1);
                float fade = Math.Clamp((FadeEnd - distance) / (FadeEnd - FadeStart), 0, 1);
                if (fade <= 0) { LastFadedCount++; continue; }
                if (!Enabled(light.Enable, inputs.SimulationSeconds, inputs.TimeOfDay)) { LastDisabledCount++; continue; }
                if (Prepare(light.MaterialTag) is not { } material) continue;
                // 0x7FF742EB7D20: the camera is inside when the near plane reaches the +0x70 sphere.
                bool inside = nearPlane >= Vector3.Distance(cameraPosition, center) - radius;
                // Extern 1 holds the frame clock; extern 2 the camera.
                var tfx = NativeFrameInputs.Create(inputs.SimulationSeconds, timeOfDay: inputs.TimeOfDay);
                foreach (var item in camera.Matrices) tfx.Matrices[item.Key] = item.Value;
                foreach (var item in camera.Floats) tfx.Floats[item.Key] = item.Value;
                foreach (var item in camera.Vectors) tfx.Vectors[item.Key] = item.Value;
                tfx.Parameters = parameters;
                tfx.Floats[new(1, 7)] = MathF.Pow(2, inputs.ExposureEv);
                tfx.Resources[new(3, 7)] = depthToken;
                tfx.Resources[new(3, 10)] = normalToken;
                tfx.Resources[new(3, 11)] = thirdToken;
                ApplyExtern4(tfx, light, toCamera, inputs.WorldToView, fade);
                tfx.Matrices[new(15, 0)] = light.VolumeTransform * light.Transform * viewProjection;
                TfxEvaluation vertex, pixel;
                try
                {
                    vertex = TfxEvaluator.Evaluate(material.Execution.Vertex, tfx);
                    pixel = TfxEvaluator.Evaluate(material.Execution.Pixel, tfx);
                    vertex.RequireComplete(); pixel.RequireComplete();
                }
                catch (Exception error) when (error is InvalidOperationException or NotSupportedException or InvalidDataException)
                {
                    refused[light.MaterialTag] = error.Message;
                    materials.Remove(light.MaterialTag);
                    continue;
                }
                renderer.UpdateNativeConstantBuffer(material.Vertex, MemoryMarshal.AsBytes(vertex.Constants.AsSpan()));
                renderer.UpdateNativeConstantBuffer(material.Pixel, MemoryMarshal.AsBytes(pixel.Constants.AsSpan()));
                material.Bindings.MaterialEvaluations[GameShaderStage.Vertex] = vertex;
                material.Bindings.MaterialEvaluations[GameShaderStage.Pixel] = pixel;
                material.Bindings.ApplyTfxResources(pixel, views);
                foreach (var item in pixel.Samplers)
                    if (!material.Bindings.Samplers.ContainsKey(item.Key))
                        material.Bindings.Samplers[item.Key] = Own(renderer.Device.CreateSamplerState(item.Value.Description));
                var pipeline = inside ? material.Inside : material.Outside;
                // A material reading a resource this pass does not supply, such as a light shadow map, is refused.
                if (pipeline.Validate(material.Bindings, targets) is { Count: > 0 } missing)
                {
                    refused[light.MaterialTag] = string.Join(" ", missing);
                    materials.Remove(light.MaterialTag);
                    continue;
                }
                renderer.DrawNative(pipeline, cube, material.Bindings, targets);
                LastDrawCount++;
            }
        }
        finally
        {
            for (uint slot = 0; slot < 4; slot++) renderer.Context.PSSetShaderResource(slot, null!);
            renderer.RestoreMainTarget();
        }
    }

    /**
     * Extern 4 as 0x7FF742E74EF0 writes it. Rows 0 to 3 take the light to view space, 4 to 7 hold the transposed
     * inverse of rows 12 to 15, 8 to 11 the inverse of the camera-relative volume, 12 to 15 the light to
     * camera-relative world. Floats 64 to 68: the distance fade, zero, zero, the QTS scale, one.
     */
    private static void ApplyExtern4(TfxInputs tfx, NativeLocalLight light, Matrix4x4 toCamera, Matrix4x4 worldToView, float fade)
    {
        var relative = light.Transform * toCamera;
        Matrix4x4.Invert(relative, out var fromRelative);
        Matrix4x4.Invert(light.VolumeTransform * relative, out var fromVolume);
        tfx.Matrices[new(4, 0)] = light.Transform * worldToView;
        tfx.Matrices[new(4, 4)] = Matrix4x4.Transpose(fromRelative);
        tfx.Matrices[new(4, 8)] = fromVolume;
        tfx.Matrices[new(4, 12)] = relative;
        tfx.Floats[new(4, 64)] = fade;
        tfx.Floats[new(4, 65)] = 0;
        tfx.Floats[new(4, 66)] = 0;
        tfx.Floats[new(4, 67)] = light.Scale;
        tfx.Floats[new(4, 68)] = 1;
    }

    /** 0x7FF742059520 and 0x7FF742059430: the centre goes through the light matrix; the radius grows by its longest axis. */
    private static (Vector3 Center, float Radius) WorldSphere(Vector4 sphere, Matrix4x4 t)
    {
        float axis = MathF.Sqrt(MathF.Max(new Vector3(t.M11, t.M12, t.M13).LengthSquared(),
            MathF.Max(new Vector3(t.M21, t.M22, t.M23).LengthSquared(), new Vector3(t.M31, t.M32, t.M33).LengthSquared())));
        return (Vector3.Transform(new Vector3(sphere.X, sphere.Y, sphere.Z), t), sphere.W * axis);
    }

    /**
     * 0x7FF742E50670: the row's +0x8C program runs with extern 4 floats 66 = 0 and 68 = 1; the light draws when an
     * output lane is above zero. A program that reads an input the viewer lacks counts as enabled.
     */
    private bool Enabled(NativeLightProgram? program, double seconds, float timeOfDay)
    {
        if (program is null) return true;
        try
        {
            var stage = new MaterialStageData(GameShaderStage.Pixel, null, program.Bytecode, program.Constants, program.Outputs, 0, []);
            var tfx = NativeFrameInputs.Create(seconds, timeOfDay: timeOfDay);
            tfx.Parameters = parameters;
            tfx.Floats[new(4, 66)] = 0;
            tfx.Floats[new(4, 68)] = 1;
            var result = TfxEvaluator.Evaluate(stage, tfx);
            result.RequireComplete();
            return result.Constants.Take(4).Any(row => row.X > 0 || row.Y > 0 || row.Z > 0 || row.W > 0);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or InvalidDataException)
        {
            unevaluated.Add(program.Tag);
            return true;
        }
    }

    /** Planes as (normal, distance) with the inside positive, for row-vector clip = world * viewProjection. */
    private static Vector4[] FrustumPlanes(Matrix4x4 m)
    {
        var c1 = new Vector4(m.M11, m.M21, m.M31, m.M41); var c2 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c3 = new Vector4(m.M13, m.M23, m.M33, m.M43); var c4 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        Vector4[] planes = [c4 + c1, c4 - c1, c4 + c2, c4 - c2, c3, c4 - c3];
        for (int i = 0; i < planes.Length; i++)
        {
            float length = new Vector3(planes[i].X, planes[i].Y, planes[i].Z).Length();
            if (length > 0) planes[i] /= length;
        }
        return planes;
    }

    private static bool Outside(Vector4[] planes, Vector3 center, float radius)
    {
        foreach (var plane in planes)
            if (Vector3.Dot(new(plane.X, plane.Y, plane.Z), center) + plane.W < -radius) return true;
        return false;
    }

    /** Releases the GPU resources this pass owns. */
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        owned.Clear();
    }
}
