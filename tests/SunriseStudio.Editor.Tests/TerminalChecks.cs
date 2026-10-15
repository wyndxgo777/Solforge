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
        Check(VtScreen.Width(0x0301) == 0 && VtScreen.Width(0x200D) == 0 && VtScreen.Width(0xFE0F) == 0,
            "A combining or format mark took a column.");

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
        Check(line[0].Char == 'e' && line[1].Char == 'f' && screen.CursorX == 2, "A combining mark advanced the cursor.");

        screen.Feed("\r\n中\rz");
        line = screen.Line(3);
        Check(line[0].Char == 'z' && line[1].Char == ' ', "Overwriting half of a wide character left the other half behind.");

        var edge = new VtScreen(5, 3);
        edge.Feed("abcd中");
        Check(edge.Line(0)[4].Char == ' ' && edge.Line(1)[0].Char == '中' && edge.Line(1)[1].Char == VtScreen.Continuation
            && edge.CursorX == 2 && edge.CursorY == 1, "A wide character in the last column did not wrap whole.");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
