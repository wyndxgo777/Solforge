using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor.Ai;

/**
 * Solforge Phase 47B: draws a ConPTY screen with ImGui and turns keys into terminal input while the
 * terminal has focus. Characters come from the form's KeyPress (control characters included); keys
 * that make no character (arrows, Home, Delete...) come from ImGui.
 */
internal sealed class TerminalView : IDisposable
{
    private readonly Queue<char> typed = new();
    private int scroll;

    public ConPtyTerminal? Terminal { get; private set; }
    public bool Focused { get; private set; }
    public string? Failure { get; private set; }

    public void Start(string commandLine, string folder, int columns, int rows, IReadOnlyDictionary<string, string>? environment = null)
    {
        Stop();
        Failure = null;
        try { Terminal = new ConPtyTerminal(commandLine, folder, Math.Max(20, columns), Math.Max(5, rows), environment); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        { Failure = exception.Message; Terminal = null; }
    }

    public void Stop()
    {
        Terminal?.Dispose();
        Terminal = null;
        typed.Clear();
    }

    /** From the form's KeyPress; only kept while the terminal has focus. */
    public void OnChar(char c)
    {
        if (Focused && Terminal is not null) typed.Enqueue(c);
    }

    public void Paste(string text)
    {
        if (Terminal is not { } terminal || text.Length == 0) return;
        terminal.Send(terminal.Screen.BracketedPaste ? "\x1b[200~" + text + "\x1b[201~" : text);
    }

    public void Draw(ImFontPtr font, Vector2 size)
    {
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        draw.AddRectFilled(origin, origin + size, SolTheme.U32(SolTheme.Bg));
        ImGui.InvisibleButton("##ai-terminal", Vector2.Max(size, Vector2.One));
        if (ImGui.IsItemClicked()) Focused = true;
        else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsItemHovered()) Focused = false;
        if (ImGui.IsItemHovered()) { scroll = Math.Max(0, scroll + (int)(ImGui.GetIO().MouseWheel * 3)); }
        if (Terminal is not { } terminal)
        {
            draw.AddText(origin + new Vector2(8), SolTheme.U32(SolTheme.Dim), Failure ?? "Pick an engine and press Start.");
            return;
        }
        ImGui.PushFont(font);
        var cell = ImGui.CalcTextSize("M");
        float fontSize = ImGui.GetFontSize();
        ImGui.PopFont();
        int columns = Math.Max(20, (int)((size.X - 8) / cell.X)), rows = Math.Max(5, (int)((size.Y - 8) / cell.Y));
        terminal.Resize(columns, rows);
        if (Focused) Input(terminal);
        var screen = terminal.Screen;
        lock (screen.Gate)
        {
            scroll = Math.Min(scroll, screen.Scrollback.Count);
            var start = origin + new Vector2(4);
            for (int row = 0; row < screen.Rows; row++)
            {
                int source = row - scroll;
                var line = source < 0 ? screen.Scrollback[screen.Scrollback.Count + source] : screen.Line(source);
                DrawLine(draw, font, fontSize, start + new Vector2(0, row * cell.Y), cell, line);
            }
            if (scroll == 0 && screen.CursorVisible && Focused && (ImGui.GetTime() % 1.0) < .6)
            {
                var at = start + new Vector2(screen.CursorX * cell.X, screen.CursorY * cell.Y);
                draw.AddRectFilled(at, at + cell, SolTheme.U32(SolTheme.Sun) & 0xB0FFFFFF);
            }
        }
        if (terminal.HasExited)
            draw.AddText(origin + new Vector2(8, size.Y - cell.Y - 6), SolTheme.U32(SolTheme.Sun), "[process exited - press Restart]");
        if (!Focused) draw.AddRect(origin, origin + size, SolTheme.U32(SolTheme.Line));
        else draw.AddRect(origin, origin + size, SolTheme.U32(SolTheme.Sun) & 0x80FFFFFF);
    }

    private static void DrawLine(ImDrawListPtr draw, ImFontPtr font, float fontSize, Vector2 at, Vector2 cell, VtCell[] line)
    {
        uint defaultText = SolTheme.U32(SolTheme.Text);
        int x = 0;
        var run = new System.Text.StringBuilder();
        while (x < line.Length)
        {
            var first = line[x];
            uint fg = first.Inverse ? Abgr(first.Background, SolTheme.U32(SolTheme.Bg)) : Abgr(first.Foreground, defaultText);
            uint bg = first.Inverse ? Abgr(first.Foreground, defaultText) : Abgr(first.Background, 0);
            int end = x;
            run.Clear();
            while (end < line.Length && line[end].Foreground == first.Foreground && line[end].Background == first.Background
                && line[end].Inverse == first.Inverse)
            {
                // Glyphs are one cell wide, so the right half of a wide character pads with a space.
                char c = line[end++].Char;
                run.Append(c == VtScreen.Continuation ? ' ' : c);
            }
            var from = at + new Vector2(x * cell.X, 0);
            if (bg != 0) draw.AddRectFilled(from, from + new Vector2((end - x) * cell.X, cell.Y), bg);
            string text = run.ToString();
            if (text.AsSpan().TrimEnd(' ').Length > 0) draw.AddText(font, fontSize, from, fg, text);
            x = end;
        }
    }

    /** 0xAARRGGBB (0 = fallback) to ImGui's 0xAABBGGRR. */
    private static uint Abgr(uint argb, uint fallback) => argb == 0 ? fallback
        : argb & 0xFF00FF00 | (argb & 0xFF) << 16 | (argb >> 16) & 0xFF;

    private void Input(ConPtyTerminal terminal)
    {
        var io = ImGui.GetIO();
        // The shortcut system must not see keys typed into the terminal.
        io.WantTextInput = true;
        while (typed.TryDequeue(out char c))
        {
            if (c == '\b') terminal.Send("\x7f");
            else if (c == 22 && io.KeyCtrl) Paste(ImGui.GetClipboardText() ?? "");
            else terminal.Send(c.ToString());
            scroll = 0;
        }
        string arrows = terminal.Screen.ApplicationCursor ? "\x1bO" : "\x1b[";
        (ImGuiKey Key, string Text)[] keys =
        [
            (ImGuiKey.UpArrow, arrows + "A"), (ImGuiKey.DownArrow, arrows + "B"), (ImGuiKey.RightArrow, arrows + "C"),
            (ImGuiKey.LeftArrow, arrows + "D"), (ImGuiKey.Home, "\x1b[H"), (ImGuiKey.End, "\x1b[F"),
            (ImGuiKey.Delete, "\x1b[3~"), (ImGuiKey.PageUp, "\x1b[5~"), (ImGuiKey.PageDown, "\x1b[6~"), (ImGuiKey.Insert, "\x1b[2~")
        ];
        foreach (var (key, text) in keys)
            if (ImGui.IsKeyPressed(key)) { terminal.Send(text); scroll = 0; }
        if (io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.Tab)) terminal.Send("\x1b[Z");
    }

    public void Dispose() => Stop();
}
