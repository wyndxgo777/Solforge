using System.IO.MemoryMappedFiles;

namespace SunriseStudio.Live;

/** Section buffers are reused; a new placement snapshot copies only the rows it holds. */
internal sealed class LiveWorldConnection : IDisposable
{
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly byte[] worldBytes = new byte[LiveWorldProtocol.WorldBytes];
    private readonly byte[] placementBytes = new byte[LiveWorldProtocol.PlacementsBytes];
    private int worldSequence = -1, placementSequence = -1;
    private readonly ulong opened = (ulong)Environment.TickCount64;
    public uint BridgeVersion { get; private set; }
    public string Diagnostic { get; private set; } = "";
    public LiveWorld? World { get; private set; }
    public LivePlacements? Placements { get; private set; }

    /** Creates the mapping; the magic is written last, after the header. */
    public LiveWorldConnection(int processId)
    {
        mapping = MemoryMappedFile.CreateOrOpen(LiveWorldProtocol.MappingName(processId), LiveWorldProtocol.MappingBytes);
        try
        {
            view = mapping.CreateViewAccessor(0, LiveWorldProtocol.MappingBytes, MemoryMappedFileAccess.ReadWrite);
            view.Write(4, LiveWorldProtocol.Version);
            view.Write(8, LiveWorldProtocol.MappingBytes);
            view.Write(12, Environment.ProcessId);
            view.Write(16, Environment.TickCount64);
            Thread.MemoryBarrier();
            view.Write(0, LiveWorldProtocol.Magic);
        }
        catch { mapping.Dispose(); throw; }
    }

    /** Writes the heartbeat and rereads each section whose sequence moved. */
    public void Poll(ulong now)
    {
        view.Write(16, now);
        BridgeVersion = view.ReadUInt32(24);
        Diagnostic = BridgeVersion != 0 && BridgeVersion != LiveWorldProtocol.Version
            ? "World bridge version differs. Update Sunrise and restart the game."
            : BridgeVersion == 0 && now >= opened && now - opened > LiveWorldProtocol.StaleAfterMilliseconds
                ? "World bridge unavailable. Update Sunrise and restart the game." : "";
        if (BridgeVersion != LiveWorldProtocol.Version)
        { World = null; Placements = null; worldSequence = placementSequence = -1; return; }
        if (ReadSection(LiveWorldProtocol.ControlBytes, worldBytes, worldBytes.Length, ref worldSequence))
            World = LiveWorldProtocol.ReadWorld(worldBytes, now);
        if (ReadSection(LiveWorldProtocol.PlacementsOffset, placementBytes, -1, ref placementSequence))
            Placements = LiveWorldProtocol.ReadPlacements(placementBytes, now);
        if (World is { } world && !LiveWorldProtocol.Fresh(world.Time, now)) World = null;
        if (Placements is { } placements && !LiveWorldProtocol.Fresh(placements.Time, now)) Placements = null;
    }

    /**
     * Copies one section under its sequence.
     * @param length Bytes to copy; -1 copies the placement header and only the rows it counts.
     */
    private bool ReadSection(long offset, byte[] buffer, int length, ref int sequence)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int before = view.ReadInt32(offset);
            if ((before & 1) != 0 || before == sequence) return false;
            Thread.MemoryBarrier();
            int bytes = length;
            if (bytes < 0)
            {
                view.ReadArray(offset, buffer, 0, LiveWorldProtocol.PlacementHeaderBytes);
                // A torn header can hold any count; the sequence check below decides.
                bytes = LiveWorldProtocol.PlacementHeaderBytes
                    + LiveWorldProtocol.PlacementCount(buffer) * LiveWorldProtocol.PlacementBytes;
            }
            view.ReadArray(offset, buffer, 0, bytes);
            Thread.MemoryBarrier();
            if (view.ReadInt32(offset) != before) continue;
            sequence = before;
            return true;
        }
        return false;
    }

    /** Zeroes the heartbeat and releases the mapping. */
    public void Dispose()
    { view.Write(16, 0L); view.Dispose(); mapping.Dispose(); }
}
