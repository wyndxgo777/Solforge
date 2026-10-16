using System.Numerics;
using SunriseStudio.Viewport;
using static Checks;

/** Phase 10: axis views, the corner axis gizmo and the statistics overlay. */
internal static class CameraChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(List<string> checks)
    {
        Views();
        checks.Add("axis views look along the expected world axis and keep +X or +Y to the right");
        Gizmo();
        checks.Add("the corner axis gizmo puts +X right and +Y up from Top, +Z up from Front, nearer axes last");
        Statistics();
        checks.Add("viewport statistics average FPS over a rolling window and ignore untimed frames");
        Keypad();
        checks.Add("Numpad 7/1/3 pick Top/Front/Right and Ctrl picks the opposite side");
    }

    private static void Views()
    {
        var expected = new Dictionary<CameraViewPreset, Vector3>
        {
            [CameraViewPreset.Top] = -Vector3.UnitZ, [CameraViewPreset.Bottom] = Vector3.UnitZ,
            [CameraViewPreset.Front] = Vector3.UnitY, [CameraViewPreset.Back] = -Vector3.UnitY,
            [CameraViewPreset.Right] = -Vector3.UnitX, [CameraViewPreset.Left] = Vector3.UnitX
        };
        Require(CameraViews.All.Length == expected.Count, "Every axis view needs an expected direction.");
        foreach (var (preset, forward) in expected)
        {
            var camera = new ViewerCamera();
            var (yaw, pitch) = CameraViews.Angles(preset);
            camera.SetView(yaw, pitch);
            camera.Orthographic = true;
            Require(Near(camera.Forward, forward), $"{preset} looks along {camera.Forward}, not {forward}.");
            Require(Near(CameraViews.Forward(yaw, pitch), forward), $"{preset} forward formula drifted from ViewerCamera.");
            var view = camera.ViewProjection(16f / 9);
            Require(float.IsFinite(view.M11) && float.IsFinite(view.M44), $"{preset} gave a non-finite orthographic matrix.");
        }
        var right = new ViewerCamera();
        right.SetView(CameraViews.Angles(CameraViewPreset.Right).Yaw, 0);
        Require(Near(right.Right, Vector3.UnitY), $"Right view should keep +Y to the right, got {right.Right}.");
        var top = new ViewerCamera();
        top.SetView(CameraViews.Angles(CameraViewPreset.Top).Yaw, CameraViews.Angles(CameraViewPreset.Top).Pitch);
        Require(Near(top.Right, Vector3.UnitX), $"Top view should keep +X to the right, got {top.Right}.");
    }

    private static void Gizmo()
    {
        const float radius = 30;
        AxisGizmoAxis Axis(AxisGizmoAxis[] layout, int axis) => layout.Single(item => item.Axis == axis && !item.Negative);
        var camera = new ViewerCamera();
        var (yaw, pitch) = CameraViews.Angles(CameraViewPreset.Top);
        camera.SetView(yaw, pitch);
        var layout = AxisGizmo.Layout(camera.View, radius);
        Require(layout.Length == 6, "The axis gizmo lays out both ends of three axes.");
        Require(Near(Axis(layout, 0).Offset, new(radius, 0), .1f), $"Top: +X at {Axis(layout, 0).Offset}.");
        Require(Near(Axis(layout, 1).Offset, new(0, -radius), .1f), $"Top: +Y at {Axis(layout, 1).Offset}.");
        Require(Axis(layout, 2).Depth > .99f, "Top: +Z should point at the viewer.");
        Require(layout[^1].Axis == 2 && !layout[^1].Negative, "The axis nearest the viewer draws last.");
        for (int i = 1; i < layout.Length; i++) Require(layout[i - 1].Depth <= layout[i].Depth, "Axes are not sorted far to near.");
        (yaw, pitch) = CameraViews.Angles(CameraViewPreset.Front);
        camera.SetView(yaw, pitch);
        layout = AxisGizmo.Layout(camera.View, radius);
        Require(Near(Axis(layout, 2).Offset, new(0, -radius), .1f), $"Front: +Z at {Axis(layout, 2).Offset}.");
        Require(Near(Axis(layout, 0).Offset, new(radius, 0), .1f), $"Front: +X at {Axis(layout, 0).Offset}.");
    }

    private static void Statistics()
    {
        var stats = new ViewportStatistics(4);
        Require(stats.FramesPerSecond == 0, "No frames should read as zero FPS.");
        for (int i = 0; i < 4; i++) stats.Record(.02f, 12, 3000);
        Require(Near(stats.FramesPerSecond, 50, .01f), $"Four 20 ms frames read {stats.FramesPerSecond} FPS.");
        for (int i = 0; i < 4; i++) stats.Record(.01f, 7, 1_000_000);
        Require(Near(stats.FramesPerSecond, 100, .01f), $"The window did not roll: {stats.FramesPerSecond} FPS.");
        stats.Record(0, 8, 5);
        stats.Record(float.NaN, 9, 6);
        Require(Near(stats.FramesPerSecond, 100, .01f), "Untimed frames changed the average.");
        Require(stats.DrawCalls == 9 && stats.Triangles == 6, "Counts should follow the latest frame.");
        Require(stats.Text().Contains("100 FPS") && stats.Text().Contains("draw calls"), "Overlay text: " + stats.Text());
        stats.Reset();
        Require(stats.FramesPerSecond == 0 && stats.DrawCalls == 0, "Reset kept old frames.");
    }

    private static void Keypad()
    {
        Require(CameraViews.FromKeypad(7, false) == CameraViewPreset.Top && CameraViews.FromKeypad(7, true) == CameraViewPreset.Bottom,
            "Numpad 7 should pick Top and Ctrl+Numpad 7 Bottom.");
        Require(CameraViews.FromKeypad(1, false) == CameraViewPreset.Front && CameraViews.FromKeypad(1, true) == CameraViewPreset.Back,
            "Numpad 1 should pick Front and Ctrl+Numpad 1 Back.");
        Require(CameraViews.FromKeypad(3, false) == CameraViewPreset.Right && CameraViews.FromKeypad(3, true) == CameraViewPreset.Left,
            "Numpad 3 should pick Right and Ctrl+Numpad 3 Left.");
        Require(CameraViews.FromKeypad(5, false) is null, "Numpad 5 is the projection toggle, not a view.");
    }
}
