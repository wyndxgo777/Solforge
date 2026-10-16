using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using SunriseStudio.Packages;
using Vortice.D3DCompiler;
using Vortice.DXGI;

namespace SunriseStudio.Rendering;

/**
 * Shader stage of a game shader. This file holds the decoded material and shader records returned by
 * GameMaterialReader.
 */
public enum GameShaderStage { Pixel = 0, Vertex = 1, Geometry = 2, Hull = 3, Domain = 4, Compute = 5 }

/** A texture register a shader declares, with its resource dimension (texture2d, texturecube, ...). */
public sealed record ShaderTextureSlot(uint Slot, string Dimension);
/** A secondary shader header referenced from a material at FieldOffset. */
public sealed record AdditionalShaderHeader(int FieldOffset, uint Tag, int PackageSubtype);

/** One decoded shader: its header and bytecode tags, stage, disassembly and declared texture slots. */
public sealed record GameShaderInfo(uint HeaderTag, uint BytecodeTag, GameShaderStage Stage,
    string Profile, string Disassembly, IReadOnlyList<ShaderTextureSlot> TextureSlots,
    IReadOnlyList<string> Declarations);

/**
 * A texture bound to a shader slot, with the source texture's size and whether the shader declares the
 * slot.
 */
public sealed record MaterialTextureBinding(GameShaderStage Stage, uint Slot, uint TextureTag,
    bool DeclaredByShader, string? ShaderDimension, int Width, int Height, int Depth,
    int ArraySize, Format Format)
{
    public bool IsSrgb => Format is Format.R8G8B8A8_UNorm_SRgb or Format.B8G8R8A8_UNorm_SRgb
        or Format.B8G8R8X8_UNorm_SRgb or Format.BC1_UNorm_SRgb or Format.BC2_UNorm_SRgb
        or Format.BC3_UNorm_SRgb or Format.BC7_UNorm_SRgb;
    public bool IsPreviewCandidate => Stage == GameShaderStage.Pixel && DeclaredByShader
        && ShaderDimension == "texture2d" && Depth <= 1 && ArraySize <= 1;
}

/** A decoded material: its tag and the vertex, pixel and compute shaders it selects. */
public sealed record GameMaterialInfo(uint Tag, GameShaderInfo? VertexShader,
    GameShaderInfo? PixelShader, GameShaderInfo? ComputeShader,
    IReadOnlyList<MaterialTextureBinding> Textures, IReadOnlyList<string> Diagnostics)
{
    public IReadOnlyList<AdditionalShaderHeader> AdditionalShaderHeaders { get; init; } = [];
    public uint StateOverrides { get; init; }
    /** Rows with tag 0xFFFFFFFF; the native binder sets a null view at these slots. */
    public IReadOnlySet<(GameShaderStage Stage, uint Slot)> EmptyTextureSlots { get; init; } =
        new HashSet<(GameShaderStage, uint)>();
    public MaterialTextureBinding? PreviewTexture => Textures.Where(x => x.IsPreviewCandidate)
        .OrderByDescending(x => x.IsSrgb).ThenBy(x => x.Slot).FirstOrDefault();
}

/** Texture slots are checked against native DXBC declarations; color-map purpose stays unknown. */
public sealed partial class GameMaterialReader(PackageCatalog catalog)
{
    private readonly Dictionary<uint, GameMaterialInfo> materials = [];
    private readonly Dictionary<uint, GameShaderInfo> shaders = [];

    /** Stage fields are specific to the installed pre-Beyond-Light material class. */
    public GameMaterialInfo Read(uint materialTag)
    {
        if (materials.TryGetValue(materialTag, out var cached)) return cached;
        if (!catalog.TryGetEntry(materialTag, out var entry) || entry.Reference != 0x808071E8)
            throw new NotSupportedException($"Tag 0x{materialTag:X8} is not a supported material.");
        byte[] bytes = catalog.Read(materialTag);
        if (bytes.Length < 0x400 || U64(bytes, 0) != (ulong)bytes.Length)
            throw new InvalidDataException("Material size does not match its header.");
        var textures = new List<MaterialTextureBinding>();
        var emptySlots = new HashSet<(GameShaderStage, uint)>();
        var diagnostics = new List<string>();
        GameShaderInfo? ReadStage(GameShaderStage stage, int shaderField, int textureField)
        {
            uint shaderTag = U32(bytes, shaderField);
            GameShaderInfo? shader = null;
            if (shaderTag is not (0 or uint.MaxValue))
            {
                try { shader = ReadShader(shaderTag, stage); }
                catch (Exception error) when ((error is IOException or InvalidDataException or ArgumentException
                    or NotSupportedException or KeyNotFoundException or SharpGen.Runtime.SharpGenException)
                    && !error.Message.StartsWith("Package block authentication failed", StringComparison.Ordinal))
                { diagnostics.Add($"{stage} shader 0x{shaderTag:X8}: {error.Message}"); }
            }
            foreach (int row in TextureRows(bytes, textureField))
            {
                uint slot = U32(bytes, row), textureTag = U32(bytes, row + 4);
                if (slot >= 128) throw new InvalidDataException("Texture slot exceeds the DX11 limit.");
                if (textureTag is 0 or uint.MaxValue)
                {
                    if (textureTag == uint.MaxValue) emptySlots.Add((stage, slot));
                    diagnostics.Add($"{stage} texture t{slot} has no authored texture tag.");
                    continue;
                }
                try
                {
                    if (!catalog.TryGetEntry(textureTag, out var textureEntry)
                        || textureEntry.Type != 32 || textureEntry.SubType is < 1 or > 3)
                        throw new InvalidDataException("Binding does not reference a texture header.");
                    byte[] header = catalog.Read(textureTag);
                    if (header.Length < 0x28 || U16(header, 0xC) != 0xCAFE)
                        throw new InvalidDataException("Texture header marker is invalid.");
                    var declaration = shader?.TextureSlots.FirstOrDefault(x => x.Slot == slot);
                    textures.Add(new(stage, slot, textureTag, declaration is not null,
                        declaration?.Dimension, U16(header, 0xE), U16(header, 0x10),
                        U16(header, 0x12), U16(header, 0x14), (Format)U32(header, 4)));
                    if (shader is not null && declaration is null)
                        diagnostics.Add($"{stage} texture t{slot} is absent from the shader declarations.");
                }
                catch (Exception error) when ((error is IOException or InvalidDataException or KeyNotFoundException)
                    && !error.Message.StartsWith("Package block authentication failed", StringComparison.Ordinal))
                { diagnostics.Add($"{stage} texture t{slot} 0x{textureTag:X8}: {error.Message}"); }
            }
            return shader;
        }
        var vertex = ReadStage(GameShaderStage.Vertex, 0x48, 0x50);
        var pixel = ReadStage(GameShaderStage.Pixel, 0x2C8, 0x2D0);
        var compute = ReadStage(GameShaderStage.Compute, 0x368, 0x370);
        var additional = new List<AdditionalShaderHeader>();
        foreach (int field in new[] { 0xE8, 0x188, 0x228 })
        {
            uint shaderTag = U32(bytes, field);
            if (shaderTag is 0 or uint.MaxValue) continue;
            if (!catalog.TryGetEntry(shaderTag, out var shaderEntry) || shaderEntry.Type != 33)
                throw new InvalidDataException($"Material stage field 0x{field:X} has an invalid shader header.");
            additional.Add(new(field, shaderTag, shaderEntry.SubType));
        }
        return materials[materialTag] = new(materialTag, vertex, pixel, compute, textures, diagnostics)
        { AdditionalShaderHeaders = additional, StateOverrides = U32(bytes, 0x20), EmptyTextureSlots = emptySlots };
    }

    /** Shader header and payload types must agree with the DXBC stage token. */
    public unsafe GameShaderInfo ReadShader(uint shaderTag, GameShaderStage expectedStage)
    {
        if (shaders.TryGetValue(shaderTag, out var cached))
        {
            if (cached.Stage != expectedStage) throw new InvalidDataException("Shader stage mismatch.");
            return cached;
        }
        // Package compute subtype 6 differs from the DXBC compute stage value 5.
        byte packageSubtype = expectedStage == GameShaderStage.Compute ? (byte)6 : (byte)expectedStage;
        if (!catalog.TryGetEntry(shaderTag, out var headerEntry) || headerEntry.Type != 33
            || headerEntry.SubType != packageSubtype)
            throw new InvalidDataException("Shader header type does not match its material stage.");
        byte[] header = catalog.Read(shaderTag);
        if (header.Length < 12 || U64(header, 0) != (ulong)header.Length)
            throw new InvalidDataException("Shader header is truncated.");
        if (!catalog.TryGetEntry(headerEntry.Reference, out var payloadEntry)
            || payloadEntry.Type != 41 || payloadEntry.SubType != headerEntry.SubType)
            throw new InvalidDataException("Shader payload type does not match its header.");
        byte[] bytes = catalog.Read(payloadEntry.TagHash);
        if (U32(header, 8) != bytes.Length)
            throw new InvalidDataException("Shader bytecode length does not match its header.");
        string profile = ValidateShaderContainer(bytes, expectedStage);
        string disassembly;
        fixed (byte* pointer = bytes)
        {
            using var blob = Compiler.Disassemble((IntPtr)pointer, (nuint)bytes.Length,
                DisasmFlags.None, null);
            disassembly = blob.AsString();
        }
        var slots = TextureDeclaration().Matches(disassembly).Select(match =>
            new ShaderTextureSlot(uint.Parse(match.Groups["slot"].Value),
                match.Groups["kind"].Value)).ToArray();
        string[] declarations = disassembly.Split('\n').Select(x => x.TrimEnd('\r'))
            .Where(x => x.StartsWith("dcl_", StringComparison.Ordinal)).ToArray();
        return shaders[shaderTag] = new(shaderTag, payloadEntry.TagHash, expectedStage,
            profile, disassembly, slots, declarations);
    }

    /** Native disassembly only receives a bounded, internally consistent DXBC container. */
    private static string ValidateShaderContainer(byte[] bytes, GameShaderStage expectedStage)
    {
        if (bytes.Length < 32 || !bytes.AsSpan(0, 4).SequenceEqual("DXBC"u8)
            || U32(bytes, 24) != bytes.Length)
            throw new InvalidDataException("Shader payload has no valid DXBC header.");
        uint chunks = U32(bytes, 28);
        if (chunks > (bytes.Length - 32) / 4)
            throw new InvalidDataException("DXBC chunk table exceeds its payload.");
        string? profile = null;
        for (int i = 0; i < chunks; i++)
        {
            uint offset = U32(bytes, 32 + i * 4);
            if (offset > bytes.Length - 8)
                throw new InvalidDataException("DXBC chunk offset is out of range.");
            uint size = U32(bytes, (int)offset + 4);
            if (size > bytes.Length - offset - 8)
                throw new InvalidDataException("DXBC chunk exceeds its payload.");
            string kind = Encoding.ASCII.GetString(bytes, (int)offset, 4);
            if (kind is not ("SHDR" or "SHEX")) continue;
            if (size < 8) throw new InvalidDataException("Shader instruction chunk is truncated.");
            uint token = U32(bytes, (int)offset + 8);
            if ((token >> 16) != (uint)expectedStage)
                throw new InvalidDataException("DXBC shader stage differs from its material field.");
            string prefix = expectedStage switch
            { GameShaderStage.Vertex => "vs", GameShaderStage.Pixel => "ps", GameShaderStage.Geometry => "gs",
                GameShaderStage.Hull => "hs", GameShaderStage.Domain => "ds", _ => "cs" };
            profile = $"{prefix}_{(token >> 4) & 15}_{token & 15}";
        }
        return profile ?? throw new InvalidDataException("DXBC has no shader instruction chunk.");
    }

    /** Texture records have a repeated count and a class-tagged eight-byte stride. */
    private static int[] TextureRows(byte[] bytes, int field)
    {
        long count = (long)U64(bytes, field), relative = (long)U64(bytes, field + 8);
        if (count == 0 && relative == 0) return [];
        if (count is < 0 or > 128) throw new InvalidDataException("Material texture count is invalid.");
        long header = checked(field + 8 + relative);
        if (header < 4 || header > bytes.Length - 16 || U32(bytes, (int)header - 4) != 0x80809FBD
            || U64(bytes, (int)header) != (ulong)count || U32(bytes, (int)header + 8) != 0x80807211
            || U32(bytes, (int)header + 12) != 0 || count * 8 > bytes.Length - header - 16)
            throw new InvalidDataException("Material texture array has an invalid header.");
        return Enumerable.Range(0, (int)count).Select(i => (int)header + 16 + i * 8).ToArray();
    }

    private static ushort U16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static ulong U64(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));

    [GeneratedRegex(@"^dcl_resource_(?<kind>\w+)[^\r\n]*?\bt(?<slot>\d+)\b", RegexOptions.Multiline)]
    private static partial Regex TextureDeclaration();
}
