using System.Numerics;

namespace SunriseStudio.Scene;

/** Kind of native mesh. This file holds the native mesh records. */
public enum NativeMeshKind { Static, Terrain, Entity }
/** How a vertex layout was established. */
public enum NativeLayoutEvidence { None, PackageAndShader, NativeLayout }

/** Values match DXGI_FORMAT so the renderer can map them without a graphics dependency. */
public enum NativeVertexFormat
{
    Unknown = 0,
    R32G32B32A32Float = 2,
    R32G32B32Float = 6,
    R16G16B16A16UNorm = 11,
    R16G16B16A16SNorm = 13,
    R16G16B16A16SInt = 14,
    R32G32Float = 16,
    R8G8B8A8UNorm = 28,
    R8G8B8A8UInt = 30,
    R16G16Float = 34,
    R16G16SNorm = 37,
    R16G16SInt = 38,
    R32UInt = 42,
    R8UNorm = 61
}

/** An occlusion map that applies to an instance, with its buffer, selector and offset. */
public sealed record NativeOcclusionMapMatch(uint MapTag, uint BufferHeaderTag,
    uint Selector, ulong InstanceKey, uint BaseByteOffset);

/** The placement that owns an occlusion map. */
public sealed record NativeOcclusionOwner(uint BubbleHash, uint PlacementListTag,
    int PlacementOffset, uint MapTag);

/** Visibility bits above bit 25 remain unknown until a native draw-state producer supplies them. */
public sealed class NativeStaticBinding
{
    public required NativeOcclusionMapMatch Map { get; init; }
    public required NativeVertexStream ScalarBuffer { get; init; }
    public NativeVertexFormat ScalarFormat => NativeVertexFormat.R8UNorm;
    public int ScalarSlot => 0;
    public int ConstantBufferSlot => 11;
    public required Vector4[] InstanceConstants { get; init; }
    public uint PackedWord { get; init; }
    public uint PackedWordKnownMask => 0x03FFFFFF;
    public uint InstanceByteOffset { get; init; }
    public uint BufferGroupByteOffset { get; init; }
    public uint ScalarByteAddress { get; init; }
    public bool UseSingleScalar { get; init; }
}

/** A vertex stream with its stride, type and bytes. */
public sealed record NativeVertexStream(uint HeaderTag, uint DataTag, int Stride, ushort Type,
    byte[] Data);

/** One element of a vertex input layout. */
public sealed record NativeInputElement(string Semantic, uint SemanticIndex, int Slot, int Offset,
    NativeVertexFormat Format, bool PerInstance = false, int StepRate = 0);

/** One input a vertex shader declares. */
public sealed record NativeShaderInput(string Semantic, uint SemanticIndex, uint Register,
    uint ComponentType, byte Mask, uint SystemValue);

/** Original stream bytes and draw ranges must remain unchanged by diagnostic mesh compaction. */
public sealed class NativeMeshData
{
    public required NativeMeshKind Kind { get; init; }
    public required NativeVertexStream[] VertexStreams { get; init; }
    public NativeVertexStream? SkinWeightStream { get; init; }
    public required byte[] IndexData { get; init; }
    public uint IndexHeaderTag { get; init; }
    public uint IndexDataTag { get; init; }
    public bool Is32Bit { get; init; }
    public uint FirstIndex { get; init; }
    public uint IndexCount { get; init; }
    public byte GamePrimitiveType { get; init; }
    public int BaseVertex { get; init; }
    public uint MaterialTag { get; init; }
    /** Material +0x20: blend, depth, raster and bias override bytes; bit 7 of a byte turns it on. */
    public uint MaterialStateOverrides { get; init; }
    public uint VertexShaderTag { get; init; }
    public uint VertexShaderDataTag { get; init; }
    public NativeShaderInput[] ShaderInputs { get; init; } = [];
    public NativeInputElement[] InputElements { get; init; } = [];
    public bool InputLayoutVerified { get; init; }
    public bool InputLayoutFitsSignature { get; init; }
    public NativeLayoutEvidence InputLayoutEvidence { get; init; }
    public int NativeDeclarationId { get; init; } = -1;
    public uint NativeDeclarationTag { get; init; }
    public int RenderStage { get; init; } = -1;
    public string LayoutStatus { get; init; } = "Input layout is unverified.";
    public Vector4 PositionScale { get; init; }
    public Vector4 PositionOffset { get; init; }
    public Vector4 UvTransform { get; init; }
    public Vector4 TerrainOffset { get; init; }
    /** Terrain group dye texture for pixel t14; uint.MaxValue binds no texture. */
    public uint TerrainDyeTexture { get; init; } = uint.MaxValue;
    public required byte[] SourceHeader { get; init; }
    public uint ResourceWord38 { get; init; }
    public uint BufferGroupWord0C { get; init; }
}
