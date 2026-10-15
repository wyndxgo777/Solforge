using System.Numerics;

namespace SunriseStudio.Viewport;

/** Solforge Phase 47B: where the 3D view was last drawn, so the AI tools can map objects to screen rectangles. */
public sealed partial class AssetViewport
{
    /** Top-left of the last drawn 3D image in window pixels; zero before the first frame. */
    public Vector2 ScreenOrigin { get; private set; }
    public Vector2 ScreenSize { get; private set; }

    /** The screen rectangle (window pixels) of a world box, or null when it is behind the camera or off screen. */
    public (Vector2 Min, Vector2 Max)? ScreenRect(Vector3 center, Vector3 extents)
    {
        if (ScreenSize.X <= 0 || ScreenSize.Y <= 0) return null;
        var matrix = Camera.ViewProjection(ScreenSize.X / ScreenSize.Y);
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            var point = center + extents * new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1);
            var clip = Vector4.Transform(new Vector4(point, 1), matrix);
            if (clip.W <= 1e-4f) return null;
            var screen = ScreenOrigin + new Vector2(clip.X / clip.W * .5f + .5f, .5f - clip.Y / clip.W * .5f) * ScreenSize;
            min = Vector2.Min(min, screen);
            max = Vector2.Max(max, screen);
        }
        var low = Vector2.Max(min, ScreenOrigin);
        var high = Vector2.Min(max, ScreenOrigin + ScreenSize);
        return high.X <= low.X || high.Y <= low.Y ? null : (low, high);
    }
}
