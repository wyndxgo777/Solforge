/** File: Infers struct field layouts (offset and type) from field accesses in the decompiled C; serves get_struct. */
using System.Globalization;
using System.Text.RegularExpressions;

namespace Solforge.Mcp.Decomp;

/** A field access seen in decompiled C: "*(int *)(param_1 + 0x390)" is offset 0x390, type int. */
public sealed record InferredField(int Offset, string Type, int Uses);

/** A struct or class definition found in C/C++ source. */
public sealed record SourceStruct(string Name, string File, int Line, string Text);

/** A line of C/C++ source that mentions something (a tag class hash, a name). */
public sealed record SourceHit(string File, int Line, string Text);

/**
 * Struct layouts two ways: named definitions in the C/C++ source roots (the Sunrise client and server), and layouts
 * inferred from how a decompiled function dereferences a variable ("*(type *)(var + offset)", "*(type *)var", "var[i]").
 */
public static partial class StructLayouts
{
    private static readonly string[] Extensions = [".h", ".hpp", ".hh", ".hxx", ".inl", ".c", ".cc", ".cpp", ".cxx"];

    /** The decompiled source files under the given roots (empty when a folder cannot be read). */
    public static IEnumerable<string> SourceFiles(IEnumerable<string> roots) => roots.SelectMany(root =>
    {
        try { return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(file => Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
    });

    /** struct/class/union definitions whose name contains the query (exact names first). */
    public static IEnumerable<SourceStruct> FindDefinitions(IEnumerable<string> roots, string query, CancellationToken cancel)
    {
        var exact = new List<SourceStruct>();
        var partial = new List<SourceStruct>();
        foreach (string file in SourceFiles(roots))
        {
            cancel.ThrowIfCancellationRequested();
            string text;
            try { text = File.ReadAllText(file); } catch (IOException) { continue; }
            if (!text.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (Match match in Definition().Matches(text))
            {
                string name = match.Groups["name"].Value;
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                int open = text.IndexOf('{', match.Index + match.Length - 1);
                int close = MatchingBrace(text, open);
                if (open < 0 || close < 0) continue;
                int end = Math.Min(close + 2, text.Length);
                var found = new SourceStruct(name, file, Line(text, match.Index), text[match.Index..end].Replace("\r\n", "\n"));
                (string.Equals(name, query, StringComparison.OrdinalIgnoreCase) ? exact : partial).Add(found);
            }
        }
        return exact.Concat(partial.OrderBy(found => found.Name.Length));
    }

    /** Lines in the source roots containing any of the needles (case-insensitive). */
    public static IEnumerable<SourceHit> Grep(IEnumerable<string> roots, IReadOnlyList<string> needles, CancellationToken cancel)
    {
        foreach (string file in SourceFiles(roots))
        {
            cancel.ThrowIfCancellationRequested();
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch (IOException) { continue; }
            for (int i = 0; i < lines.Length; i++)
                if (needles.Any(needle => lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase)))
                    yield return new SourceHit(file, i + 1, lines[i].Trim());
        }
    }

    /** The fields a decompiled function reads or writes through a variable, by offset. */
    public static IReadOnlyList<InferredField> Infer(string source, string variable)
    {
        var fields = new Dictionary<int, (Dictionary<string, int> Types, int Uses)>();
        string name = Regex.Escape(variable);
        var offsetAccess = new Regex(@"\*\((?<type>[A-Za-z_][\w ]*?\s*\*+)\)\(\s*(?:\([^()]*\)\s*)?" + name + @"\s*\+\s*(?<offset>0x[0-9a-fA-F]+|\d+)\s*\)");
        var plainAccess = new Regex(@"\*\((?<type>[A-Za-z_][\w ]*?\s*\*+)\)\s*" + name + @"\b(?!\s*\+)");
        var indexAccess = new Regex(@"\b" + name + @"\[(?<offset>0x[0-9a-fA-F]+|\d+)\]");
        var bareAccess = new Regex(@"(?<![\w)\]])\*" + name + @"\b(?!\s*(?:\+|\[))");
        // Only the body: the signature's "char *param_1" is a declaration, not an access.
        int body = source.IndexOf('{');
        if (body > 0) source = source[body..];
        foreach (Match match in offsetAccess.Matches(source)) Add(Number(match.Groups["offset"].Value), Pointee(match.Groups["type"].Value));
        foreach (Match match in plainAccess.Matches(source)) Add(0, Pointee(match.Groups["type"].Value));
        foreach (Match match in indexAccess.Matches(source)) Add(Number(match.Groups["offset"].Value), "[index]");
        foreach (Match match in bareAccess.Matches(source)) Add(0, "deref");
        return [.. fields.OrderBy(pair => pair.Key).Select(pair =>
            new InferredField(pair.Key, string.Join(" | ", pair.Value.Types.OrderByDescending(type => type.Value).Select(type => type.Key)), pair.Value.Uses))];

        void Add(int offset, string type)
        {
            if (offset < 0) return;
            if (!fields.TryGetValue(offset, out var entry)) entry = (new Dictionary<string, int>(), 0);
            entry.Types[type] = entry.Types.GetValueOrDefault(type) + 1;
            fields[offset] = (entry.Types, entry.Uses + 1);
        }
        /** Strips a trailing pointer star from a type name. */
        static string Pointee(string type) => Regex.Replace(type.Trim(), @"\s*\*$", "").Trim();
        /** Parses a decimal or 0x hex number. */
        static int Number(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? (int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex) ? hex : -1)
            : (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : -1);
    }

    /** The parameter and local names a function's C dereferences, most-used first (for picking a variable). */
    public static IReadOnlyList<string> Variables(string source) =>
        [.. VariableUse().Matches(source).Select(match => match.Groups["name"].Value).GroupBy(name => name)
            .OrderByDescending(group => group.Count()).Select(group => group.Key)];

    /** Index of the brace that closes the one at open, or -1. */
    private static int MatchingBrace(string text, int open)
    {
        if (open < 0) return -1;
        int depth = 0;
        for (int i = open; i < text.Length && i < open + 200_000; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /** 1-based line number of a character index. */
    private static int Line(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index; i++) if (text[i] == '\n') line++;
        return line;
    }

    /** Compile-time generated regex matching a definition in decompiled C (see its attribute above). */
    [GeneratedRegex(@"\b(?:struct|class|union)\s+(?:alignas\s*\([^)]*\)\s*)?(?<name>[A-Za-z_]\w*)\s*(?:final\s*)?(?::[^{;]*)?\{", RegexOptions.Multiline)]
    private static partial Regex Definition();

    /** Compile-time generated regex matching a field access such as (param_1 + 0x390). */
    [GeneratedRegex(@"\)\(\s*(?:\([^()]*\)\s*)?(?<name>[A-Za-z_]\w*)\s*\+\s*(?:0x[0-9a-fA-F]+|\d+)\s*\)")]
    private static partial Regex VariableUse();
}
