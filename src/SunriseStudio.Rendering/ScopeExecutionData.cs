using System.Text;

namespace SunriseStudio.Rendering;

/** A texture bound to a scope slot. This file holds the scope execution records. */
public sealed record ScopeTextureBinding(uint Slot, uint TextureTag);
/** Per-stage scope data: constant buffer slot, shader stage data and textures. */
public sealed record ScopeStageData(GameShaderStage Stage, uint ConstantBufferSlot,
    MaterialStageData Data, IReadOnlyList<ScopeTextureBinding> Textures);
/** A render scope ready to execute: its tag, index, name and pixel and vertex stage data. */
public sealed record GameScopeExecution(uint Tag, uint ScopeIndex, string Name,
    ScopeStageData Pixel, ScopeStageData Vertex);

/** Scope part of GameMaterialReader: reads render scopes into executable form. */
public sealed partial class GameMaterialReader
{
    /** Pixel and vertex blocks are decoded; other stages remain unsupported. */
    public GameScopeExecution ReadScopeExecution(uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != 0x808071F3)
            throw new InvalidDataException($"Tag 0x{tag:X8} is not a supported render scope.");
        byte[] bytes = catalog.Read(tag);
        if (bytes.Length < 0x3D0 || U64(bytes, 0) != (ulong)bytes.Length)
            throw new InvalidDataException("Scope size does not match its header.");
        long nameOffset = checked(8 + (long)U64(bytes, 8));
        if (nameOffset < 0 || nameOffset >= bytes.Length)
            throw new InvalidDataException("Scope name offset leaves its tag.");
        int end = bytes.AsSpan((int)nameOffset).IndexOf((byte)0);
        if (end < 0 || end > 256) throw new InvalidDataException("Scope name is not terminated.");
        string name = Encoding.UTF8.GetString(bytes, (int)nameOffset, end);
        return new(tag, U32(bytes, 0x10), name,
            ReadScopeStage(bytes, GameShaderStage.Pixel, 0x40),
            ReadScopeStage(bytes, GameShaderStage.Vertex, 0xD8));
    }

    /** Scope records carry the constant-buffer slot next to the external template tag. */
    private ScopeStageData ReadScopeStage(byte[] bytes, GameShaderStage stage, int start)
    {
        var data = ReadRuntimeStage(bytes, stage, null, start + 0x18, start + 0x28,
            start + 0x38, start + 0x48, start + 0x7C);
        uint slot = U32(bytes, start + 0x78);
        if (data.ConstantTemplate.Length != 0 && slot >= 14)
            throw new InvalidDataException("Scope constant-buffer slot exceeds the Direct3D limit.");
        var textures = TextureRows(bytes, start).Select(row =>
            new ScopeTextureBinding(U32(bytes, row), U32(bytes, row + 4))).ToArray();
        if (textures.Any(texture => texture.Slot >= 128))
            throw new InvalidDataException("Scope texture slot exceeds the Direct3D limit.");
        return new(stage, slot, data, textures);
    }
}
