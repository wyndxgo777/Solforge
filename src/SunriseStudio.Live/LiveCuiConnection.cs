using System.IO.MemoryMappedFiles;

namespace SunriseStudio.Live;

/** Studio owns the mapping; the game republishes a section only when its content or request changes. */
internal sealed class LiveCuiConnection : IDisposable
{
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly byte[] screensBytes = new byte[LiveCuiProtocol.ScreensBytes];
    private readonly byte[] detailBytes = new byte[LiveCuiProtocol.DetailBytes];
    private readonly byte[] treeBytes = new byte[LiveCuiProtocol.TreeBytes];
    private readonly ulong opened = (ulong)Environment.TickCount64;
    // Zero is the sequence of a section the game never wrote.
    private int screensSequence, detailSequence, treeSequence;
    private uint nextRequest, watchRequest, widgetRequest;
    private LiveCuiTarget? watch;
    private LiveCuiTree? tree;
    // Each section keeps its own error until that section reads cleanly.
    private string screensError = "", treeError = "", detailError = "";
    public LiveCuiScreens? Screens { get; private set; }
    /** Only the answer to the current watch; an older tree is never shown for a new target. */
    public LiveCuiTree? Tree => tree is { } value && watchRequest != 0 && value.Request == watchRequest ? value : null;
    public LiveCuiDetail? Detail { get; private set; }
    public bool Alive { get; private set; }
    /** The versions match and the game has built its screen manager. */
    public bool Ready { get; private set; }
    /** The game is still booting its screen manager; nothing is wrong yet. */
    public bool Settling { get; private set; }
    /** Screens and tree errors and version notes; a bad widget answer only marks Detail Unavailable. */
    public string Diagnostic { get; private set; } = "";

    /** Creates the mapping; the magic is written last, after the header. */
    public LiveCuiConnection(int processId)
    {
        mapping = MemoryMappedFile.CreateOrOpen(LiveCuiProtocol.MappingName(processId), LiveCuiProtocol.MappingBytes);
        try
        {
            view = mapping.CreateViewAccessor(0, LiveCuiProtocol.MappingBytes, MemoryMappedFileAccess.ReadWrite);
            view.Write(LiveCuiProtocol.VersionOffset, LiveCuiProtocol.Version);
            view.Write(LiveCuiProtocol.BytesOffset, LiveCuiProtocol.MappingBytes);
            view.Write(LiveCuiProtocol.EditorPidOffset, Environment.ProcessId);
            view.Write(LiveCuiProtocol.HeartbeatOffset, Environment.TickCount64);
            Thread.MemoryBarrier();
            view.Write(LiveCuiProtocol.MagicOffset, LiveCuiProtocol.Magic);
        }
        catch { mapping.Dispose(); throw; }
    }

    /** Writes the target before the new id; the same target keeps its id and its answer. */
    public void Watch(LiveCuiTarget? target)
    {
        if (target == watch) return;
        watch = target;
        treeError = "";
        if (target is not { } value) { watchRequest = 0; view.Write(LiveCuiProtocol.WatchRequestOffset, 0u); return; }
        view.Write(LiveCuiProtocol.WatchHierarchyOffset, value.HierarchyTag);
        view.Write(LiveCuiProtocol.WatchOrdinalOffset, (uint)value.Ordinal);
        Thread.MemoryBarrier();
        watchRequest = NextId();
        view.Write(LiveCuiProtocol.WatchRequestOffset, watchRequest);
    }

    /** Writes the widget before the new id; every call gets its own answer. Null stops waiting. */
    public void Request(LiveCuiWidgetTarget? target)
    {
        detailError = "";
        if (target is not { } value)
        { widgetRequest = 0; Detail = null; view.Write(LiveCuiProtocol.WidgetRequestOffset, 0u); return; }
        view.Write(LiveCuiProtocol.WidgetHierarchyOffset, value.HierarchyTag);
        view.Write(LiveCuiProtocol.WidgetOrdinalOffset, (uint)value.Ordinal);
        view.Write(LiveCuiProtocol.WidgetNodeOffset, (uint)value.NodeId);
        Thread.MemoryBarrier();
        widgetRequest = NextId();
        view.Write(LiveCuiProtocol.WidgetRequestOffset, widgetRequest);
        Detail = new(value, LiveCuiStatus.Pending, 0);
    }

    /** Writes the heartbeat, then reads each section whose sequence moved. */
    public void Poll(ulong now)
    {
        view.Write(LiveCuiProtocol.HeartbeatOffset, now);
        uint version = view.ReadUInt32(LiveCuiProtocol.ClientVersionOffset);
        if (version != LiveCuiProtocol.Version)
        {
            Screens = null; tree = null; Alive = Ready = Settling = false;
            screensError = treeError = detailError = "";
            screensSequence = detailSequence = treeSequence = 0;
            Diagnostic = version != 0 ? "CUI bridge version differs. Update Sunrise and restart the game."
                : now >= opened && now - opened > LiveWorldProtocol.StaleAfterMilliseconds
                    ? "CUI bridge unavailable. Update Sunrise and restart the game." : "";
            return;
        }
        ulong checkedTime = view.ReadUInt64(LiveCuiProtocol.CheckedTimeOffset);
        // Read the clock after checkedTime; the game may have written it after `now` was taken.
        Alive = LiveCuiProtocol.Alive(checkedTime, (ulong)Environment.TickCount64);
        if (Copy(LiveCuiProtocol.ScreensOffset, screensBytes, screensBytes.Length, ref screensSequence))
            Screens = Parse(() => LiveCuiProtocol.ReadScreens(screensBytes), ref screensError);
        if (watchRequest != 0 && Copy(LiveCuiProtocol.TreeOffset, treeBytes, -1, ref treeSequence))
            tree = Parse(() => LiveCuiProtocol.ReadTree(treeBytes), ref treeError);
        if (Detail is { Status: LiveCuiStatus.Pending } pending && widgetRequest != 0
            && Copy(LiveCuiProtocol.DetailOffset, detailBytes, detailBytes.Length, ref detailSequence))
        {
            var answer = Parse(() => LiveCuiProtocol.ReadDetail(detailBytes, widgetRequest, pending.Target), ref detailError);
            // The game sends each answer once, so a bad one ends the wait.
            Detail = answer ?? (detailError != "" ? pending with { Status = LiveCuiStatus.Unavailable } : pending);
        }
        Ready = (view.ReadUInt32(LiveCuiProtocol.BridgeFlagsOffset) & LiveCuiProtocol.ManagerReady) != 0;
        string error = screensError != "" ? screensError : treeError;
        Settling = error == "" && !Ready;
        Diagnostic = error != "" ? error : Ready ? "" : "The game has not built its screen list yet.";
    }

    /** A bad section drops only itself; its error stays until that section reads cleanly. */
    private static T? Parse<T>(Func<T?> read, ref string error) where T : class
    {
        try { var value = read(); error = ""; return value; }
        catch (InvalidDataException failure) { error = failure.Message; return null; }
    }

    /**
     * Copies one section under its sequence.
     * @param length Bytes to copy; -1 copies the tree header and only the rows it counts.
     * @return False when the section is unchanged, mid-write or kept changing.
     */
    private bool Copy(long offset, byte[] buffer, int length, ref int sequence)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int before = view.ReadInt32(offset);
            if ((before & 1) != 0 || before == sequence) return false;
            Thread.MemoryBarrier();
            int bytes = length;
            if (bytes < 0)
            {
                view.ReadArray(offset, buffer, 0, LiveCuiProtocol.TreeHeaderBytes);
                // A torn header can hold any count; the sequence check below decides.
                bytes = LiveCuiProtocol.TreeHeaderBytes + LiveCuiProtocol.TreeCount(buffer) * LiveCuiProtocol.WidgetRowBytes;
            }
            view.ReadArray(offset, buffer, 0, bytes);
            Thread.MemoryBarrier();
            if (view.ReadInt32(offset) != before) continue;
            sequence = before;
            return true;
        }
        return false;
    }

    private uint NextId()
    {
        uint id = ++nextRequest;
        return id != 0 ? id : ++nextRequest;
    }

    public void Dispose() { view.Write(LiveCuiProtocol.HeartbeatOffset, 0L); view.Dispose(); mapping.Dispose(); }
}
