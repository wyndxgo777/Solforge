using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Rendering;
using Vortice.DXGI;

/** Checks native cubemap lighting on owned cube colors: stencil coverage, box fades, interior lighting and rejection of foreground and outside samples. */
internal static class CubemapFixture
{
    /** Owned cube colors and geometry test stencil coverage, box fades, and foreground rejection. */
    public static void Run(Dx11Renderer renderer, GameMaterialReader reader, TfxParameterLookup parameters)
    {
        using var lighting = new NativeCubemapLighting(renderer, reader, new(), parameters);
        byte[] source = new byte[0x1A0];
        void F(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(source.AsSpan(offset), value);
        void I(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(offset), value);
        void V(int offset, Vector4 value) => MemoryMarshal.AsBytes(new[] { value }.AsSpan()).CopyTo(source.AsSpan(offset));
        var model = Matrix4x4.CreateTranslation(0, 0, -3);
        MemoryMarshal.AsBytes(new[] { model }.AsSpan()).CopyTo(source.AsSpan(0xB0));
        MemoryMarshal.AsBytes(new[] { model }.AsSpan()).CopyTo(source.AsSpan(0xF0));
        V(0x10, new(1, 1, 1, 0)); V(0x40, new(.25f, .25f, .25f, 0)); V(0x50, new(.25f, .25f, .25f, 0));
        F(0x30, 1); I(0x130, 1); I(0x134, 1); I(0x138, 1); F(0x13C, 1); V(0x140, new(1, .5f, .5f, 0));
        var binding = NativeCubemapBinding.Create(source, 1, 255, 2);
        var uploads = new List<TextureSubresourceUpload>();
        for (int face = 0; face < 6; face++)
            for (int mip = 0; mip < 2; mip++)
            {
                int width = 2 >> mip;
                var pixels = Enumerable.Repeat(new Vector4(.5f, .25f, .125f, 1), width * width).ToArray();
                uploads.Add(new(mip, face, MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray(), width * 16, width * width * 16));
            }
        using var cube = renderer.CreateTextureResource(2, 2, 1, 6, 2, Format.R32G32B32A32_Float, true, false, uploads);
        using var gbuffer = new NativeGBufferTargets(renderer, 64, 64);
        using var baseLighting = renderer.CreateNativeTargets(64, 64, Format.R11G11B10_Float, Format.R11G11B10_Float);
        renderer.ClearNativeTargets(baseLighting, Vector4.Zero, 1);
        GpuTexture Image(Vector4 value) => renderer.CreateTexture(64, 64, Format.R32G32B32A32_Float,
            MemoryMarshal.AsBytes(Enumerable.Repeat(value, 4096).ToArray().AsSpan()), 64 * 16);
        using var albedo = Image(new(.5f, .5f, .5f, 0));
        using var normal = Image(new(.5f, .5f, .95f, 1));
        using var third = Image(new(1, .5f, 0, 1));
        using var ao = Image(Vector4.One);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100);
        var scope = reader.ReadScopeExecution(0x80B356A5);
        Vector4[] Render(Vector3 eye, float planeZ)
        {
            var view = Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY);
            var camera = NativeViewInputs.EvaluateCameraScope(scope, 64, 64, view, projection);
            var clip = Vector4.Transform(new Vector4(0, 0, planeZ, 1), view * projection);
            gbuffer.Clear(Vector4.Zero, clip.Z / clip.W);
            var depth = gbuffer.Resolve().Depth;
            lighting.Render([new(binding, cube)], new(albedo, normal, third, depth), gbuffer.WriteTargets,
                baseLighting.Textures[0], baseLighting.Textures[1], ao, camera, view, projection, 0, 0, .1f);
            return renderer.ReadTexturePixels(lighting.Ibl!, [(32, 32), (21, 32), (1, 1)]);
        }
        var outside = Render(Vector3.Zero, -3);
        Console.WriteLine($"cubemap519 outside_samples={string.Join(';', outside)}");
        Check(outside[0].X > .01f && outside[0].X > outside[0].Y * 2, "Authored cube did not light the volume interior.");
        Check(outside[1].X < outside[0].X * .5f, "Authored box fade did not reduce the edge contribution.");
        Check(outside[2].X < 1e-6f, "Cubemap light leaked outside its volume.");
        var foreground = Render(Vector3.Zero, -1);
        Check(foreground[0].X < 1e-6f, "Cubemap stencil lit foreground geometry outside its volume.");
        var inside = Render(new(0, 0, -3), -3.5f);
        Check(inside[0].X > .01f, "Camera-inside cubemap stencil rejected its volume.");
        Console.WriteLine($"cubemap519 center={outside[0]} edge={outside[1]} outside={outside[2]} foreground={foreground[0]} inside={inside[0]}");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
