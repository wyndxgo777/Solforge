using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Live;

/** Whether the game has answered an object request, and how. */
public enum LiveObjectStatus : uint { Pending, Complete, Absent, Unavailable }
/** A child object, identified by handle plus generation so reused slots are not confused. */
public sealed record LiveObjectChild(uint Handle, uint Generation);

/**
 * The game's one answer to one object request. Parent and children come from the object's datum and
 * the last placement scan; models come from the last animation capture.
 */
public sealed record LiveObjectSnapshot(uint Handle, uint Generation, LiveObjectStatus Status, ulong Time)
{
    public ulong Identity { get; init; }
    public uint ObjectListTag { get; init; }
    public uint EntryIndex { get; init; }
    public uint ClassTag { get; init; }
    public bool Dynamic { get; init; }
    /** Null when the game could not read the datum. */
    public uint? DatumFlags { get; init; }
    /** The game's placed-and-not-hidden test; null when the parent chain was not read. */
    public bool? PlacedNotHidden { get; init; }
    /** The top of the parent chain and its flags; the placed bit lives on the root. */
    public uint RootHandle { get; init; } = uint.MaxValue;
    public uint RootFlags { get; init; }
    /** Flags of the object and every ancestor, combined. */
    public uint ChainFlags { get; init; }
    public uint ParentHandle { get; init; } = uint.MaxValue;
    public bool TransformAvailable { get; init; }
    public Vector3 Position { get; init; }
    public Quaternion Orientation { get; init; } = Quaternion.Identity;
    public float? Scale { get; init; }
    public IReadOnlyList<LiveObjectChild> Children { get; init; } = [];
    public bool ChildrenTruncated { get; init; }
    public IReadOnlyList<uint> Models { get; init; } = [];
    /** Time of the animation capture the models come from; 0 when none was published. */
    public ulong PoseTime { get; init; }
    /** Raw datum bytes over the layout the game reader knows. */
    public byte[] Datum { get; init; } = [];
}

/** One request and one answer per selected object; the game never pushes this record. */
public static class LiveObjectProtocol
{
    public const uint Magic = 0x424F5353, Version = 1;
    public const int HeartbeatOffset = 16, RequestOffset = 24, SequenceOffset = 28;
    public const int HandleOffset = 32, GenerationOffset = 36, ResponseOffset = 40, ClientVersionOffset = 52;
    public const int ChildCapacity = 64, ModelCapacity = 32, DatumCapacity = 256;
    public const int ChildrenOffset = 160, ModelsOffset = 672, DatumOffset = 800;
    public const int MappingBytes = DatumOffset + DatumCapacity;
    /** Per-process shared-memory name for the object-query channel. */
    public static string MappingName(int processId) => $@"Local\SunriseStudio.Object.{processId}";

    /**
     * Reads one answer.
     * @return Null when the bytes answer a different request.
     */
    public static LiveObjectSnapshot? Read(ReadOnlySpan<byte> bytes, uint expectedProcess, uint request, uint handle, uint generation)
    {
        if (bytes.Length < MappingBytes || U32(bytes, 0) != Magic || U32(bytes, 4) != Version
            || U32(bytes, 8) != MappingBytes || (U32(bytes, SequenceOffset) & 1) != 0)
            throw new InvalidDataException("The object answer uses an incompatible layout.");
        if (U32(bytes, ResponseOffset) != request) return null;
        if (U32(bytes, 48) != expectedProcess) throw new InvalidDataException("The object answer belongs to another process.");
        var status = (LiveObjectStatus)U32(bytes, 44);
        if (status is < LiveObjectStatus.Complete or > LiveObjectStatus.Unavailable)
            throw new InvalidDataException("The object answer has an unknown status.");
        var result = new LiveObjectSnapshot(handle, generation, status, U64(bytes, 56));
        if (status != LiveObjectStatus.Complete) return result;
        uint flags = U32(bytes, 92), children = U32(bytes, 128), models = U32(bytes, 132), datum = U32(bytes, 144);
        if (children > ChildCapacity || models > ModelCapacity || datum > DatumCapacity)
            throw new InvalidDataException("The object answer counts exceed their buffers.");
        bool transform = (flags & 1) != 0;
        var orientation = new Quaternion(F(bytes, 112), F(bytes, 116), F(bytes, 120), F(bytes, 124));
        float orientationLength = orientation.LengthSquared();
        transform &= float.IsFinite(orientationLength) && orientationLength >= 1e-8f;
        var childRows = new LiveObjectChild[children];
        for (int i = 0; i < childRows.Length; i++)
            childRows[i] = new(U32(bytes, ChildrenOffset + i * 8), U32(bytes, ChildrenOffset + i * 8 + 4));
        var modelTags = new uint[models];
        for (int i = 0; i < modelTags.Length; i++) modelTags[i] = U32(bytes, ModelsOffset + i * 4);
        return result with
        {
            Identity = U64(bytes, 64), ObjectListTag = U32(bytes, 72), EntryIndex = U32(bytes, 76),
            ClassTag = U32(bytes, 80), Dynamic = (flags & 2) != 0,
            DatumFlags = (flags & 32) != 0 ? U32(bytes, 84) : null,
            PlacedNotHidden = (flags & 64) != 0 ? (flags & 4) != 0 : null,
            RootHandle = (flags & 64) != 0 ? U32(bytes, 148) : uint.MaxValue,
            RootFlags = U32(bytes, 152), ChainFlags = U32(bytes, 156),
            ParentHandle = U32(bytes, 88), TransformAvailable = transform,
            Position = transform ? new(F(bytes, 96), F(bytes, 100), F(bytes, 104)) : Vector3.Zero,
            Scale = transform ? F(bytes, 108) : null,
            Orientation = transform ? Quaternion.Normalize(orientation) : Quaternion.Identity,
            Children = childRows, ChildrenTruncated = (flags & 8) != 0, Models = modelTags,
            PoseTime = (flags & 16) != 0 ? U64(bytes, 136) : 0,
            Datum = bytes.Slice(DatumOffset, (int)datum).ToArray()
        };
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static float F(ReadOnlySpan<byte> bytes, int offset)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
        if (!float.IsFinite(value)) throw new InvalidDataException("The object answer has a non-finite transform.");
        return value;
    }
}
