using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vortice.Direct3D11;

namespace SunriseStudio.Rendering;

/**
 * Which of the two depth-stencil tables a state selection indexes. This file also loads the native
 * render-state catalog.
 */
public enum NativeDepthTable { Primary, Alternate }
/** Indices into the native blend, depth-stencil, rasterizer and bias tables. */
public readonly record struct NativeStateSelection(byte Blend, byte DepthStencil, byte Rasterizer, byte Bias);
/** D3D11 state descriptions resolved from a NativeStateSelection. */
public sealed record NativeRenderStateDescriptions(NativeStateSelection Selection,
    BlendDescription Blend, DepthStencilDescription DepthStencil, RasterizerDescription Rasterizer,
    Vector4 BlendFactor);

/** Build 86657 constructs these descriptions from fixed native tables. */
public sealed class NativeRenderStateCatalog
{
    private sealed class Tables
    {
        public int Build { get; set; }
        public uint[][] Blend { get; set; } = [];
        public uint[][] Depth { get; set; } = [];
        public uint[][] AlternateDepth { get; set; } = [];
        public uint[][] Stencil { get; set; } = [];
        public uint[][] Rasterizer { get; set; } = [];
        public uint[][] Bias { get; set; } = [];
        public uint[][] DepthStatePairs { get; set; } = [];
        public uint[][] BlendFactor { get; set; } = [];
    }

    private readonly Tables tables;
    public int BlendCount => tables.Blend.Length;
    public int DepthStencilCount => tables.DepthStatePairs.Length;
    public int RasterizerCount => tables.Rasterizer.Length;
    public int BiasCount => tables.Bias.Length;

    /** Reject a changed snapshot or ABI instead of constructing different native states. */
    public NativeRenderStateCatalog()
    {
        using var stream = typeof(NativeRenderStateCatalog).Assembly.GetManifestResourceStream("NativeRenderStates86657.json")
            ?? throw new InvalidOperationException("Native render-state snapshot is missing.");
        tables = JsonSerializer.Deserialize<Tables>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Native render-state snapshot is invalid.");
        if (tables.Build != 86657 || Marshal.SizeOf<BlendDescription>() != 264
            || Marshal.SizeOf<DepthStencilDescription>() != 52 || Marshal.SizeOf<RasterizerDescription>() != 40)
            throw new NotSupportedException("Native render-state ABI does not match build 86657.");
        ValidateRows(tables.Blend, 90, 33); ValidateRows(tables.Depth, 13, 4);
        ValidateRows(tables.AlternateDepth, 13, 4); ValidateRows(tables.Stencil, 49, 11);
        ValidateRows(tables.Rasterizer, 9, 11); ValidateRows(tables.Bias, 9, 4);
        ValidateRows(tables.DepthStatePairs, 83, 2); ValidateRows(tables.BlendFactor, 1, 4);
    }

    private static void ValidateRows(uint[][] rows, int count, int words)
    {
        if (rows.Length != count || rows.Any(row => row.Length != words))
            throw new InvalidDataException("Native render-state table shape is invalid.");
    }

    /** Each high bit selects an override; forced bytes take precedence over material bytes. */
    public static NativeStateSelection ResolveSelection(uint passStates, uint materialOverrides, uint forcedOverrides)
    {
        byte Resolve(int shift)
        {
            byte value = (byte)(passStates >> shift);
            byte material = (byte)(materialOverrides >> shift), forced = (byte)(forcedOverrides >> shift);
            if ((material & 0x80) != 0) value = material;
            if ((forced & 0x80) != 0) value = forced;
            return (byte)(value & 0x7F);
        }
        return new(Resolve(0), Resolve(8), Resolve(16), Resolve(24));
    }

    /** Depth-table choice stays explicit until the native view selector is reproduced. */
    public NativeRenderStateDescriptions Resolve(uint passStates, uint materialOverrides,
        uint forcedOverrides, NativeDepthTable depthTable)
    {
        var selection = ResolveSelection(passStates, materialOverrides, forcedOverrides);
        uint[] factor = tables.BlendFactor[0];
        return new(selection, GetBlend(selection.Blend), GetDepthStencil(selection.DepthStencil, depthTable),
            GetRasterizer(selection.Rasterizer, selection.Bias),
            new(F32(factor[0]), F32(factor[1]), F32(factor[2]), F32(factor[3])));
    }

    /** The native creator enables independent blending and disables color writes on targets 4..7. */
    public BlendDescription GetBlend(int index)
    {
        uint[] row = Row(tables.Blend, index);
        byte[] bytes = new byte[264];
        Write(bytes, 4, 1);
        for (int target = 0; target < 8; target++)
        {
            int source = 1 + Math.Min(target, 3) * 8;
            for (int word = 0; word < 8; word++) Write(bytes, 8 + target * 32 + word * 4, row[source + word]);
            if (target >= 4) bytes[8 + target * 32 + 28] = 0;
        }
        return MemoryMarshal.Read<BlendDescription>(bytes);
    }

    /** State ids select depth/stencil pairs; the alternate table only replaces the depth comparison. */
    public DepthStencilDescription GetDepthStencil(int index, NativeDepthTable depthTable)
    {
        if (depthTable is not (NativeDepthTable.Primary or NativeDepthTable.Alternate))
            throw new ArgumentOutOfRangeException(nameof(depthTable));
        uint[] pair = Row(tables.DepthStatePairs, index);
        uint[] depth = Row(tables.Depth, (int)pair[0]);
        uint[] alternate = Row(tables.AlternateDepth, (int)pair[0]);
        uint[] stencil = Row(tables.Stencil, (int)pair[1]);
        byte[] bytes = new byte[52];
        Write(bytes, 0, depth[1]); Write(bytes, 4, depth[2]);
        Write(bytes, 8, depthTable == NativeDepthTable.Primary ? depth[3] : alternate[3]);
        Write(bytes, 12, stencil[1]);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), (ushort)stencil[2]);
        Write(bytes, 20, stencil[5]); Write(bytes, 24, stencil[6]);
        Write(bytes, 28, stencil[4]); Write(bytes, 32, stencil[3]);
        Write(bytes, 36, stencil[9]); Write(bytes, 40, stencil[10]);
        Write(bytes, 44, stencil[8]); Write(bytes, 48, stencil[7]);
        return MemoryMarshal.Read<DepthStencilDescription>(bytes);
    }

    /** @return The first depth/stencil id with the same depth row and stencil off. */
    public int WithoutStencil(int index)
    {
        uint depth = Row(tables.DepthStatePairs, index)[0];
        for (int candidate = 0; candidate < tables.DepthStatePairs.Length; candidate++)
            if (tables.DepthStatePairs[candidate][0] == depth
                && Row(tables.Stencil, (int)tables.DepthStatePairs[candidate][1])[1] == 0)
                return candidate;
        throw new NotSupportedException("No depth/stencil state has this depth row without stencil.");
    }

    /** Bias presets replace all three bias values; native raster creation disables multisampling. */
    public RasterizerDescription GetRasterizer(int index, int biasIndex)
    {
        uint[] rasterizer = Row(tables.Rasterizer, index), bias = Row(tables.Bias, biasIndex);
        byte[] bytes = new byte[40];
        for (int word = 0; word < 10; word++) Write(bytes, word * 4, rasterizer[word + 1]);
        Write(bytes, 12, bias[1]); Write(bytes, 16, bias[3]); Write(bytes, 20, bias[2]);
        Write(bytes, 32, 0);
        return MemoryMarshal.Read<RasterizerDescription>(bytes);
    }

    private static uint[] Row(uint[][] rows, int index) => (uint)index < rows.Length ? rows[index]
        : throw new ArgumentOutOfRangeException(nameof(index), "Native render-state index is outside its table.");
    private static float F32(uint bits) => BitConverter.UInt32BitsToSingle(bits);
    private static void Write(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
