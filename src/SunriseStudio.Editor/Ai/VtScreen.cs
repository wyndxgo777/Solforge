using System.Text;

namespace SunriseStudio.Editor.Ai;

/** One terminal cell: a UTF-16 character and its colours (0xAARRGGBB; 0 = default). */
internal struct VtCell
{
    public char Char;
    public uint Foreground, Background;
    public bool Bold, Inverse;
}

/**
 * Solforge Phase 47B: the VT100/xterm subset that ConPTY and the AI CLIs emit: cursor moves, erase,
 * scroll regions, insert/delete, SGR 16/256/true colour, the alternate screen and bracketed paste.
 * Feed runs on the reader thread and drawing on the UI thread, so both lock the screen.
 */
internal sealed class VtScreen(int columns, int rows)
{
    private const int ScrollbackLimit = 2000;
    private enum State { Ground, Escape, Csi, Osc, OscEscape, Charset }

    private VtCell[][] lines = Make(columns, rows);
    private VtCell[][]? saved;
    private readonly List<VtCell[]> scrollback = [];
    private State state;
    private readonly StringBuilder parameters = new();
    private int top, bottom = rows - 1, savedX, savedY;
    private bool pendingWrap;
    private VtCell pen = new() { Char = ' ' };

    public object Gate { get; } = new();
    public int Columns { get; private set; } = columns;
    public int Rows { get; private set; } = rows;
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool BracketedPaste { get; private set; }
    public bool ApplicationCursor { get; private set; }
    public long Revision { get; private set; }
    public IReadOnlyList<VtCell[]> Scrollback => scrollback;
    public VtCell[] Line(int row) => lines[row];
    /** Answers device queries (cursor position) back to the program. */
    public Action<string>? Reply { get; set; }

    private static VtCell[][] Make(int columns, int rows)
    {
        var made = new VtCell[rows][];
        for (int row = 0; row < rows; row++) made[row] = Blank(columns);
        return made;
    }

    private static VtCell[] Blank(int columns, VtCell? fill = null)
    {
        var line = new VtCell[columns];
        Array.Fill(line, fill ?? new VtCell { Char = ' ' });
        return line;
    }

    public void Resize(int columns, int rows)
    {
        lock (Gate)
        {
            var next = Make(columns, rows);
            int shift = Math.Max(0, CursorY - (rows - 1));
            for (int row = 0; row < rows && row + shift < Rows; row++)
                Array.Copy(lines[row + shift], next[row], Math.Min(columns, Columns));
            for (int row = 0; row < shift; row++) PushScrollback(lines[row]);
            lines = next;
            Columns = columns; Rows = rows;
            top = 0; bottom = rows - 1;
            CursorX = Math.Min(CursorX, columns - 1);
            CursorY = Math.Clamp(CursorY - shift, 0, rows - 1);
            saved = null;
            Revision++;
        }
    }

    /** Plain text of the visible screen, for checks and copy. */
    public string Text()
    {
        lock (Gate)
            return string.Join('\n', lines.Select(line => new string(line.Select(cell => cell.Char).ToArray()).TrimEnd()));
    }

    public void Feed(ReadOnlySpan<char> text)
    {
        lock (Gate)
        {
            foreach (char c in text) Step(c);
            Revision++;
        }
    }

    private void Step(char c)
    {
        switch (state)
        {
            case State.Ground:
                if (c == '\x1b') state = State.Escape;
                else if (c < ' ' || c == '\x7f') Control(c);
                else Print(c);
                return;
            case State.Escape:
                state = State.Ground;
                switch (c)
                {
                    case '[': parameters.Clear(); state = State.Csi; break;
                    case ']': parameters.Clear(); state = State.Osc; break;
                    case '(' or ')' or '*' or '+': state = State.Charset; break;
                    case '7': savedX = CursorX; savedY = CursorY; break;
                    case '8': CursorX = savedX; CursorY = savedY; pendingWrap = false; break;
                    case 'M': if (CursorY == top) ScrollDown(1); else if (CursorY > 0) CursorY--; break;
                    case 'D': LineFeed(); break;
                    case 'E': CursorX = 0; LineFeed(); break;
                    case 'c': Reset(); break;
                }
                return;
            case State.Charset: state = State.Ground; return;
            case State.Osc:
                if (c == '\x07') state = State.Ground;
                else if (c == '\x1b') state = State.OscEscape;
                return;
            case State.OscEscape: state = c == '\\' ? State.Ground : State.Osc; return;
            case State.Csi:
                if (c is >= '0' and <= '9' or ';' or ':' or '?' or '>' or '=' or '!' or ' ' or '"' or '$' or '\'')
                {
                    if (parameters.Length < 64) parameters.Append(c);
                    return;
                }
                state = State.Ground;
                if (c >= '@' && c <= '~') Csi(c, parameters.ToString());
                return;
        }
    }

    private void Control(char c)
    {
        switch (c)
        {
            case '\r': CursorX = 0; pendingWrap = false; break;
            case '\n' or '\v' or '\f': LineFeed(); break;
            case '\b': if (CursorX > 0) CursorX--; pendingWrap = false; break;
            case '\t': CursorX = Math.Min(Columns - 1, (CursorX / 8 + 1) * 8); break;
        }
    }

    private void Print(char c)
    {
        if (pendingWrap) { CursorX = 0; LineFeed(); pendingWrap = false; }
        var cell = pen;
        cell.Char = char.IsSurrogate(c) ? (char.IsHighSurrogate(c) ? '?' : '\0') : c;
        if (cell.Char == '\0') return;
        lines[CursorY][CursorX] = cell;
        if (CursorX == Columns - 1) pendingWrap = true;
        else CursorX++;
    }

    private void LineFeed()
    {
        pendingWrap = false;
        if (CursorY == bottom) ScrollUp(1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    private void ScrollUp(int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (top == 0 && saved is null) PushScrollback(lines[top]);
            for (int row = top; row < bottom; row++) lines[row] = lines[row + 1];
            lines[bottom] = Blank(Columns, BlankPen());
        }
    }

    private void ScrollDown(int count)
    {
        for (int i = 0; i < count; i++)
        {
            for (int row = bottom; row > top; row--) lines[row] = lines[row - 1];
            lines[top] = Blank(Columns, BlankPen());
        }
    }

    private void PushScrollback(VtCell[] line)
    {
        scrollback.Add(line);
        if (scrollback.Count > ScrollbackLimit) scrollback.RemoveRange(0, scrollback.Count - ScrollbackLimit);
    }

    private VtCell BlankPen() => new() { Char = ' ', Background = pen.Background };

    private void Reset()
    {
        lines = Make(Columns, Rows);
        pen = new VtCell { Char = ' ' };
        CursorX = CursorY = 0; top = 0; bottom = Rows - 1;
        CursorVisible = true;
    }

    private void Csi(char final, string text)
    {
        bool isPrivate = text.StartsWith('?');
        var values = text.TrimStart('?', '>', '=', '!').Split(';')
            .Select(part => int.TryParse(part.Split(':')[0], out int value) ? value : 0).ToArray();
        int Arg(int index, int fallback) => index < values.Length && values[index] > 0 ? values[index] : fallback;
        pendingWrap = false;
        switch (final)
        {
            case 'A': CursorY = Math.Max(CursorY < top ? 0 : top, CursorY - Arg(0, 1)); break;
            case 'B': CursorY = Math.Min(CursorY > bottom ? Rows - 1 : bottom, CursorY + Arg(0, 1)); break;
            case 'C': CursorX = Math.Min(Columns - 1, CursorX + Arg(0, 1)); break;
            case 'D': CursorX = Math.Max(0, CursorX - Arg(0, 1)); break;
            case 'E': CursorY = Math.Min(Rows - 1, CursorY + Arg(0, 1)); CursorX = 0; break;
            case 'F': CursorY = Math.Max(0, CursorY - Arg(0, 1)); CursorX = 0; break;
            case 'G' or '`': CursorX = Math.Clamp(Arg(0, 1) - 1, 0, Columns - 1); break;
            case 'd': CursorY = Math.Clamp(Arg(0, 1) - 1, 0, Rows - 1); break;
            case 'H' or 'f':
                CursorY = Math.Clamp(Arg(0, 1) - 1, 0, Rows - 1);
                CursorX = Math.Clamp(Arg(1, 1) - 1, 0, Columns - 1);
                break;
            case 'J': Erase(values.Length > 0 ? values[0] : 0); break;
            case 'K': EraseLine(values.Length > 0 ? values[0] : 0); break;
            case 'X': for (int x = CursorX; x < Math.Min(Columns, CursorX + Arg(0, 1)); x++) lines[CursorY][x] = BlankPen(); break;
            case 'P': Shift(-Arg(0, 1)); break;
            case '@': Shift(Arg(0, 1)); break;
            case 'L': if (CursorY >= top && CursorY <= bottom) { int keep = top; top = CursorY; ScrollDown(Arg(0, 1)); top = keep; } break;
            case 'M': if (CursorY >= top && CursorY <= bottom) { int keep = top; top = CursorY; for (int i = 0; i < Arg(0, 1); i++) { for (int row = top; row < bottom; row++) lines[row] = lines[row + 1]; lines[bottom] = Blank(Columns, BlankPen()); } top = keep; } break;
            case 'S': ScrollUp(Arg(0, 1)); break;
            case 'T': ScrollDown(Arg(0, 1)); break;
            case 'r':
                top = Math.Clamp(Arg(0, 1) - 1, 0, Rows - 1);
                bottom = Math.Clamp(Arg(1, Rows) - 1, top, Rows - 1);
                CursorX = 0; CursorY = 0;
                break;
            case 's': savedX = CursorX; savedY = CursorY; break;
            case 'u': CursorX = savedX; CursorY = savedY; break;
            case 'm': Sgr(values); break;
            case 'n' when !isPrivate && Arg(0, 0) == 6: Reply?.Invoke($"\x1b[{CursorY + 1};{CursorX + 1}R"); break;
            case 'h' or 'l' when isPrivate: Mode(values, final == 'h'); break;
        }
    }

    private void Mode(int[] values, bool on)
    {
        foreach (int mode in values)
            switch (mode)
            {
                case 1: ApplicationCursor = on; break;
                case 25: CursorVisible = on; break;
                case 2004: BracketedPaste = on; break;
                case 47 or 1047 or 1049:
                    if (on && saved is null) { saved = lines; lines = Make(Columns, Rows); if (mode == 1049) { savedX = CursorX; savedY = CursorY; } }
                    else if (!on && saved is not null) { lines = saved; saved = null; if (mode == 1049) { CursorX = savedX; CursorY = savedY; } }
                    break;
            }
    }

    private void Erase(int mode)
    {
        if (mode == 3) { scrollback.Clear(); return; }
        int from = mode == 0 ? CursorY + 1 : 0, to = mode == 1 ? CursorY - 1 : Rows - 1;
        if (mode != 2) EraseLine(mode);
        for (int row = Math.Max(0, from); row <= to; row++) lines[row] = Blank(Columns, BlankPen());
    }

    private void EraseLine(int mode)
    {
        int from = mode == 0 ? CursorX : 0, to = mode == 1 ? CursorX : Columns - 1;
        for (int x = from; x <= to && x < Columns; x++) lines[CursorY][x] = BlankPen();
    }

    private void Shift(int count)
    {
        var line = lines[CursorY];
        if (count < 0)
        {
            int n = Math.Min(-count, Columns - CursorX);
            Array.Copy(line, CursorX + n, line, CursorX, Columns - CursorX - n);
            for (int x = Columns - n; x < Columns; x++) line[x] = BlankPen();
        }
        else
        {
            int n = Math.Min(count, Columns - CursorX);
            Array.Copy(line, CursorX, line, CursorX + n, Columns - CursorX - n);
            for (int x = CursorX; x < CursorX + n; x++) line[x] = BlankPen();
        }
    }

    private static readonly uint[] Palette =
    [
        0xFF1A1D26, 0xFFF07A7A, 0xFF5FD38A, 0xFFFFB648, 0xFF6EA8FE, 0xFFC792EA, 0xFF5CC8D7, 0xFFC8CCD6,
        0xFF5B6070, 0xFFFF9A9A, 0xFF8AF0B0, 0xFFFFD88A, 0xFF9CC4FF, 0xFFE0B0FF, 0xFF8AE8F0, 0xFFECEEF3
    ];

    private void Sgr(int[] values)
    {
        if (values.Length == 0) values = [0];
        for (int i = 0; i < values.Length; i++)
        {
            int v = values[i];
            switch (v)
            {
                case 0: pen = new VtCell { Char = ' ' }; break;
                case 1: pen.Bold = true; break;
                case 22: pen.Bold = false; break;
                case 7: pen.Inverse = true; break;
                case 27: pen.Inverse = false; break;
                case >= 30 and <= 37: pen.Foreground = Palette[v - 30 + (pen.Bold ? 8 : 0)]; break;
                case 39: pen.Foreground = 0; break;
                case >= 40 and <= 47: pen.Background = Palette[v - 40]; break;
                case 49: pen.Background = 0; break;
                case >= 90 and <= 97: pen.Foreground = Palette[v - 90 + 8]; break;
                case >= 100 and <= 107: pen.Background = Palette[v - 100 + 8]; break;
                case 38 or 48:
                    uint color = 0;
                    if (i + 2 < values.Length && values[i + 1] == 5) { color = Indexed(values[i + 2]); i += 2; }
                    else if (i + 4 < values.Length && values[i + 1] == 2)
                    { color = 0xFF000000 | (uint)(values[i + 2] & 255) << 16 | (uint)(values[i + 3] & 255) << 8 | (uint)(values[i + 4] & 255); i += 4; }
                    if (v == 38) pen.Foreground = color; else pen.Background = color;
                    break;
            }
        }
    }

    private static uint Indexed(int index)
    {
        if (index < 16) return Palette[Math.Max(0, index)];
        if (index < 232)
        {
            index -= 16;
            static uint Level(int n) => n == 0 ? 0u : (uint)(55 + n * 40);
            return 0xFF000000 | Level(index / 36) << 16 | Level(index / 6 % 6) << 8 | Level(index % 6);
        }
        uint gray = (uint)(8 + (Math.Min(index, 255) - 232) * 10);
        return 0xFF000000 | gray << 16 | gray << 8 | gray;
    }
}
