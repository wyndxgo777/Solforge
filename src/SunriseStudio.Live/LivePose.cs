using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Live;

/** Layout of one bone's palette entry in shared memory: dual quaternion, 3x4 or 4x4 matrix. */
public enum LivePaletteKind : uint { DualQuaternion = 1, Matrix3x4 = 2, Matrix4x4 = 3 }

/** One skinned draw's world-space palette; rows are copies and never alias shared memory. */
public sealed class LiveDraw
{
    public ulong Time { get; }
    public uint Owner { get; }
    public uint Component { get; }
    public uint OwnerGeneration { get; }
    public uint ComponentGeneration { get; }
    public uint ModelTag { get; }
    public LivePaletteKind Kind { get; }
    public int ModelBones { get; }
    public int PoseBones { get; }
    public IReadOnlyList<Vector4> FixedRows { get; }
    public IReadOnlyList<Vector4> Palette { get; }

    public LiveDraw(ulong time, uint owner, uint component, uint modelTag, LivePaletteKind kind,
        int modelBones, int poseBones, IEnumerable<Vector4> fixedRows, IEnumerable<Vector4> palette,
        uint ownerGeneration, uint componentGeneration)
        : this(time, owner, component, modelTag, kind, modelBones, poseBones, fixedRows.Take(4).ToArray(),
            palette.Take(CopyLimit(kind, modelBones)).ToArray(), ownerGeneration, componentGeneration)
    {
    }

    /** Takes ownership of both arrays; the caller must not keep or change them. */
    internal LiveDraw(ulong time, uint owner, uint component, uint modelTag, LivePaletteKind kind,
        int modelBones, int poseBones, Vector4[] fixedCopy, Vector4[] copy,
        uint ownerGeneration, uint componentGeneration)
    {
        if (owner == uint.MaxValue || modelTag is 0 or uint.MaxValue || modelBones is < 1 or > LivePoseProtocol.BoneCapacity
            || component == uint.MaxValue || ownerGeneration == uint.MaxValue || componentGeneration == uint.MaxValue
            || poseBones < modelBones || poseBones > LivePoseProtocol.BoneCapacity)
            throw new InvalidDataException("Live draw identity or bone count is invalid.");
        int count = checked(modelBones * LivePoseProtocol.VectorsPerBone(kind));
        if (fixedCopy.Length != 3 || copy.Length != count || !Finite(fixedCopy) || !Finite(copy))
            throw new InvalidDataException("Live draw rows are incomplete or non-finite.");
        var anchor = kind == LivePaletteKind.DualQuaternion ? DualTranslation(copy[0], copy[1])
            : new Vector3(copy[0].W, copy[1].W, copy[2].W);
        if (!float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y) || !float.IsFinite(anchor.Z))
            throw new InvalidDataException("Live draw translation is non-finite.");
        Time = time; Owner = owner; Component = component; ModelTag = modelTag; Kind = kind;
        OwnerGeneration = ownerGeneration; ComponentGeneration = componentGeneration;
        ModelBones = modelBones; PoseBones = poseBones;
        FixedRows = Array.AsReadOnly(fixedCopy); Palette = Array.AsReadOnly(copy);
        Anchor = anchor;
    }

    /** Where the first bone sits in the world; a stand-in position for bounds and picking. */
    public Vector3 Anchor { get; }

    /** One row past the expected count, so a long input still fails the length check. */
    private static int CopyLimit(LivePaletteKind kind, int modelBones) =>
        modelBones is < 1 or > LivePoseProtocol.BoneCapacity ? 0 : modelBones * LivePoseProtocol.VectorsPerBone(kind) + 1;

    private static bool Finite(Vector4[] rows)
    {
        foreach (var value in rows)
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W))
                return false;
        return true;
    }

    private static Vector3 DualTranslation(Vector4 real, Vector4 dual)
    {
        var r = new Vector3(real.X, real.Y, real.Z); var d = new Vector3(dual.X, dual.Y, dual.Z);
        return 2 * (real.W * d - dual.W * r + Vector3.Cross(r, d));
    }
}

/** One game object by handle and generation. */
public sealed record LivePoseOwner(uint Handle, uint Generation);
/** The draws of one captured game frame. */
public sealed record LiveDraws(ulong Time, IReadOnlyList<LiveDraw> Draws, int Dropped, int Rejected,
    LivePoseOwner? Player = null);

/** Wire layout and limits of the skinned-pose shared frame (header, per-draw table, vector pool). */
public static class LivePoseProtocol
{
    public const uint Magic = 0x504C5353, Version = 5;
    public const int BoneCapacity = 512, DrawCapacity = 1024, VectorCapacity = 131072;
    /** Handles name their object slot in the low 13 bits; the owner filter has one bit per slot. */
    public const uint SlotMask = 0x1FFF;
    public const int OwnerWords = (int)(SlotMask + 1) / 32, OwnersOffset = 64;
    public const int ControlBytes = OwnersOffset + OwnerWords * 4, HeaderBytes = 64, DrawBytes = 88;
    public const int DrawsOffset = HeaderBytes, PaletteOffset = HeaderBytes + DrawCapacity * DrawBytes;
    public const int SnapshotBytes = PaletteOffset + VectorCapacity * 16;
    public const int MappingBytes = ControlBytes + SnapshotBytes;
    public const ulong StaleAfterMilliseconds = 500;
    /** Per-process shared-memory name for the pose channel. */
    public static string MappingName(int processId) => $@"Local\SunriseStudio.Pose.{processId}";
    /** Number of Vector4 rows each bone occupies for a palette form. */
    public static int VectorsPerBone(LivePaletteKind kind) => kind switch
    { LivePaletteKind.DualQuaternion => 2, LivePaletteKind.Matrix3x4 => 3, LivePaletteKind.Matrix4x4 => 4,
        _ => throw new InvalidDataException("Live palette form is unsupported.") };
    /** True while a capture time is recent enough to trust. */
    public static bool Fresh(ulong time, ulong now) => time != 0 && time <= now && now - time <= StaleAfterMilliseconds;

    /** Header fields: valid flag, capture time and the draw and vector counts. */
    public static (bool Valid, ulong Time, int Draws, int Vectors, int Dropped) ReadHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderBytes || (U(header, 0) & 1) != 0)
            throw new InvalidDataException("Live draw publication is incomplete.");
        uint draws = U(header, 16), vectors = U(header, 20);
        if (draws > DrawCapacity || vectors > VectorCapacity)
            throw new InvalidDataException("Live draw counts exceed their buffers.");
        return (U(header, 4) != 0, Q(header, 8), (int)draws, (int)vectors, (int)Math.Min(U(header, 24), int.MaxValue));
    }

    /**
     * Reads one captured frame. A malformed draw is counted and skipped; the frame is kept.
     * @param draws Draw rows, `count * DrawBytes` long.
     * @param palette The first `vectors` palette vectors.
     */
    public static LiveDraws? Read(ReadOnlySpan<byte> header, ReadOnlySpan<byte> draws, ReadOnlySpan<byte> palette, ulong now)
    {
        var (valid, time, count, vectors, dropped) = ReadHeader(header);
        if (!valid || !Fresh(time, now)) return null;
        if (draws.Length < count * DrawBytes || palette.Length < vectors * 16)
            throw new InvalidDataException("Live draw buffers are shorter than their counts.");
        var result = new List<LiveDraw>(count); int rejected = 0;
        for (int i = 0; i < count; i++)
        {
            var row = draws.Slice(i * DrawBytes, DrawBytes);
            uint owner = U(row, 0), component = U(row, 4), model = U(row, 8), kind = U(row, 12);
            uint bones = U(row, 16), poseBones = U(row, 20), first = U(row, 24), length = U(row, 28);
            if (owner == 0 && model == 0 && kind == 0) continue;
            try
            {
                if (kind is < 1 or > 3 || bones is < 1 or > BoneCapacity
                    || length != bones * VectorsPerBone((LivePaletteKind)kind) || first > vectors || length > vectors - first)
                    throw new InvalidDataException("Live draw rows leave the captured palette.");
                var fixedRows = new Vector4[3]; var rows = new Vector4[length];
                for (int j = 0; j < 3; j++) fixedRows[j] = V(row, 32 + j * 16);
                for (int j = 0; j < rows.Length; j++) rows[j] = V(palette, (int)(first + j) * 16);
                result.Add(new(time, owner, component, model, (LivePaletteKind)kind, (int)bones, (int)poseBones,
                    fixedRows, rows, U(row, 80), U(row, 84)));
            }
            catch (InvalidDataException) { rejected++; }
        }
        var player = U(header, 32) == 1 && U(header, 36) != uint.MaxValue && U(header, 40) != uint.MaxValue
            ? new LivePoseOwner(U(header, 36), U(header, 40)) : null;
        return new(time, result, dropped, rejected, player);
    }

    private static uint U(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ulong Q(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static Vector4 V(ReadOnlySpan<byte> bytes, int offset) => new(
        BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 4)..]),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 8)..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 12)..]));
}
