namespace SunriseStudio.Missions;

/** Line, span and table-field edits over Lua source text. */
internal static class LuaText
{
    /** Replaces a span of source with text. */
    public static string Edit(string source, LuaSpan span, string text) => source[..span.Start] + text + source[span.End..];

    /** The file's own line break: CRLF when the source has one, LF otherwise. */
    public static string Newline(string source) => source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /** Inserted text takes the file's own line breaks. */
    public static string Lines(string source, string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", Newline(source), StringComparison.Ordinal);

    /** Offset of the start of the line containing position. */
    public static int LineStart(string source, int position) => position == 0 ? 0 : source.LastIndexOf('\n', position - 1) + 1;

    /** The offset of the line break that ends the line, or the end of the source. */
    public static int LineEnd(string source, int position)
    {
        int end = source.IndexOf('\n', position);
        return end < 0 ? source.Length : end;
    }

    /** The spaces and tabs that start the line holding position. */
    public static string Indent(string source, int position)
    {
        int line = LineStart(source, position), i = line;
        while (i < source.Length && source[i] is ' ' or '\t') i++;
        return source[line..i];
    }

    /** One indent level: a tab when the first indented line starts with one, four spaces otherwise. */
    public static string IndentUnit(string source)
    {
        for (int i = source.IndexOf('\n'); i >= 0 && i + 1 < source.Length; i = source.IndexOf('\n', i + 1))
            if (source[i + 1] is ' ' or '\t') return source[i + 1] == '\t' ? "\t" : "    ";
        return "    ";
    }

    /**
     * Inserts a field at the end of a table in the table's own style: one line or one field per line,
     * its separator, and a trailing separator only when the table already ends with one.
     * @param entry True for a list entry, such as a node; in an empty one-line table it goes on its own line.
     */
    public static string InsertField(string source, LuaTable table, string text, bool entry = false)
    {
        string newline = Newline(source);
        text = Lines(source, text);
        int open = table.Span.Start + 1, close = table.Span.End - 1;
        if (table.Fields.Count == 0)
        {
            string inner = source[open..close];
            if (!inner.Contains('\n'))
            {
                string outer = Indent(source, table.Span.Start);
                string body = entry ? newline + outer + IndentUnit(source) + text + "," + newline + outer
                    : inner.Length > 0 ? " " + text + " " : text;
                return Edit(source, LuaSpan.FromBounds(open, close), body);
            }
            string closing = Indent(source, close);
            return Edit(source, new(LineStart(source, close), 0), closing + IndentUnit(source) + text + "," + newline);
        }
        var last = table.Fields[^1];
        char separator = table.Fields.Select(field => field.Separator).OfType<LuaSpan>().Select(span => source[span.Start]).FirstOrDefault(',');
        bool trailing = last.Separator is not null;
        bool multiline = source[open..table.Fields[0].Span.Start].Contains('\n')
            || table.Fields.Zip(table.Fields.Skip(1)).Any(pair => source[pair.First.Span.End..pair.Second.Span.Start].Contains('\n'));
        if (!multiline)
        {
            string gap = table.Fields.Count > 1 && table.Fields[0].Separator is { } first
                ? source[first.End..table.Fields[1].Span.Start] : " ";
            return trailing ? Edit(source, new(last.Span.End, 0), gap + text + separator)
                : Edit(source, new(last.Value.End, 0), separator + gap + text);
        }
        string indent = Indent(source, last.Span.Start);
        // A comment after the last field stays on its own line.
        int lineEnd = LineEnd(source, last.Span.End);
        bool ownLine = EndsLine(source[last.Span.End..lineEnd].Trim());
        int at = !ownLine ? last.Span.End : lineEnd > 0 && source[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
        string insertion = newline + indent + text + (trailing ? separator.ToString() : "");
        string result = Edit(source, new(at, 0), insertion);
        return trailing ? result : Edit(result, new(last.Value.End, 0), separator.ToString());
    }

    /** Removes a field with its separator; a field alone on its lines takes its lines with it. */
    public static string RemoveField(string source, LuaTable table, LuaField field)
    {
        int index = table.Fields.ToList().IndexOf(field);
        if (index < 0) throw new InvalidOperationException("The field is not in this table.");
        int start = field.Span.Start, end = field.Span.End;
        int lineStart = LineStart(source, start), lineEnd = LineEnd(source, end);
        bool aloneBefore = source[lineStart..start].Trim().Length == 0;
        bool aloneAfter = EndsLine(source[end..lineEnd].Trim());
        bool last = index == table.Fields.Count - 1;
        // The new last field drops its separator when the removed one had none, so the table keeps its style.
        LuaSpan? previous = last && field.Separator is null && index > 0 ? table.Fields[index - 1].Separator : null;
        string result;
        if (aloneBefore && aloneAfter && source[(table.Span.Start + 1)..start].Contains('\n'))
        {
            int stop = lineEnd < source.Length ? lineEnd + 1 : lineEnd;
            result = Edit(source, LuaSpan.FromBounds(lineStart, stop), "");
        }
        else if (field.Separator is not null)
        {
            while (end < source.Length && source[end] is ' ' or '\t') end++;
            result = Edit(source, LuaSpan.FromBounds(start, end), "");
        }
        else if (index > 0)
        {
            var before = table.Fields[index - 1];
            result = Edit(source, LuaSpan.FromBounds(before.Value.End, end), "");
            previous = null;
        }
        else
        {
            result = Edit(source, LuaSpan.FromBounds(table.Span.Start + 1, table.Span.End - 1), "");
        }
        return previous is { } separator ? Edit(result, separator, "") : result;
    }

    /** True when the rest of a line is empty or a line comment. */
    private static bool EndsLine(string rest) =>
        rest.Length == 0 || rest.StartsWith("--", StringComparison.Ordinal) && !rest.StartsWith("--[", StringComparison.Ordinal);

    /** The value is read where it will sit, so a trailing comment cannot swallow the source after it. */
    public static void CheckExpression(string expression)
    {
        LuaExpression wrapper;
        try { wrapper = LuaScript.ParseExpression("{ value = " + expression + " }"); }
        catch (InvalidDataException error) { throw new InvalidDataException("Enter one Lua expression.", error); }
        if (wrapper is not LuaTable { Fields: [{ Name: "value", Separator: null }] }) throw new InvalidDataException("Enter one Lua expression.");
    }
}
