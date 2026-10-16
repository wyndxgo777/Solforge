// Console test runner for the asset surface: material execution data, display names and shader texture slots must survive reading without guessing.
using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;
using Vortice.D3DCompiler;

string temporary = Path.Combine(Path.GetTempPath(), "sunrise-surfaces-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(temporary, "packages"));
try
{
    const uint first = 0x80A00000;
    byte[] pixel = Compiler.Compile("""
        Texture2D<float4> image : register(t3);
        SamplerState imageSampler : register(s2);
        float4 main(float2 uv:TEXCOORD0):SV_TARGET { return image.Sample(imageSampler, uv); }
        """, "main", "SurfaceFixture", "ps_5_0", ShaderFlags.OptimizationLevel3).ToArray();
    byte[] vertex = Compiler.Compile("float4 main(float3 p:POSITION):SV_POSITION{return float4(p,1);}",
        "main", "SurfaceFixture", "vs_5_0", ShaderFlags.OptimizationLevel3).ToArray();
    byte[] geometry = Compiler.Compile("""
        struct Vertex { float4 position:SV_POSITION; };
        [maxvertexcount(3)] void main(triangle Vertex input[3], inout TriangleStream<Vertex> output)
        { output.Append(input[0]); output.Append(input[1]); output.Append(input[2]); }
        """, "main", "SurfaceFixture", "gs_5_0", ShaderFlags.OptimizationLevel3).ToArray();
    byte[] texture = new byte[40]; U(texture, 0, 4); U(texture, 4, 28); S(texture, 12, 0xCAFE);
    foreach (int p in new[] { 14, 16, 18, 20 }) S(texture, p, 1);
    texture[23] = 1; U(texture, 36, uint.MaxValue);
    byte[] material = new byte[1088]; Q(material, 0, material.Length); U(material, 0x48, first + 5); U(material, 0x2C8, first + 2);
    Array(material, 0x2D0, 1040, 1, 0x80807211); U(material, 1056, 3); U(material, 1060, first);
    PackageWriter.WriteNew(Path.Combine(temporary, "packages", "w64_surface_fixture_0100_0.pkg"), 0x100,
    [
        new(first + 1, (32 << 9) | (1 << 6), texture, "fixture_texture"),
        new(uint.MaxValue, (40 << 9) | (1 << 6), [32, 96, 192, 255]),
        new(first + 3, 33 << 9, ShaderHeader(pixel)), new(uint.MaxValue, 41 << 9, pixel),
        new(0x808071E8, 8 << 9, material, "fixture_material"),
        new(first + 6, (33 << 9) | (1 << 6), ShaderHeader(vertex)), new(uint.MaxValue, (41 << 9) | (1 << 6), vertex),
        new(first + 8, (33 << 9) | (2 << 6), ShaderHeader(geometry)), new(uint.MaxValue, (41 << 9) | (2 << 6), geometry)
    ]);
    using var catalog = PackageCatalog.Open(temporary);
    var shared = new Dictionary<uint, TextureData> { [first] = new TextureDataReader(catalog).Read(first) };
    var surface = AssetSurface.Read(catalog, first + 4, nameResolver: tag => tag == first + 4 ? "Named material" : null,
        decodedTextures: shared);
    Check(surface.Materials.Single().Name == "Named material" && surface.Materials[0].Execution is not null,
        "Material surface lost its execution data or display name.");
    Check(surface.Materials[0].Material.Textures.Single() is { Slot: 3, Stage: GameShaderStage.Pixel, DeclaredByShader: true },
        "Shader texture slot was guessed or lost.");
    Check(surface.Shaders.Select(x => x.Profile).Order().SequenceEqual(new[] { "ps_5_0", "vs_5_0" }), "Shader profiles differ.");
    Check(surface.Shaders.All(x => x.Disassembly.Length > 0 && x.Sha256.Length == 64 && x.Bytecode.Length > 32),
        "Shader details are incomplete.");
    Check(ReferenceEquals(surface.Textures[first], shared[first]), "Texture payload reuse copied its bytes.");
    shared.Clear();
    Check(surface.Textures.Count == 1 && surface.Textures[first].Subresources[0].Data.SequenceEqual(new byte[] { 32, 96, 192, 255 }),
        "Native-plan cache cleanup removed the surface texture.");
    Check(AssetSurface.Read(catalog, first + 1).Textures.ContainsKey(first), "Texture payload did not resolve its header.");
    Check(AssetSurface.Read(catalog, first + 3).Shaders.Single().Profile == "ps_5_0", "Shader payload did not resolve its header.");
    Check(AssetSurface.Read(catalog, first + 7).Shaders.Single().Profile == "gs_5_0", "Geometry shader was mislabeled.");
    var preparedOnly = AssetSurface.Read(catalog, first + 7,
        decodedTextures: new Dictionary<uint, TextureData> { [first] = surface.Textures[first] });
    Check(preparedOnly.Textures.Count == 1 && ReferenceEquals(preparedOnly.Textures[first], surface.Textures[first]),
        "A prepared texture outside material bindings was omitted.");
    var terrain = new SceneDocument();
    foreach (uint dye in new[] { first, first + 4 })
        terrain.Instances.Add(new MeshInstance { Mesh = new MeshData { Native = new NativeMeshData
        { Kind = NativeMeshKind.Terrain, VertexStreams = [], IndexData = [], SourceHeader = [], TerrainDyeTexture = dye } } });
    var terrainSurface = AssetSurface.Read(catalog, first + 7, terrain);
    Check(terrainSurface.Textures.Count == 1 && terrainSurface.Textures.ContainsKey(first)
        && terrainSurface.TextureErrors.Count == 0, "Terrain dye texture discovery missed a valid tag or admitted a non-texture.");
    Reject<OperationCanceledException>(() => AssetSurface.Read(catalog, first, token: new CancellationToken(true)));
    var mesh = new MeshData { Native = new NativeMeshData { Kind = NativeMeshKind.Static, VertexStreams = [],
        IndexData = [], SourceHeader = [], PositionScale = new(2), PositionOffset = new(1, 2, 3, 0), UvTransform = new(4, 4, 5, 6) } };
    var source = new MeshInstance { Mesh = mesh, Transform = Matrix4x4.CreateTranslation(7, 8, 9) };
    var binding = NativeAssetBindings.CreateStatic(source);
    Check(binding.UseSingleScalar && binding.ScalarBuffer.Data.All(x => x == 255), "Isolated AO is not explicitly neutral.");
    Check(binding.InstanceConstants[0] == new Vector4(2, 0, 0, 8)
        && binding.InstanceConstants[1] == new Vector4(0, 2, 0, 10)
        && binding.InstanceConstants[2] == new Vector4(0, 0, 2, 12), "Isolated model transform differs from native CB11.");
    Reject<NotSupportedException>(() => NativeAssetBindings.CreateStatic(new MeshInstance { Mesh = mesh, InstanceSourceTag = first }));
    Console.WriteLine("pass surface=material_texture_shader profiles=ps_vs_gs roles=declared_registers textures=retained cancellation=verified isolated_bindings=verified");
    if (args.Length >= 2 && args[0] == "--game-root")
        Installed(args[1], args.Length >= 4 && args[2] == "--output" ? args[3] : Path.Combine(temporary, "isolated-native.png"));
}
finally
{
    if (Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(temporary, true);
}
return 0;

static void Installed(string gameRoot, string output)
{
    using var catalog = PackageCatalog.Open(gameRoot);
    var scene = new AssetSceneLoader(catalog).LoadMesh(0x80B9EEE1);
    var strict = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: long.MaxValue);
    Check(strict.PreparedPartCount == 0 && strict.Diagnostics.Any(x => x.Contains("48-byte instance row", StringComparison.Ordinal)),
        "Map preparation silently replaced missing authored context.");
    var plan = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: long.MaxValue, assetPreview: true);
    Check(plan.PreparedPartCount > 0 && plan.IsolatedPartCount == plan.PreparedPartCount, string.Join(';', plan.Diagnostics));
    var surface = AssetSurface.Read(catalog, scene.TagHash, scene, decodedTextures: plan.Textures);
    Check(surface.Materials.Count > 0 && surface.Textures.Count > 0 && surface.Shaders.Count >= 2,
        "Installed surface data is missing.");
    int textureCount = surface.Textures.Count;
    using var renderer = new Dx11Renderer(IntPtr.Zero, 640, 480, true);
    using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
    viewport.SetScene(scene);
    viewport.SetNativeScene(plan);
    Check(viewport.NativePartCount > 0, string.Join(';', viewport.NativeDiagnostics));
    var image = viewport.RenderImage(640, 480, 1.5);
    Check(viewport.UseGameShaders && viewport.NativeRenderer!.LastFrame.DrawnParts > 0,
        viewport.NativeError ?? "Native rendering did not draw the asset.");
    Check(surface.Textures.Count == textureCount, "Native upload removed inspector textures.");
    var points = Enumerable.Range(1, 23).SelectMany(y => Enumerable.Range(1, 31)
        .Select(x => (X: x * 20, Y: y * 20))).ToArray();
    var depth = renderer.ReadNativeDepthPixels(viewport.NativeRenderer!.Targets, points);
    Check(depth.Any(x => x.X > 0 && x.X < .9999f), "Native asset wrote no visible depth.");
    var albedo = renderer.ReadTexturePixels(viewport.NativeRenderer.Targets.Textures[0], points);
    Check(albedo.Any(x => x.X > .01f || x.Y > .01f || x.Z > .01f), "Native material wrote no G-buffer color.");
    var pixels = renderer.ReadTexturePixels(image, points);
    var hitPixels = pixels.Where((_, i) => depth[i].X > 0 && depth[i].X < .9999f).ToArray();
    var samples = viewport.NativeRenderer.SamplePixels(points).Where((_, i) => depth[i].X > 0 && depth[i].X < .9999f).ToArray();
    Console.WriteLine($"asset_lighting hits={hitPixels.Length} rgb_max={hitPixels.Max(x => Math.Max(x.X, Math.Max(x.Y, x.Z)))} albedo_max={samples.Max(x => Math.Max(x.Albedo.X, Math.Max(x.Albedo.Y, x.Albedo.Z)))} diffuse_max={samples.Max(x => Math.Max(x.Diffuse.X, Math.Max(x.Diffuse.Y, x.Diffuse.Z)))} stops={viewport.NativeRenderer.AppliedViewerStops} black={viewport.NativeRenderer.BlackFraction}");
    Check(hitPixels.Max(x => Math.Max(x.X, Math.Max(x.Y, x.Z))) > .4f
        && hitPixels.Select(x => MathF.Round(x.X + x.Y + x.Z, 2)).Distinct().Count() > 8,
        "The first ready asset frame lacks visible material detail.");
    Check(pixels.All(x => float.IsFinite(x.X + x.Y + x.Z))
        && pixels.Select(x => MathF.Round(x.X + x.Y + x.Z, 3)).Distinct().Count() > 8, "Native output has no material detail.");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    SaveTexture(renderer, image, output);
    var zeroAo = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: long.MaxValue, assetPreview: true);
    foreach (var scalar in zeroAo.Groups.SelectMany(x => x.Parts).Select(x => x.Scalars).OfType<NativeVertexStream>())
        System.Array.Clear(scalar.Data);
    viewport.SetNativeScene(zeroAo);
    viewport.RenderImage(640, 480, 1.5);
    var occluded = viewport.NativeRenderer!.SamplePixels(points).Where((_, i) => depth[i].X > 0 && depth[i].X < .9999f).ToArray();
    float oneLight = samples.Sum(x => x.Diffuse.X + x.Diffuse.Y + x.Diffuse.Z);
    float zeroLight = occluded.Sum(x => x.Diffuse.X + x.Diffuse.Y + x.Diffuse.Z);
    Check(oneLight > zeroLight + .0001f, "AO scalar one is not less occluded than zero for the fixture.");
    Console.WriteLine($"asset_ao scalar_one={oneLight} scalar_zero={zeroLight} polarity=verified");
    var geometry = catalog.Entries.FirstOrDefault(x => x.Type == 33 && x.SubType == 2);
    if (geometry is not null)
        Check(AssetSurface.Read(catalog, geometry.TagHash).Shaders.Single().Profile.StartsWith("gs_", StringComparison.Ordinal),
            "Installed geometry shader profile is wrong.");
    Console.WriteLine($"pass installed=isolated_static native_parts={viewport.NativePartCount} textures={surface.Textures.Count} shaders={surface.Shaders.Count} depth=verified gbuffer=verified screenshot={Path.GetFullPath(output)}");
}

static void SaveTexture(Dx11Renderer renderer, GpuTexture texture, string path)
{
    var points = Enumerable.Range(0, texture.Height).SelectMany(y => Enumerable.Range(0, texture.Width)
        .Select(x => (X: x, Y: y))).ToArray();
    var colors = renderer.ReadTexturePixels(texture, points);
    using var bitmap = new System.Drawing.Bitmap(texture.Width, texture.Height);
    for (int y = 0; y < texture.Height; y++)
        for (int x = 0; x < texture.Width; x++)
        {
            var color = colors[y * texture.Width + x];
            bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(255, Byte(color.X), Byte(color.Y), Byte(color.Z)));
        }
    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    static int Byte(float value) => (int)Math.Clamp(MathF.Round(value * 255), 0, 255);
}

static byte[] ShaderHeader(byte[] code) { var header = new byte[12]; Q(header, 0, 12); U(header, 8, (uint)code.Length); return header; }
static void Array(byte[] bytes, int field, int header, int count, uint type)
{ Q(bytes, field, count); Q(bytes, field + 8, header - field - 8); U(bytes, header - 4, 0x80809FBD); Q(bytes, header, count); U(bytes, header + 8, type); }
static void U(byte[] bytes, int p, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(p), value);
static void S(byte[] bytes, int p, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(p), value);
static void Q(byte[] bytes, int p, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(p), value);
static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
static void Reject<T>(Action action) where T : Exception
{ try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
