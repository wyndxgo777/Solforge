using System.Globalization;
using System.Text;

namespace SunriseStudio.Editor.Ai;

/**
 * One terminal cell: a UTF-16 character, its colours (0xAARRGGBB; 0 = default) and SGR attributes. The
 * right half of a wide character holds VtScreen.Continuation.
 */
internal struct VtCell
{
    public char Char;
    public uint Foreground, Background;
    public bool Bold, Inverse, Dim, Underline, Strike;
}

/**
 * Solforge Phase 47B: the VT100/xterm subset that ConPTY and the AI CLIs emit: cursor moves, erase,
 * scroll regions, insert/delete, SGR 16/256/true colour, the alternate screen and bracketed paste.
 * Feed runs on the reader thread and drawing on the UI thread, so both lock the screen.
 */
internal sealed class VtScreen(int columns, int rows)
{
    private const int ScrollbackLimit = 2000;
    /** Osc also swallows DCS, APC, PM and SOS strings up to their terminator. */
    private enum State { Ground, Escape, Csi, Osc, OscEscape, Charset }

    private VtCell[][] lines = Make(columns, rows);
    private VtCell[][]? saved;
    private readonly List<VtCell[]> scrollback = [];
    private State state;
    private readonly StringBuilder parameters = new();
    private int top, bottom = rows - 1, savedX, savedY;
    private bool pendingWrap, savedWrap, altScreen;
    private char high;
    private VtCell pen = new() { Char = ' ' }, savedPen = new() { Char = ' ' };

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
    /** The right half of a wide character; drawn as nothing. */
    public const char Continuation = '\0';
    /** Stands in for characters outside the BMP (emoji), which the 16-bit font atlas cannot hold. */
    public const char Placeholder = '□';
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
            int shift = Math.Max(0, CursorY - (rows - 1));
            if (!altScreen) for (int row = 0; row < shift; row++) PushScrollback(lines[row]);
            lines = Fit(lines, columns, rows, shift);
            // The main screen behind ?1049 follows the resize too, keeping its saved cursor row on screen.
            if (saved is not null)
            {
                int savedShift = Math.Max(0, savedY - (rows - 1));
                saved = Fit(saved, columns, rows, savedShift);
                savedY -= savedShift;
            }
            Columns = columns; Rows = rows;
            top = 0; bottom = rows - 1;
            CursorX = Math.Min(CursorX, columns - 1);
            CursorY = Math.Clamp(CursorY - shift, 0, rows - 1);
            savedX = Math.Min(savedX, columns - 1);
            savedY = Math.Clamp(savedY, 0, rows - 1);
            if (CursorX != columns - 1) pendingWrap = false;
            Revision++;
        }
    }

    /** Copies rows from shift down into a new grid, cutting or padding columns. */
    private static VtCell[][] Fit(VtCell[][] source, int columns, int rows, int shift)
    {
        var made = Make(columns, rows);
        for (int row = 0; row < rows && row + shift < source.Length; row++)
            Array.Copy(source[row + shift], made[row], Math.Min(columns, source[row + shift].Length));
        return made;
    }

    /** Plain text of the visible screen, for checks and copy. */
    public string Text()
    {
        lock (Gate)
            return string.Join('\n', lines.Select(line =>
                new string(line.Where(cell => cell.Char != Continuation).Select(cell => cell.Char).ToArray()).TrimEnd()));
    }

    /**
     * The bottom lastRows rows as text, with each character's drawn background (the background colour, or the
     * complemented foreground for inverse cells; 0 is the default), for ApprovalPrompts. Wide characters' right
     * halves are left out of both, so indexes line up.
     */
    public (string[] Rows, uint[][] Shade) Snapshot(int lastRows)
    {
        lock (Gate)
        {
            int first = Math.Max(0, lines.Length - lastRows);
            var rows = new string[lines.Length - first];
            var shade = new uint[rows.Length][];
            var text = new char[lines.Length == 0 ? 0 : lines[0].Length];
            for (int row = 0; row < rows.Length; row++)
            {
                var line = lines[first + row];
                if (text.Length < line.Length) text = new char[line.Length];
                var shades = new uint[line.Length];
                int count = 0;
                for (int i = 0; i < line.Length; i++)
                {
                    if (line[i].Char == Continuation) continue;
                    text[count] = line[i].Char;
                    shades[count++] = line[i].Inverse ? ~line[i].Foreground : line[i].Background;
                }
                rows[row] = new string(text, 0, count);
                shade[row] = count == shades.Length ? shades : shades[..count];
            }
            return (rows, shade);
        }
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
                    case ']' or 'P' or '_' or '^' or 'X': parameters.Clear(); state = State.Osc; break;
                    case '(' or ')' or '*' or '+' or '#' or '%' or ' ': state = State.Charset; break;
                    case '7': Save(); break;
                    case '8': Restore(); break;
                    case 'M':
                        pendingWrap = false;
                        if (CursorY == top) ScrollDown(1); else if (CursorY > 0) CursorY--;
                        break;
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
                // Parameter bytes 0x30-0x3F (digits ; : < = > ?) and intermediates 0x20-0x2F.
                if (c is >= ' ' and <= '?')
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
        if (char.IsHighSurrogate(c)) { high = c; return; }
        int code = c;
        if (char.IsLowSurrogate(c))
        {
            if (high == 0) return;
            code = char.ConvertToUtf32(high, c);
        }
        high = '\0';
        int width = Columns < 2 ? 1 : Width(code);
        // A wide character never straddles the edge: blank the last column and wrap first.
        if (width == 2 && CursorX == Columns - 1 && !pendingWrap) { Put(CursorX, BlankPen()); pendingWrap = true; }
        if (pendingWrap) { CursorX = 0; LineFeed(); pendingWrap = false; }
        var cell = pen;
        cell.Char = Invisible(code) ? ' ' : code > 0xFFFF ? Placeholder : (char)code;
        Put(CursorX, cell);
        if (width == 2) { cell.Char = Continuation; Put(CursorX + 1, cell); }
        if (CursorX + width >= Columns) { CursorX = Columns - 1; pendingWrap = true; }
        else CursorX += width;
    }

    /** Writes one cell, blanking the other half of any wide character it splits. */
    private void Put(int x, VtCell cell)
    {
        var line = lines[CursorY];
        if (line[x].Char == Continuation && x > 0) line[x - 1].Char = ' ';
        if (x + 1 < line.Length && line[x + 1].Char == Continuation) line[x + 1].Char = ' ';
        line[x] = cell;
    }

    /**
     * Start and end pairs of the code points conhost gives two cells, measured with DSR 6 over ConPTY on
     * Windows 11 build 26200 (East Asian Wide/Fullwidth plus emoji presentation, not whole blocks).
     */
    private static readonly int[] Wide =
    [
        0x1100, 0x115F, 0x231A, 0x231B, 0x2329, 0x232A, 0x23E9, 0x23EC, 0x23F0, 0x23F0, 0x23F3, 0x23F3,
        0x25FD, 0x25FE, 0x2614, 0x2615, 0x2648, 0x2653, 0x267F, 0x267F, 0x2693, 0x2693, 0x26A1, 0x26A1,
        0x26AA, 0x26AB, 0x26BD, 0x26BE, 0x26C4, 0x26C5, 0x26CE, 0x26CE, 0x26D4, 0x26D4, 0x26EA, 0x26EA,
        0x26F2, 0x26F3, 0x26F5, 0x26F5, 0x26FA, 0x26FA, 0x26FD, 0x26FD, 0x2705, 0x2705, 0x270A, 0x270B,
        0x2728, 0x2728, 0x274C, 0x274C, 0x274E, 0x274E, 0x2753, 0x2755, 0x2757, 0x2757, 0x2795, 0x2797,
        0x27B0, 0x27B0, 0x27BF, 0x27BF, 0x2B1B, 0x2B1C, 0x2B50, 0x2B50, 0x2B55, 0x2B55, 0x2E80, 0x2E99,
        0x2E9B, 0x2EF3, 0x2F00, 0x2FD5, 0x2FF0, 0x2FFB, 0x3000, 0x303E, 0x3041, 0x3096, 0x3099, 0x30FF,
        0x3105, 0x312F, 0x3131, 0x318E, 0x3190, 0x31E3, 0x31F0, 0x321E, 0x3220, 0x3247, 0x3250, 0x4DBF,
        0x4E00, 0xA48C, 0xA490, 0xA4C6, 0xA960, 0xA97C, 0xAC00, 0xD7A3, 0xF900, 0xFAFF, 0xFE10, 0xFE19,
        0xFE30, 0xFE52, 0xFE54, 0xFE66, 0xFE68, 0xFE6B, 0xFF01, 0xFF60, 0xFFE0, 0xFFE6, 0x16FE0, 0x16FE4,
        0x16FF0, 0x16FF1, 0x17000, 0x187F7, 0x18800, 0x18CD5, 0x18D00, 0x18D08, 0x1AFF0, 0x1AFF3,
        0x1AFF5, 0x1AFFB, 0x1AFFD, 0x1AFFE, 0x1B000, 0x1B122, 0x1B132, 0x1B132, 0x1B150, 0x1B152,
        0x1B155, 0x1B155, 0x1B164, 0x1B167, 0x1B170, 0x1B2FB, 0x1F004, 0x1F004, 0x1F0CF, 0x1F0CF,
        0x1F18E, 0x1F18E, 0x1F191, 0x1F19A, 0x1F1E6, 0x1F202, 0x1F210, 0x1F23B, 0x1F240, 0x1F248,
        0x1F250, 0x1F251, 0x1F260, 0x1F265, 0x1F300, 0x1F320, 0x1F32D, 0x1F335, 0x1F337, 0x1F37C,
        0x1F37E, 0x1F393, 0x1F3A0, 0x1F3CA, 0x1F3CF, 0x1F3D3, 0x1F3E0, 0x1F3F0, 0x1F3F4, 0x1F3F4,
        0x1F3F8, 0x1F43E, 0x1F440, 0x1F440, 0x1F442, 0x1F4FC, 0x1F4FF, 0x1F53D, 0x1F54B, 0x1F54E,
        0x1F550, 0x1F567, 0x1F57A, 0x1F57A, 0x1F595, 0x1F596, 0x1F5A4, 0x1F5A4, 0x1F5FB, 0x1F64F,
        0x1F680, 0x1F6C5, 0x1F6CC, 0x1F6CC, 0x1F6D0, 0x1F6D2, 0x1F6D5, 0x1F6D7, 0x1F6DC, 0x1F6DF,
        0x1F6EB, 0x1F6EC, 0x1F6F4, 0x1F6FC, 0x1F7E0, 0x1F7EB, 0x1F7F0, 0x1F7F0, 0x1F90C, 0x1F93A,
        0x1F93C, 0x1F945, 0x1F947, 0x1F9FF, 0x1FA70, 0x1FA7C, 0x1FA80, 0x1FA88, 0x1FA90, 0x1FABD,
        0x1FABF, 0x1FAC5, 0x1FACE, 0x1FADB, 0x1FAE0, 0x1FAE8, 0x1FAF0, 0x1FAF8, 0x20000, 0x2FFFD,
        0x30000, 0x3FFFD
    ];

    /**
     * Columns a code point takes in ConPTY's layout: 2 for conhost's wide set, otherwise 1. Conhost has no
     * zero-width cells, so combining marks, ZWJ and variation selectors each take a column too.
     */
    public static int Width(int code)
    {
        if (code < 0x1100) return 1;
        int index = Array.BinarySearch(Wide, code);
        // An exact hit is a range bound; otherwise an odd insertion point lies inside a range.
        return index >= 0 || (~index & 1) == 1 ? 2 : 1;
    }

    /** Combining and format marks hold a cell in conhost but draw as a blank here, not as the font's '?'. */
    private static bool Invisible(int code) => code >= 0x300 && CharUnicodeInfo.GetUnicodeCategory(code)
        is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format;

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
            if (top == 0 && !altScreen) PushScrollback(lines[top]);
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
        saved = null; altScreen = false;
        pen = savedPen = new VtCell { Char = ' ' };
        CursorX = CursorY = savedX = savedY = 0; top = 0; bottom = Rows - 1;
        pendingWrap = savedWrap = false;
        CursorVisible = true; BracketedPaste = ApplicationCursor = false;
    }

    /** DECSC: the cursor, pen and pending-wrap flag. */
    private void Save() { savedX = CursorX; savedY = CursorY; savedPen = pen; savedWrap = pendingWrap; }

    /** DECRC, clamped in case the screen shrank since. */
    private void Restore()
    {
        CursorX = Math.Min(savedX, Columns - 1); CursorY = Math.Min(savedY, Rows - 1);
        pen = savedPen; pendingWrap = savedWrap;
    }

    private static int Number(string text) => int.TryParse(text, out int value) ? value : 0;

    private void Csi(char final, string text)
    {
        // A leading ? > = < makes the sequence private and bytes 0x20-0x2F are intermediates; either changes what
        // the final means (CSI > 1 u is a keyboard mode, not restore-cursor), so only ? h/l/J/K are acted on.
        char marker = (text.Length > 0 && text[0] is '?' or '>' or '=' or '<') ? text[0] : '\0';
        if (marker is not ('\0' or '?') || text.Any(c => c is >= ' ' and <= '/')) return;
        var parts = (marker == '\0' ? text : text[1..]).Split(';');
        var values = parts.Select(part => Number(part.Split(':')[0])).ToArray();
        int Arg(int index, int fallback) => index < values.Length && values[index] > 0 ? values[index] : fallback;
        int Up(int count) => Math.Max(CursorY < top ? 0 : top, CursorY - count);
        int Down(int count) => Math.Min(CursorY > bottom ? Rows - 1 : bottom, CursorY + count);
        if (marker == '?')
        {
            if (final is 'h' or 'l') Mode(values, final == 'h');
            // DECSED and DECSEL: nothing is protected, so they erase like ED and EL.
            else if (final == 'J') { pendingWrap = false; Erase(values[0]); }
            else if (final == 'K') { pendingWrap = false; EraseLine(values[0]); }
            return;
        }
        // Only cursor motion and edits end a pending wrap. SGR, modes and reports leave it set, or a colour change
        // between a full row and its soft-wrapped continuation puts the next glyph over the last column (VT510, xterm).
        if ("ABCDEFG`adeHfJKXP@LMSTr".Contains(final)) pendingWrap = false;
        switch (final)
        {
            case 'A': CursorY = Up(Arg(0, 1)); break;
            case 'B' or 'e': CursorY = Down(Arg(0, 1)); break;
            case 'C' or 'a': CursorX = Math.Min(Columns - 1, CursorX + Arg(0, 1)); break;
            case 'D': CursorX = Math.Max(0, CursorX - Arg(0, 1)); break;
            case 'E': CursorY = Down(Arg(0, 1)); CursorX = 0; break;
            case 'F': CursorY = Up(Arg(0, 1)); CursorX = 0; break;
            case 'G' or '`': CursorX = Math.Clamp(Arg(0, 1) - 1, 0, Columns - 1); break;
            case 'd': CursorY = Math.Clamp(Arg(0, 1) - 1, 0, Rows - 1); break;
            case 'H' or 'f':
                CursorY = Math.Clamp(Arg(0, 1) - 1, 0, Rows - 1);
                CursorX = Math.Clamp(Arg(1, 1) - 1, 0, Columns - 1);
                break;
            case 'J': Erase(values[0]); break;
            case 'K': EraseLine(values[0]); break;
            case 'X': for (int x = CursorX; x < Math.Min(Columns, CursorX + Arg(0, 1)); x++) lines[CursorY][x] = BlankPen(); break;
            case 'P': Shift(-Arg(0, 1)); break;
            case '@': Shift(Arg(0, 1)); break;
            case 'L': if (CursorY >= top && CursorY <= bottom) { int keep = top; top = CursorY; ScrollDown(Arg(0, 1)); top = keep; } break;
            case 'M': if (CursorY >= top && CursorY <= bottom) { int keep = top; top = CursorY; for (int i = 0; i < Arg(0, 1); i++) { for (int row = top; row < bottom; row++) lines[row] = lines[row + 1]; lines[bottom] = Blank(Columns, BlankPen()); } top = keep; } break;
            case 'S': ScrollUp(Arg(0, 1)); break;
            case 'T': ScrollDown(Arg(0, 1)); break;
            case 'r':
                // DECSTBM is ignored unless the region has at least two rows.
                int first = Arg(0, 1) - 1, last = Math.Min(Arg(1, Rows), Rows) - 1;
                if (first < last) { top = first; bottom = last; CursorX = 0; CursorY = 0; }
                break;
            case 's': Save(); break;
            case 'u': Restore(); break;
            case 'm': Sgr(parts); break;
            case 'n' when Arg(0, 0) == 6: Reply?.Invoke($"\x1b[{CursorY + 1};{CursorX + 1}R"); break;
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
                    if (on == altScreen) break;
                    if (on) { if (mode == 1049) Save(); saved = lines; lines = Make(Columns, Rows); pendingWrap = false; }
                    else { lines = saved ?? Make(Columns, Rows); saved = null; pendingWrap = false; if (mode == 1049) Restore(); }
                    altScreen = on;
                    break;
            }
    }

    private void Erase(int mode)
    {
        if (mode == 3) { scrollback.Clear(); return; }
        if (mode is < 0 or > 2) return;
        int from = mode == 0 ? CursorY + 1 : 0, to = mode == 1 ? CursorY - 1 : Rows - 1;
        if (mode != 2) EraseLine(mode);
        for (int row = Math.Max(0, from); row <= to; row++) lines[row] = Blank(Columns, BlankPen());
    }

    private void EraseLine(int mode)
    {
        if (mode is < 0 or > 2) return;
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

    /** SGR; each part may carry colon sub-parameters (4:3 curly underline, 38:2::r:g:b). */
    private void Sgr(string[] parts)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            var sub = parts[i].Split(':');
            int v = Number(sub[0]);
            switch (v)
            {
                case 0: pen = new VtCell { Char = ' ' }; break;
                case 1: pen.Bold = true; break;
                case 2: pen.Dim = true; break;
                case 22: pen.Bold = pen.Dim = false; break;
                case 4: pen.Underline = sub.Length < 2 || Number(sub[1]) != 0; break;
                case 21: pen.Underline = true; break;
                case 24: pen.Underline = false; break;
                case 7: pen.Inverse = true; break;
                case 27: pen.Inverse = false; break;
                case 9: pen.Strike = true; break;
                case 29: pen.Strike = false; break;
                case >= 30 and <= 37: pen.Foreground = Palette[v - 30 + (pen.Bold ? 8 : 0)]; break;
                case 39: pen.Foreground = 0; break;
                case >= 40 and <= 47: pen.Background = Palette[v - 40]; break;
                case 49: pen.Background = 0; break;
                case >= 90 and <= 97: pen.Foreground = Palette[v - 90 + 8]; break;
                case >= 100 and <= 107: pen.Background = Palette[v - 100 + 8]; break;
                case 38 or 48:
                    uint color = 0;
                    if (sub.Length > 2 && sub[1] == "5") color = Indexed(Number(sub[2]));
                    else if (sub.Length > 4 && sub[1] == "2") color = Rgb(sub[^3], sub[^2], sub[^1]);
                    else if (sub.Length == 1 && i + 2 < parts.Length && Number(parts[i + 1]) == 5) { color = Indexed(Number(parts[i + 2])); i += 2; }
                    else if (sub.Length == 1 && i + 4 < parts.Length && Number(parts[i + 1]) == 2) { color = Rgb(parts[i + 2], parts[i + 3], parts[i + 4]); i += 4; }
                    if (v == 38) pen.Foreground = color; else pen.Background = color;
                    break;
            }
        }
    }

    private static uint Rgb(string r, string g, string b) =>
        0xFF000000 | (uint)(Number(r) & 255) << 16 | (uint)(Number(g) & 255) << 8 | (uint)(Number(b) & 255);

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
