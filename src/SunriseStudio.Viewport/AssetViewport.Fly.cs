using System.Numerics;
using ImGuiNET;

namespace SunriseStudio.Viewport;

/**
 * Solforge Phase 03B: Unreal-style fly in the orbit camera. Hold the right button and W, A, S, D, Q or E to fly;
 * the wheel sets the fly speed meanwhile and Shift speeds it up. A right drag no preset binds is a plain fly look.
 */
public sealed partial class AssetViewport
{
    private const string FlyLook = "3d.fly_look";
    private const float FlySpeed = 10;
    private static readonly ImGuiKey[] FlyKeys = [ImGuiKey.W, ImGuiKey.A, ImGuiKey.S, ImGuiKey.D, ImGuiKey.Q, ImGuiKey.E];
    private float flySpeedScale = 1;
    private int flyFrame = -2;

    /** True while the previous frame flew, so the wheel and the keys stay with the fly until the button lifts. */
    private bool Flying => flyFrame >= ImGui.GetFrameCount() - 1;

    /** An unbound right button starts a fly look in the 3D view, as in Unreal. */
    private static bool StartsFlyLook(ShortcutButton button, bool image) => !image && button == ShortcutButton.Right;

    private static bool FlyKeyDown()
    {
        if (ImGui.GetIO().WantTextInput) return false;
        foreach (var key in FlyKeys) if (ImGui.IsKeyDown(key)) return true;
        return false;
    }

    /**
     * Runs one frame of a right-button drag.
     * @return True when the frame flew; an orbit with no fly key held stays an orbit.
     */
    private bool Fly(string action)
    {
        if (PlayerCamera || dragButton != (int)ShortcutButton.Right || !Camera.PivotFree) return false;
        bool keys = FlyKeyDown();
        if (!Flying && action != FlyLook && !(action == ShortcutActions.Orbit3D && keys)) return false;
        flyFrame = ImGui.GetFrameCount();
        var io = ImGui.GetIO();
        Camera.Look(io.MouseDelta.X, io.MouseDelta.Y);
        if (!keys) return true;
        var move = Vector3.Zero;
        if (ImGui.IsKeyDown(ImGuiKey.W)) move += Camera.Forward;
        if (ImGui.IsKeyDown(ImGuiKey.S)) move -= Camera.Forward;
        if (ImGui.IsKeyDown(ImGuiKey.D)) move += Camera.Right;
        if (ImGui.IsKeyDown(ImGuiKey.A)) move -= Camera.Right;
        if (ImGui.IsKeyDown(ImGuiKey.E)) move += Vector3.UnitZ;
        if (ImGui.IsKeyDown(ImGuiKey.Q)) move -= Vector3.UnitZ;
        if (move.LengthSquared() < 1e-6f) return true;
        float speed = FlySpeed * flySpeedScale * (io.KeyShift ? SprintFactor : 1);
        Camera.MovePivot(Camera.Pivot + Vector3.Normalize(move) * speed * io.DeltaTime);
        return true;
    }

    private void ScaleFlySpeed(float steps)
    {
        if (steps != 0) flySpeedScale = Math.Clamp(flySpeedScale * MathF.Pow(1.25f, steps), MinimumSpeedScale, MaximumSpeedScale);
    }

    /** Q, W, E and R pick the transform tool; never while flying, dragging a gizmo or walking as the player. */
    private void HandleToolKeys()
    {
        if (!ShowTransformTools || PlayerCamera || Flying
            || ImGui.IsMouseDown(ImGuiMouseButton.Right) || ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;
        if (ShortcutInput.Pressed(ShortcutActions.SelectTool3D)) Tool = TransformTool.Select;
        else if (ShortcutInput.Pressed(ShortcutActions.MoveTool3D)) Tool = TransformTool.Move;
        else if (ShortcutInput.Pressed(ShortcutActions.RotateTool3D)) Tool = TransformTool.Rotate;
        else if (ShortcutInput.Pressed(ShortcutActions.ScaleTool3D)) Tool = TransformTool.Scale;
    }
}
