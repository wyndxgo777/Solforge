using System.Text;

namespace SunriseStudio.Editor.Ai;

/** A spot in the terminal: a line number (see VtScreen.Pushed) and a column, either a cell or the gap before it. */
internal readonly record struct TerminalPoint(long Line, int Column) : IComparable<TerminalPoint>
{
    public int CompareTo(TerminalPoint other) => Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);
    public static bool operator <(TerminalPoint a, TerminalPoint b) => a.CompareTo(b) < 0;
    public static bool operator >(TerminalPoint a, TerminalPoint b) => a.CompareTo(b) > 0;
    public static bool operator <=(TerminalPoint a, TerminalPoint b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TerminalPoint a, TerminalPoint b) => a.CompareTo(b) >= 0;
}

internal enum TerminalSelectMode { Character, Word, Line }

/**
 * Mouse selection in the AI terminal, kept apart from ImGui so it can be checked headless. Points use VtScreen line
 * numbers, which stay put while output scrolls, so a selection keeps its text and can reach into the scrollback.
 * Character mode follows carets (the gap nearest the mouse); word and line modes follow the cell under the mouse.
 */
internal sealed class TerminalSelection
{
    /** Characters that end a word on double-click besides blanks; paths, URLs and flags stay one word. */
    private const string Breaks = "()[]{}<>\"'`,;|│";
    private bool started;

    public TerminalSelectMode Mode { get; private set; }
    public TerminalPoint Anchor { get; private set; }
    public TerminalPoint Head { get; private set; }
    public bool Dragging { get; private set; }

    /** Something is selected; a plain click selects nothing until the mouse moves. */
    public bool Active => started && (Mode != TerminalSelectMode.Character || Anchor != Head);

    /**
     * A left press. @param clicks ImGui's click count: 1 characters, 2 words, 3 or more lines.
     * @param extend Shift held: move the far end of the current selection and keep its mode.
     */
    public void Press(TerminalPoint caret, TerminalPoint cell, int clicks, bool extend)
    {
        if (extend && started) Head = Mode == TerminalSelectMode.Character ? caret : cell;
        else
        {
            Mode = clicks >= 3 ? TerminalSelectMode.Line : clicks == 2 ? TerminalSelectMode.Word : TerminalSelectMode.Character;
            Anchor = Head = Mode == TerminalSelectMode.Character ? caret : cell;
            started = true;
        }
        Dragging = true;
    }

    public void Drag(TerminalPoint caret, TerminalPoint cell)
    {
        if (Dragging) Head = Mode == TerminalSelectMode.Character ? caret : cell;
    }

    public void Release() => Dragging = false;

    public void Clear() { started = false; Dragging = false; }

    /** Every line from first to last, whole. */
    public void SelectAll(long first, long last)
    {
        Mode = TerminalSelectMode.Line;
        Anchor = new(first, 0);
        Head = new(Math.Max(first, last), 0);
        started = true;
        Dragging = false;
    }

    /** The selection as [Start, End); End's column may pass the line's end. Null when nothing is selected. */
    public (TerminalPoint Start, TerminalPoint End)? Span(Func<long, VtCell[]?> lineAt)
    {
        if (!Active) return null;
        var (low, high) = Anchor <= Head ? (Anchor, Head) : (Head, Anchor);
        return Mode switch
        {
            TerminalSelectMode.Line => (new TerminalPoint(low.Line, 0), new TerminalPoint(high.Line, int.MaxValue)),
            TerminalSelectMode.Word => (new TerminalPoint(low.Line, WordStart(lineAt(low.Line), low.Column)),
                new TerminalPoint(high.Line, WordEnd(lineAt(high.Line), high.Column))),
            _ => (low, high)
        };
    }

    /** The selected columns [From, To) of one line of a width, or null when the line has none. */
    public static (int From, int To)? Columns((TerminalPoint Start, TerminalPoint End) span, long line, int width)
    {
        if (line < span.Start.Line || line > span.End.Line) return null;
        int from = line == span.Start.Line ? Math.Clamp(span.Start.Column, 0, width) : 0;
        int to = line == span.End.Line ? Math.Clamp(span.End.Column, 0, width) : width;
        return to > from ? (from, to) : null;
    }

    /** The selected text: wide characters once, each line's trailing blanks dropped, lines joined with CRLF. Lock the screen. */
    public string Text(Func<long, VtCell[]?> lineAt)
    {
        if (Span(lineAt) is not { } span) return "";
        var text = new StringBuilder();
        for (long line = span.Start.Line; line <= span.End.Line; line++)
        {
            if (line > span.Start.Line) text.Append("\r\n");
            if (lineAt(line) is not { } cells || Columns(span, line, cells.Length) is not { } columns) continue;
            int begin = text.Length;
            for (int x = columns.From; x < columns.To; x++)
                if (cells[x].Char != VtScreen.Continuation) text.Append(cells[x].Char);
            int end = text.Length;
            while (end > begin && text[end - 1] == ' ') end--;
            text.Length = end;
        }
        return text.ToString().TrimEnd();
    }

    /** 0 blank, 1 word, 2 a break character (selected alone). The right half of a wide character counts as a word cell. */
    private static int Kind(char c) => c is ' ' or '\t' ? 0 : c != VtScreen.Continuation && Breaks.Contains(c) ? 2 : 1;

    private static int WordStart(VtCell[]? cells, int column)
    {
        if (cells is not { Length: > 0 }) return column;
        column = Math.Clamp(column, 0, cells.Length - 1);
        int kind = Kind(cells[column].Char);
        if (kind == 2) return column;
        while (column > 0 && Kind(cells[column - 1].Char) == kind) column--;
        return column;
    }

    private static int WordEnd(VtCell[]? cells, int column)
    {
        if (cells is not { Length: > 0 }) return column + 1;
        column = Math.Clamp(column, 0, cells.Length - 1);
        int kind = Kind(cells[column].Char), end = column + 1;
        if (kind == 2) return end;
        while (end < cells.Length && Kind(cells[end].Char) == kind) end++;
        return end;
    }
}

/** Ctrl+C in the AI terminal: one press interrupts the engine, a second within Window seconds stops it. */
internal sealed class DoublePress(double window = 1.0)
{
    private double last = double.NegativeInfinity;

    public double Window { get; } = window;

    /** True for the second press inside the window; that press starts a new count. */
    public bool Press(double now)
    {
        bool second = now - last <= Window;
        last = second ? double.NegativeInfinity : now;
        return second;
    }

    /** A first press is waiting for its second. */
    public bool Pending(double now) => now - last <= Window;

    public void Reset() => last = double.NegativeInfinity;
}
