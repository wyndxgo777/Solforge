using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;

namespace SunriseStudio.Rendering;

/**
 * Solforge MCP UI control. Dear ImGui keeps no widget tree, so the editor calls these wrappers instead of
 * ImGui.Button, ImGui.Checkbox and the rest: each forwards to ImGui unchanged and, only while an MCP client has
 * asked for controls in the last minute, records the widget (id, label, window, menu/tab/combo scope, rectangle,
 * value) and applies a queued command to it (click, set value, set checked, select). Commands are applied the
 * way the code reacts to a real click: the wrapper returns true and writes the ref value, so no input is faked.
 * Code drawn between PushBlocked and PopBlocked (the AI workspace and AI settings) is never listed or driven.
 * Everything here runs on the UI thread except Snapshot, Enqueue and Touch.
 */
public static class UiAuto
{
    /** Kind of command a client can send to a control. */
    public enum Op { Invoke, SetValue, SetChecked, Select }

    /** One drawn control; Id is ImGui's own id of the label (stable while the label and its parents are). */
    public sealed record Control(string Surface, uint Id, string Kind, string Label, string Window, string Scope,
        float X, float Y, float Width, float Height, bool Visible, string? Value, bool? Checked, string[]? Options);

    /** A queued command; Done carries (ok, message). */
    public sealed class Command(string surface, uint id, Op op, string? value, bool check)
    {
        public string Surface { get; } = surface;
        public uint Id { get; } = id;
        public Op Operation { get; } = op;
        public string? Value { get; } = value;
        public bool Check { get; } = check;
        public TaskCompletionSource<(bool Ok, string Message)> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long Deadline, Expires;
        internal bool Opened;
        internal readonly List<string> Seen = [];
    }

    private sealed class State
    {
        public string Surface = "main";
        public readonly List<Control> Items = [];
        public volatile Control[] Published = [];
        public long PublishedAt;
        public bool Active;
        public int Blocked;
        public string Window = "";
        public readonly List<(string Kind, uint Id, string Name)> Scopes = [];
        public string ScopeText = "";
    }

    private const int CommandMilliseconds = 1500, RecordMilliseconds = 60_000, MaximumOptions = 200;
    private static readonly ConcurrentDictionary<IntPtr, State> States = new();
    private static readonly ConcurrentQueue<Command> Queue = new();
    private static readonly Dictionary<(string, uint), Command> Pending = [];
    private static long recordUntil;

    /** On while the MCP server runs; off makes every wrapper a plain ImGui call. */
    public static volatile bool Enabled;

    /** Keeps recording on for the next minute (any MCP thread). */
    public static void Touch() => Interlocked.Exchange(ref recordUntil, Environment.TickCount64 + RecordMilliseconds);

    /** Every surface's controls from its last finished frame; surfaces not drawn for 2 s are left out. */
    public static IReadOnlyList<Control> Snapshot()
    {
        long now = Environment.TickCount64;
        return States.Values.Where(state => now - Interlocked.Read(ref state.PublishedAt) < 2000).SelectMany(state => state.Published).ToArray();
    }

    /**
     * The controls, waiting (up to 1.5 s) for a frame drawn with recording on when recording was off, so the first
     * call after a quiet minute still sees everything.
     */
    public static async Task<IReadOnlyList<Control>> SnapshotAsync(CancellationToken cancel)
    {
        long asked = Environment.TickCount64;
        bool recording = Interlocked.Read(ref recordUntil) > asked;
        Touch();
        if (!recording)
            for (int wait = 0; wait < 50 && !States.Values.Any(state => Interlocked.Read(ref state.PublishedAt) > asked + 1); wait++)
                await Task.Delay(30, cancel).ConfigureAwait(false);
        return Snapshot();
    }

    /** Queues a command for the UI thread; it fails if the control is not drawn within 1.5 s. */
    public static Task<(bool Ok, string Message)> Enqueue(Command command)
    {
        Touch();
        // A command Solforge could not draw in time (minimised, busy) is dropped, never applied late.
        command.Expires = Environment.TickCount64 + 5000;
        Queue.Enqueue(command);
        return command.Done.Task;
    }

    /** Call right after ImGui.NewFrame with the context current. */
    public static void BeginFrame(string surface)
    {
        if (!Enabled) return;
        var state = States.GetOrAdd(ImGui.GetCurrentContext(), _ => new State());
        state.Surface = surface;
        if (state.Active)
        {
            state.Published = state.Items.ToArray();
            Interlocked.Exchange(ref state.PublishedAt, Environment.TickCount64);
        }
        state.Items.Clear();
        state.Scopes.Clear();
        state.ScopeText = "";
        state.Window = "";
        state.Blocked = 0;
        long now = Environment.TickCount64;
        while (Queue.TryDequeue(out var command))
        {
            if (now > command.Expires) { command.Done.TrySetResult((false, "Solforge did not draw a frame in time; try again.")); continue; }
            command.Deadline = Math.Min(now + CommandMilliseconds, command.Expires);
            if (Pending.Remove((command.Surface, command.Id), out var replaced)) replaced.Done.TrySetResult((false, "Replaced by a newer command."));
            Pending[(command.Surface, command.Id)] = command;
        }
        foreach (var (key, command) in Pending.Where(pair => now > pair.Value.Deadline).ToArray())
        {
            Pending.Remove(key);
            command.Done.TrySetResult((false, command.Opened && command.Seen.Count > 0
                ? $"No option '{command.Value}' in that list; options: {string.Join(", ", command.Seen.Distinct().Take(40))}."
                : "The control was not drawn (its panel, tab or menu may be closed or scrolled away); call list_ui_controls again."));
        }
        state.Active = Pending.Count > 0 || Environment.TickCount64 < Interlocked.Read(ref recordUntil);
    }

    /** The panel the next controls belong to (StudioForm.BeginPanel and the dialogs). */
    public static void SetWindow(string name)
    {
        if (Current() is { } state) state.Window = Visible(name);
    }

    /** Starts a region whose controls are neither listed nor driven. */
    public static void PushBlocked()
    {
        if (Enabled && States.TryGetValue(ImGui.GetCurrentContext(), out var state)) state.Blocked++;
    }

    /** Ends the region started by PushBlocked. */
    public static void PopBlocked()
    {
        if (Enabled && States.TryGetValue(ImGui.GetCurrentContext(), out var state) && state.Blocked > 0) state.Blocked--;
    }

    /** The visible part of an ImGui label: text before "##" (a "###" id keeps the text before it too). */
    public static string Visible(string label)
    {
        int hash = label.IndexOf("##", StringComparison.Ordinal);
        return (hash < 0 ? label : label[..hash]).Trim();
    }

    /** ImGui's ImHashStr (CRC32, "###" resets to the seed); used for the combo popup id. */
    public static uint Hash(string text, uint seed)
    {
        uint start = ~seed, crc = start;
        byte[] data = Encoding.UTF8.GetBytes(text);
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == '#' && i + 2 < data.Length && data[i + 1] == '#' && data[i + 2] == '#') crc = start;
            crc = (crc >> 8) ^ Crc[(crc ^ data[i]) & 0xFF];
        }
        return ~crc;
    }

    private static readonly uint[] Crc = Enumerable.Range(0, 256).Select(index =>
    {
        uint value = (uint)index;
        for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
        return value;
    }).ToArray();

    // ---- plumbing ----

    private static State? Current() =>
        Enabled && States.TryGetValue(ImGui.GetCurrentContext(), out var state) && state.Active && state.Blocked == 0 ? state : null;

    private static Command? Find(State state, uint id) => Pending.GetValueOrDefault((state.Surface, id));

    private static bool Finish(State state, Command command, bool ok, string message)
    {
        Pending.Remove((state.Surface, command.Id));
        command.Done.TrySetResult((ok, message));
        return ok;
    }

    private static void Record(State state, uint id, string kind, string label, string? value, bool? check, string[]? options = null)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        if (value is { Length: > 300 }) value = value[..300] + "...";
        if (options is { Length: > MaximumOptions }) options = options[..MaximumOptions];
        state.Items.Add(new Control(state.Surface, id, kind, label, state.Window, state.ScopeText, min.X, min.Y, max.X - min.X, max.Y - min.Y,
            ImGui.IsItemVisible(), value, check, options));
    }

    private static void PushScope(State state, string kind, uint id, string label)
    {
        state.Scopes.Add((kind, id, Visible(label)));
        state.ScopeText = string.Join(" > ", state.Scopes.Select(scope => scope.Name));
    }

    private static void PopScope(string kind)
    {
        if (Current() is not { } state || state.Scopes.Count == 0 || state.Scopes[^1].Kind != kind) return;
        state.Scopes.RemoveAt(state.Scopes.Count - 1);
        state.ScopeText = string.Join(" > ", state.Scopes.Select(scope => scope.Name));
    }

    private static bool InPopup(State state) => state.Scopes.Count > 0 && state.Scopes[^1].Kind is "menu" or "combo";

    private static bool Matches(string label, string? wanted) =>
        wanted is not null && (string.Equals(Visible(label), wanted.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(label, wanted, StringComparison.Ordinal));

    /** Buttons, menu items, selectables, radio buttons: a click returns true; a pending ui_select in the enclosing combo picks by label. */
    private static bool Press(State state, uint id, string kind, string label, bool result, bool? check, bool enabled = true)
    {
        Record(state, id, kind, label, null, check);
        if (state.Scopes.Count > 0 && state.Scopes[^1].Kind == "combo"
            && Pending.GetValueOrDefault((state.Surface, state.Scopes[^1].Id)) is { Operation: Op.Select or Op.SetValue, Opened: true } select)
        {
            select.Seen.Add(Visible(label));
            if (Matches(label, select.Value))
            {
                ImGui.CloseCurrentPopup();
                Finish(state, select, true, $"Selected '{Visible(label)}' in '{state.Scopes[^1].Name}'.");
                return true;
            }
        }
        if (Find(state, id) is not { } command) return result;
        if (command.Operation is not (Op.Invoke or Op.Select)) return Finish(state, command, false, $"A {kind} can only be invoked.") || result;
        if (!enabled) return Finish(state, command, false, $"'{Visible(label)}' is disabled right now.") || result;
        if (InPopup(state)) ImGui.CloseCurrentPopup();
        return Finish(state, command, true, $"Clicked {kind} '{Visible(label)}'" + (state.ScopeText.Length > 0 ? $" in {state.ScopeText}." : "."));
    }

    private static bool Toggle(State state, uint id, string kind, string label, bool result, ref bool value)
    {
        Record(state, id, kind, label, null, value);
        if (Find(state, id) is not { } command) return result;
        switch (command.Operation)
        {
            case Op.Invoke:
                value = !value;
                if (kind == "menu_item" && InPopup(state)) ImGui.CloseCurrentPopup();
                Finish(state, command, true, $"Toggled '{Visible(label)}' to {(value ? "on" : "off")}.");
                return true;
            case Op.SetChecked:
                bool changed = value != command.Check;
                value = command.Check;
                Finish(state, command, true, $"'{Visible(label)}' is {(value ? "on" : "off")}" + (changed ? "." : " (unchanged)."));
                return changed || result;
            default:
                return Finish(state, command, false, $"A {kind} takes ui_invoke or ui_set_checked.") || result;
        }
    }

    private static bool Text(State state, uint id, string label, bool result, ref string input, uint maxLength, ImGuiInputTextFlags flags)
    {
        bool secret = (flags & ImGuiInputTextFlags.Password) != 0;
        Record(state, id, "text", label, secret ? "(hidden)" : input, null);
        if (Find(state, id) is not { } command) return result;
        if (command.Operation != Op.SetValue) return Finish(state, command, false, "A text field takes ui_set_value.") || result;
        if ((flags & ImGuiInputTextFlags.ReadOnly) != 0) return Finish(state, command, false, "That text field is read-only.") || result;
        string next = command.Value ?? "";
        int limit = (int)Math.Min(maxLength > 0 ? maxLength - 1 : 0, int.MaxValue);
        if (limit > 0 && next.Length > limit) next = next[..limit];
        input = next;
        Finish(state, command, true, $"Set '{Visible(label)}' to {next.Length} chars.");
        return true;
    }

    private static bool Number<T>(State state, uint id, string kind, string label, bool result, ref T value, T? minimum, T? maximum)
        where T : struct, INumber<T>
    {
        Record(state, id, kind, label, value.ToString(null, CultureInfo.InvariantCulture), null);
        if (Find(state, id) is not { } command) return result;
        if (command.Operation != Op.SetValue) return Finish(state, command, false, $"A {kind} takes ui_set_value.") || result;
        if (!T.TryParse(command.Value?.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out T parsed))
            return Finish(state, command, false, $"'{command.Value}' is not a number.") || result;
        // Sliders clamp like a drag would; min == max means unbounded in ImGui.
        if (minimum is { } low && maximum is { } high && low < high) parsed = T.Clamp(parsed, low, high);
        value = parsed;
        Finish(state, command, true, $"Set '{Visible(label)}' to {parsed.ToString(null, CultureInfo.InvariantCulture)}.");
        return true;
    }

    // ---- wrappers (same overloads as the ImGui.NET calls the editor makes) ----

    /** Drop-in for ImGui.Button; also records the control and applies a pending command. */
    public static bool Button(string label)
    {
        if (Current() is not { } state) return ImGui.Button(label);
        uint id = ImGui.GetID(label);
        return Press(state, id, "button", label, ImGui.Button(label), null);
    }

    /** Drop-in for ImGui.Button; also records the control and applies a pending command. */
    public static bool Button(string label, Vector2 size)
    {
        if (Current() is not { } state) return ImGui.Button(label, size);
        uint id = ImGui.GetID(label);
        return Press(state, id, "button", label, ImGui.Button(label, size), null);
    }

    /** Drop-in for ImGui.SmallButton; also records the control and applies a pending command. */
    public static bool SmallButton(string label)
    {
        if (Current() is not { } state) return ImGui.SmallButton(label);
        uint id = ImGui.GetID(label);
        return Press(state, id, "button", label, ImGui.SmallButton(label), null);
    }

    /** Drop-in for ImGui.MenuItem; also records the control and applies a pending command. */
    public static bool MenuItem(string label)
    {
        if (Current() is not { } state) return ImGui.MenuItem(label);
        uint id = ImGui.GetID(label);
        return Press(state, id, "menu_item", label, ImGui.MenuItem(label), null);
    }

    /** Drop-in for ImGui.MenuItem; also records the control and applies a pending command. */
    public static bool MenuItem(string label, string shortcut)
    {
        if (Current() is not { } state) return ImGui.MenuItem(label, shortcut);
        uint id = ImGui.GetID(label);
        return Press(state, id, "menu_item", label, ImGui.MenuItem(label, shortcut), null);
    }

    /** Drop-in for ImGui.MenuItem; also records the control and applies a pending command. */
    public static bool MenuItem(string label, string shortcut, bool selected)
    {
        if (Current() is not { } state) return ImGui.MenuItem(label, shortcut, selected);
        uint id = ImGui.GetID(label);
        return Press(state, id, "menu_item", label, ImGui.MenuItem(label, shortcut, selected), selected);
    }

    /** Drop-in for ImGui.MenuItem; also records the control and applies a pending command. */
    public static bool MenuItem(string label, string shortcut, bool selected, bool enabled)
    {
        if (Current() is not { } state) return ImGui.MenuItem(label, shortcut, selected, enabled);
        uint id = ImGui.GetID(label);
        return Press(state, id, "menu_item", label, ImGui.MenuItem(label, shortcut, selected, enabled), selected, enabled);
    }

    /** Drop-in for ImGui.MenuItem; also records the control and applies a pending command. */
    public static bool MenuItem(string label, string shortcut, ref bool selected)
    {
        if (Current() is not { } state) return ImGui.MenuItem(label, shortcut, ref selected);
        uint id = ImGui.GetID(label);
        bool result = ImGui.MenuItem(label, shortcut, ref selected);
        return Toggle(state, id, "menu_item", label, result, ref selected);
    }

    /** Drop-in for ImGui.Selectable; also records the control and applies a pending command. */
    public static bool Selectable(string label)
    {
        if (Current() is not { } state) return ImGui.Selectable(label);
        uint id = ImGui.GetID(label);
        return Press(state, id, "selectable", label, ImGui.Selectable(label), null);
    }

    /** Drop-in for ImGui.Selectable; also records the control and applies a pending command. */
    public static bool Selectable(string label, bool selected)
    {
        if (Current() is not { } state) return ImGui.Selectable(label, selected);
        uint id = ImGui.GetID(label);
        return Press(state, id, "selectable", label, ImGui.Selectable(label, selected), selected);
    }

    /** Drop-in for ImGui.Selectable; also records the control and applies a pending command. */
    public static bool Selectable(string label, bool selected, ImGuiSelectableFlags flags)
    {
        if (Current() is not { } state) return ImGui.Selectable(label, selected, flags);
        uint id = ImGui.GetID(label);
        return Press(state, id, "selectable", label, ImGui.Selectable(label, selected, flags), selected, (flags & ImGuiSelectableFlags.Disabled) == 0);
    }

    /** Drop-in for ImGui.Selectable; also records the control and applies a pending command. */
    public static bool Selectable(string label, bool selected, ImGuiSelectableFlags flags, Vector2 size)
    {
        if (Current() is not { } state) return ImGui.Selectable(label, selected, flags, size);
        uint id = ImGui.GetID(label);
        return Press(state, id, "selectable", label, ImGui.Selectable(label, selected, flags, size), selected, (flags & ImGuiSelectableFlags.Disabled) == 0);
    }

    /** Drop-in for ImGui.RadioButton; also records the control and applies a pending command. */
    public static bool RadioButton(string label, bool active)
    {
        if (Current() is not { } state) return ImGui.RadioButton(label, active);
        uint id = ImGui.GetID(label);
        return Press(state, id, "radio", label, ImGui.RadioButton(label, active), active);
    }

    /** Drop-in for ImGui.RadioButton; also records the control and applies a pending command. */
    public static bool RadioButton(string label, ref int value, int button)
    {
        if (Current() is not { } state) return ImGui.RadioButton(label, ref value, button);
        uint id = ImGui.GetID(label);
        bool result = ImGui.RadioButton(label, ref value, button);
        bool pressed = Press(state, id, "radio", label, result, value == button);
        if (pressed && !result) value = button;
        return pressed;
    }

    /** Drop-in for ImGui.Checkbox; also records the control and applies a pending command. */
    public static bool Checkbox(string label, ref bool value)
    {
        if (Current() is not { } state) return ImGui.Checkbox(label, ref value);
        uint id = ImGui.GetID(label);
        bool result = ImGui.Checkbox(label, ref value);
        return Toggle(state, id, "checkbox", label, result, ref value);
    }

    /** Drop-in for ImGui.InputText; also records the control and applies a pending command. */
    public static bool InputText(string label, ref string input, uint maxLength)
    {
        if (Current() is not { } state) return ImGui.InputText(label, ref input, maxLength);
        uint id = ImGui.GetID(label);
        bool result = ImGui.InputText(label, ref input, maxLength);
        return Text(state, id, label, result, ref input, maxLength, ImGuiInputTextFlags.None);
    }

    /** Drop-in for ImGui.InputText; also records the control and applies a pending command. */
    public static bool InputText(string label, ref string input, uint maxLength, ImGuiInputTextFlags flags)
    {
        if (Current() is not { } state) return ImGui.InputText(label, ref input, maxLength, flags);
        uint id = ImGui.GetID(label);
        bool result = ImGui.InputText(label, ref input, maxLength, flags);
        return Text(state, id, label, result, ref input, maxLength, flags);
    }

    /** Drop-in for ImGui.InputTextMultiline; also records the control and applies a pending command. */
    public static bool InputTextMultiline(string label, ref string input, uint maxLength, Vector2 size)
    {
        if (Current() is not { } state) return ImGui.InputTextMultiline(label, ref input, maxLength, size);
        uint id = ImGui.GetID(label);
        bool result = ImGui.InputTextMultiline(label, ref input, maxLength, size);
        return Text(state, id, label, result, ref input, maxLength, ImGuiInputTextFlags.None);
    }

    /** Drop-in for ImGui.InputTextMultiline; also records the control and applies a pending command. */
    public static bool InputTextMultiline(string label, ref string input, uint maxLength, Vector2 size, ImGuiInputTextFlags flags)
    {
        if (Current() is not { } state) return ImGui.InputTextMultiline(label, ref input, maxLength, size, flags);
        uint id = ImGui.GetID(label);
        bool result = ImGui.InputTextMultiline(label, ref input, maxLength, size, flags);
        return Text(state, id, label, result, ref input, maxLength, flags);
    }

    /** Drop-in for ImGui.InputTextWithHint; also records the control and applies a pending command. */
    public static bool InputTextWithHint(string label, string hint, ref string input, uint maxLength)
    {
        if (Current() is not { } state) return ImGui.InputTextWithHint(label, hint, ref input, maxLength);
        uint id = ImGui.GetID(label);
        bool result = ImGui.InputTextWithHint(label, hint, ref input, maxLength);
        return Text(state, id, label.StartsWith("##", StringComparison.Ordinal) ? hint + label : label, result, ref input, maxLength, ImGuiInputTextFlags.None);
    }

    /** Drop-in for ImGui.InputTextWithHint; also records the control and applies a pending command. */
    public static bool InputTextWithHint(string label, string hint, ref string input, uint maxLength, ImGuiInputTextFlags flags)
    {
        if (Current() is not { } state) return ImGui.InputTextWithHint(label, hint, ref input, maxLength, flags);
        uint id = ImGui.GetID(label);
        bool result = ImGui.InputTextWithHint(label, hint, ref input, maxLength, flags);
        return Text(state, id, label.StartsWith("##", StringComparison.Ordinal) ? hint + label : label, result, ref input, maxLength, flags);
    }

    /** Drop-in for ImGui.SliderFloat; also records the control and applies a pending command. */
    public static bool SliderFloat(string label, ref float value, float minimum, float maximum)
    {
        if (Current() is not { } state) return ImGui.SliderFloat(label, ref value, minimum, maximum);
        uint id = ImGui.GetID(label);
        bool result = ImGui.SliderFloat(label, ref value, minimum, maximum);
        return Number(state, id, "slider", label, result, ref value, minimum, maximum);
    }

    /** Drop-in for ImGui.SliderFloat; also records the control and applies a pending command. */
    public static bool SliderFloat(string label, ref float value, float minimum, float maximum, string format)
    {
        if (Current() is not { } state) return ImGui.SliderFloat(label, ref value, minimum, maximum, format);
        uint id = ImGui.GetID(label);
        bool result = ImGui.SliderFloat(label, ref value, minimum, maximum, format);
        return Number(state, id, "slider", label, result, ref value, minimum, maximum);
    }

    /** Drop-in for ImGui.SliderInt; also records the control and applies a pending command. */
    public static bool SliderInt(string label, ref int value, int minimum, int maximum)
    {
        if (Current() is not { } state) return ImGui.SliderInt(label, ref value, minimum, maximum);
        uint id = ImGui.GetID(label);
        bool result = ImGui.SliderInt(label, ref value, minimum, maximum);
        return Number(state, id, "slider", label, result, ref value, minimum, maximum);
    }

    /** Drop-in for ImGui.SliderInt; also records the control and applies a pending command. */
    public static bool SliderInt(string label, ref int value, int minimum, int maximum, string format)
    {
        if (Current() is not { } state) return ImGui.SliderInt(label, ref value, minimum, maximum, format);
        uint id = ImGui.GetID(label);
        bool result = ImGui.SliderInt(label, ref value, minimum, maximum, format);
        return Number(state, id, "slider", label, result, ref value, minimum, maximum);
    }

    /** Drop-in for ImGui.DragFloat; also records the control and applies a pending command. */
    public static bool DragFloat(string label, ref float value, float speed)
    {
        if (Current() is not { } state) return ImGui.DragFloat(label, ref value, speed);
        uint id = ImGui.GetID(label);
        bool result = ImGui.DragFloat(label, ref value, speed);
        return Number<float>(state, id, "number", label, result, ref value, null, null);
    }

    /** Drop-in for ImGui.DragFloat; also records the control and applies a pending command. */
    public static bool DragFloat(string label, ref float value, float speed, float minimum, float maximum, string format)
    {
        if (Current() is not { } state) return ImGui.DragFloat(label, ref value, speed, minimum, maximum, format);
        uint id = ImGui.GetID(label);
        bool result = ImGui.DragFloat(label, ref value, speed, minimum, maximum, format);
        return Number(state, id, "number", label, result, ref value, minimum, maximum);
    }

    /** Drop-in for ImGui.InputInt; also records the control and applies a pending command. */
    public static bool InputInt(string label, ref int value)
    {
        if (Current() is not { } state) return ImGui.InputInt(label, ref value);
        uint id = ImGui.GetID(label);
        bool result = ImGui.InputInt(label, ref value);
        return Number<int>(state, id, "number", label, result, ref value, null, null);
    }

    /** Drop-in for ImGui.InputInt; also records the control and applies a pending command. */
    public static bool InputInt(string label, ref int value, int step)
    {
        if (Current() is not { } state) return ImGui.InputInt(label, ref value, step);
        uint id = ImGui.GetID(label);
        bool result = ImGui.InputInt(label, ref value, step);
        return Number<int>(state, id, "number", label, result, ref value, null, null);
    }

    /** Drop-in for ImGui.InputFloat; also records the control and applies a pending command. */
    public static bool InputFloat(string label, ref float value)
    {
        if (Current() is not { } state) return ImGui.InputFloat(label, ref value);
        uint id = ImGui.GetID(label);
        bool result = ImGui.InputFloat(label, ref value);
        return Number<float>(state, id, "number", label, result, ref value, null, null);
    }

    /** Drop-in for ImGui.InputFloat; also records the control and applies a pending command. */
    public static bool InputFloat(string label, ref float value, float step, float stepFast, string format)
    {
        if (Current() is not { } state) return ImGui.InputFloat(label, ref value, step, stepFast, format);
        uint id = ImGui.GetID(label);
        bool result = ImGui.InputFloat(label, ref value, step, stepFast, format);
        return Number<float>(state, id, "number", label, result, ref value, null, null);
    }

    /** Drop-in for ImGui.Combo; also records the control and applies a pending command. */
    public static bool Combo(string label, ref int current, string[] items, int count)
    {
        if (Current() is not { } state) return ImGui.Combo(label, ref current, items, count);
        uint id = ImGui.GetID(label);
        bool result = ImGui.Combo(label, ref current, items, count);
        int shown = Math.Min(count, items.Length);
        Record(state, id, "combo", label, current >= 0 && current < shown ? items[current] : null, null, items[..shown]);
        if (Find(state, id) is not { } command) return result;
        if (command.Operation is not (Op.Select or Op.SetValue)) return Finish(state, command, false, "A list box takes ui_select with an option.") || result;
        int index = Array.FindIndex(items, 0, shown, item => Matches(item, command.Value));
        if (index < 0 && int.TryParse(command.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number >= 0 && number < shown) index = number;
        if (index < 0)
            return Finish(state, command, false, $"No option '{command.Value}'; options: {string.Join(", ", items.Take(Math.Min(shown, 40)))}.") || result;
        current = index;
        Finish(state, command, true, $"Selected '{items[index]}' in '{Visible(label)}'.");
        return true;
    }

    /** A pending ui_invoke or ui_select opens the combo's popup the way a click would (ImGui's "##ComboPopup" id). */
    public static bool BeginCombo(string label, string preview) => BeginComboCore(label, preview, ImGuiComboFlags.None);

    /** Combo popup; records it and opens it for a pending command. */
    public static bool BeginCombo(string label, string preview, ImGuiComboFlags flags) => BeginComboCore(label, preview, flags);

    private static bool BeginComboCore(string label, string preview, ImGuiComboFlags flags)
    {
        if (Current() is not { } state) return ImGui.BeginCombo(label, preview, flags);
        uint id = ImGui.GetID(label);
        var command = Find(state, id);
        if (command is { Operation: Op.Invoke or Op.Select or Op.SetValue, Opened: false })
        {
            ImGui.OpenPopup(Hash("##ComboPopup", id));
            command.Opened = true;
            command.Deadline = Environment.TickCount64 + CommandMilliseconds;
        }
        bool open = ImGui.BeginCombo(label, preview, flags);
        Record(state, id, "combo", label, preview, null);
        if (command is { Operation: Op.Invoke }) Finish(state, command, open, open ? $"Opened '{Visible(label)}'; its options are listed under that scope." : "The list did not open.");
        else if (command is { Operation: Op.SetChecked }) Finish(state, command, false, "A combo takes ui_select.");
        if (open) PushScope(state, "combo", id, label.StartsWith("##", StringComparison.Ordinal) ? preview + label : label);
        return open;
    }

    /** Closes the popup opened by BeginCombo. */
    public static void EndCombo()
    {
        if (Current() is { } state && state.Scopes.Count > 0 && state.Scopes[^1].Kind == "combo"
            && Pending.GetValueOrDefault((state.Surface, state.Scopes[^1].Id)) is { Operation: Op.Select or Op.SetValue, Opened: true } select
            && select.Seen.Count > 0)
            Finish(state, select, false, $"No option '{select.Value}'; options: {string.Join(", ", select.Seen.Distinct().Take(40))}.");
        PopScope("combo");
        ImGui.EndCombo();
    }

    /** Menu; a pending ui_invoke opens it. Records the menu and scopes the items inside. */
    public static bool BeginMenu(string label) => BeginMenuCore(label, true);

    /** Menu; a pending ui_invoke opens it. Records the menu and scopes the items inside. */
    public static bool BeginMenu(string label, bool enabled) => BeginMenuCore(label, enabled);

    private static bool BeginMenuCore(string label, bool enabled)
    {
        if (Current() is not { } state) return ImGui.BeginMenu(label, enabled);
        uint id = ImGui.GetID(label);
        var command = Find(state, id);
        if (command is { Operation: Op.Invoke or Op.Select } && enabled) ImGui.OpenPopup(label);
        bool open = ImGui.BeginMenu(label, enabled);
        Record(state, id, "menu", label, null, open);
        if (command is not null)
            Finish(state, command, open && command.Operation is Op.Invoke or Op.Select,
                !enabled ? $"Menu '{Visible(label)}' is disabled." : open ? $"Opened menu '{Visible(label)}'; list_ui_controls shows its items." : "A menu takes ui_invoke.");
        if (open) PushScope(state, "menu", id, label);
        return open;
    }

    /** Closes the menu opened by BeginMenu. */
    public static void EndMenu()
    {
        PopScope("menu");
        ImGui.EndMenu();
    }

    /** Tab; a pending ui_invoke or ui_select selects it. Records the tab and scopes the content. */
    public static bool BeginTabItem(string label)
    {
        if (Current() is not { } state) return ImGui.BeginTabItem(label);
        uint id = ImGui.GetID(label);
        var command = Find(state, id);
        bool open = command is { Operation: Op.Invoke or Op.Select } ? BeginTabItemSelected(label) : ImGui.BeginTabItem(label);
        Record(state, id, "tab", label, null, open);
        if (command is not null)
            Finish(state, command, command.Operation is Op.Invoke or Op.Select,
                command.Operation is Op.Invoke or Op.Select ? $"Selected tab '{Visible(label)}'." : "A tab takes ui_invoke or ui_select.");
        if (open) PushScope(state, "tab", id, label);
        return open;
    }

    /** Closes the tab opened by BeginTabItem. */
    public static void EndTabItem()
    {
        PopScope("tab");
        ImGui.EndTabItem();
    }

    /** BeginTabItem with SetSelected and no close button (ImGui.NET has no flags overload without p_open). */
    private static unsafe bool BeginTabItemSelected(string label)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(label + "\0");
        fixed (byte* text = bytes) return ImGuiNative.igBeginTabItem(text, null, ImGuiTabItemFlags.SetSelected) != 0;
    }
}
