namespace SunriseStudio.Missions;

/** A UTF-16 range of the original source text. */
public readonly record struct LuaSpan(int Start, int Length)
{
    public int End => checked(Start + Length);
    /** The text this span covers in source. */
    public string Read(string source) => source.Substring(Start, Length);
    /** Span from a start offset to an end offset. */
    public static LuaSpan FromBounds(int start, int end) => new(start, end - start);
}

/** Class of a highlighted token. */
public enum LuaTokenKind { Plain, Keyword, String, Number, Comment }
/** A lexed token with its text, span and kind. */
public sealed record LuaToken(string Text, LuaSpan Span, LuaTokenKind Kind);

/** Statements and expressions; Span is exact source, so an edit replaces one span. */
public abstract record LuaSyntax(LuaSpan Span);

/** Base of expression nodes. */
public abstract record LuaExpression(LuaSpan Span) : LuaSyntax(Span);
/** A bare name expression. */
public sealed record LuaName(LuaSpan Span, string Name) : LuaExpression(Span);

/** Kinds of literal expressions. */
public enum LuaLiteralKind { Nil, True, False, Number, String, VarArgs }

/** @param Text The literal as written, quotes included. */
public sealed record LuaLiteral(LuaSpan Span, LuaLiteralKind Kind, string Text) : LuaExpression(Span)
{
    /** Decoded value of a quoted string with plain escapes; null for any other literal. */
    public string? StringValue => Kind == LuaLiteralKind.String ? LuaScript.DecodeString(Text) : null;
}

/** `Target.Name`. */
public sealed record LuaMember(LuaSpan Span, LuaExpression Target, string Name, LuaSpan NameSpan) : LuaExpression(Span);

/** `Target[Key]`. */
public sealed record LuaIndex(LuaSpan Span, LuaExpression Target, LuaExpression Key) : LuaExpression(Span);

/** How call arguments are written: a list, a table constructor or a string literal. */
public enum LuaArgumentsForm { List, Table, String }

/**
 * Call arguments: `(a, b)`, `{...}` or `"text"`.
 * @param Span The arguments with their parentheses.
 * @param Inner From the first value to the last; an empty span at ')' when there are none. Equal to Span for a table or string argument.
 */
public sealed record LuaArguments(LuaSpan Span, LuaArgumentsForm Form, IReadOnlyList<LuaExpression> Values, LuaSpan Inner)
{
    public LuaTable? Table => Form == LuaArgumentsForm.Table ? (LuaTable)Values[0] : null;
}

/**
 * `Target(args)`, or `Target:Method(args)` when Method is set.
 * @param MethodSpan The method name after the colon.
 */
public sealed record LuaCall(LuaSpan Span, LuaExpression Target, string? Method, LuaSpan? MethodSpan, LuaArguments Arguments)
    : LuaExpression(Span)
{
    /** The called target through the method name, without the arguments. */
    public LuaSpan Callee => LuaSpan.FromBounds(Span.Start, (MethodSpan ?? Target.Span).End);
}

/** A table constructor and its fields. */
public sealed record LuaTable(LuaSpan Span, IReadOnlyList<LuaField> Fields) : LuaExpression(Span)
{
    /** The field with this name, or null. */
    public LuaField? Field(string name) => Fields.FirstOrDefault(value => value.Name == name);
}

/**
 * One table field: `name = value`, `[key] = value` or a positional value.
 * @param Span The field with its separator, when it has one.
 * @param Separator The `,` or `;` after the field.
 */
public sealed record LuaField(LuaSpan Span, string? Name, LuaSpan? NameSpan, LuaExpression? Key, LuaExpression Expression,
    LuaSpan? Separator)
{
    public LuaSpan Value => Expression.Span;
    public string? StringValue => (Expression as LuaLiteral)?.StringValue;
    public LuaTable? Table => Expression as LuaTable;
    public bool Positional => Name is null && Key is null;
}

/**
 * A function expression or the function of a function statement.
 * @param Span From the `function` keyword through `end`.
 * @param Parameters Parameter names; `...` for a vararg.
 */
public sealed record LuaFunction(LuaSpan Span, IReadOnlyList<string> Parameters, LuaBlock Body) : LuaExpression(Span);

/** Binary operator expression. */
public sealed record LuaBinary(LuaSpan Span, string Operator, LuaExpression Left, LuaExpression Right) : LuaExpression(Span);
/** Unary operator expression. */
public sealed record LuaUnary(LuaSpan Span, string Operator, LuaExpression Operand) : LuaExpression(Span);
/** Parenthesised expression. */
public sealed record LuaParenthesized(LuaSpan Span, LuaExpression Inner) : LuaExpression(Span);

/** Source the reader could not read as Lua 5.4; only a script with errors has one. */
public sealed record LuaInvalidExpression(LuaSpan Span) : LuaExpression(Span);

/**
 * One statement of a block.
 * @param Span The statement with the comment lines directly above it.
 * @param Code The statement alone, its semicolon included.
 * @param Keyword Text of the statement's first token.
 */
public abstract record LuaStatement(LuaSpan Span, LuaSpan Code, string Keyword) : LuaSyntax(Span);

/** A local variable name with its optional attribute (const or close). */
public sealed record LuaLocalName(LuaSpan Span, string Name, string? Attribute);

/** `local a, b <const> = x, y`; EqualsSign is null without values. */
public sealed record LuaLocalStatement(LuaSpan Span, LuaSpan Code, string Keyword, IReadOnlyList<LuaLocalName> Names,
    LuaSpan? EqualsSign, IReadOnlyList<LuaExpression> Values) : LuaStatement(Span, Code, Keyword);

/** Assignment or local declaration: targets, equals sign and values. */
public sealed record LuaAssignmentStatement(LuaSpan Span, LuaSpan Code, string Keyword, IReadOnlyList<LuaExpression> Targets,
    LuaSpan EqualsSign, IReadOnlyList<LuaExpression> Values) : LuaStatement(Span, Code, Keyword);

/** An expression used as a statement; Lua allows only a call. */
public sealed record LuaCallStatement(LuaSpan Span, LuaSpan Code, string Keyword, LuaExpression Expression)
    : LuaStatement(Span, Code, Keyword);

/** `function a.b:c() end` or `local function f() end`; Name is the dotted name without spaces. */
public sealed record LuaFunctionStatement(LuaSpan Span, LuaSpan Code, string Keyword, string Name, LuaSpan NameSpan, bool Local,
    LuaFunction Function) : LuaStatement(Span, Code, Keyword);

/** A return statement and its values. */
public sealed record LuaReturnStatement(LuaSpan Span, LuaSpan Code, string Keyword, IReadOnlyList<LuaExpression> Values)
    : LuaStatement(Span, Code, Keyword);

/** A break statement. */
public sealed record LuaBreakStatement(LuaSpan Span, LuaSpan Code, string Keyword) : LuaStatement(Span, Code, Keyword);
/** A goto statement and its label. */
public sealed record LuaGotoStatement(LuaSpan Span, LuaSpan Code, string Keyword, string Label) : LuaStatement(Span, Code, Keyword);
/** A label statement. */
public sealed record LuaLabelStatement(LuaSpan Span, LuaSpan Code, string Keyword, string Label) : LuaStatement(Span, Code, Keyword);

/** An if, while, for, repeat or do statement. */
public sealed record LuaControlStatement(LuaSpan Span, LuaSpan Code, string Keyword, LuaControlBlock Control)
    : LuaStatement(Span, Code, Keyword);

/** Statement the parser could not understand; kept so the source round-trips. */
public sealed record LuaInvalidStatement(LuaSpan Span, LuaSpan Code, string Keyword) : LuaStatement(Span, Code, Keyword);

/**
 * The statements between a header and the token that closes them.
 * @param Label "if", "elseif" or "else" for an if branch; "body" otherwise.
 * @param AfterHeader Source offset after the header: `then`, `do`, `repeat`, `else` or the parameter list.
 * @param Closing Source offset of the token that ends the statements.
 * @param Span From the block's opening keyword up to Closing.
 */
public sealed record LuaBlock(string Label, LuaExpression? Condition, IReadOnlyList<LuaStatement> Statements, int AfterHeader,
    int Closing, LuaSpan Span);

/**
 * @param Kind "if", "while", "for", "repeat" or "do".
 * @param Input The loop header, or the condition of a while, repeat or the first if branch.
 * @param Variables The loop variables of a for.
 * @param Header Header expressions in source order; an if keeps its conditions on its bodies.
 */
public sealed record LuaControlBlock(string Kind, IReadOnlyList<LuaBlock> Bodies, LuaSpan? Input,
    IReadOnlyList<string> Variables, IReadOnlyList<LuaExpression> Header);
