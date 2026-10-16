using System.Numerics;
using System.Text;
using ImGuiNET;
using SunriseStudio.Missions;

namespace SunriseStudio.Editor;

/** MissionGraphPage part: syntax-coloured Lua source view. */
public sealed partial class MissionGraphPage
{
    private sealed record SyntaxSnapshot(string Source, IReadOnlyList<LuaToken> Tokens, int[] Lines)
    {
        private ImFontPtr measuredFont;
        private float measuredSize = -1, width;

        /** The widest line at this font and size; measured again only when either changes. */
        internal float Width(ImFontPtr font, float size)
        {
            if (measuredSize == size && measuredFont.Equals(font)) return width;
            measuredFont = font; measuredSize = size; width = 0;
            for (int line = 0; line < Lines.Length; line++)
            {
                var (start, end) = Line(line);
                string text = Source[start..end].Replace("\t", "    ", StringComparison.Ordinal);
                width = Math.Max(width, font.CalcTextSizeA(size, float.MaxValue, 0, text).X);
            }
            return width;
        }

        /** Start and end offsets of a source line, excluding the line break. */
        internal (int Start, int End) Line(int index)
        {
            int start = Lines[index], end = index + 1 < Lines.Length ? Lines[index + 1] - 1 : Source.Length;
            if (end > start && Source[end - 1] == '\r') end--;
            return (start, end);
        }
    }
    private readonly Dictionary<string, SyntaxSnapshot> syntaxSnapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> codeEditing = new(StringComparer.Ordinal);
    // The field being typed in; its buffer only grows while it is active, so a paste has room.
    private string? activeInput;
    private uint activeCapacity;
    private const uint PasteRoom = 256 * 1024;

    private void DrawCanvasLua(ImDrawListPtr draw, ImFontPtr font, float size, Vector2 at, string source, float lineHeight)
    {
        var snapshot = Syntax(source);
        for (int line = 0; line < snapshot.Lines.Length; line++)
        {
            var (start, end) = snapshot.Line(line);
            DrawLuaLine(draw, font, size, at + new Vector2(0, line * lineHeight), snapshot, start, end);
        }
    }

    /**
     * The preview and editor share the caller's draft; toggling never copies or reparses the document.
     * @param preview False for an always-open editor with no Edit/Preview toggle.
     * @param headroom Bytes the user can type past the current text.
     */
    private void LuaText(string id, ref string text, Vector2 size, bool editable = true, bool preview = true, uint headroom = 64 * 1024)
    {
        bool editing = editable && (!preview || codeEditing.GetValueOrDefault(id));
        if (editable && preview && UiAuto.SmallButton((editing ? "Preview" : "Edit") + "##" + id))
            codeEditing[id] = editing = !editing;
        if (CodeFont is { } font) ImGui.PushFont(font);
        if (editing) CodeInput("##lua-" + id, ref text, headroom, size, ImGuiInputTextFlags.AllowTabInput);
        else LuaPreview(id, text, size);
        if (CodeFont is not null) ImGui.PopFont();
    }

    /**
     * The same code editor for the unified Graph panel's other models (a node graph's JSON source and compiled output).
     * @param lua Lua gets the highlighted preview when read-only; other text uses the plain code field.
     * @return True when the text changed.
     */
    internal bool CodeText(string id, ref string text, Vector2 size, bool editable, bool lua)
    {
        string before = text;
        if (lua) { LuaText(id, ref text, size, editable, preview: false); return text != before; }
        if (CodeFont is { } font) ImGui.PushFont(font);
        CodeInput("##code-" + id, ref text, 256 * 1024, size,
            editable ? ImGuiInputTextFlags.AllowTabInput : ImGuiInputTextFlags.ReadOnly);
        if (CodeFont is not null) ImGui.PopFont();
        return text != before;
    }

    /** ImGui copies the whole buffer each frame, so an idle field gets one sized to its text. */
    private bool CodeInput(string label, ref string text, uint headroom, Vector2 size,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, bool multiline = true)
    {
        uint capacity = label == activeInput ? activeCapacity : (uint)Encoding.UTF8.GetByteCount(text) + headroom;
        bool changed = multiline ? UiAuto.InputTextMultiline(label, ref text, capacity, size, flags)
            : UiAuto.InputText(label, ref text, capacity, flags);
        if (ImGui.IsItemActive())
        {
            if (activeInput != label) { activeInput = label; activeCapacity = Math.Max(capacity, PasteRoom); }
        }
        else if (activeInput == label) activeInput = null;
        return changed;
    }

    /** Lexical spans are cached by exact text and only visible lines are painted. */
    private void LuaPreview(string id, string source, Vector2 size)
    {
        var snapshot = Syntax(source);
        ImGui.BeginChild("syntax-" + id, size, ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar);
        var origin = ImGui.GetCursorScreenPos();
        float height = ImGui.GetTextLineHeight();
        int first = Math.Max(0, (int)(ImGui.GetScrollY() / height) - 1);
        int last = Math.Min(snapshot.Lines.Length, first + (int)(ImGui.GetWindowHeight() / height) + 3);
        var font = ImGui.GetFont(); float fontSize = ImGui.GetFontSize();
        float width = snapshot.Width(font, fontSize);
        for (int line = first; line < last; line++)
        {
            var (start, end) = snapshot.Line(line);
            DrawLuaLine(ImGui.GetWindowDrawList(), font, fontSize,
                origin + new Vector2(0, line * height), snapshot, start, end);
        }
        ImGui.Dummy(new(Math.Max(width + 12, ImGui.GetContentRegionAvail().X), Math.Max(height, height * snapshot.Lines.Length)));
        ImGui.EndChild();
    }

    /** Tokens and line starts of source, cached by exact text. */
    private SyntaxSnapshot Syntax(string source)
    {
        if (!syntaxSnapshots.TryGetValue(source, out var snapshot))
        {
            var lines = new List<int> { 0 };
            for (int i = 0; i < source.Length; i++) if (source[i] == '\n') lines.Add(i + 1);
            snapshot = new(source, LuaScript.Syntax(source), lines.ToArray());
            // Every code node on the canvas needs its own entry, so the count cap must exceed a canvas.
            if (syntaxSnapshots.Count >= 512 || syntaxSnapshots.Keys.Sum(key => (long)key.Length) + source.Length > 2 * 1024 * 1024)
                syntaxSnapshots.Clear();
            syntaxSnapshots[source] = snapshot;
        }
        return snapshot;
    }

    /** Paints one line colored by token kind; text between tokens is plain. */
    private static void DrawLuaLine(ImDrawListPtr draw, ImFontPtr font, float size, Vector2 at,
        SyntaxSnapshot snapshot, int start, int end)
    {
        var tokens = snapshot.Tokens;
        int low = 0, high = tokens.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (tokens[middle].Span.End <= start) low = middle + 1; else high = middle;
        }
        int cursor = start;
        float width = 0;
        for (int i = low; i < tokens.Count && tokens[i].Span.Start < end; i++)
        {
            var token = tokens[i];
            int left = Math.Max(start, token.Span.Start), right = Math.Min(end, token.Span.End);
            if (left > cursor) Paint(cursor, left, LuaTokenKind.Plain);
            Paint(left, right, token.Kind); cursor = right;
        }
        if (cursor < end) Paint(cursor, end, LuaTokenKind.Plain);

        void Paint(int from, int to, LuaTokenKind kind)
        {
            string text = snapshot.Source[from..to].Replace("\t", "    ", StringComparison.Ordinal);
            var tint = kind switch
            {
                LuaTokenKind.Keyword => new Vector4(.78f, .58f, .95f, 1),
                LuaTokenKind.String => new Vector4(.68f, .82f, .52f, 1),
                LuaTokenKind.Comment => new Vector4(.49f, .62f, .66f, 1),
                LuaTokenKind.Number => new Vector4(.95f, .72f, .42f, 1),
                _ => new Vector4(.86f, .88f, .92f, 1)
            };
            draw.AddText(font, size, at + new Vector2(width, 0), ImGui.GetColorU32(tint), text);
            width += font.CalcTextSizeA(size, float.MaxValue, 0, text).X;
        }
    }
}
