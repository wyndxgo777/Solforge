using System.Text.RegularExpressions;

namespace Solforge.Mcp;

/** An engine's allow prompt on screen: the question, the highlighted choice and a key for this prompt instance. */
public sealed record ApprovalPrompt(string Engine, string Question, string Choice, string Signature);

/**
 * Finds an engine's permission prompt at the bottom of the AI terminal whose highlighted choice is a one-time allow,
 * for the Auto-allow toggle. Conservative: a known question, its options and the hint footer must be the last thing
 * on screen, exactly one option carries the pointer, a No/Deny sibling exists, and the pointed label is one of the
 * plain one-shot allows (never "always allow", "don't ask again", trust, elevation or a model-written question).
 * Strings come from the installed claude 2.1.300, cline 3.0.71 and agy 1.3.4 binaries.
 */
public static partial class ApprovalPrompts
{
    /** Claude Code's tool, file edit, fetch, sandbox network and Chrome asks. */
    private static readonly Regex[] ClaudeQuestions =
    [
        new(@"^Do you want to proceed\?$"),
        new(@"^Do you want to (make this edit to|create|overwrite|write to) .+\?$"),
        new(@"^Do you want to allow Claude to fetch this content\?$"),
        new(@"^Do you want to allow this connection\?$"),
        new(@"^Allow Claude in Chrome to .+\?$")
    ];

    private static readonly HashSet<string> ClaudeAllows = new(StringComparer.Ordinal) { "Yes", "Allow" };

    /** agy's prompts; administrator elevation, sandbox bypass and workspace trust are left out on purpose. */
    private static readonly Regex[] AgyQuestions =
    [
        new(@"^Allow calling this tool\?$"),
        new(@"^Allow access to this URL\?$"),
        new(@"^Run this command\?$"),
        new(@"^Allow access to this file\?$"),
        new(@"^Allow creation of this file\?$"),
        new(@"^Accept this file edit\?$"),
        new(@"^Grant requested permission\?$"),
        new(@"^Do you want to proceed\?$")
    ];

    private static readonly HashSet<string> AgyAllows = new(StringComparer.Ordinal)
    {
        "Yes, allow tool call", "Yes, allow access", "Yes, run command", "Yes, allow creation",
        "Yes, accept this change", "Yes, grant permission", "Yes, proceed"
    };

    /** Footer hints: Claude's "Esc to cancel · Tab to amend", agy's "↑/↓ Navigate · tab Amend" and "esc to cancel". */
    private static readonly string[] Hints = ["esc to cancel", "to amend", "tab amend", "navigate", "press 1 to accept"];

    private const int MaxOptions = 14;

    [GeneratedRegex(@"^[❯>›▶]\s+")]
    private static partial Regex Pointer();

    [GeneratedRegex(@"^\d+\.\s+")]
    private static partial Regex Number();

    [GeneratedRegex(@"^(No|Deny)\b")]
    private static partial Regex Decline();

    /**
     * The allow prompt showing at the bottom of rows, or null. engine is the command Solforge launched (claude, cline
     * or agy; anything else never matches). marked flags, per row and character, cells drawn highlighted (a background
     * colour or inverse); Cline shows its selected button by colour only, so it never matches without them.
     */
    public static ApprovalPrompt? Find(string engine, IReadOnlyList<string> rows, IReadOnlyList<bool[]>? marked = null)
    {
        var lines = new List<(int Row, string Text)>();
        for (int row = 0; row < rows.Count; row++)
            if (Clean(rows[row]) is { Length: > 0 } text) lines.Add((row, text));
        return engine switch
        {
            "claude" => FindListed(engine, lines, ClaudeQuestions, ClaudeAllows),
            "agy" => FindListed(engine, lines, AgyQuestions, AgyAllows),
            "cline" => FindCline(lines, rows, marked),
            _ => null
        };
    }

    /** Edge padding and box borders off; a row of only box-drawing characters counts as empty. */
    private static string Clean(string row)
    {
        string text = row.Trim().Trim('│', '┃', '║', '╎', '┆').Trim();
        return text.All(c => c is >= '─' and <= '╿') ? "" : text;
    }

    /** Claude Code and agy: question, options with one pointer row, then the hint footer as the last thing on screen. */
    private static ApprovalPrompt? FindListed(string engine, List<(int Row, string Text)> lines, Regex[] questions, HashSet<string> allows)
    {
        int hint = lines.Count - 1;
        if (hint < 0 || !IsHint(lines[hint].Text)) return null;
        while (hint > 0 && IsHint(lines[hint - 1].Text)) hint--;

        int question = -1;
        string asked = "";
        for (int i = hint - 1; i >= Math.Max(0, hint - 1 - MaxOptions) && question < 0; i--)
        {
            string text = lines[i].Text;
            if (questions.Any(pattern => pattern.IsMatch(text))) { question = i; asked = text; }
            else if (i > 0 && lines[i - 1].Text + " " + text is var joined && questions.Any(pattern => pattern.IsMatch(joined)))
            { question = i; asked = joined; }
        }
        if (question < 0 || hint - question < 2) return null;

        var options = lines.Skip(question + 1).Take(hint - question - 1).Select(line => line.Text).ToList();
        var pointed = options.Where(text => Pointer().IsMatch(text)).ToList();
        if (pointed.Count != 1) return null;
        string choice = Label(pointed[0]);
        if (!allows.Contains(choice) || !options.Any(text => Decline().IsMatch(Label(text)))) return null;
        return new(engine, asked, choice, engine + "|" + asked + "|" + string.Join("\n", options));
    }

    /** Cline's panel: "Cline needs permission", "Approve tool call?", then "[y] Approve [n] Deny" with Approve highlighted. */
    private static ApprovalPrompt? FindCline(List<(int Row, string Text)> lines, IReadOnlyList<string> rows, IReadOnlyList<bool[]>? marked)
    {
        if (marked is null) return null;
        int buttons = -1;
        for (int i = lines.Count - 1; i >= Math.Max(0, lines.Count - 5); i--)
            if (lines[i].Text.Contains("[y] Approve", StringComparison.Ordinal) && lines[i].Text.Contains("[n] Deny", StringComparison.Ordinal))
            { buttons = i; break; }
        if (buttons < 1) return null;
        int asked = buttons - 1;
        while (asked > Math.Max(0, buttons - 5) && lines[asked].Text != "Approve tool call?") asked--;
        if (lines[asked].Text != "Approve tool call?") return null;
        int title = asked - 1;
        while (title > Math.Max(-1, asked - 4) && lines[title].Text != "Cline needs permission") title--;
        if (title < 0 || lines[title].Text != "Cline needs permission") return null;

        int row = lines[buttons].Row;
        string raw = rows[row];
        int approve = raw.IndexOf("[y] Approve", StringComparison.Ordinal) + 1, deny = raw.IndexOf("[n] Deny", StringComparison.Ordinal) + 1;
        if (row >= marked.Count || marked[row] is not { } cells || Math.Max(approve, deny) >= cells.Length) return null;
        if (!cells[approve] || cells[deny]) return null;
        var panel = lines.Skip(title).Take(buttons - title + 1).Select(line => line.Text);
        return new("cline", "Approve tool call?", "Approve", "cline|" + string.Join("\n", panel));
    }

    private static bool IsHint(string text) => Hints.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase));

    /** An option's label without the pointer and the "1." number. */
    private static string Label(string option) => Number().Replace(Pointer().Replace(option, ""), "").Trim();
}
