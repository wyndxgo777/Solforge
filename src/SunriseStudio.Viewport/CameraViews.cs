using System.Numerics;

namespace SunriseStudio.Viewport;

/** Solforge Phase 10: axis-aligned views. The world is Z up; Front looks along +Y, as in Blender. */
public enum CameraViewPreset { Top, Bottom, Front, Back, Right, Left }

/** Axis-aligned preset views and the Blender keypad that selects them. */
public static class CameraViews
{
    public static readonly CameraViewPreset[] All =
        [CameraViewPreset.Top, CameraViewPreset.Bottom, CameraViewPreset.Front, CameraViewPreset.Back,
            CameraViewPreset.Right, CameraViewPreset.Left];

    /**
     * Yaw and pitch for ViewerCamera.SetView. Top and Bottom keep yaw at a quarter turn so +X points right
     * on screen; the camera clamps the pitch just short of straight down or up.
     */
    public static (float Yaw, float Pitch) Angles(CameraViewPreset preset) => preset switch
    {
        CameraViewPreset.Top => (MathF.PI / 2, -MathF.PI / 2),
        CameraViewPreset.Bottom => (MathF.PI / 2, MathF.PI / 2),
        CameraViewPreset.Front => (MathF.PI / 2, 0),
        CameraViewPreset.Back => (-MathF.PI / 2, 0),
        CameraViewPreset.Right => (MathF.PI, 0),
        CameraViewPreset.Left => (0, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(preset))
    };

    /** The direction the camera looks along, the same formula ViewerCamera.Forward uses. */
    public static Vector3 Forward(float yaw, float pitch) =>
        new(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch));

    /** Display name of a preset. */
    public static string Name(CameraViewPreset preset) => preset.ToString();

    /** Blender keypad layout: 7 top, 1 front, 3 right; Ctrl gives the opposite side. */
    public static CameraViewPreset? FromKeypad(int digit, bool opposite) => digit switch
    {
        7 => opposite ? CameraViewPreset.Bottom : CameraViewPreset.Top,
        1 => opposite ? CameraViewPreset.Back : CameraViewPreset.Front,
        3 => opposite ? CameraViewPreset.Left : CameraViewPreset.Right,
        _ => null
    };
}

/** One axis of the corner gizmo: its screen offset (Y down), its depth toward the viewer, and whether it is the negative end. */
public readonly record struct AxisGizmoAxis(int Axis, Vector2 Offset, float Depth, bool Negative);

/** Solforge Phase 10: the corner world-axis gizmo, laid out from the view matrix alone. */
public static class AxisGizmo
{
    /**
     * Projects the world axes through the view rotation into a circle of the given radius.
     * @return Six entries (+X, +Y, +Z, then -X, -Y, -Z), farthest first so nearer axes draw on top.
     */
    public static AxisGizmoAxis[] Layout(Matrix4x4 view, float radius)
    {
        var axes = new AxisGizmoAxis[6];
        for (int i = 0; i < 6; i++)
        {
            var world = Vector3.Zero;
            world[i % 3] = i < 3 ? 1 : -1;
            // Row-vector convention: the view rotation maps world directions into camera space (-Z forward).
            var camera = Vector3.TransformNormal(world, view);
            axes[i] = new(i % 3, new Vector2(camera.X, -camera.Y) * radius, camera.Z, i >= 3);
        }
        Array.Sort(axes, (a, b) => a.Depth.CompareTo(b.Depth));
        return axes;
    }
}

/** Solforge Phase 10: FPS, draw calls and triangles for the viewport overlay. */
public sealed class ViewportStatistics
{
    private readonly float[] frames;
    private int count, next;
    private float total;

    public ViewportStatistics(int window = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        frames = new float[window];
    }

    public int DrawCalls { get; private set; }
    public long Triangles { get; private set; }

    /** Mean frames per second over the window; zero before the first timed frame. */
    public float FramesPerSecond => total > 0 ? count / total : 0;

    /** Mean frame time in milliseconds over the window. */
    public float FrameMilliseconds => count > 0 ? total / count * 1000 : 0;

    /** Records one frame; frames with no or a non-finite time are ignored. */
    public void Record(float seconds, int drawCalls, long triangles)
    {
        DrawCalls = Math.Max(0, drawCalls);
        Triangles = Math.Max(0, triangles);
        if (!(seconds > 0) || !float.IsFinite(seconds)) return;
        if (count == frames.Length) total -= frames[next]; else count++;
        frames[next] = seconds;
        total += seconds;
        next = (next + 1) % frames.Length;
    }

    /** Zeroes the accumulated frame statistics. */
    public void Reset()
    {
        count = next = 0;
        total = 0;
        DrawCalls = 0;
        Triangles = 0;
    }

    /** Overlay text with frame rate, frame time, draw calls and triangles. */
    public string Text() => $"{FramesPerSecond:0} FPS  {FrameMilliseconds:0.0} ms\n{DrawCalls:N0} draw calls\n{Triangles:N0} triangles";
}
