using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace SunriseStudio.Rendering;

/** A sampler state a material selects, with the header and payload tags it was read from. */
public sealed record MaterialSampler(uint HeaderTag, uint PayloadTag, SamplerDescription Description);

/** Per-stage data a material needs to run: shader, TFX bytecode, its constants and the constant template. */
public sealed record MaterialStageData(GameShaderStage Stage, GameShaderInfo? Shader,
    byte[] TfxBytecode, Vector4[] TfxConstants, Vector4[] ConstantTemplate,
    uint ConstantSourceTag, IReadOnlyList<MaterialSampler> Samplers);

/** Everything needed to execute a material: the material plus its vertex, pixel and compute stage data. */
public sealed record MaterialExecutionData(GameMaterialInfo Material,
    MaterialStageData Vertex, MaterialStageData Pixel, MaterialStageData Compute);

/** Material-execution part of GameMaterialReader: assembles stage data from the material's shaders. */
public sealed partial class GameMaterialReader
{
    /** Authored data stays separate from runtime externs and GPU binding slots. */
    public MaterialExecutionData ReadExecution(uint tag)
    {
        var material = Read(tag);
        byte[] bytes = catalog.Read(tag);
        return new(material,
            ReadStageData(bytes, GameShaderStage.Vertex, material.VertexShader, 0x48),
            ReadStageData(bytes, GameShaderStage.Pixel, material.PixelShader, 0x2C8),
            ReadStageData(bytes, GameShaderStage.Compute, material.ComputeShader, 0x368));
    }

    /** Reads the raw DXBC bytecode of a shader from the package catalog. */
    public byte[] ReadShaderBytecode(GameShaderInfo shader) => catalog.Read(shader.BytecodeTag);

    /** An external constant-buffer entry takes precedence over inline vectors. */
    private MaterialStageData ReadStageData(byte[] bytes, GameShaderStage stage,
        GameShaderInfo? shader, int start) => ReadRuntimeStage(bytes, stage, shader,
            start + 0x20, start + 0x30, start + 0x40, start + 0x50, start + 0x84);

    /** Material and scope records share typed arrays but use different field offsets. */
    private MaterialStageData ReadRuntimeStage(byte[] bytes, GameShaderStage stage,
        GameShaderInfo? shader, int tfxField, int expressionField, int samplerField,
        int templateField, int containerField)
    {
        byte[] bytecode = ReadTypedArray(bytes, tfxField, 0x80800009, 1);
        var expressionConstants = ReadVectors(bytes, expressionField);
        byte[] samplerRows = ReadTypedArray(bytes, samplerField, 0x808073F3, 16);
        var samplers = new List<MaterialSampler>();
        for (int offset = 0; offset < samplerRows.Length; offset += 16)
            samplers.Add(ReadSampler(U32(samplerRows, offset)));
        uint container = U32(bytes, containerField);
        Vector4[] constants;
        if (container is not (0 or uint.MaxValue))
        {
            if (!catalog.TryGetEntry(container, out var entry) || entry.Type != 32 || entry.SubType != 7)
                throw new InvalidDataException("Material constant source is not a constant-buffer header.");
            byte[] header = catalog.Read(container);
            if (header.Length < 4 || !catalog.TryGetEntry(entry.Reference, out var payload)
                || payload.Type != 40 || payload.SubType != 7)
                throw new InvalidDataException("Material constant-buffer payload type is invalid.");
            byte[] data = catalog.Read(entry.Reference);
            if (U32(header, 0) != data.Length || data.Length % 16 != 0)
                throw new InvalidDataException("Material constant-buffer length is invalid.");
            constants = MemoryMarshal.Cast<byte, Vector4>(data).ToArray();
        }
        else
        {
            constants = ReadVectors(bytes, templateField);
            container = 0;
        }
        return new(stage, shader, bytecode, expressionConstants, constants, container, samplers);
    }

    /** The payload must match the 52-byte native D3D11 sampler descriptor. */
    private MaterialSampler ReadSampler(uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var header) || header.Type != 34 || header.SubType != 1
            || !catalog.TryGetEntry(header.Reference, out var payload)
            || payload.Type != 42 || payload.SubType != 1)
            throw new InvalidDataException($"Sampler 0x{tag:X8} has an invalid resource type.");
        byte[] bytes = catalog.Read(payload.TagHash);
        if (bytes.Length != 52 || Marshal.SizeOf<SamplerDescription>() != 52)
            throw new InvalidDataException("Sampler payload does not fit the native descriptor.");
        var description = MemoryMarshal.Read<SamplerDescription>(bytes);
        if (!Enum.IsDefined(description.Filter) || !Enum.IsDefined(description.AddressU)
            || !Enum.IsDefined(description.AddressV) || !Enum.IsDefined(description.AddressW)
            || !Enum.IsDefined(description.ComparisonFunc) || description.MaxAnisotropy is < 1 or > 16
            || !float.IsFinite(description.MipLODBias) || !float.IsFinite(description.MinLOD)
            || !float.IsFinite(description.MaxLOD) || description.MinLOD > description.MaxLOD)
            throw new InvalidDataException("Sampler payload contains an invalid Direct3D value.");
        return new(tag, payload.TagHash, description);
    }

    private static Vector4[] ReadVectors(byte[] bytes, int field) =>
        MemoryMarshal.Cast<byte, Vector4>(ReadTypedArray(bytes, field, 0x80800090, 16)).ToArray();

    /** Array data must fit both the class marker and the repeated element count. */
    private static byte[] ReadTypedArray(byte[] bytes, int field, uint elementClass, int stride)
    {
        long count = (long)U64(bytes, field), relative = (long)U64(bytes, field + 8);
        if (count == 0 && relative == 0) return [];
        if (count is < 0 or > 1_000_000) throw new InvalidDataException("Material array count is invalid.");
        long header = checked(field + 8 + relative);
        if (header < 4 || header > bytes.Length - 16 || U32(bytes, (int)header - 4) != 0x80809FBD
            || U64(bytes, (int)header) != (ulong)count || U32(bytes, (int)header + 8) != elementClass
            || U32(bytes, (int)header + 12) != 0 || count * stride > bytes.Length - header - 16)
            throw new InvalidDataException($"Material array 0x{field:X} has an invalid header.");
        return bytes.AsSpan((int)header + 16, checked((int)count * stride)).ToArray();
    }
}
