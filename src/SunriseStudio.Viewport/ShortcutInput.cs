using System.Numerics;
using ImGuiNET;

namespace SunriseStudio.Viewport;

/** Pan and zoom a 2D canvas applies this frame. Zoom is in wheel steps around the anchor. */
public readonly record struct CanvasInput(Vector2 Pan, float Zoom, Vector2 ZoomAnchor);

/** Reads ImGui input through the active shortcut map. Call only on the window thread inside a frame. */
public static class ShortcutInput
{
    // A zoom drag of this many pixels equals one wheel step.
    private const float DragPixelsPerStep = 50;
    private static readonly HashSet<string> Fired = new(StringComparer.Ordinal);
    private static readonly Vector2[] PressPositions = new Vector2[5];
    private static ShortcutSection? hoveredView, nextHoveredView;

    public static ShortcutMap Map { get; set; } = new();
    /** Set while the Shortcuts window captures input, so no binding fires. */
    public static bool Suspended { get; set; }
    private static bool active = true;
    /** False while the host is inactive or a shortcut capture owns input. */
    public static bool Enabled => active && !Suspended;
    /** Counts BeginFrame calls; a view compares it to know whether it was hovered this frame. */
    public static long Frame { get; private set; }
    public static IReadOnlyList<ShortcutButton> Buttons { get; } =
        [ShortcutButton.Left, ShortcutButton.Right, ShortcutButton.Middle, ShortcutButton.Back, ShortcutButton.Forward];

    /**
     * Resolves key presses after ImGui starts the frame and before drawing.
     * @param inputActive False while the host forbids shortcut input.
     */
    public static void BeginFrame(bool inputActive = true)
    {
        Frame++;
        active = inputActive;
        hoveredView = Enabled ? nextHoveredView : null;
        nextHoveredView = null;
        Fired.Clear();
        var io = ImGui.GetIO();
        if (!Enabled) return;
        var held = Held();
        foreach (var button in Buttons)
            if (ImGui.IsMouseClicked((ImGuiMouseButton)button))
            {
                PressPositions[(int)button] = io.MousePos;
                // Menu clicks, such as the side buttons for navigation, work anywhere in the window.
                Fired.UnionWith(Map.ResolveMenuClick(button, held));
            }
        if (io.WantTextInput) return;
        foreach (string key in Map.BoundKeys)
            if (ToImGui(key) is var imguiKey && imguiKey != ImGuiKey.None && ImGui.IsKeyPressed(imguiKey))
                Fired.UnionWith(Map.ResolveKey(key, held, hoveredView));
    }

    /** A view under the pointer reports itself; the next frame's key presses prefer its section. */
    public static void Hover(ShortcutSection view) { if (Enabled) nextHoveredView = view; }

    /** True on the frame a key binding of the action fired. Text input and capture suppress it. */
    public static bool Pressed(string id) => Enabled && !ImGui.GetIO().WantTextInput && Fired.Contains(id);

    /** Display text of an action's bindings. */
    public static string Text(string id) => Map.Text(id);

    /** Modifier keys held this frame. */
    public static ShortcutModifiers Held()
    {
        var io = ImGui.GetIO();
        var held = ShortcutModifiers.None;
        if (io.KeyCtrl) held |= ShortcutModifiers.Ctrl;
        if (io.KeyShift) held |= ShortcutModifiers.Shift;
        if (io.KeyAlt) held |= ShortcutModifiers.Alt;
        return held;
    }

    /** Click and drag actions that this button, with the held modifiers, starts in the view. */
    public static IReadOnlyList<string> Mouse(ShortcutSection view, ShortcutButton button) =>
        Enabled ? Map.ResolveMouse(view, button, Held()) : [];

    /** True on the frame a click binding of the action is pressed; reports the button used. */
    public static bool Clicked(ShortcutSection view, string id, out ShortcutButton button)
    {
        foreach (var candidate in Buttons)
            if (ImGui.IsMouseClicked((ImGuiMouseButton)candidate) && Mouse(view, candidate).Contains(id))
            { button = candidate; return true; }
        button = ShortcutButton.Left;
        return false;
    }

    /** Wheel steps for a zoom action, or zero when the wheel is bound to nothing there. */
    public static float Wheel(ShortcutSection view, string id)
    {
        if (!Enabled) return 0;
        float wheel = ImGui.GetIO().MouseWheel;
        return wheel != 0 && Map.ResolveWheel(view, Held()).Contains(id) ? wheel : 0;
    }

    /** Drag up or right to zoom in. */
    public static float DragZoom(Vector2 delta) => (delta.X - delta.Y) / DragPixelsPerStep;

    /** Pan and zoom for a 2D canvas; a drag counts only while the pointer is over the canvas. */
    public static CanvasInput Canvas(bool hovered)
    {
        if (!Enabled || !hovered) return default;
        Hover(ShortcutSection.View2D);
        var io = ImGui.GetIO();
        var held = Held();
        Vector2 pan = Vector2.Zero, anchor = io.MousePos;
        float zoom = 0;
        bool panned = false;
        foreach (var button in Buttons)
        {
            if (!ImGui.IsMouseDragging((ImGuiMouseButton)button)) continue;
            var actions = Map.ResolveMouse(ShortcutSection.View2D, button, held);
            if (!panned && actions.Contains(ShortcutActions.Pan2D)) { pan = io.MouseDelta; panned = true; }
            if (actions.Contains(ShortcutActions.Zoom2D)) { zoom += DragZoom(io.MouseDelta); anchor = PressPositions[(int)button]; }
        }
        zoom += Wheel(ShortcutSection.View2D, ShortcutActions.Zoom2D);
        return new(pan, zoom, anchor);
    }

    /**
     * Reads one binding for the Shortcuts window. Keys and buttons are taken on their first press.
     * @param cancelled Set when Escape was pressed.
     * @return Null until the action receives an input kind it accepts.
     */
    public static ShortcutBinding? Capture(ShortcutAction action, out bool cancelled)
    {
        if (!active) { cancelled = false; return null; }
        cancelled = ImGui.IsKeyPressed(ImGuiKey.Escape);
        if (cancelled) return null;
        var held = Held();
        if (action.Allows(ShortcutKind.Key))
            foreach (string key in ShortcutKeys.Names)
                if (ImGui.IsKeyPressed(ToImGui(key))) return ShortcutBinding.Keyboard(key, held);
        if (action.Allows(ShortcutKind.Wheel) && ImGui.GetIO().MouseWheel != 0) return ShortcutBinding.Wheel(held);
        foreach (var button in Buttons)
        {
            if (!ImGui.IsMouseClicked((ImGuiMouseButton)button)) continue;
            if (action.Allows(ShortcutKind.Drag)) return ShortcutBinding.Drag(button, held);
            if (action.Allows(ShortcutKind.Click)) return ShortcutBinding.Click(button, held);
        }
        return null;
    }

    /** Maps a ShortcutKeys name to the key the window forwards to ImGui. */
    public static ImGuiKey ToImGui(string key)
    {
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z') return ImGuiKey.A + (key[0] - 'A');
        if (key.Length == 1 && key[0] is >= '0' and <= '9') return ImGuiKey._0 + (key[0] - '0');
        if (key.Length == 8 && key.StartsWith("Numpad ", StringComparison.Ordinal) && key[7] is >= '0' and <= '9')
            return ImGuiKey.Keypad0 + (key[7] - '0');
        if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out int function) && function is >= 1 and <= 12)
            return ImGuiKey.F1 + (function - 1);
        return key switch
        {
            "Numpad ." => ImGuiKey.KeypadDecimal,
            "Numpad /" => ImGuiKey.KeypadDivide,
            "Numpad *" => ImGuiKey.KeypadMultiply,
            "Numpad -" => ImGuiKey.KeypadSubtract,
            "Numpad +" => ImGuiKey.KeypadAdd,
            "Delete" => ImGuiKey.Delete,
            "Backspace" => ImGuiKey.Backspace,
            "Insert" => ImGuiKey.Insert,
            "Home" => ImGuiKey.Home,
            "End" => ImGuiKey.End,
            "Page Up" => ImGuiKey.PageUp,
            "Page Down" => ImGuiKey.PageDown,
            "Space" => ImGuiKey.Space,
            "Enter" => ImGuiKey.Enter,
            "Tab" => ImGuiKey.Tab,
            "Left Arrow" => ImGuiKey.LeftArrow,
            "Right Arrow" => ImGuiKey.RightArrow,
            "Up Arrow" => ImGuiKey.UpArrow,
            "Down Arrow" => ImGuiKey.DownArrow,
            "'" => ImGuiKey.Apostrophe,
            "," => ImGuiKey.Comma,
            "-" => ImGuiKey.Minus,
            "." => ImGuiKey.Period,
            "/" => ImGuiKey.Slash,
            ";" => ImGuiKey.Semicolon,
            "=" => ImGuiKey.Equal,
            "[" => ImGuiKey.LeftBracket,
            "\\" => ImGuiKey.Backslash,
            "]" => ImGuiKey.RightBracket,
            "`" => ImGuiKey.GraveAccent,
            "Pause" => ImGuiKey.Pause,
            _ => ImGuiKey.None
        };
    }
}
