namespace SunriseStudio.Live;

/** Partial: the UI (Cui) channel; retries creating its mapping and tracks the watched screen target. */
public sealed partial class LiveConnection
{
    // One CreateNew attempt per second while a fast re-attach still holds the old name.
    private const long CuiRetryMilliseconds = 1000;
    private LiveCuiConnection? cui;
    private LiveCuiTarget? cuiWatch;
    private LiveCuiWidgetTarget? cuiWidget;
    private bool cuiWanted;
    private long cuiRetryAt;
    private string cuiOpenError = "";
    public LiveCuiScreens? CuiScreens => cui?.Screens;
    /** The tree of the current watch only; null until the game answers it. */
    public LiveCuiTree? CuiTree => cui?.Tree;
    /** The answer to the last widget request; Pending until the game answers. */
    public LiveCuiDetail? CuiDetail => cui?.Detail;
    public string CuiBridgeDiagnostic => cui?.Diagnostic ?? cuiOpenError;
    /** The diagnostic names a state that clears by itself: a busy re-attach or a booting game. */
    public bool CuiSettling => cui?.Settling ?? cuiOpenError.Length > 0;
    /** The game captured CUI state within the last two seconds. */
    public bool CuiAlive => cui?.Alive ?? false;

    /** Starts the read-only CUI link; the game then publishes its open screens. */
    public void OpenCui()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cuiWanted = true;
        TryOpenCui(Environment.TickCount64);
    }

    /** Asks for one screen's widget tree; the same target is a no-op and null stops. */
    public void WatchCui(LiveCuiTarget? target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cuiWatch = target;
        if (target is not null) OpenCui();
        cui?.Watch(target);
    }

    /** Asks the game once for one widget's record; null stops waiting. */
    public void RequestCuiWidget(LiveCuiWidgetTarget? target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (cui is not null) { cui.Request(target); return; }
        cuiWidget = target;
        if (target is not null) OpenCui();
    }

    private void PollCui(long now)
    {
        if (cuiWanted) TryOpenCui(now);
        cui?.Poll((ulong)now);
    }

    /** Opens the mapping and replays the watch and the widget request made while it was closed. */
    private void TryOpenCui(long now)
    {
        if (cui is not null || now < cuiRetryAt) return;
        try { cui = new(process.Id); }
        catch (IOException) { cuiOpenError = "CUI link busy; retrying"; cuiRetryAt = now + CuiRetryMilliseconds; return; }
        cuiOpenError = "";
        cui.Watch(cuiWatch);
        if (cuiWidget is { } widget) cui.Request(widget);
        cuiWidget = null;
    }
}
