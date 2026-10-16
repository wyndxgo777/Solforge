using System.Numerics;
using System.Text;

namespace SunriseStudio.Scene;

/** Native-mesh part of AssetSceneLoader: reads vertex streams and matches them to the shader's input layout. */
public sealed partial class AssetSceneLoader
{
    private sealed record ShaderInputs(uint Header, uint Data, NativeShaderInput[] Inputs, string Status,
        uint StateOverrides = 0);
    private readonly Dictionary<uint, ShaderInputs> nativeInputs = [];
    private NativeLayoutCatalog? nativeLayouts;

    /** Raw buffers use their original vertex numbering and retain the authored draw range. */
    private NativeMeshData NativeMesh(NativeMeshKind kind, TagData source, uint material,
        uint vertex0, uint vertex1, uint index, uint first, uint count, byte primitive,
        uint word38 = 0, uint word0C = 0, uint weights = 0, int declarationId = -1, int renderStage = -1,
        (Vector4 UvTransform, uint DyeTexture)? terrainGroup = null)
    {
        var streams = new List<NativeVertexStream> { NativeStream(vertex0) };
        if (catalog.TryGetEntry(vertex1, out var extra) && extra.Type == 32 && extra.SubType == 4)
            streams.Add(NativeStream(vertex1));
        var indices = Buffer(index, 6, out var indexHeader);
        var indexEntry = catalog.TryGetEntry(index, out var found) ? found : throw new InvalidDataException("Index header disappeared.");
        var input = MaterialInputs(material);
        var elements = MatchInputLayout(kind, streams, input.Inputs, out var layoutStatus);
        var nativeVerified = false;
        uint declarationTag = 0;
        if (declarationId >= 0)
        {
            try
            {
                nativeLayouts ??= new NativeLayoutCatalog(catalog);
                if (nativeLayouts.TryGet(declarationId, out var authored, out declarationTag))
                {
                    elements = authored;
                    nativeVerified = true;
                    layoutStatus = "Input declaration follows its authored id and the native DXGI translation table.";
                }
                else layoutStatus = $"Authored input declaration {declarationId} is absent or ambiguous.";
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OverflowException)
            {
                layoutStatus = $"Native declaration table failed: {error.Message}";
            }
        }
        var fits = elements.Length > 0 && input.Inputs.Where(i => i.SystemValue == 0).All(i =>
            elements.Any(e => e.Semantic == i.Semantic && e.SemanticIndex == i.SemanticIndex && e.Slot < streams.Count));
        var scale = kind switch
        {
            NativeMeshKind.Static => new Vector4(source.F32(0x6C)),
            NativeMeshKind.Entity => Vector(source, 0x50),
            _ => Vector4.One
        };
        NativeVertexStream? skin = null;
        if (catalog.TryGetEntry(weights, out var skinEntry) && skinEntry.Type == 32 && skinEntry.SubType == 4)
            skin = NativeStream(weights);
        return new NativeMeshData
        {
            Kind = kind,
            VertexStreams = streams.ToArray(),
            SkinWeightStream = skin,
            IndexData = indices.Bytes,
            IndexHeaderTag = index,
            IndexDataTag = indexEntry.Reference,
            Is32Bit = indexHeader.U8(1) != 0,
            FirstIndex = first,
            IndexCount = count,
            GamePrimitiveType = primitive,
            MaterialTag = material,
            MaterialStateOverrides = input.StateOverrides,
            VertexShaderTag = input.Header,
            VertexShaderDataTag = input.Data,
            ShaderInputs = input.Inputs,
            InputElements = elements,
            InputLayoutFitsSignature = fits,
            InputLayoutEvidence = nativeVerified ? NativeLayoutEvidence.NativeLayout : elements.Length > 0 ? NativeLayoutEvidence.PackageAndShader : NativeLayoutEvidence.None,
            InputLayoutVerified = nativeVerified,
            NativeDeclarationId = declarationId,
            NativeDeclarationTag = declarationTag,
            RenderStage = renderStage,
            LayoutStatus = input.Status.Length != 0 ? input.Status : layoutStatus,
            PositionScale = scale,
            PositionOffset = kind == NativeMeshKind.Terrain ? Vector4.Zero : Vector(source, 0x60),
            UvTransform = kind == NativeMeshKind.Terrain ? terrainGroup?.UvTransform ?? Vector4.Zero : Vector(source, 0x70),
            TerrainDyeTexture = terrainGroup?.DyeTexture ?? uint.MaxValue,
            TerrainOffset = kind == NativeMeshKind.Terrain ? Vector(source, 0x30) : Vector4.Zero,
            SourceHeader = source.Bytes,
            ResourceWord38 = word38,
            BufferGroupWord0C = word0C
        };
    }

    private static Vector4 Vector(TagData data, int offset) => new(data.F32(offset), data.F32(offset + 4), data.F32(offset + 8), data.F32(offset + 12));

    /** Preview UVs use the authored semantic offset, including UVs stored with positions. Terrain reads TEXCOORD1. */
    private static void ApplyNativeTexcoords(MeshData mesh)
    {
        var native = mesh.Native;
        if (native == null || !native.InputLayoutVerified) return;
        uint index = native.Kind == NativeMeshKind.Terrain ? 1U : 0U;
        var element = native.InputElements.FirstOrDefault(e => e.Semantic == "TEXCOORD" && e.SemanticIndex == index);
        if (element == null || element.PerInstance || element.Slot >= native.VertexStreams.Length) return;
        var stream = native.VertexStreams[element.Slot];
        var data = new TagData(stream.Data);
        for (var i = 0; i < mesh.Vertices.Length; ++i)
        {
            var offset = checked((int)mesh.SourceVertexIndices[i] * stream.Stride + element.Offset);
            var uv = element.Format switch
            {
                NativeVertexFormat.R16G16SNorm or NativeVertexFormat.R16G16B16A16SNorm =>
                    new Vector2(Snorm(data.I16(offset)), Snorm(data.I16(offset + 2))),
                NativeVertexFormat.R16G16Float => new Vector2((float)BitConverter.UInt16BitsToHalf(data.U16(offset)),
                    (float)BitConverter.UInt16BitsToHalf(data.U16(offset + 2))),
                NativeVertexFormat.R32G32Float or NativeVertexFormat.R32G32B32A32Float =>
                    new Vector2(data.F32(offset), data.F32(offset + 4)),
                _ => mesh.Vertices[i].TexCoord
            };
            if (element.Format is not (NativeVertexFormat.R16G16SNorm or NativeVertexFormat.R16G16B16A16SNorm
                or NativeVertexFormat.R16G16Float or NativeVertexFormat.R32G32Float or NativeVertexFormat.R32G32B32A32Float)) continue;
            float scaleY = native.Kind == NativeMeshKind.Static ? native.UvTransform.X : native.UvTransform.Y;
            mesh.Vertices[i].TexCoord = new Vector2(uv.X * native.UvTransform.X + native.UvTransform.Z,
                uv.Y * scaleY + native.UvTransform.W);
        }
    }

    /** Vertex headers carry byte size, stride and the original type word. */
    private NativeVertexStream NativeStream(uint tag)
    {
        var data = Buffer(tag, 4, out var header);
        var stride = header.U16(4);
        if (stride == 0 || header.U32(0) != data.Length || data.Length % stride != 0)
            throw new InvalidDataException("Native vertex stream size or stride is invalid.");
        catalog.TryGetEntry(tag, out var entry);
        return new NativeVertexStream(tag, entry.Reference, stride, header.U16(6), data.Bytes);
    }

    /** Shader signatures omit input-assembler formats. */
    private ShaderInputs MaterialInputs(uint material)
    {
        if (nativeInputs.TryGetValue(material, out var cached)) return cached;
        uint states = 0;
        try
        {
            if (Class(material) != 0x808071E8) return nativeInputs[material] = new(0, 0, [], "Material input layout is unavailable.");
            var materialData = Read(material);
            states = materialData.U32(0x20);
            var shader = materialData.U32(0x48);
            if (!catalog.TryGetEntry(shader, out var header) || header.Type != 33 || header.SubType != 1
                || !catalog.TryGetEntry(header.Reference, out var payload) || payload.Type != 41 || payload.SubType != 1)
                return nativeInputs[material] = new(shader, 0, [], "Material has no supported vertex shader.", states);
            var data = Read(payload.TagHash);
            if (!data.Has(0, 32) || data.U32(0) != 0x43425844 || data.U32(24) != data.Length)
                throw new InvalidDataException("Native shader has no bounded DXBC container.");
            var chunkCount = data.U32(28);
            if (chunkCount > (data.Length - 32) / 4) throw new InvalidDataException("DXBC chunk count is invalid.");
            for (var i = 0; i < chunkCount; ++i)
            {
                var offset = checked((int)data.U32(32 + i * 4));
                if (!data.Has(offset, 8)) throw new InvalidDataException("DXBC chunk offset is invalid.");
                var length = checked((int)data.U32(offset + 4));
                if (!data.Has(offset + 8, length)) throw new InvalidDataException("DXBC chunk length is invalid.");
                if (data.U32(offset) != 0x4E475349) continue;
                var start = offset + 8;
                var count = data.U32(start);
                if (length < 8 || count > (length - 8) / 24) throw new InvalidDataException("DXBC input signature count is invalid.");
                var inputs = new NativeShaderInput[count];
                for (var j = 0; j < count; ++j)
                {
                    var row = start + 8 + j * 24;
                    var name = checked((int)data.U32(row));
                    if (name >= length) throw new InvalidDataException("DXBC semantic name is outside its signature.");
                    var end = Array.IndexOf(data.Bytes, (byte)0, start + name, length - name);
                    if (end < 0) throw new InvalidDataException("DXBC semantic name is unterminated.");
                    inputs[j] = new NativeShaderInput(Encoding.ASCII.GetString(data.Bytes, start + name, end - start - name),
                        data.U32(row + 4), data.U32(row + 16), data.U32(row + 12), data.U8(row + 20), data.U32(row + 8));
                }
                return nativeInputs[material] = new(shader, payload.TagHash, inputs, "", states);
            }
            return nativeInputs[material] = new(shader, payload.TagHash, [], "Native shader has no supported ISGN chunk.", states);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            return nativeInputs[material] = new(0, 0, [], $"Native input signature failed: {error.Message}", states);
        }
    }

    /** Candidate layouts are restricted to measured signature and stride families. */
    private static NativeInputElement[] MatchInputLayout(NativeMeshKind kind, List<NativeVertexStream> streams,
        NativeShaderInput[] inputs, out string status)
    {
        status = "No measured input layout matches these streams and shader inputs.";
        if (streams.Count != 2 || streams[0].Stride != 8 || streams.Any(s => s.Type != 0)) return [];
        var attributes = inputs.Where(i => i.SystemValue == 0).ToArray();
        var signature = string.Join(",", attributes.Select(i => $"{i.Semantic}{i.SemanticIndex}"));
        if (kind == NativeMeshKind.Terrain && signature != "POSITION0,NORMAL0,TEXCOORD1") return [];
        if (kind != NativeMeshKind.Terrain && signature is not ("POSITION0,TEXCOORD0,NORMAL0"
            or "POSITION0,TEXCOORD0,NORMAL0,TANGENT0" or "POSITION0,TEXCOORD0,NORMAL0,TANGENT0,COLOR0")) return [];
        var elements = new List<NativeInputElement>();
        var offset = 0;
        for (var i = 0; i < attributes.Length; ++i)
        {
            var input = attributes[i];
            NativeVertexFormat format;
            var size = 0;
            var slot = i == 0 ? 0 : 1;
            if (i == 0 && input.Semantic == "POSITION" && input.SemanticIndex == 0 && input.Mask == 15
                && input.ComponentType == (kind == NativeMeshKind.Terrain ? 2U : 3U))
            {
                format = kind == NativeMeshKind.Terrain ? NativeVertexFormat.R16G16B16A16SInt : NativeVertexFormat.R16G16B16A16SNorm;
                elements.Add(new(input.Semantic, input.SemanticIndex, 0, 0, format));
                continue;
            }
            if (i == 0 || input.ComponentType != 3) return [];
            switch (input.Semantic)
            {
                case "TEXCOORD" when input.Mask == 3:
                    format = kind == NativeMeshKind.Terrain ? NativeVertexFormat.R16G16Float : NativeVertexFormat.R16G16SNorm;
                    size = 4;
                    break;
                case "NORMAL" or "TANGENT" when input.Mask is 7 or 15:
                    format = NativeVertexFormat.R16G16B16A16SNorm;
                    size = 8;
                    break;
                case "COLOR" when input.Mask == 15 && kind == NativeMeshKind.Static:
                    format = NativeVertexFormat.R8G8B8A8UNorm;
                    size = 4;
                    break;
                default: return [];
            }
            elements.Add(new(input.Semantic, input.SemanticIndex, slot, offset, format));
            offset += size;
        }
        if (offset != streams[1].Stride || attributes.Length < 3) return [];
        status = "Matched by signature and stride (unverified)";
        return elements.ToArray();
    }
}
