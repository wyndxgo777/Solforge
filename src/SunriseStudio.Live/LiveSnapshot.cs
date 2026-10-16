using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Live;

/** Game camera in world space as read from the live frame. */
public sealed record LiveCamera(Vector3 Position, Vector3 Forward, Vector3 Up,
    float HorizontalFieldOfView, float Aspect);
/** PlayerHandle is the full handle of the object the local player controls. */
public sealed record LiveSnapshot(ulong FrameTime, uint ProcessId, bool InWorld, Vector3? Player,
    LiveCamera? Camera, bool SliceAvailable, int? CurrentSlice, uint? PlayerHandle = null);

/** The shared frame uses fixed little-endian fields and no process pointers. */
public static class LiveProtocol
{
    public const uint Magic = 0x564C5353;
    public const uint Version = 1;
    public const int Size = 128;
    public const int HeartbeatOffset = 16;
    public const int SequenceOffset = 24;
    public const ulong StaleAfterMilliseconds = 1500;
    /** Per-process shared-memory name for the main live frame. */
    public static string MappingName(int processId) => $@"Local\SunriseStudio.Live.{processId}";

    /** Unknown fields remain absent; an expired frame cannot drive camera or cursor updates. */
    public static LiveSnapshot? Read(ReadOnlySpan<byte> bytes, uint expectedProcess, ulong now)
    {
        if (bytes.Length != Size || U32(bytes, 0) != Magic || U32(bytes, 4) != Version
            || U32(bytes, 8) != Size || (U32(bytes, 24) & 1) != 0)
            throw new InvalidDataException("The live connection uses an incompatible frame.");
        uint process = U32(bytes, 40);
        ulong time = BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]);
        if (process == 0 || time == 0) return null;
        if (process != expectedProcess) throw new InvalidDataException("The live frame belongs to another process.");
        if (time > now || now - time > StaleAfterMilliseconds) return null;
        uint flags = U32(bytes, 44);
        bool world = (flags & 1) != 0;
        Vector3? player = world && (flags & 2) != 0 ? Vector(bytes, 56) : null;
        LiveCamera? camera = null;
        if (world && (flags & 4) != 0)
        {
            var position = Vector(bytes, 68);
            var forward = Vector(bytes, 80);
            var up = Vector(bytes, 92);
            float fov = Float(bytes, 104), aspect = Float(bytes, 108);
            float forwardLength = forward.LengthSquared(), upLength = up.LengthSquared();
            float crossLength = Vector3.Cross(forward, up).LengthSquared();
            if (!float.IsFinite(forwardLength) || !float.IsFinite(upLength) || !float.IsFinite(crossLength)
                || forwardLength < 1e-6f || upLength < 1e-6f || crossLength < 1e-6f
                || fov <= 0 || fov >= MathF.PI || aspect <= 0)
                throw new InvalidDataException("The live camera has an invalid basis or lens.");
            var direction = Vector3.Normalize(forward);
            var vertical = Vector3.Normalize(up);
            float normalizedCross = Vector3.Cross(direction, vertical).LengthSquared();
            if (!float.IsFinite(normalizedCross) || normalizedCross < 1e-6f)
                throw new InvalidDataException("The normalized live camera basis is degenerate.");
            camera = new(position, direction, vertical, fov, aspect);
        }
        bool sliceAvailable = (flags & 8) != 0;
        int? slice = sliceAvailable && (flags & 16) != 0 ? unchecked((int)U32(bytes, 48)) : null;
        if (slice is < 0 or > 511) throw new InvalidDataException("The live slice index is invalid.");
        uint? playerHandle = world && (flags & 32) != 0 ? U32(bytes, 112) : null;
        return new(time, process, world, player, camera, sliceAvailable, slice, playerHandle);
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static float Float(ReadOnlySpan<byte> bytes, int offset)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
        if (!float.IsFinite(value)) throw new InvalidDataException("The live frame contains a non-finite value.");
        return value;
    }
    private static Vector3 Vector(ReadOnlySpan<byte> bytes, int offset) =>
        new(Float(bytes, offset), Float(bytes, offset + 4), Float(bytes, offset + 8));
}
