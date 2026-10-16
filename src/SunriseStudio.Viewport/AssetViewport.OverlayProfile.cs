namespace SunriseStudio.Viewport;

/** Milliseconds spent on each overlay phase in the last frame. */
public readonly record struct ViewportOverlayTimings(double MaskMilliseconds, double CompositeMilliseconds,
    double CollisionMilliseconds, double GridMilliseconds, double VolumeMilliseconds,
    double MarkerMilliseconds, double PointMilliseconds, double CursorMilliseconds);

/** Overlay profiling half of AssetViewport: phase timings and GPU markers. */
public sealed partial class AssetViewport
{
    public ViewportOverlayTimings LastOverlayTimings { get; private set; }
    /** Mark zero closes the scene; marks one through eight close the ordered overlay phases. */
    public Action<int>? OverlayGpuMark { get; set; }

    private void RecordOverlayPhase(int phase, ref long began)
    {
        OverlayGpuMark?.Invoke(phase);
        if (!MeasureFrameTimes) return;
        double elapsed = Elapsed(began);
        LastOverlayTimings = phase switch
        {
            1 => LastOverlayTimings with { MaskMilliseconds = elapsed },
            2 => LastOverlayTimings with { CompositeMilliseconds = elapsed },
            3 => LastOverlayTimings with { CollisionMilliseconds = elapsed },
            4 => LastOverlayTimings with { GridMilliseconds = elapsed },
            5 => LastOverlayTimings with { VolumeMilliseconds = elapsed },
            6 => LastOverlayTimings with { MarkerMilliseconds = elapsed },
            7 => LastOverlayTimings with { PointMilliseconds = elapsed },
            8 => LastOverlayTimings with { CursorMilliseconds = elapsed },
            _ => LastOverlayTimings
        };
        began = System.Diagnostics.Stopwatch.GetTimestamp();
    }
}
