using System.Text.RegularExpressions;

namespace Solforge.Mcp;

/** An engine's allow prompt on screen: the question, the highlighted choice and a key for this prompt instance. */
public sealed record ApprovalPrompt(string Engine, string Question, string Choice, string Signature);

/**
 * Finds an engine's permission prompt at the bottom of the AI terminal whose highlighted choice is a one-time allow,
 * for the Auto-allow toggle. Conservative and structural: a known question, directly under it option 1 and then only
 * option rows (numbered in order, or Claude's unnumbered list) or their wrapped tails, then the engine's own footer as
 * the last thing on screen. Exactly one option carries the pointer, a No/Deny sibling exists, and the pointed label is
 * one of the plain one-shot allows (never "always allow", "don't ask again", trust, elevation, sandbox or a
 * model-written question). Strings come from the installed claude 2.1.300, cline 3.0.71 and agy 1.3.4 binaries.
 */
public static partial class ApprovalPrompts
{
    /** Claude Code's tool, file write and fetch asks, and Claude in Chrome. Sandbox network requests stay manual. */
    private static readonly Regex[] ClaudeQuestions =
    [
        new(@"^Do you want to proceed\?$"),
        new(@"^Do you want to (make this edit to|create|overwrite|write to) \S+\?$"),
        new(@"^Do you want to allow Claude to fetch this content\?$"),
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

    private const int MaxOptions = 14, ContextRows = 6;

    /** Horizontal box lines: a rule or box top ends the context above a question. */
    private static readonly char[] Rules = ['─', '━', '═'];

    /** Claude's permission footer: "Esc to cancel", optionally "· Tab to amend" and "· ctrl+e to explain". */
    [GeneratedRegex(@"^Esc to cancel(\s*·\s*(Tab to amend|ctrl\+[a-z] to [a-z]+))*$", RegexOptions.IgnoreCase)]
    private static partial Regex ClaudeHint();

    /** agy's footer rows: "↑/↓ Navigate · tab Amend", "esc to cancel" (+ status bar) and the file edit review's. */
    [GeneratedRegex(@"^(↑/↓ Navigate\b|esc to cancel\b|Esc disabled during file edits\b)", RegexOptions.IgnoreCase)]
    private static partial Regex AgyHint();

    /** Text that marks a model-written question, a sandbox escape, elevation or trust: never auto-allowed. */
    [GeneratedRegex(@"Type something|Chat about this|\bOther\b|Write-in|Question \d+/\d+|Submit|(?i:sandbox|administrator|trust)")]
    private static partial Regex Unsafe();

    [GeneratedRegex(@"^[❯>›▶]\s+")]
    private static partial Regex Pointer();

    [GeneratedRegex(@"^(\d+)\.\s+")]
    private static partial Regex Number();

    [GeneratedRegex(@"^(No|Deny)\b")]
    private static partial Regex Decline();

    [GeneratedRegex(@"^\[y\] Approve\s+\[n\] Deny$")]
    private static partial Regex ClineButtons();

    /**
     * The allow prompt showing at the bottom of rows, or null. engine is the command Solforge launched (claude, cline
     * or agy; anything else never matches). shade holds, per row and character, the drawn background (0 = default);
     * Cline shows its selected button by colour only, so it never matches without it.
     */
    public static ApprovalPrompt? Find(string engine, IReadOnlyList<string> rows, IReadOnlyList<uint[]>? shade = null)
    {
        var lines = new List<(int Row, string Text)>();
        for (int row = 0; row < rows.Count; row++)
            if (Clean(rows[row]) is { Length: > 0 } text) lines.Add((row, text));
        return engine switch
        {
            "claude" => FindListed(engine, lines, rows, ClaudeQuestions, ClaudeAllows, ClaudeHint()),
            "agy" => FindListed(engine, lines, rows, AgyQuestions, AgyAllows, AgyHint()),
            "cline" => FindCline(lines, rows, shade),
            _ => null
        };
    }

    /** Edge padding and box borders off; a row of only box-drawing characters counts as empty. */
    private static string Clean(string row)
    {
        string text = row.Trim().Trim('│', '┃', '║', '╎', '┆').Trim();
        return text.All(c => c is >= '─' and <= '╿') ? "" : text;
    }

    /** One list row: the column its number (or unnumbered label) starts at, whether it has the pointer, its number (-1 if none) and label. */
    private static (int Column, bool Pointed, int Number, string Label) Option(string raw)
    {
        string text = raw.TrimEnd().TrimEnd('│', '┃', '║').TrimEnd();
        int lead = text.Length - text.TrimStart().Length;
        if (lead < text.Length && text[lead] is '│' or '┃' or '║') text = text[(lead + 1)..];
        int column = text.Length - text.TrimStart().Length;
        text = text.TrimStart();
        var pointer = Pointer().Match(text);
        if (pointer.Success) { column += pointer.Length; text = text[pointer.Length..]; }
        var number = Number().Match(text);
        if (number.Success && int.TryParse(number.Groups[1].Value, out int index))
            return (column, pointer.Success, index, text[number.Length..].Trim());
        return (column, pointer.Success, -1, text.Trim());
    }

    /** Claude Code and agy: question, then option 1 and only option rows, then the engine's footer as the last thing on screen. */
    private static ApprovalPrompt? FindListed(string engine, List<(int Row, string Text)> lines, IReadOnlyList<string> rows,
        Regex[] questions, HashSet<string> allows, Regex hints)
    {
        int hint = lines.Count - 1;
        if (hint < 0 || !hints.IsMatch(lines[hint].Text)) return null;
        while (hint > 0 && hints.IsMatch(lines[hint - 1].Text)) hint--;

        int question = -1, top = -1;
        string asked = "";
        for (int i = hint - 1; i >= Math.Max(0, hint - 1 - MaxOptions) && question < 0; i--)
        {
            string text = lines[i].Text;
            if (questions.Any(pattern => pattern.IsMatch(text))) { question = top = i; asked = text; }
            else if (i > 0 && lines[i - 1].Text + " " + text is var joined && questions.Any(pattern => pattern.IsMatch(joined)))
            { question = i; top = i - 1; asked = joined; }
        }
        if (question < 0 || hint - question < 3) return null;
        for (int i = Math.Max(0, top - 2); i < hint; i++)
            if (i != question && i != top && Unsafe().IsMatch(lines[i].Text)) return null;

        // Option 1 sits right under the question; every row after it is the next option or a deeper-indented wrap.
        var options = new List<(bool Pointed, string Label)>();
        var first = Option(rows[lines[question + 1].Row]);
        bool numbered = first.Number >= 0;
        if (numbered ? first.Number != 1 : engine != "claude") return null;
        for (int i = question + 1; i < hint; i++)
        {
            var row = Option(rows[lines[i].Row]);
            if (row.Column == first.Column && row.Number == (numbered ? options.Count + 1 : -1)) options.Add((row.Pointed, row.Label));
            else if (row.Column > first.Column && !row.Pointed) options[^1] = (options[^1].Pointed, options[^1].Label + " " + Clean(rows[lines[i].Row]));
            else return null;
        }
        if (options.Count(option => option.Pointed) != 1 || !options.Any(option => Decline().IsMatch(option.Label))) return null;
        string choice = options.First(option => option.Pointed).Label;
        if (!allows.Contains(choice)) return null;
        string list = string.Join("\n", options.Select(option => (option.Pointed ? "> " : "  ") + option.Label));
        return new(engine, asked, choice, engine + "|" + Context(rows, lines[top].Row) + "|" + asked + "|" + list);
    }

    /**
     * Up to six rows above the question (the command, file or tool being asked about) so back-to-back prompts with
     * the same options get different signatures. Stops at a horizontal rule or box top, or two blank rows.
     */
    private static string Context(IReadOnlyList<string> rows, int above)
    {
        var context = new List<string>();
        int blanks = 0;
        for (int row = above - 1; row >= 0 && context.Count < ContextRows && blanks < 2; row--)
        {
            if (rows[row].IndexOfAny(Rules) >= 0) break;
            string text = Clean(rows[row]);
            if (text.Length == 0) { blanks++; continue; }
            blanks = 0;
            context.Add(text);
        }
        context.Reverse();
        return string.Join("\n", context);
    }

    /**
     * Cline's panel: "Cline needs permission" and "Approve tool call?" above "[y] Approve   [n] Deny" near the bottom,
     * with the Approve button's background differing from the row's usual one and Deny's matching it.
     */
    private static ApprovalPrompt? FindCline(List<(int Row, string Text)> lines, IReadOnlyList<string> rows, IReadOnlyList<uint[]>? shade)
    {
        if (shade is null) return null;
        int buttons = -1;
        for (int i = lines.Count - 1; i >= Math.Max(0, lines.Count - 5) && buttons < 0; i--)
            if (ClineButtons().IsMatch(lines[i].Text)) buttons = i;
        if (buttons < 1) return null;
        int title = -1, asked = -1;
        for (int i = buttons - 1; i >= Math.Max(0, buttons - MaxOptions) && title < 0; i--)
        {
            string text = lines[i].Text;
            if (text == "Cline is asking a question") return null;
            if (text == "Approve tool call?" && asked < 0) asked = i;
            if (text == "Cline needs permission") title = i;
        }
        if (title < 0 || asked < title) return null;

        int row = lines[buttons].Row;
        string raw = rows[row];
        int approve = raw.IndexOf("[y] Approve", StringComparison.Ordinal), deny = raw.IndexOf("[n] Deny", StringComparison.Ordinal);
        if (row >= shade.Count || shade[row] is not { } cells || deny + 8 > cells.Length) return null;
        // The row's usual background: the commonest one outside both padded buttons.
        var outside = cells.Where((_, i) => i < approve - 1 || i > deny + 8).ToList();
        if (outside.Count == 0) return null;
        uint usual = outside.GroupBy(cell => cell).OrderByDescending(group => group.Count()).First().Key;
        if (cells[approve] == usual || cells[deny] != usual) return null;
        var panel = lines.Skip(title).Take(buttons - title + 1).Select(line => line.Text);
        return new("cline", "Approve tool call?", "Approve", "cline|" + string.Join("\n", panel));
    }
}
