using System.Numerics;

namespace SunriseStudio.Scene;

/**
 * Authored light parameter block: the TFX bytecode with its constants and defaults. This file holds
 * the authored light records and their reader.
 */
public sealed record AuthoredLightParameters(
    uint TagHash,
    ReadOnlyMemory<byte> SourceData,
    ReadOnlyMemory<byte> Bytecode,
    IReadOnlyList<Vector4> Constants,
    IReadOnlyList<Vector4> Defaults);

/** One authored probe or volumetric light: its placement, materials, parameters and flags. */
public sealed record AuthoredLightRow(
    int Index,
    int SourceOffset,
    int TransformOffset,
    Quaternion Rotation,
    Vector3 Translation,
    float Scale,
    Matrix4x4 Transform,
    Matrix4x4 VolumeTransform,
    Vector4 SourceVector60,
    Vector4 SourceVector70,
    uint MaterialTag,
    uint VolumetricMaterialTag,
    AuthoredLightParameters? ProbeParameters,
    AuthoredLightParameters? SkipParameters,
    ushort Flags,
    ReadOnlyMemory<byte> SourceData,
    IReadOnlyList<int> AuthoredStages)
{
    // A placement update moves the row in place, so world lights keep their identity.
    public Quaternion Rotation { get; internal set; } = Rotation;
    public Vector3 Translation { get; internal set; } = Translation;
    public float Scale { get; internal set; } = Scale;
    public Matrix4x4 Transform { get; internal set; } = Transform;
    public Matrix4x4 VolumeWorldTransform => VolumeTransform * Transform;
}

/** An authored light table: probe and volumetric rows plus the bounds tag. */
public sealed record AuthoredLightTable(
    uint TagHash,
    string RendererName,
    int ProbeRowCount,
    int VolumetricRowCount,
    uint BoundsTag,
    IReadOnlyList<AuthoredLightRow> Rows);

/** A light table as placed in a bubble, with its placement transform and owner marker. */
public sealed record PlacedLightTable(
    uint BubbleHash,
    uint PlacementSourceTag,
    int PlacementSourceOffset,
    int AuxiliaryOffset,
    Matrix4x4 PlacementTransform,
    AuthoredLightTable Table,
    SceneMarker? OwnerMarker)
{
    public Matrix4x4 PlacementTransform { get; internal set; } = PlacementTransform;
    public IReadOnlyList<MapContainerInfo> MapContainers => OwnerMarker?.MapContainers ?? [];
}

/** Light part of AssetSceneLoader: reads placed light tables and their rows. */
public sealed partial class AssetSceneLoader
{
    private readonly Dictionary<uint, AuthoredLightTable> lightTables = [];
    private readonly Dictionary<uint, AuthoredLightParameters> lightParameters = [];

    /** One missing light dependency cannot suppress sibling placement rows. */
    private void ReadPlacedLights(uint tableTag, SceneMarker marker, int auxiliary,
        Matrix4x4 placement)
    {
        try
        {
            scene.LightTables.Add(new PlacedLightTable(marker.BubbleHash, marker.SourceTag,
                marker.SourceOffset, auxiliary, placement,
                ReadLightTable(tableTag, cancellation), marker));
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException
            or OverflowException or IndexOutOfRangeException or IOException or KeyNotFoundException)
        {
            scene.DecodeErrorCount++;
            scene.Diagnostics.Add($"Cannot decode light table 0x{tableTag:X8}: {error.Message}");
        }
    }

    /** Table transforms are world QTS values; the placement transform is not applied again. */
    public AuthoredLightTable ReadLightTable(uint tag, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (lightTables.TryGetValue(tag, out var cached)) return cached;
        if (Class(tag) != 0x8080713A)
            throw new InvalidDataException("Light table has the wrong class.");
        var data = Read(tag);
        var rows = data.Rows(0x30, 0x8080713E, 160);
        var transforms = data.Rows(0x40, 0x80809F75, 32);
        var probeCount = data.I32(0x50);
        var volumetricCount = data.I32(0x54);
        if (transforms.Length != rows.Length || probeCount < 0 || probeCount > rows.Length
            || volumetricCount < 0 || volumetricCount > rows.Length)
            throw new InvalidDataException("Light row counts leave their typed arrays.");
        var output = new List<AuthoredLightRow>(rows.Length);
        for (var index = 0; index < rows.Length; ++index)
        {
            token.ThrowIfCancellationRequested();
            var row = rows[index];
            var transformRow = transforms[index];
            var material = LightReference(data.U32(row + 0x80), 0x808071E8);
            var volumetric = LightReference(data.U32(row + 0x84), 0x808071E8);
            var probe = ReadLightParameters(data.U32(row + 0x88));
            var skip = ReadLightParameters(data.U32(row + 0x8C));
            var flags = data.U16(row + 0x98);
            var stages = new List<int>();
            if ((flags & 0x2000) == 0 && material != uint.MaxValue) stages.Add(4);
            if (index < probeCount && probe != null) stages.Add(5);
            if (index < volumetricCount && (flags & 0x1000) != 0
                && volumetric != uint.MaxValue) stages.Add(19);
            var pose = LightPose(data, transformRow);
            output.Add(new AuthoredLightRow(index, row, transformRow, pose.Rotation,
                pose.Translation, pose.Scale, pose.Transform, LightMatrix(data, row + 0x20),
                LightVector(data, row + 0x60), LightVector(data, row + 0x70), material,
                volumetric, probe, skip, flags, data.Bytes.AsMemory(row, 160), stages));
        }
        var table = new AuthoredLightTable(tag, "chunked_lights_renderer", probeCount,
            volumetricCount, data.U32(0x58), output);
        lightTables.Add(tag, table);
        return table;
    }

    /** A light transform row is a world QTS: rotation, translation, then scale in the last lane. */
    internal static (Quaternion Rotation, Vector3 Translation, float Scale, Matrix4x4 Transform) LightPose(
        TagData data, int row) => (data.Q4(row), data.V3(row + 16), data.F32(row + 28), Placement(data, row));

    /** Defaults must be copied before the embedded program changes its output vectors. */
    private AuthoredLightParameters? ReadLightParameters(uint tag)
    {
        if (tag == uint.MaxValue) return null;
        if (lightParameters.TryGetValue(tag, out var cached)) return cached;
        LightReference(tag, 0x808071DC);
        var data = Read(tag);
        var code = data.Rows(0x30, 0x80800009, 1);
        var constants = data.Rows(0x40, 0x80800090, 16)
            .Select(row => LightVector(data, row)).ToArray();
        var defaults = data.Rows(0x60, 0x80800090, 16)
            .Select(row => LightVector(data, row)).ToArray();
        var result = new AuthoredLightParameters(tag, data.Bytes,
            code.Length == 0 ? ReadOnlyMemory<byte>.Empty
                : data.Bytes.AsMemory(code[0], code.Length), constants, defaults);
        lightParameters.Add(tag, result);
        return result;
    }

    private uint LightReference(uint tag, uint expectedClass)
    {
        if (tag != uint.MaxValue && Class(tag) != expectedClass)
            throw new InvalidDataException($"Light reference 0x{tag:X8} has the wrong class.");
        return tag;
    }

    private static Vector4 LightVector(TagData data, int row) =>
        new(data.F32(row), data.F32(row + 4), data.F32(row + 8), data.F32(row + 12));

    private static Matrix4x4 LightMatrix(TagData data, int row) => new(
        data.F32(row), data.F32(row + 4), data.F32(row + 8), data.F32(row + 12),
        data.F32(row + 16), data.F32(row + 20), data.F32(row + 24), data.F32(row + 28),
        data.F32(row + 32), data.F32(row + 36), data.F32(row + 40), data.F32(row + 44),
        data.F32(row + 48), data.F32(row + 52), data.F32(row + 56), data.F32(row + 60));
}
