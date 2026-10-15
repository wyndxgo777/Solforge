using SunriseStudio.Editor.Ai;

internal static class TerminalChecks
{
    /** Phase 47B: wide, emoji and combining characters keep the AI terminal's columns where ConPTY puts them. */
    public static void Run()
    {
        Check(VtScreen.Width(0x273B) == 1 && VtScreen.Width(0x23FA) == 1 && VtScreen.Width(0x2500) == 1
            && VtScreen.Width(0x2800) == 1, "A Claude Code logo, rule or spinner glyph was measured as wide.");
        Check(VtScreen.Width(0x4E2D) == 2 && VtScreen.Width(0xAC00) == 2 && VtScreen.Width(0xFF21) == 2
            && VtScreen.Width(0x1F600) == 2 && VtScreen.Width(0x2705) == 2, "A CJK or emoji character was measured as narrow.");
        Check(VtScreen.Width(0x0301) == 1 && VtScreen.Width(0x200D) == 1 && VtScreen.Width(0xFE0F) == 1,
            "A combining or format mark did not take its own column as it does in conhost.");
        Check(VtScreen.Width(0x1F6E0) == 1 && VtScreen.Width(0x1F1E6) == 2,
            "The wide table disagrees with conhost (hammer and wrench is narrow, regional indicators are wide).");

        var screen = new VtScreen(20, 4);
        screen.Feed("a中b");
        var line = screen.Line(0);
        Check(line[1].Char == '中' && line[2].Char == VtScreen.Continuation && line[3].Char == 'b' && screen.CursorX == 4,
            "A wide character did not take two cells.");
        Check(screen.Text().Split('\n')[0] == "a中b", "The right half of a wide character leaked into the screen text.");

        screen.Feed("\r\nx\U0001F600y");
        line = screen.Line(1);
        Check(line[1].Char == VtScreen.Placeholder && line[2].Char == VtScreen.Continuation && line[3].Char == 'y'
            && screen.CursorX == 4, "An emoji surrogate pair did not draw as one two-cell placeholder.");
        Check(!screen.Text().Contains('?'), "A surrogate pair drew as '?'.");

        screen.Feed("\r\néf");
        line = screen.Line(2);
        Check(line[0].Char == 'e' && line[1].Char == ' ' && line[2].Char == 'f' && screen.CursorX == 3,
            "A combining mark did not hold one blank cell as conhost lays it out.");

        screen.Feed("\r\n中\rz");
        line = screen.Line(3);
        Check(line[0].Char == 'z' && line[1].Char == ' ', "Overwriting half of a wide character left the other half behind.");

        var edge = new VtScreen(5, 3);
        edge.Feed("abcd中");
        Check(edge.Line(0)[4].Char == ' ' && edge.Line(1)[0].Char == '中' && edge.Line(1)[1].Char == VtScreen.Continuation
            && edge.CursorX == 2 && edge.CursorY == 1, "A wide character in the last column did not wrap whole.");

        WrapChecks();
        SequenceChecks();
    }

    /** The last-column wrap flag survives SGR and modes, as ConPTY assumes when it soft-wraps a row. */
    private static void WrapChecks()
    {
        var screen = new VtScreen(5, 3);
        screen.Feed("abcde\x1b[31mf");
        Check(Row(screen, 0) == "abcde" && Row(screen, 1) == "f", "An SGR between a full row and its continuation dropped the wrap.");
        screen = new VtScreen(5, 3);
        screen.Feed("abcde\x1b[?25lf");
        Check(Row(screen, 0) == "abcde" && Row(screen, 1) == "f", "A mode change between a full row and its continuation dropped the wrap.");

        // agy's stray "h": a right-aligned status line ends in the last column; the taller prompt then repaints that
        // row as the soft-wrapped continuation of a full option row, with an SGR between them and explicit spaces.
        screen = new VtScreen(20, 4);
        screen.Feed("\x1b[3;9H\x1b[2mFlash \u00B7 high\x1b[0m");
        Check(screen.Line(2)[19].Char == 'h', "The status line did not end in the last column.");
        screen.Feed("\x1b[2;1H\x1b[36m> 1. Allow: dotnet r\x1b[0m   dotnet run       ");
        Check(Row(screen, 1) == "> 1. Allow: dotnet r" && Row(screen, 2) == "   dotnet run" && screen.Line(2)[19].Char == ' ',
            "The repainted row kept the old status line's last letter in the last column.");

        // Cursor motion still ends the pending wrap; RI and RIS do too.
        screen = new VtScreen(5, 3);
        screen.Feed("abcde\x1b[1;5Hx");
        Check(Row(screen, 0) == "abcdx" && Row(screen, 1) == "", "CUP to the last column kept a stale wrap.");
        screen = new VtScreen(5, 4);
        screen.Feed("\r\n\r\nabcde\x1bMX");
        Check(screen.Line(1)[4].Char == 'X' && Row(screen, 2) == "abcde", "Reverse index kept the pending wrap.");
        screen = new VtScreen(5, 3);
        screen.Feed("abcde\u001bcX");
        Check(Row(screen, 0) == "X" && screen.CursorY == 0, "A reset kept the pending wrap.");
    }

    /** Private and intermediate CSI forms, SGR attributes, DECSC/DECRC, margins and the alternate screen. */
    private static void SequenceChecks()
    {
        var screen = new VtScreen(40, 4);
        screen.Feed("abc\x1b[>1udef\x1b[<u\x1b[=5;1u\x1b[?ugh\x1b[>4;1mi\x1b[0 qj\x1bP+q544e\x1b\\k");
        Check(Row(screen, 0) == "abcdefghijk" && !screen.Line(0)[8].Bold,
            "A private or intermediate CSI, or a DCS string, was run as its plain namesake or printed.");

        screen = new VtScreen(20, 3);
        screen.Feed("\x1b[2ma\x1b[22;4mb\x1b[4:0mc\x1b[9md\x1b[29;38:2::255:0:0me");
        var line = screen.Line(0);
        Check(line[0].Dim && !line[1].Dim && line[1].Underline && !line[2].Underline && line[3].Strike && !line[4].Strike
            && line[4].Foreground == 0xFFFF0000, "Faint, underline, strikethrough or colon-form colour was not tracked.");

        screen = new VtScreen(20, 5);
        screen.Feed("\x1b[3;3H\x1b[31m\u001b7\x1b[0m\x1b[1;1H\u001b8Z\x1b[4;2rY");
        Check(screen.Line(2)[2].Char == 'Z' && screen.Line(2)[2].Foreground != 0 && screen.Line(2)[3].Char == 'Y',
            "DECRC did not restore the pen, or an empty scroll region was applied.");

        // ?1049 survives a resize: leaving it restores the main screen and nothing from the alt screen is scrolled back.
        screen = new VtScreen(20, 5);
        screen.Feed("main\r\n\x1b[?1049halt");
        screen.Resize(30, 6);
        screen.Feed("\x1b[?1049l");
        Check(Row(screen, 0) == "main" && Row(screen, 1) == "" && screen.Scrollback.Count == 0,
            "A resize on the alternate screen lost the main screen.");

        // A saved cursor below a shrunken screen is clamped instead of indexing past the rows.
        screen = new VtScreen(80, 24);
        screen.Feed("\x1b[24;80H\u001b7\x1b[?1049h");
        screen.Resize(40, 10);
        screen.Feed("\u001b8x\x1b[ux\x1b[?1049lx");
        Check(screen.CursorY < 10 && screen.CursorX < 40, "A restored cursor was left outside the resized screen.");
    }

    private static string Row(VtScreen screen, int row) => screen.Text().Split('\n')[row];

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
