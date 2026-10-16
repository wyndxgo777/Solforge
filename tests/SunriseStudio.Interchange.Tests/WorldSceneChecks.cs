using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using Vortice.D3DCompiler;

/** Checks the world scene exporter: content-identical geometry is shared, material variants and markers survive, and unsupported entries are counted. */
internal static class WorldSceneChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(string parent, string? keep = null)
    {
        string root = Path.Combine(parent, "world-export-checks"), install = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_world_0100_0.pkg"), 0x100, [new(0x80800001, 8 << 9, [1])]);
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var mesh = Triangle(0x80A00000, 0, 3);
        var copy = Triangle(0x80A00001, uint.MaxValue, 4);
        var scene = new SceneDocument { TagHash = 0x80A00000, Name = "Shared transform fixture", UnsupportedCount = 2, DecodeErrorCount = 1 };
        Matrix4x4 shear = Matrix4x4.Identity; shear.M12 = .5f; shear.M31 = .25f; shear.M41 = 10;
        Matrix4x4[] transforms = [Matrix4x4.Identity, Matrix4x4.CreateScale(-2, 4, .5f) * Matrix4x4.CreateTranslation(4, 5, 6), shear];
        foreach (var transform in transforms)
        {
            var owner = new SceneObjectPlacement(0x80A00000, scene.Instances.Count / 2, transform);
            scene.Instances.Add(new() { Mesh = mesh, ObjectPlacement = owner, Transform = transform, Name = "Shared model", InitiallyVisible = true });
            scene.Instances.Add(new() { Mesh = copy, ObjectPlacement = owner,
                Transform = Matrix4x4.CreateTranslation(0, 2, 0) * transform, Name = "Shared model", InitiallyVisible = false,
                InitialVisibilityReason = "fixture hidden part" });
        }
        scene.Markers.Add(new() { Name = "Trigger marker", SourceTag = 0x80A00000, SourceOffset = 64,
            Position = new(2, 3, 4), Rotation = Quaternion.Identity, Extents = new(1, 2, 3), Layer = SceneLayer.Volumes });
        string output = keep is null ? Path.Combine(root, "export") : Path.GetFullPath(keep);
        var exported = WorldSceneExport.Export(catalog, scene, output, new(IncludeMaterials: false));
        var manifest = JsonSerializer.Deserialize<WorldSceneManifest>(File.ReadAllBytes(exported.ManifestPath))!;
        Check(manifest.Statistics.Parts == 6 && manifest.Statistics.Instances == 3 && manifest.Geometry.Count == 1,
            "World exporter did not share content-identical geometry.");
        Check(manifest.Geometry[0].Positions.Count == 3 && manifest.Materials.Count == 2 && manifest.Markers.Count == 1,
            "World exporter lost material variants or marker identity.");
        Check(manifest.Notices.Any(notice => notice.Status == "Partial source decode"
            && notice.Message == "Unsupported source entries: 2; decode errors: 1."),
            "Partial source counts were hidden when the diagnostics list was empty.");
        foreach (var buffer in manifest.Buffers)
        {
            byte[] data = File.ReadAllBytes(Path.Combine(output, buffer.Path));
            Check(data.LongLength == buffer.Length && Convert.ToHexString(SHA256.HashData(data)) == buffer.Sha256,
                "World shard length or streamed digest differs.");
        }
        Check(!Directory.Exists(Path.Combine(output, "native")) && !Directory.Exists(Path.Combine(output, "geometry"))
            && !File.ReadAllText(exported.ManifestPath).Contains("base64"), "Read-only export retained duplicate editable payloads.");
        var table = View(manifest.Instances.Rows, 1);
        Check(table.Length == 3 * 128, "Instance wire stride differs.");
        var vertices = View(manifest.Geometry[0].Positions, 4);
        for (int instance = 0; instance < 3; instance++)
        {
            int offset = instance * 128;
            int prototype = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(offset));
            var outer = Matrix(Enumerable.Range(0, 16).Select(lane => BinaryPrimitives.ReadSingleLittleEndian(table.AsSpan(offset + 8 + lane * 4))).ToArray());
            var parts = manifest.Prototypes[prototype].Parts;
            Check(parts.Count == 2 && parts[0].ModelTag == mesh.TagHash && parts[1].ModelTag == copy.TagHash
                && parts[0].Material != parts[1].Material && parts[1].InitiallyVisible == false, "Shared geometry lost part identity or visibility.");
            for (int part = 0; part < 2; part++)
                for (int vertex = 0; vertex < 3; vertex++)
                {
                    Vector3 p = new(BinaryPrimitives.ReadSingleLittleEndian(vertices.AsSpan(vertex * 12)),
                        BinaryPrimitives.ReadSingleLittleEndian(vertices.AsSpan(vertex * 12 + 4)),
                        BinaryPrimitives.ReadSingleLittleEndian(vertices.AsSpan(vertex * 12 + 8)));
                    var actual = Vector3.Transform(p, Matrix(parts[part].Transform) * outer);
                    var expected = Vector3.Transform(scene.Instances[instance * 2 + part].Mesh.Vertices[vertex].Position,
                        scene.Instances[instance * 2 + part].Transform);
                    Check(Vector3.Distance(actual, expected) < .00001f, "Shared placement changed reflection, nonuniform scale, shear or local offset.");
                }
        }
        string canceled = Path.Combine(root, "cancelled");
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel(); Reject<OperationCanceledException>(() => WorldSceneExport.Export(catalog, scene, canceled, token: stop.Token));
        }
        Check(!Directory.Exists(canceled) && !Directory.EnumerateDirectories(root, ".sunrise-world-*").Any(),
            "Canceled world export left staged output.");
        Terrain(root, keep is null ? null : Path.GetFullPath(keep) + "-terrain");
        Inputs(root, keep is null ? null : Path.GetFullPath(keep) + "-inputs");
        Decorators(root, keep is null ? null : Path.GetFullPath(keep) + "-decorators");
        Console.WriteLine($"world_export shared_geometry=pass placements=3 parts=6 matrices=pass visibility=pass cancellation=clean path={output}");

        byte[] View(WorldBufferView view, int size)
        {
            byte[] all = File.ReadAllBytes(Path.Combine(output, manifest.Buffers[view.Buffer].Path));
            return all.AsSpan(checked((int)view.Offset), checked(view.Count * view.Components * size)).ToArray();
        }
    }

    private static void Terrain(string root, string? keep)
    {
        string game = Path.Combine(root, "terrain-game"); Directory.CreateDirectory(Path.Combine(game, "packages"));
        byte[] material = new byte[0x400]; BinaryPrimitives.WriteUInt64LittleEndian(material, (ulong)material.Length);
        byte[] pixel = Compiler.Compile("""
            Texture2D<float4> dye:register(t14); SamplerState sampleDye:register(s0);
            struct Output {float4 color:SV_Target0;float4 normal:SV_Target1;float4 metal:SV_Target2;};
            Output main(float2 uv:TEXCOORD0) {Output o;o.color=dye.Sample(sampleDye,uv);o.normal=float4(0,0,1,.5);o.metal=0;return o;}
            """, "main", "WorldTerrainFixture", "ps_5_0", ShaderFlags.OptimizationLevel3).ToArray();
        byte[] shader = new byte[12]; BinaryPrimitives.WriteUInt64LittleEndian(shader, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(shader.AsSpan(8), (uint)pixel.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(0x2C8), 0x80A00005);
        byte[] texture = new byte[40]; BinaryPrimitives.WriteUInt32LittleEndian(texture, 4); BinaryPrimitives.WriteUInt32LittleEndian(texture.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt16LittleEndian(texture.AsSpan(12), 0xCAFE);
        foreach (int at in new[] { 14, 16, 18, 20 }) BinaryPrimitives.WriteUInt16LittleEndian(texture.AsSpan(at), 1);
        texture[23] = 1; BinaryPrimitives.WriteUInt32LittleEndian(texture.AsSpan(36), uint.MaxValue);
        PackageWriter.WriteNew(Path.Combine(game, "packages", "w64_terrain_0100_0.pkg"), 0x100,
            [new(0x808071E8, 8 << 9, material), new(0x80A00002, (32 << 9) | (1 << 6), texture),
             new(uint.MaxValue, (40 << 9) | (1 << 6), [255, 0, 0, 255]),
             new(0x80A00004, (32 << 9) | (1 << 6), texture), new(uint.MaxValue, (40 << 9) | (1 << 6), [0, 255, 0, 255]),
             new(0x80A00006, 33 << 9, shader), new(uint.MaxValue, 41 << 9, pixel)]);
        using var catalog = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false });
        var scene = new SceneDocument { Name = "Terrain bindings", TagHash = 0x80A00000 };
        foreach (uint dye in new uint[] { 0x80A00001, 0x80A00003 })
        {
            var original = Triangle(0x80A00000, 0x80A00000, 0);
            var mesh = new MeshData { TagHash = original.TagHash, MaterialTag = original.MaterialTag, Vertices = original.Vertices,
                Indices = original.Indices, Native = new() { Kind = NativeMeshKind.Terrain, VertexStreams = [], IndexData = [],
                    SourceHeader = [], TerrainDyeTexture = dye, RenderStage = 0 } };
            scene.Instances.Add(new() { Mesh = mesh, Name = "Terrain" });
        }
        string output = keep ?? Path.Combine(root, "terrain-export");
        var result = WorldSceneExport.Export(catalog, scene, output).Manifest;
        Check(result.Geometry.Count == 1 && result.Materials.Count == 2 && result.Textures.Count == 2,
            "Terrain dye variants were merged or omitted.");
        foreach (var value in result.Materials)
        {
            Check(value.TextureOverrides?.ContainsKey(14) == true && value.Path is not null, "Terrain t14 override was not retained.");
            var data = JsonSerializer.Deserialize<WorldMaterialDocument>(File.ReadAllBytes(Path.Combine(output, value.Path!)))!;
            Check(data.Native.Slots.Single(slot => slot.Stage == "Pixel" && slot.Slot == 14).TextureTag == value.TextureOverrides![14],
                "Terrain material slot differs from its effective texture.");
            Check(data.Preview.Bindings.Any(binding => binding.Role == "BaseColor" && binding.Slot == 14
                && binding.TextureTag == value.TextureOverrides![14]), "Terrain t14 producer was not connected through shader dataflow.");
        }
    }

    /**
     * Plated parts take their dye bit, dye buffer and plate atlas; the shader reads COLOR0, the raw UV w lane,
     * a mask the vertex shader sets from entity CB11 row 7, the world normal from CB11 rows 0 to 2, and packed
     * emission; cloth keeps its draw set.
     */
    private static void Inputs(string root, string? keep)
    {
        string game = Path.Combine(root, "inputs-game"); Directory.CreateDirectory(Path.Combine(game, "packages"));
        byte[] vertex = Compiler.Compile("""
            cbuffer Object:register(b11) {float4 rows[8];};
            struct VsIn {float4 p:POSITION; float4 uv:TEXCOORD0; float4 c:COLOR0; float3 n:NORMAL0;};
            struct VsOut {float4 uv:TEXCOORD0; float4 color:TEXCOORD1; float4 mask:TEXCOORD2; float3 normal:TEXCOORD3;
                float3 height:TEXCOORD4; float4 position:SV_Position;};
            VsOut main(VsIn i) {VsOut o; o.uv=i.uv; o.color=i.c; o.position=i.p; o.height=i.p.z*rows[3].x;
                o.normal=normalize(float3(dot(rows[0].xyz,i.n),dot(rows[1].xyz,i.n),dot(rows[2].xyz,i.n)));
                o.mask=saturate(saturate(i.p.z*rows[7].z)+rows[7].w); return o;}
            """, "main", "WorldInputsVertex", "vs_5_0", ShaderFlags.OptimizationLevel3).ToArray();
        byte[] pixel = Compiler.Compile("""
            Texture2D<float4> plate:register(t0); SamplerState samplePlate:register(s0);
            cbuffer Dye:register(b7) {float4 dye[3];};
            struct Input {float4 uv:TEXCOORD0; float4 color:TEXCOORD1; float4 mask:TEXCOORD2; float3 normal:TEXCOORD3;
                float3 height:TEXCOORD4;};
            struct Output {float4 color:SV_Target0; float4 normal:SV_Target1; float4 third:SV_Target2;};
            Output main(Input i) {Output o;
                float3 c=plate.Sample(samplePlate,i.uv.xy).rgb*dye[2].rgb*i.color.y*saturate(i.normal.z*.5+.5);
                float e=saturate(log2(dye[2].w*i.uv.w*i.mask.w+0.0078125)*0.0769231+0.538462);
                o.color=float4(c,.2); o.normal=float4(.5,.5,1,0); o.third=float4(saturate(i.height.x),(e+1.007843)*.5,0,0); return o;}
            """, "main", "WorldInputsPixel", "ps_5_0", ShaderFlags.OptimizationLevel3).ToArray();
        static byte[] Header(byte[] code)
        {
            byte[] header = new byte[12]; BinaryPrimitives.WriteUInt64LittleEndian(header, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)code.Length);
            return header;
        }
        byte[] material = new byte[0x400]; BinaryPrimitives.WriteUInt64LittleEndian(material, (ulong)material.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(0x48), 0x80A00001);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(0x2C8), 0x80A00003);
        PackageWriter.WriteNew(Path.Combine(game, "packages", "w64_inputs_0100_0.pkg"), 0x100,
            [new(0x808071E8, 8 << 9, material), new(0x80A00002, (33 << 9) | (1 << 6), Header(vertex)),
             new(uint.MaxValue, (41 << 9) | (1 << 6), vertex), new(0x80A00004, 33 << 9, Header(pixel)), new(uint.MaxValue, 41 << 9, pixel)]);
        using var catalog = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false });
        // Stride 12: TEXCOORD0 as four snorm16 lanes, then COLOR0 as four unorm8 lanes.
        byte[] stream = new byte[36];
        for (int row = 0; row < 3; row++)
        {
            for (int lane = 0; lane < 4; lane++)
                BinaryPrimitives.WriteInt16LittleEndian(stream.AsSpan(row * 12 + lane * 2), (short)(lane == 3 ? 10000 * (row + 1) : 0));
            stream[row * 12 + 8] = 255; stream[row * 12 + 9] = (byte)(85 * row); stream[row * 12 + 10] = 0; stream[row * 12 + 11] = 255;
        }
        var triangle = Triangle(0x80A00010, 0x80A00000, 0);
        var mesh = new MeshData { TagHash = triangle.TagHash, MaterialTag = triangle.MaterialTag, Vertices = triangle.Vertices,
            Indices = triangle.Indices, SourceVertexIndices = [0, 1, 2],
            Native = new() { Kind = NativeMeshKind.Entity, VertexStreams = [new(0, 0, 12, 0, stream)], IndexData = [], SourceHeader = [],
                InputLayoutVerified = true, RenderStage = 0,
                InputElements = [new("TEXCOORD", 0, 0, 0, NativeVertexFormat.R16G16B16A16SNorm), new("COLOR", 0, 0, 8, NativeVertexFormat.R8G8B8A8UNorm)] } };
        const uint Atlas = 0x00000ABC;
        byte[] texels = [255, 128, 64, 255, 255, 128, 64, 255, 255, 128, 64, 255, 255, 128, 64, 255];
        var atlas = new TextureData(Atlas, 28, 2, 2, 1, 1, 1, 0, TextureKind.Texture2D, "", [new(0, 0, 2, 2, 1, 8, 16, texels)]);
        var buffers = new Dictionary<uint, Vector4[]> { [7] = [Vector4.Zero, Vector4.Zero, new(1, .5f, .25f, 4)] };
        var dyed = new BundleShaderInputs("plate|dye 1", "Plated", 1, buffers, new Dictionary<uint, uint> { [0] = Atlas },
            new Dictionary<uint, TextureData> { [Atlas] = atlas });
        var undyed = dyed with { Key = "plate|dye 0", DyeBit = 0 };
        var scene = new SceneDocument { Name = "Shader inputs", TagHash = 0x80A00000 };
        MeshInstance Add(string name, Matrix4x4 transform, uint resourceClass = 0, bool visible = true, string? reason = null,
            SceneObjectPlacement? owner = null)
        {
            var instance = new MeshInstance { Mesh = mesh, Name = name, Transform = transform, ObjectPlacement = owner,
                ModelSourceResourceClass = resourceClass, InitiallyVisible = visible, InitialVisibilityReason = reason };
            scene.Instances.Add(instance);
            return instance;
        }
        var inputs = new Dictionary<MeshInstance, BundleShaderInputs>(ReferenceEqualityComparer.Instance)
        {
            [Add("Plated A", Matrix4x4.Identity)] = dyed,
            [Add("Plated B", Matrix4x4.CreateTranslation(2, 0, 0))] = dyed,
            [Add("Plated C", Matrix4x4.CreateTranslation(4, 0, 0))] = undyed
        };
        Add("Plain", Matrix4x4.CreateTranslation(6, 0, 0));
        var cloth = new SceneObjectPlacement(0x80A00000, 0, Matrix4x4.CreateTranslation(8, 0, 0));
        Add("Cloth", cloth.WorldTransform, 0x80807286, owner: cloth);
        Add("Cloth", cloth.WorldTransform, 0x80807286, false, MeshData.ClothSetPartReason, cloth);
        string output = keep ?? Path.Combine(root, "inputs-export");
        var result = WorldSceneExport.Export(catalog, scene, output, new() { ShaderInputs = inputs }).Manifest;
        var documents = result.Materials.Where(value => value.Path is not null).ToDictionary(value => value.Name,
            value => JsonSerializer.Deserialize<WorldMaterialDocument>(File.ReadAllBytes(Path.Combine(output, value.Path!)))!);
        Check(result.Materials.Count == 3 && documents.Count == 3, "Plated parts did not get one material per distinct shader input.");
        var plated = documents.Where(pair => pair.Key.Contains("Plated", StringComparison.Ordinal)).Select(pair => pair.Value).ToArray();
        var plain = documents.Single(pair => !pair.Key.Contains("Plated", StringComparison.Ordinal)).Value;
        Check(plated.Length == 2 && plated.All(value => value.Preview.Program?.Outputs.ContainsKey("BaseColor") == true
            && value.Native.Slots.Any(slot => slot is { Stage: "Pixel", Slot: 0, TextureTag: Atlas })),
            "A material on a plated part lacks its plate or its base colour.");
        Check(plain.Preview.Program?.Outputs.ContainsKey("BaseColor") != true, "A part without plate inputs resolved a plate colour.");
        Check(documents.Keys.Count(name => name.EndsWith("dye 1)", StringComparison.Ordinal)) == 1
            && plated.All(value => value.Preview.Program!.Nodes.Any(node => node is { Operation: "constant", Value: .25f })),
            "Dyed materials lost their dye bit or dye buffer.");
        var program = plated[0].Preview.Program!;
        Check(program.Nodes.Any(node => node is { Operation: "color", Channel: 1 }) && program.Nodes.Any(node => node is { Operation: "uvzw", Channel: 1 })
            && program.Outputs.ContainsKey("Emission"), "COLOR0, the raw UV w lane or packed emission did not reach the program.");
        Check(program.Nodes.Any(node => node is { Operation: "surface", Slot: 0, Channel: 2 }),
            "The world normal the vertex shader writes did not reach the program.");
        Check(!program.Nodes.Any(node => node.Operation == "position"), "A height broadcast from POSITION.z was read as world position.");
        Check(result.Textures.Count(value => value.TagHash == Atlas) == 1 && File.Exists(Path.Combine(output, result.Textures.Single().Path)),
            "The plate atlas was not written exactly once.");
        var geometry = result.Geometry.Single();
        Check(geometry.Colors is { ComponentType: "UInt8", Components: 4, Count: 3 } && geometry.TexCoordsZw is { Count: 3 },
            "Vertex colour or the raw UV z and w were not exported.");
        byte[] shard = File.ReadAllBytes(Path.Combine(output, result.Buffers[geometry.Colors!.Buffer].Path));
        Check(shard[(int)geometry.Colors.Offset + 4 + 1] == 85, "Exported vertex colour differs from COLOR0.");
        var clothParts = result.Prototypes.SelectMany(value => value.Parts).Where(part => part.ResourceClass == 0x80807286).ToArray();
        Check(clothParts.Length == 2 && clothParts.Count(part => part.InitiallyVisible == false
            && part.VisibilityReason == MeshData.ClothSetPartReason) == 1, "Cloth parts or their draw-set visibility were lost.");
        // Stage 0 with no material override culls back faces; override 0x00810000 picks a two-sided rasterizer.
        Check(result.Materials.Where(value => value.Path is not null).All(value => value.BackfaceCulling)
            && !MaterialPreviewAnalyzer.CullsClockwise(0x00810000, 0), "Native back-face culling was not exported.");
    }

    /**
     * A ground decorator colour adds the record tint the vertex shader unpacks with extern 72 rows 6 and 7,
     * which its TFX copies to cb0 rows 6 and 7; each placement carries its own record and AO inputs.
     */
    private static void Decorators(string root, string? keep)
    {
        string game = Path.Combine(root, "decorator-game"); Directory.CreateDirectory(Path.Combine(game, "packages"));
        byte[] vertex = Compiler.Compile("""
            cbuffer Material:register(b0) {float4 rows[8];};
            struct VsIn {float4 p:POSITION; float2 uv:TEXCOORD0; float4 record:TEXCOORD7; float4 ao:BLENDINDICES0;};
            struct VsOut {float2 uv:TEXCOORD0; float4 tint:TEXCOORD4; float4 position:SV_Position;};
            VsOut main(VsIn i) {VsOut o; o.uv=i.uv; o.position=i.p;
                o.tint=float4(rows[6].yzw*i.record.yzw+rows[7].yzw, i.ao.w); return o;}
            """, "main", "WorldDecoratorVertex", "vs_5_0", ShaderFlags.OptimizationLevel3).ToArray();
        byte[] pixel = Compiler.Compile("""
            struct Input {float2 uv:TEXCOORD0; float4 tint:TEXCOORD4;};
            struct Output {float4 color:SV_Target0; float4 normal:SV_Target1; float4 third:SV_Target2;};
            Output main(Input i) {Output o; o.color=float4(float3(.2,.3,.1)+i.tint.xyz,1); o.normal=float4(.5,.5,1,0);
                o.third=float4(0,.5,0,i.tint.w); return o;}
            """, "main", "WorldDecoratorPixel", "ps_5_0", ShaderFlags.OptimizationLevel3).ToArray();
        static byte[] Header(byte[] code)
        {
            byte[] header = new byte[12]; BinaryPrimitives.WriteUInt64LittleEndian(header, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)code.Length);
            return header;
        }
        byte[] material = new byte[0x4D0]; BinaryPrimitives.WriteUInt64LittleEndian(material, (ulong)material.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(0x48), 0x80A00001);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(0x2C8), 0x80A00003);
        // Vertex TFX at +0x68 copies extern 72 vectors 6 and 7 to cb0 rows 6 and 7; +0x98 holds the 8-row template.
        void TypedArray(int field, int header, uint elementClass, byte[] data, int count)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(material.AsSpan(field), (ulong)count);
            BinaryPrimitives.WriteUInt64LittleEndian(material.AsSpan(field + 8), (ulong)(header - field - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(header - 4), 0x80809FBD);
            BinaryPrimitives.WriteUInt64LittleEndian(material.AsSpan(header), (ulong)count);
            BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(header + 8), elementClass);
            data.CopyTo(material, header + 16);
        }
        TypedArray(0x68, 0x404, 0x80800009, [0x3D, 72, 6, 0x43, 6, 0x3D, 72, 7, 0x43, 7], 10);
        TypedArray(0x98, 0x434, 0x80800090, new byte[8 * 16], 8);
        PackageWriter.WriteNew(Path.Combine(game, "packages", "w64_decorator_0100_0.pkg"), 0x100,
            [new(0x808071E8, 8 << 9, material), new(0x80A00002, (33 << 9) | (1 << 6), Header(vertex)),
             new(uint.MaxValue, (41 << 9) | (1 << 6), vertex), new(0x80A00004, 33 << 9, Header(pixel)), new(uint.MaxValue, 41 << 9, pixel)]);
        using var catalog = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false });
        var mesh = Triangle(0x80A00010, 0x80A00000, 0);
        var scene = new SceneDocument { Name = "Decorator inputs", TagHash = 0x80A00000 };
        var inputs = new Dictionary<MeshInstance, IReadOnlyList<WorldDecoratorInputs>>(ReferenceEqualityComparer.Instance);
        WorldDecoratorInputs[] values = [new(new(.1f, .2f, .3f, .4f), new(.5f, .6f, .7f, .8f)), new(new(0, .25f, .5f, .75f), new(1, 1, 1, .125f))];
        for (int plant = 0; plant < 3; plant++)
        {
            var transform = Matrix4x4.CreateTranslation(2 * plant, 0, 0);
            var instance = new MeshInstance { Mesh = mesh, Name = "Plant", Transform = transform,
                ObjectPlacement = new SceneObjectPlacement(0x80A00000, plant, transform) };
            scene.Instances.Add(instance);
            // The third plant has no inputs and reads zero.
            if (plant < 2) inputs[instance] = [values[plant]];
        }
        string output = keep ?? Path.Combine(root, "decorator-export");
        var result = WorldSceneExport.Export(catalog, scene, output, new() { DecoratorInputs = inputs }).Manifest;
        var program = JsonSerializer.Deserialize<WorldMaterialDocument>(File.ReadAllBytes(Path.Combine(output, result.Materials.Single().Path!)))!
            .Preview.Program;
        Check(program?.Outputs.TryGetValue("BaseColor", out var color) == true && color!.Length == 3, "The decorator colour did not resolve.");
        HashSet<int> Channels(int index)
        {
            var node = program!.Nodes[index];
            var found = node.Operation == ShaderVaryingsInstance ? new HashSet<int> { node.Channel } : [];
            foreach (int input in node.Inputs) found.UnionWith(Channels(input));
            return found;
        }
        var baseColor = program!.Outputs["BaseColor"];
        Check(Enumerable.Range(0, 3).All(lane => Channels(baseColor[lane]).SetEquals([lane + 1])),
            "The decorator colour does not read its record lanes y, z and w.");
        if (result.Instances.Inputs is not { Count: 3, Components: 8, ComponentType: "Float32" } view)
            throw new InvalidDataException("Placement decorator inputs were not exported.");
        byte[] shard = File.ReadAllBytes(Path.Combine(output, result.Buffers[view.Buffer].Path));
        float Lane(int row, int lane) => BinaryPrimitives.ReadSingleLittleEndian(shard.AsSpan((int)view.Offset + (row * 8 + lane) * 4));
        for (int row = 0; row < 3; row++)
            for (int lane = 0; lane < 8; lane++)
            {
                float expected = row == 2 ? 0 : lane < 4 ? values[row].Record[lane] : values[row].Occlusion[lane - 4];
                Check(Lane(row, lane) == expected, "A placement's decorator inputs differ from its record.");
            }
    }

    // ShaderVaryings is internal to the exporter; its program operation name is part of the export format.
    private const string ShaderVaryingsInstance = "instance";

    private static MeshData Triangle(uint tag, uint material, int part) => new()
    {
        TagHash = tag, MaterialTag = material, PartIndex = part,
        Vertices = [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero, Vector4.One),
            new(Vector3.UnitX, Vector3.UnitZ, Vector2.UnitX, Vector4.One), new(Vector3.UnitY, Vector3.UnitZ, Vector2.UnitY, Vector4.One)],
        Indices = [0, 1, 2]
    };

    private static Matrix4x4 Matrix(float[] m) => new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
