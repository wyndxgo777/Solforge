using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace SunriseStudio.Live;

/** One loaded map slice: bubble hash, index and residency state. */
public sealed record LiveSlice(uint BubbleHash, ushort Index, byte Residency);
/** Map, scenario and activity the game currently has loaded, with its slice list. */
public sealed record LiveWorld(ulong Time, bool Available, bool Present, uint MapDefinitionTag,
    uint MapNameHash, uint ScenarioTag, uint MapRootTag, int? CurrentSlice, string Activity, IReadOnlyList<LiveSlice> Slices);
/**
 * One object the game's registry lists. PlacedNotHidden is the game's test: built, its root linked
 * into spatial cells, and nothing up its parent chain hides it. Null means the chain was not read.
 */
public sealed record LivePlacement(ulong Identity, uint ObjectListTag, uint EntryIndex,
    uint Handle, uint Generation, uint DatumFlags,
    Vector3 Position, Quaternion Orientation, ulong Time, bool TransformAvailable, uint ClassTag, bool Dynamic)
{
    public float? Scale { get; init; }
    public bool? PlacedNotHidden { get; init; } = true;
    /** The object hangs from the object the local player controls. */
    public bool PlayerAttached { get; init; }
    /** Exactly at the world origin. Studio only labels it; the game's test decides the model. */
    public bool AtOrigin => TransformAvailable && Position == Vector3.Zero;
    /** Studio draws the full model only when the game's test passes; other rows are drawn as markers. */
    public bool ShowsModel => PlacedNotHidden != false;
}
/** Excluded counts the rows the game left out because their object was destroyed or reused. */
public sealed record LivePlacements(ulong Time, bool Available, bool Complete, bool Truncated,
    uint Total, ulong Overflow, ulong Epoch, ulong Started, IReadOnlyList<LivePlacement> Rows)
{
    public uint Excluded { get; init; }
    /** Only an exhaustive snapshot proves that an object missing from it is gone. */
    public bool Exhaustive => Available && Complete && !Truncated && Overflow == 0 && Total == Rows.Count + Excluded;
}

/** World and placement sections have separate sequences so entity sampling cannot stall the camera. */
public static class LiveWorldProtocol
{
    public const uint Magic = 0x574C5353;
    public const uint Version = 4;
    public const int ControlBytes = 32;
    public const int WorldBytes = 4192;
    public const int PlacementHeaderBytes = 64;
    public const int PlacementBytes = 80;
    public const int PlacementCapacity = 16384;
    public const int PlacementsBytes = PlacementHeaderBytes + PlacementCapacity * PlacementBytes;
    public const int PlacementsOffset = ControlBytes + WorldBytes;
    public const int MappingBytes = PlacementsOffset + PlacementsBytes;
    public const ulong StaleAfterMilliseconds = 5000;
    /** Per-process shared-memory name for the world channel. */
    public static string MappingName(int processId) => $@"Local\SunriseStudio.World.{processId}";

    /** True while an observed time is recent enough to trust. */
    public static bool Fresh(ulong observed, ulong now) => observed != 0 && observed <= now
        && now - observed <= StaleAfterMilliseconds;

    /** Reads the world section; null when it is stale. */
    public static LiveWorld? ReadWorld(ReadOnlySpan<byte> bytes, ulong now)
    {
        if (bytes.Length != WorldBytes || (U32(bytes, 0) & 1) != 0)
            throw new InvalidDataException("The world snapshot is incomplete.");
        ulong time = U64(bytes, 8);
        if (!Fresh(time, now)) return null;
        uint flags = U32(bytes, 4), count = U32(bytes, 28);
        if (count > 512) throw new InvalidDataException("The world snapshot has too many slices.");
        int current = unchecked((int)U32(bytes, 24));
        if (current is < -1 or > 511) throw new InvalidDataException("The current slice is invalid.");
        var slices = new List<LiveSlice>((int)count);
        var seen = new HashSet<ushort>();
        for (int i = 0; i < count; i++)
        {
            int at = 96 + i * 8;
            ushort index = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 4)..]);
            byte residency = bytes[at + 6];
            if (index >= 512 || residency is not (3 or 4) || !seen.Add(index))
                throw new InvalidDataException("The world snapshot contains an invalid loaded slice.");
            slices.Add(new(U32(bytes, at), index, residency));
        }
        var activity = bytes.Slice(32, 40);
        int terminator = activity.IndexOf((byte)0);
        string name = Encoding.UTF8.GetString(terminator < 0 ? activity : activity[..terminator]);
        bool available = (flags & 1) != 0, present = available && (flags & 2) != 0;
        return new(time, available, present, present ? U32(bytes, 16) : 0,
            present ? U32(bytes, 20) : 0, present ? U32(bytes, 72) : 0, present ? U32(bytes, 76) : 0,
            present && current >= 0 ? current : null,
            present ? name : "", present ? slices : []);
    }

    /** The row count at header offset 16; the caller copies only the rows it names. */
    public static int PlacementCount(ReadOnlySpan<byte> header) =>
        (int)Math.Min(U32(header, 16), (uint)PlacementCapacity);

    /**
     * Row timestamps expose a bounded scan's age instead of claiming one atomic game tick.
     * @param bytes The header and at least `count` rows.
     */
    public static LivePlacements? ReadPlacements(ReadOnlySpan<byte> bytes, ulong now)
    {
        if (bytes.Length < PlacementHeaderBytes || (U32(bytes, 0) & 1) != 0)
            throw new InvalidDataException("The placement snapshot is incomplete.");
        ulong time = U64(bytes, 8);
        if (!Fresh(time, now)) return null;
        uint flags = U32(bytes, 4), count = U32(bytes, 16), total = U32(bytes, 20), excluded = U32(bytes, 48);
        if (count > PlacementCapacity || (ulong)count + excluded > total
            || bytes.Length < PlacementHeaderBytes + (long)count * PlacementBytes)
            throw new InvalidDataException("The placement snapshot count is invalid.");
        var rows = new List<LivePlacement>((int)count);
        var seen = new HashSet<(uint, uint)>();
        for (int i = 0; i < count; i++)
        {
            int at = PlacementHeaderBytes + i * PlacementBytes;
            uint rowFlags = U32(bytes, at + 72);
            bool validTransform = (rowFlags & 1) != 0 && Fresh(U64(bytes, at + 64), now);
            uint handle = U32(bytes, at + 16), generation = U32(bytes, at + 20);
            if (handle == uint.MaxValue || !seen.Add((handle, generation)))
                throw new InvalidDataException("The placement snapshot contains a duplicate or invalid handle.");
            var position = new Vector3(Float(bytes, at + 32), Float(bytes, at + 36), Float(bytes, at + 40));
            float scale = Float(bytes, at + 44);
            var orientation = new Quaternion(Float(bytes, at + 48), Float(bytes, at + 52),
                Float(bytes, at + 56), Float(bytes, at + 60));
            float orientationLength = orientation.LengthSquared();
            validTransform &= float.IsFinite(orientationLength) && orientationLength >= 1e-8f;
            rows.Add(new(U64(bytes, at), U32(bytes, at + 8), U32(bytes, at + 12), handle, generation,
                U32(bytes, at + 28), position,
                validTransform ? Quaternion.Normalize(orientation) : Quaternion.Identity,
                U64(bytes, at + 64), validTransform, U32(bytes, at + 24), (rowFlags & 2) != 0)
            {
                Scale = validTransform && (rowFlags & 4) != 0 ? scale : null,
                PlacedNotHidden = (rowFlags & 32) == 0 ? null : (rowFlags & 8) != 0, PlayerAttached = (rowFlags & 16) != 0
            });
        }
        return new(time, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0,
            total, U64(bytes, 24), U64(bytes, 32), U64(bytes, 40), rows) { Excluded = excluded };
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static float Float(ReadOnlySpan<byte> bytes, int offset)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
        if (!float.IsFinite(value)) throw new InvalidDataException("The placement has a non-finite transform.");
        return value;
    }
}
