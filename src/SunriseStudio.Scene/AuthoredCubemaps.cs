using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Scene;

/** SourceData begins at resource +0x10, as copied by the native component loader. */
public sealed record AuthoredCubemap(ReadOnlyMemory<byte> ResourceData)
{
    public ReadOnlyMemory<byte> SourceData => ResourceData.Slice(0x10, 0x1A0);
    public Vector3 Extents => Vector3At(0x10);
    public Vector4 SourceVector20 => VectorAt(0x20);
    public Vector4 FadeA => VectorAt(0x40);
    public Vector4 FadeB => VectorAt(0x50);
    public Matrix4x4 SourceMatrixB0 => MatrixAt(0xB0);
    public Matrix4x4 SourceMatrixF0 => MatrixAt(0xF0);
    public int ResolutionX => IntegerAt(0x130);
    public int ResolutionY => IntegerAt(0x134);
    public int ResolutionZ => IntegerAt(0x138);
    public uint Texture0Tag => UnsignedAt(0x188);
    public uint Texture1Tag => UnsignedAt(0x18C);
    public uint Texture2Tag => UnsignedAt(0x190);
    public ulong VisibilityBinding => BinaryPrimitives.ReadUInt64LittleEndian(ResourceData.Span[0x1B0..]);

    /** Reads a little-endian float at the byte offset of SourceData. */
    public float FloatAt(int offset) => BitConverter.UInt32BitsToSingle(UnsignedAt(offset));
    /** Reads a little-endian int32 at the byte offset of SourceData. */
    public int IntegerAt(int offset) => BinaryPrimitives.ReadInt32LittleEndian(SourceData.Span[offset..]);
    /** Reads a little-endian uint32 at the byte offset of SourceData. */
    public uint UnsignedAt(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(SourceData.Span[offset..]);
    /** Reads three consecutive floats at the byte offset. */
    public Vector3 Vector3At(int offset) => new(FloatAt(offset), FloatAt(offset + 4), FloatAt(offset + 8));
    /** Reads a Vector3 plus a fourth float at the byte offset. */
    public Vector4 VectorAt(int offset) => new(Vector3At(offset), FloatAt(offset + 12));
    /** Reads sixteen consecutive floats as a matrix. */
    public Matrix4x4 MatrixAt(int offset) => new(
        FloatAt(offset), FloatAt(offset + 4), FloatAt(offset + 8), FloatAt(offset + 12),
        FloatAt(offset + 16), FloatAt(offset + 20), FloatAt(offset + 24), FloatAt(offset + 28),
        FloatAt(offset + 32), FloatAt(offset + 36), FloatAt(offset + 40), FloatAt(offset + 44),
        FloatAt(offset + 48), FloatAt(offset + 52), FloatAt(offset + 56), FloatAt(offset + 60));
}

/** Authored matrices already include world placement; PlacementTransform is ownership metadata. */
public sealed record PlacedCubemap(
    uint BubbleHash,
    uint PlacementSourceTag,
    int PlacementSourceOffset,
    int AuxiliaryOffset,
    Matrix4x4 PlacementTransform,
    AuthoredCubemap Cubemap,
    SceneMarker OwnerMarker)
{
    public IReadOnlyList<MapContainerInfo> MapContainers => OwnerMarker.MapContainers;
}

/** Cubemap part of AssetSceneLoader: reads authored cubemap placements. */
public sealed partial class AssetSceneLoader
{
    /** Resource class and payload bounds must be checked before any field reaches a renderer. */
    public AuthoredCubemap ReadCubemap(uint placementTag, int auxiliaryOffset)
    {
        if (Class(placementTag) != 0x808099D6)
            throw new InvalidDataException("Cubemap source is not a placement list.");
        var data = Read(placementTag);
        if (!data.Has(auxiliaryOffset - 4, 0x1C4)
            || data.U32(auxiliaryOffset - 4) != 0x80806B7F)
            throw new InvalidDataException("Cubemap resource has the wrong class or size.");
        var result = new AuthoredCubemap(data.Bytes.AsMemory(auxiliaryOffset, 0x1C0));
        foreach (int offset in new[] { 0x10, 0x20, 0x40, 0x50, 0x60, 0x80, 0x90,
            0xB0, 0xC0, 0xD0, 0xE0, 0xF0, 0x100, 0x110, 0x120, 0x140, 0x150 })
        {
            var value = result.VectorAt(offset);
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y)
                || !float.IsFinite(value.Z) || !float.IsFinite(value.W))
                throw new InvalidDataException("Cubemap contains a nonfinite transform or parameter.");
        }
        if (result.Extents.X <= 0 || result.Extents.Y <= 0 || result.Extents.Z <= 0)
            throw new InvalidDataException("Cubemap volume extents must be positive.");
        foreach (uint texture in new[] { result.Texture0Tag, result.Texture1Tag, result.Texture2Tag })
            if (texture != uint.MaxValue
                && (!catalog.TryGetEntry(texture, out var entry) || entry.Type != 32))
                throw new InvalidDataException($"Cubemap texture 0x{texture:X8} is absent or has the wrong type.");
        return result;
    }

    /** A bad cubemap keeps its placement marker and cannot discard neighboring resources. */
    private void ReadPlacedCubemap(uint tag, int auxiliary, Matrix4x4 placement, SceneMarker marker)
    {
        try
        {
            scene.Cubemaps.Add(new(marker.BubbleHash, tag, marker.SourceOffset,
                auxiliary, placement, ReadCubemap(tag, auxiliary), marker));
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException
            or OverflowException or IndexOutOfRangeException or IOException or KeyNotFoundException)
        {
            scene.DecodeErrorCount++;
            scene.Diagnostics.Add($"Cannot decode cubemap in 0x{tag:X8}: {error.Message}");
        }
    }
}
