using System.IO.MemoryMappedFiles;

namespace SunriseStudio.Live;

/** Studio owns the mapping; the game answers each new request id once on its camera callback. */
internal sealed class LiveObjectConnection : IDisposable
{
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly byte[] buffer = new byte[LiveObjectProtocol.MappingBytes];
    private readonly int processId;
    private readonly ulong opened = (ulong)Environment.TickCount64;
    private uint nextRequest, request, handle, generation;
    public LiveObjectSnapshot? Snapshot { get; private set; }
    public string Diagnostic { get; private set; } = "";

    /** Creates the mapping; the magic is written last, after the header. */
    public LiveObjectConnection(int processId)
    {
        this.processId = processId;
        mapping = MemoryMappedFile.CreateOrOpen(LiveObjectProtocol.MappingName(processId), LiveObjectProtocol.MappingBytes);
        try
        {
            view = mapping.CreateViewAccessor(0, LiveObjectProtocol.MappingBytes, MemoryMappedFileAccess.ReadWrite);
            view.Write(4, LiveObjectProtocol.Version);
            view.Write(8, LiveObjectProtocol.MappingBytes);
            view.Write(12, Environment.ProcessId);
            view.Write(LiveObjectProtocol.HeartbeatOffset, Environment.TickCount64);
            Thread.MemoryBarrier();
            view.Write(0, LiveObjectProtocol.Magic);
        }
        catch { mapping.Dispose(); throw; }
    }

    /** Writes the object before the new id, so the game never answers a torn request. */
    public void Request(uint objectHandle, uint objectGeneration)
    {
        request = ++nextRequest;
        if (request == 0) request = ++nextRequest;
        handle = objectHandle; generation = objectGeneration;
        view.Write(LiveObjectProtocol.HandleOffset, handle);
        view.Write(LiveObjectProtocol.GenerationOffset, generation);
        Thread.MemoryBarrier();
        view.Write(LiveObjectProtocol.RequestOffset, request);
        Snapshot = new(handle, generation, LiveObjectStatus.Pending, 0);
    }

    /** Stops waiting; a late answer to the old id is ignored. */
    public void Cancel() { request = 0; Snapshot = null; }

    /** Writes the heartbeat and reads a pending answer once its sequence is stable. */
    public void Poll(ulong now)
    {
        view.Write(LiveObjectProtocol.HeartbeatOffset, now);
        uint version = view.ReadUInt32(LiveObjectProtocol.ClientVersionOffset);
        Diagnostic = version != 0 && version != LiveObjectProtocol.Version ? "Object bridge version differs. Update Sunrise and restart the game."
            : version == 0 && now >= opened && now - opened > LiveWorldProtocol.StaleAfterMilliseconds
                ? "Object bridge unavailable. Update Sunrise and restart the game." : "";
        if (request == 0 || version != LiveObjectProtocol.Version || Snapshot is not { Status: LiveObjectStatus.Pending }) return;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int before = view.ReadInt32(LiveObjectProtocol.SequenceOffset);
            if ((before & 1) != 0 || view.ReadUInt32(LiveObjectProtocol.ResponseOffset) != request) return;
            Thread.MemoryBarrier();
            view.ReadArray(0, buffer, 0, buffer.Length);
            Thread.MemoryBarrier();
            if (view.ReadInt32(LiveObjectProtocol.SequenceOffset) != before) continue;
            Snapshot = LiveObjectProtocol.Read(buffer, (uint)processId, request, handle, generation) ?? Snapshot;
            return;
        }
    }

    /** Zeroes the heartbeat and releases the mapping. */
    public void Dispose() { view.Write(LiveObjectProtocol.HeartbeatOffset, 0L); view.Dispose(); mapping.Dispose(); }
}
