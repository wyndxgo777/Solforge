using System.IO.MemoryMappedFiles;

namespace SunriseStudio.Live;

/** Studio owns the mapping; the game fills it with captured draws only while it is enabled. */
internal sealed class LivePoseConnection : IDisposable
{
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly byte[] header = new byte[LivePoseProtocol.HeaderBytes];
    private readonly byte[] draws = new byte[LivePoseProtocol.DrawCapacity * LivePoseProtocol.DrawBytes];
    private readonly byte[] palette = new byte[LivePoseProtocol.VectorCapacity * 16];
    private readonly uint[] owners = new uint[LivePoseProtocol.OwnerWords];
    private readonly uint[] next = new uint[LivePoseProtocol.OwnerWords];
    private int sequence = -1;
    private bool enabled;
    private readonly ulong opened = (ulong)Environment.TickCount64;
    public LiveDraws? Draws { get; private set; }
    public string Diagnostic { get; private set; } = "";

    /** Creates the mapping; the magic is written last, after the header. */
    public LivePoseConnection(int processId)
    {
        mapping = MemoryMappedFile.CreateOrOpen(LivePoseProtocol.MappingName(processId), LivePoseProtocol.MappingBytes);
        try
        {
            view = mapping.CreateViewAccessor(); view.Write(4, LivePoseProtocol.Version);
            view.Write(8, LivePoseProtocol.MappingBytes); view.Write(12, Environment.ProcessId);
            view.Write(36, uint.MaxValue);
            view.Write(16, Environment.TickCount64); Thread.MemoryBarrier(); view.Write(0, LivePoseProtocol.Magic);
        }
        catch { mapping.Dispose(); throw; }
    }

    /** Turning capture off drops the held frame; the game stops copying on its next frame. */
    public void Enable(bool value)
    {
        if (enabled == value) return;
        enabled = value; view.Write(32, value ? 1u : 0u);
        if (!value) { Draws = null; sequence = -1; }
    }

    /** The game copies palettes only for these owners, matched by object slot; the mapping changes only on a new set. */
    public void SetOwners(IEnumerable<uint> handles)
    {
        Array.Clear(next);
        foreach (uint handle in handles)
        {
            if (handle == uint.MaxValue) continue;
            uint slot = handle & LivePoseProtocol.SlotMask;
            next[slot / 32] |= 1u << (int)(slot % 32);
        }
        if (next.AsSpan().SequenceEqual(owners)) return;
        next.CopyTo(owners, 0);
        view.WriteArray(LivePoseProtocol.OwnersOffset, owners, 0, owners.Length);
    }

    /** Selects which player object the game filters pose capture to; null clears the filter. */
    public void SetPlayer(uint? handle) => view.Write(36, handle ?? uint.MaxValue);

    /** Writes the heartbeat and copies a new frame once its sequence is stable. */
    public void Poll(ulong now)
    {
        view.Write(16, now);
        uint version = view.ReadUInt32(24), ready = view.ReadUInt32(28);
        Diagnostic = version != 0 && version != LivePoseProtocol.Version ? "Pose bridge version differs; update Sunrise and restart."
            : version != 0 && ready == 0 ? "Native pose observer is unavailable."
            : version == 0 && now >= opened && now - opened > LiveWorldProtocol.StaleAfterMilliseconds
                ? "Pose bridge unavailable; update Sunrise and restart." : "";
        if (version != LivePoseProtocol.Version || ready == 0 || !enabled) { Draws = null; return; }
        const int at = LivePoseProtocol.ControlBytes;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int before = view.ReadInt32(at);
            if ((before & 1) != 0 || before == sequence) break;
            Thread.MemoryBarrier();
            view.ReadArray(at, header, 0, header.Length);
            int count, vectors;
            // A header torn by a concurrent publish can hold any counts; the sequence check decides.
            try { (_, _, count, vectors, _) = LivePoseProtocol.ReadHeader(header); }
            catch (InvalidDataException) { continue; }
            view.ReadArray(at + LivePoseProtocol.DrawsOffset, draws, 0, count * LivePoseProtocol.DrawBytes);
            view.ReadArray(at + LivePoseProtocol.PaletteOffset, palette, 0, vectors * 16);
            Thread.MemoryBarrier();
            if (before != view.ReadInt32(at)) continue;
            Draws = LivePoseProtocol.Read(header, draws, palette.AsSpan(0, vectors * 16), now); sequence = before; break;
        }
        if (Draws is { } frame && !LivePoseProtocol.Fresh(frame.Time, now)) Draws = null;
    }

    /** Zeroes the heartbeat and releases the mapping. */
    public void Dispose() { view.Write(16, 0L); view.Dispose(); mapping.Dispose(); }
}
