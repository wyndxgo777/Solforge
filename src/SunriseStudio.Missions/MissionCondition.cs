/**
 * File: Mission conditions (facts, all/any groups, custom Lua) parsed from and written back to flow source.
 */
namespace SunriseStudio.Missions;

/** Base of mission conditions. */
public abstract record MissionCondition;
/** A single fact that must hold. */
public sealed record MissionFactCondition(string Fact) : MissionCondition;
/** A group where all (or any) children must hold. */
public sealed record MissionConditionGroup(bool All, IReadOnlyList<MissionCondition> Children) : MissionCondition;
/** A Lua predicate kept as its own source. */
public sealed record MissionCustomCondition(string Source) : MissionCondition;

/** Conditions use the installed flow.fact/all/any contract without executing predicates. */
public static class MissionConditions
{
    /** Parses a condition expression; null for nil or empty. */
    public static MissionCondition? Read(string? expression, MissionScriptBindings bindings) =>
        expression is null || expression.Trim() == "nil" ? null : Parse(expression, bindings, 1);

    /** A fact, an all or any group in table or flow-call form, or a custom Lua predicate. */
    private static MissionCondition Parse(string expression, MissionScriptBindings bindings, int depth)
    {
        if (depth > 16) throw new InvalidDataException("Flow conditions exceed 16 levels.");
        var values = LuaScript.ParseExpressions(expression, true);
        if (values.Count == 0) throw new InvalidDataException("Flow condition is empty.");
        if (expression.Trim() == "nil") throw new InvalidDataException("Flow condition groups cannot contain holes.");
        if (values is [LuaTable table])
        {
            var operators = table.Fields.Where(field => field.Name is "fact" or "all" or "any").ToArray();
            if (operators.Length != 1) throw new InvalidDataException("Flow condition needs one fact, all or any operator.");
            var field = operators[0];
            if (field.Name == "fact")
            {
                if (field.StringValue == "") throw new InvalidDataException("Flow fact name is empty.");
                if (field.StringValue is not { Length: > 0 } fact)
                    return new MissionCustomCondition(expression);
                return table.Fields.Count == 1 ? new MissionFactCondition(fact) : new MissionCustomCondition(expression);
            }
            if (field.Table is not { } children) return new MissionCustomCondition(expression);
            ValidateList(children.Fields.Count, children.Fields.Any(child => child.Name is not null));
            if (table.Fields.Count != 1) return new MissionCustomCondition(expression);
            return new MissionConditionGroup(field.Name == "all", children.Fields.Select(child =>
                Parse(child.Value.Read(expression), bindings, depth + 1)).ToArray());
        }
        if (values is [LuaCall { Method: null, Arguments: { Form: LuaArgumentsForm.List, Values: var items } } call]
            && bindings.Expand(call.Target.Span.Read(expression)) is ("flow.fact" or "flow.all" or "flow.any") and var function)
        {
            ValidateList(items.Count, false);
            if (function == "flow.fact")
            {
                if (items.Count != 1) throw new InvalidDataException("Flow fact needs one name.");
                string? fact = (items[0] as LuaLiteral)?.StringValue;
                if (fact == "") throw new InvalidDataException("Flow fact name is empty.");
                return fact is not null ? new MissionFactCondition(fact) : new MissionCustomCondition(expression);
            }
            return new MissionConditionGroup(function == "flow.all", items.Select(child =>
                Parse(child.Span.Read(expression), bindings, depth + 1)).ToArray());
        }
        // A predicate never starts with a boolean, string or number constant.
        if (LuaScript.First(values[0]) is LuaLiteral { Kind: LuaLiteralKind.True or LuaLiteralKind.False or LuaLiteralKind.String }
            || LuaScript.First(values[0]) is LuaLiteral { Kind: LuaLiteralKind.Number } number && char.IsDigit(number.Text[0]))
            throw new InvalidDataException("Flow condition must be a fact, group or Lua predicate.");
        return new MissionCustomCondition(expression);
    }

    private static void ValidateList(int count, bool keyed)
    {
        if (count is < 1 or > 64 || keyed) throw new InvalidDataException("Flow condition groups need 1 to 64 list entries.");
    }

    /** Facts the condition refers to. */
    public static IReadOnlyList<string> Facts(MissionCondition? condition) => condition switch
    {
        MissionFactCondition fact => [fact.Fact], MissionConditionGroup group => group.Children.SelectMany(Facts).ToArray(),
        _ => []
    };

    /** Writes the condition back as flow source. */
    public static string Write(MissionCondition condition) => Write(condition, 1);

    /** Writes the table form; a custom condition keeps its own source. */
    private static string Write(MissionCondition condition, int depth)
    {
        if (depth > 16) throw new InvalidDataException("Flow conditions exceed 16 levels.");
        return condition switch
        {
            MissionFactCondition { Fact.Length: > 0 } fact => "{ fact = " + LuaScript.Quote(fact.Fact) + " }",
            MissionConditionGroup group when group.Children.Count is >= 1 and <= 64 =>
                "{ " + (group.All ? "all" : "any") + " = { " + string.Join(", ", group.Children.Select(child => Write(child, depth + 1))) + " } }",
            MissionCustomCondition custom => custom.Source,
            _ => throw new InvalidDataException("Flow condition is incomplete.")
        };
    }

    /** One-line description for display. */
    public static string Summary(MissionCondition? condition) => condition switch
    {
        null => "Immediately", MissionFactCondition fact => fact.Fact.Replace('_', ' '),
        MissionConditionGroup group => (group.All ? "All: " : "Any: ") + string.Join(", ", group.Children.Select(Summary)),
        _ => "Custom Lua"
    };
}
