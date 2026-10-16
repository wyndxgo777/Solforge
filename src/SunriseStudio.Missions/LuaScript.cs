using System.Collections.Frozen;
using System.Text;
using Loretta.CodeAnalysis;
using Loretta.CodeAnalysis.Lua;
using Loretta.CodeAnalysis.Lua.Syntax;
using LorettaKind = Loretta.CodeAnalysis.Lua.SyntaxKind;

namespace SunriseStudio.Missions;

/** A Lua 5.4 chunk read by Loretta; Text is never rewritten and every span points into it. */
public sealed partial class LuaScript
{
    // The largest generated SDK module is about 12 MiB.
    public const int MaxLength = 64 * 1024 * 1024;
    // Bracket nesting fails before a typed input can exhaust the editor stack.
    private const int MaximumBracketDepth = 256;
    private const int MaximumNodeDepth = 1024;
    private const int MaximumErrors = 64;
    private const string ExpressionPrefix = "return ";
    private const char ByteOrderMark = (char)0xFEFF;
    private static readonly LuaParseOptions Options = new(LuaSyntaxOptions.Lua54);

    private readonly SyntaxNode root;
    private readonly int offset;
    private readonly List<string> errors = [];
    private IReadOnlyList<LuaToken>? tokens;
    private IReadOnlyList<LuaSpan>? comments;

    public string Text { get; }
    /** The top-level statements; empty for a parsed expression list. */
    public LuaBlock Chunk { get; }
    /** The values of a parsed expression list. */
    public IReadOnlyList<LuaExpression> Values { get; } = [];
    /** Syntax errors as "line L, column C: message"; Parse and the expression readers throw on the first. */
    public IReadOnlyList<string> Errors => errors;
    public IReadOnlyList<LuaToken> Tokens => tokens ??= ReadTokens();
    public IReadOnlyList<LuaSpan> Comments => comments ??= ReadComments();

    private LuaScript(string text, bool expressions)
    {
        if (text.Length > MaxLength) throw new InvalidDataException("Lua source is too large.");
        Text = text;
        offset = expressions ? ExpressionPrefix.Length : 0;
        string parsed = expressions ? ExpressionPrefix + text : MaskByteOrderMark(text);
        SyntaxTree tree;
        try
        {
            tree = LuaSyntaxTree.ParseText(parsed, Options);
            root = tree.GetRoot();
        }
        catch (Exception error) when (error is NullReferenceException or InsufficientExecutionStackException)
        {
            throw new InvalidDataException("Lua nesting exceeds the reader limit.", error);
        }
        var statements = ((CompilationUnitSyntax)root).Statements;
        // Loretta reads a bare `return` at the end of the file as a return of one missing value.
        int bare = statements.Statements.LastOrDefault() is ReturnStatementSyntax { Expressions: [{ IsMissing: true } missing] }
            ? missing.SpanStart : -1;
        foreach (var diagnostic in tree.GetDiagnostics().Where(value => value.Severity == DiagnosticSeverity.Error))
            if (diagnostic.Location.SourceSpan.Start != bare) Error(diagnostic.Location.SourceSpan.Start, diagnostic.GetMessage());
        if (!expressions)
        {
            Chunk = Block("chunk", null, statements, 0, Text.Length, 0);
            return;
        }
        Chunk = new("chunk", null, [], 0, 0, new(0, 0));
        if (statements.Statements is not [ReturnStatementSyntax values] || values.SemicolonToken.Span.Length != 0)
        {
            Error(offset, "Enter Lua expressions only.");
            return;
        }
        Separators(values.Expressions);
        Values = values.Expressions.Where(value => !value.IsMissing).Select(Expression).ToArray();
    }

    /** Reads a whole chunk; a syntax error throws. */
    public static LuaScript Parse(string text) => Checked(new LuaScript(text, false));

    /** Reads a whole chunk and keeps its errors; an error-recovered tree still has exact spans. */
    public static LuaScript Read(string text) => new(text, false);

    /** Reads a comma-separated expression list, such as call arguments; spans are relative to text. */
    public static IReadOnlyList<LuaExpression> ParseExpressions(string text, bool allowEmpty = false)
    {
        var script = Checked(new LuaScript(text, true));
        if (script.Values.Count == 0 && !allowEmpty) throw new InvalidDataException("A Lua expression is required.");
        return script.Values;
    }

    /** Reads exactly one expression; spans are relative to text. */
    public static LuaExpression ParseExpression(string text) => ParseExpressions(text) is [var value] ? value
        : throw new InvalidDataException("Enter one Lua expression.");

    private static LuaScript Checked(LuaScript script) =>
        script.errors.Count == 0 ? script : throw new InvalidDataException("Lua syntax error at " + script.errors[0]);

    /** A byte order mark reads as one space, so every offset still matches Text. */
    private static string MaskByteOrderMark(string text) => text.StartsWith(ByteOrderMark) ? " " + text[1..] : text;

    /** Loretta's own text of the tree; equal to Text when nothing was lost. */
    public string Reconstruct()
    {
        string text = root.ToFullString()[offset..];
        return offset == 0 && Text.StartsWith(ByteOrderMark) ? ByteOrderMark + text[1..] : text;
    }

    /** Index of the first token that starts at or after position. */
    public int TokenAt(int position)
    {
        var list = Tokens;
        int low = 0, high = list.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (list[middle].Span.Start < position) low = middle + 1; else high = middle;
        }
        return low;
    }

    /** Comment lines directly above a statement belong to it; a comment on the previous line's end does not. */
    public int LeadingComments(int previous, int start)
    {
        int boundary = Text.IndexOf('\n', previous, start - previous);
        if (boundary < 0) return start;
        int first = start, line = LineStart(start);
        // Walk up while each line above is a comment; a blank line or the previous statement stops it.
        while (line - 1 > boundary)
        {
            int above = LineStart(line - 1);
            if (!Text[above..(line - 1)].TrimStart().StartsWith("--", StringComparison.Ordinal)) break;
            first = above;
            while (char.IsWhiteSpace(Text[first])) first++;
            line = above;
        }
        return first;
    }

    private int LineStart(int position) => LuaText.LineStart(Text, position);

    /** Every statement and expression of the chunk in source order, parents before children. */
    public IEnumerable<LuaSyntax> Descendants() => Descendants(Chunk.Statements);

    /** Every node under roots, roots included, in source order with parents before children. */
    public static IEnumerable<LuaSyntax> Descendants(IEnumerable<LuaSyntax> roots)
    {
        var stack = new Stack<IEnumerator<LuaSyntax>>();
        stack.Push(roots.GetEnumerator());
        while (stack.Count > 0)
        {
            if (!stack.Peek().MoveNext()) { stack.Pop().Dispose(); continue; }
            var node = stack.Peek().Current;
            yield return node;
            stack.Push(Children(node).GetEnumerator());
        }
    }

    /** Direct children in source order. */
    public static IEnumerable<LuaSyntax> Children(LuaSyntax node)
    {
        switch (node)
        {
            case LuaLocalStatement local: return local.Values;
            case LuaAssignmentStatement assignment: return assignment.Targets.Concat(assignment.Values);
            case LuaCallStatement call: return [call.Expression];
            case LuaFunctionStatement function: return [function.Function];
            case LuaReturnStatement value: return value.Values;
            case LuaControlStatement { Control: var control }:
                if (control.Kind == "if")
                    return control.Bodies.SelectMany(body => body.Condition is null ? body.Statements : body.Statements.Prepend<LuaSyntax>(body.Condition));
                IEnumerable<LuaSyntax> statements = control.Bodies.SelectMany(body => body.Statements);
                return control.Kind == "repeat" ? statements.Concat(control.Header) : control.Header.Concat(statements);
            case LuaMember member: return [member.Target];
            case LuaIndex index: return [index.Target, index.Key];
            case LuaCall call: return call.Arguments.Values.Prepend(call.Target);
            case LuaTable table:
                return table.Fields.SelectMany(field => field.Key is null ? [field.Expression] : new[] { field.Key, field.Expression });
            case LuaFunction function: return function.Body.Statements;
            case LuaBinary binary: return [binary.Left, binary.Right];
            case LuaUnary unary: return [unary.Operand];
            case LuaParenthesized parenthesized: return [parenthesized.Inner];
            default: return [];
        }
    }

    /** The innermost expression that starts where expression starts, such as the left operand. */
    public static LuaExpression First(LuaExpression expression)
    {
        while (true)
        {
            var next = expression switch
            {
                LuaBinary binary => binary.Left, LuaCall call => call.Target, LuaMember member => member.Target,
                LuaIndex index => index.Target, _ => null
            };
            if (next is null) return expression;
            expression = next;
        }
    }

    /** Lexical tokens for coloring; an incomplete draft still gets bounded spans. */
    public static IReadOnlyList<LuaToken> Syntax(string source)
    {
        if (source.Length > MaxLength) throw new InvalidDataException("Lua source is too large.");
        var result = new List<LuaToken>();
        foreach (var token in SyntaxFactory.ParseTokens(source, options: Options))
        {
            AddComments(token.LeadingTrivia);
            if (token.Kind() != LorettaKind.EndOfFileToken && token.Span.Length > 0)
                result.Add(new(token.Text, new(token.Span.Start, token.Span.Length), Classify(token)));
            AddComments(token.TrailingTrivia);
        }
        return result;

        void AddComments(SyntaxTriviaList trivia)
        {
            foreach (var item in trivia)
                if (IsComment(item)) result.Add(new(item.ToString(), new(item.Span.Start, item.Span.Length), LuaTokenKind.Comment));
        }
    }

    private static readonly FrozenSet<string> Keywords = new[]
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in",
        "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while"
    }.ToFrozenSet(StringComparer.Ordinal);

    /** True for a valid Lua name (letter or underscore, then letters, digits and underscores). */
    public static bool IsIdentifier(string value) => value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_')
        && value.All(c => char.IsLetterOrDigit(c) || c == '_');

    /** An identifier that is not a reserved word. */
    public static bool IsName(string value) => IsIdentifier(value) && !Keywords.Contains(value);

    /** Lua double-quoted string literal with backslash, quote and line-break escapes. */
    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";

    /** Only quoted strings with plain escapes decode; graph identifiers come only from these. */
    public static string? DecodeString(string raw)
    {
        if (raw.Length < 2 || raw[0] is not ('\'' or '"')) return null;
        var output = new StringBuilder();
        for (int i = 1; i < raw.Length - 1; i++)
        {
            char value = raw[i];
            if (value != '\\') { output.Append(value); continue; }
            if (++i >= raw.Length - 1) return null;
            value = raw[i];
            switch (value)
            {
                case '\\': case '\'': case '"': output.Append(value); break;
                case 'n': output.Append('\n'); break;
                case 'r': output.Append('\r'); break;
                case 't': output.Append('\t'); break;
                default: return null;
            }
        }
        return output.ToString();
    }

    private IReadOnlyList<LuaToken> ReadTokens()
    {
        var result = new List<LuaToken>();
        foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
            if (!token.IsMissing && token.Span.Length > 0 && token.SpanStart >= offset)
                result.Add(new(token.Text, Shift(token.Span), Classify(token)));
        return result;
    }

    private IReadOnlyList<LuaSpan> ReadComments() =>
        root.DescendantTrivia(descendIntoTrivia: true).Where(IsComment).Select(trivia => Shift(trivia.Span)).ToArray();

    private static bool IsComment(SyntaxTrivia trivia) =>
        trivia.Kind() is LorettaKind.SingleLineCommentTrivia or LorettaKind.MultiLineCommentTrivia;

    private static LuaTokenKind Classify(SyntaxToken token) => token.Kind() switch
    {
        LorettaKind.StringLiteralToken or LorettaKind.HashStringLiteralToken or LorettaKind.InterpolatedStringToken => LuaTokenKind.String,
        LorettaKind.NumericLiteralToken => LuaTokenKind.Number,
        _ => Keywords.Contains(token.Text) ? LuaTokenKind.Keyword : LuaTokenKind.Plain
    };

    private LuaSpan Shift(Loretta.CodeAnalysis.Text.TextSpan span) => new(span.Start - offset, span.Length);

    private void Error(int position, string message)
    {
        if (errors.Count >= MaximumErrors) return;
        int at = Math.Clamp(position - offset, 0, Text.Length);
        int line = Text.AsSpan(0, at).Count('\n') + 1, column = at - LineStart(at) + 1;
        errors.Add($"line {line}, column {column}: {message}");
    }
}
