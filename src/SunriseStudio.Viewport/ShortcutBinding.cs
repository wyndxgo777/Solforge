namespace SunriseStudio.Viewport;

/** Menu shortcuts are global; 2D and 3D shortcuts apply only to the view under the pointer. */
public enum ShortcutSection { Menus, View2D, View3D }

/** Modifier keys of a binding. */
[Flags]
public enum ShortcutModifiers { None = 0, Ctrl = 1, Shift = 2, Alt = 4 }

/** Kind of input a binding listens for. */
public enum ShortcutKind { Key, Click, Drag, Wheel }

/** Values match ImGui mouse button indices; Back and Forward are the side buttons. */
public enum ShortcutButton { Left = 0, Right = 1, Middle = 2, Back = 3, Forward = 4 }

/** One keyboard chord or mouse gesture. Its text form is also its stored form. */
public readonly record struct ShortcutBinding
{
    private const ShortcutModifiers AllModifiers = ShortcutModifiers.Ctrl | ShortcutModifiers.Shift | ShortcutModifiers.Alt;
    // Longer text is never a valid binding; the limit also bounds work on a damaged file.
    private const int MaximumTextLength = 40;
    private readonly string? key;

    private ShortcutBinding(ShortcutKind kind, ShortcutModifiers modifiers, string key, ShortcutButton button)
    {
        Kind = kind;
        Modifiers = modifiers & AllModifiers;
        this.key = key;
        Button = button;
    }

    public ShortcutKind Kind { get; }
    public ShortcutModifiers Modifiers { get; }
    /** A name from ShortcutKeys.Names; empty for mouse gestures. */
    public string Key => key ?? "";
    /** Left for keys and the wheel. */
    public ShortcutButton Button { get; }

    /** Key binding; throws for an unknown key name. */
    public static ShortcutBinding Keyboard(string key, ShortcutModifiers modifiers = ShortcutModifiers.None) =>
        ShortcutKeys.Find(key) is { } name ? new ShortcutBinding(ShortcutKind.Key, modifiers, name, ShortcutButton.Left)
            : throw new ArgumentException($"Unknown key '{key}'.", nameof(key));

    /** Mouse click binding. */
    public static ShortcutBinding Click(ShortcutButton button, ShortcutModifiers modifiers = ShortcutModifiers.None) =>
        new(ShortcutKind.Click, modifiers, "", button);

    /** Mouse drag binding. */
    public static ShortcutBinding Drag(ShortcutButton button, ShortcutModifiers modifiers = ShortcutModifiers.None) =>
        new(ShortcutKind.Drag, modifiers, "", button);

    /** Mouse wheel binding. */
    public static ShortcutBinding Wheel(ShortcutModifiers modifiers = ShortcutModifiers.None) =>
        new(ShortcutKind.Wheel, modifiers, "", ShortcutButton.Left);

    public override string ToString()
    {
        string prefix = (Modifiers.HasFlag(ShortcutModifiers.Ctrl) ? "Ctrl+" : "")
            + (Modifiers.HasFlag(ShortcutModifiers.Shift) ? "Shift+" : "")
            + (Modifiers.HasFlag(ShortcutModifiers.Alt) ? "Alt+" : "");
        return prefix + Kind switch
        {
            ShortcutKind.Key => Key,
            ShortcutKind.Click => Button + " click",
            ShortcutKind.Drag => Button + " drag",
            _ => "Wheel"
        };
    }

    /**
     * Reads the text form, such as "Ctrl+Shift+Z", "Alt+Left drag", "Right click" or "Ctrl+Wheel".
     * @return False for any text that is not exactly one binding.
     */
    public static bool TryParse(string? text, out ShortcutBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength) return false;
        string rest = text.Trim();
        var modifiers = ShortcutModifiers.None;
        while (true)
        {
            var next = rest.StartsWith("Ctrl+", StringComparison.OrdinalIgnoreCase) ? ShortcutModifiers.Ctrl
                : rest.StartsWith("Shift+", StringComparison.OrdinalIgnoreCase) ? ShortcutModifiers.Shift
                : rest.StartsWith("Alt+", StringComparison.OrdinalIgnoreCase) ? ShortcutModifiers.Alt
                : ShortcutModifiers.None;
            if (next == ShortcutModifiers.None) break;
            if ((modifiers & next) != 0) return false;
            modifiers |= next;
            rest = rest[(rest.IndexOf('+') + 1)..];
        }
        if (rest.Equals("Wheel", StringComparison.OrdinalIgnoreCase)) { binding = Wheel(modifiers); return true; }
        int space = rest.LastIndexOf(' ');
        if (space > 0 && ParseButton(rest[..space]) is { } button)
        {
            string gesture = rest[(space + 1)..];
            if (gesture.Equals("click", StringComparison.OrdinalIgnoreCase)) { binding = Click(button, modifiers); return true; }
            if (gesture.Equals("drag", StringComparison.OrdinalIgnoreCase)) { binding = Drag(button, modifiers); return true; }
        }
        if (ShortcutKeys.Find(rest) is not { } name) return false;
        binding = new(ShortcutKind.Key, modifiers, name, ShortcutButton.Left);
        return true;
    }

    private static ShortcutButton? ParseButton(string text) => text.ToLowerInvariant() switch
    {
        "left" => ShortcutButton.Left,
        "right" => ShortcutButton.Right,
        "middle" => ShortcutButton.Middle,
        "back" => ShortcutButton.Back,
        "forward" => ShortcutButton.Forward,
        _ => null
    };
}

/** Keys a binding may use: the keys the window forwards to ImGui, without modifiers and Escape. */
public static class ShortcutKeys
{
    public static IReadOnlyList<string> Names { get; } = BuildNames();
    private static readonly Dictionary<string, string> Lookup = Names.ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);

    /** Returns the canonical spelling, or null for an unknown key. */
    public static string? Find(string? name) => name is null ? null : Lookup.GetValueOrDefault(name.Trim());

    private static string[] BuildNames()
    {
        List<string> names = [];
        for (char letter = 'A'; letter <= 'Z'; letter++) names.Add(letter.ToString());
        for (char digit = '0'; digit <= '9'; digit++) names.Add(digit.ToString());
        for (int function = 1; function <= 12; function++) names.Add("F" + function.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (char digit = '0'; digit <= '9'; digit++) names.Add("Numpad " + digit);
        names.AddRange(["Numpad .", "Numpad /", "Numpad *", "Numpad -", "Numpad +",
            "Delete", "Backspace", "Insert", "Home", "End", "Page Up", "Page Down", "Space", "Enter", "Tab",
            "Left Arrow", "Right Arrow", "Up Arrow", "Down Arrow",
            "'", ",", "-", ".", "/", ";", "=", "[", "\\", "]", "`", "Pause"]);
        return names.ToArray();
    }
}
