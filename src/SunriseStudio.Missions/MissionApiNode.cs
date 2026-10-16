using System.Globalization;
using System.Text;

namespace SunriseStudio.Missions;

/** The editor a parameter gets; Lua is the expression editor every parameter also has. */
public enum MissionApiWidget { Lua, Boolean, Integer, Number, Text, Scalar, Choice, Reference, SlotHandle, List, Table, Atom }

/**
 * One argument, table field, list item or atom field of a typed call.
 * @param Path "name", "name.field", "name[2]" or "name[2].field"; a positional argument uses its schema name.
 * @param Value The value in the source; null when the argument is absent.
 */
public sealed record MissionApiParameter(string Path, MissionApiValue Spec, LuaExpression? Value, MissionApiWidget Widget)
{
    public bool Present => Value is not null;
    /** The value is a literal or constant the typed widget shows; otherwise the parameter is in Lua mode. */
    public bool Literal { get; init; }
    public string Text { get; init; } = "";
    public MissionSdkReference? Reference { get; init; }
    /** The current enum entry or atom kind. */
    public string? Choice { get; init; }
    /** What a reference or slot picker offers, already filtered by slot gate and scope. */
    public IReadOnlyList<MissionSdkReference> Choices { get; init; } = [];
    /** Enum entries or atom kinds. */
    public IReadOnlyList<string> Entries { get; init; } = [];
    /** Table fields, list items or atom fields; absent optional fields are listed too. */
    public IReadOnlyList<MissionApiParameter> Children { get; init; } = [];
    /** The table field that holds the value; null for a positional argument. */
    public LuaField? Field { get; init; }
    /** True for a list item, which is removed rather than reset. */
    public bool Item { get; init; }
    public string Label => Spec.Name ?? Path[Math.Max(0, Path.LastIndexOf('['))..];
}

/** A problem found while checking an API call, with the path it was found at. */
public sealed record MissionApiFinding(string Path, string Message);

/** A call the schema types: its parameters as they stand in the source, and the checks the runtime makes. */
public sealed class MissionApiNode
{
    public MissionApiCall Call { get; }
    public MissionApiMember Member { get; }
    public MissionApiType Receiver { get; }
    public MissionApiAnalysis Analysis { get; }
    /** The table of a named call; null when the call passes none. */
    public LuaTable? Table { get; }
    /** The call passes its arguments as a table built in Lua; its parameters are not shown. */
    public bool Computed { get; }
    public IReadOnlyList<MissionApiParameter> Parameters { get; }
    public IReadOnlyList<MissionApiFinding> Findings { get; }
    public string Title => MissionApiText.Friendly(Member.Name);

    private readonly List<MissionApiFinding> findings = [];
    private readonly string source;

    /** Reads the call's parameters, then makes the runtime's checks on them. */
    private MissionApiNode(MissionApiAnalysis analysis, MissionApiCall call)
    {
        Analysis = analysis; Call = call; Member = call.Member!; Receiver = call.Receiver!; source = analysis.Source;
        var arguments = call.Call.Arguments;
        if (Member.Named)
        {
            Table = arguments.Table ?? (arguments is { Form: LuaArgumentsForm.List, Values: [LuaTable table] } ? table : null);
            // A table built elsewhere cannot be read here; only a value that is never a table is wrong.
            Computed = Table is null && arguments is { Form: LuaArgumentsForm.List, Values: [not LuaLiteral] };
        }
        Parameters = Member.Named ? ReadNamed(arguments) : ReadPositional(arguments.Values);
        CheckCall();
        if (!Computed) foreach (var parameter in Parameters) Check(parameter, Receiver);
        Findings = findings;
    }

    /** Parameters from the call's table, with findings for a wrong table or a wrong key. */
    private MissionApiParameter[] ReadNamed(LuaArguments arguments)
    {
        if (Table is null && !Computed && arguments.Values.Count > 0) findings.Add(new("", $"{Member.Name} takes one table of named arguments."));
        var parameters = Member.Arguments.Select(spec => BuildField(spec.Name!, spec, Table?.Field(spec.Name!), Receiver)).ToArray();
        foreach (var field in Table?.Fields ?? [])
            if (field.Name is null) findings.Add(new("", $"{Member.Name} takes named arguments only; this table has a positional or computed key."));
            else if (Member.RefusesUnknownKeys && Member.Arguments.All(spec => spec.Name != field.Name))
                findings.Add(new(field.Name, $"{field.Name} is not an argument of {Member.Name}; the runtime refuses unknown keys."));
        return parameters;
    }

    /** Parameters from the argument list, with a finding when the call passes too many. */
    private MissionApiParameter[] ReadPositional(IReadOnlyList<LuaExpression> values)
    {
        var parameters = Member.Arguments.Select((spec, index) => BuildValue(spec.Name ?? "#" + (index + 1), spec,
            index < values.Count ? values[index] : null, Receiver)).ToArray();
        if (values.Count > Member.Arguments.Count)
            findings.Add(new("", $"{Member.Name} takes {Member.Arguments.Count} argument(s); this call passes {values.Count}."));
        return parameters;
    }

    /** The receiver's slot must pass the member's gate, and the call must use the member's separator. */
    private void CheckCall()
    {
        if (Member.Gate is { } gate && Receiver.SlotType is int slotType && slotType != gate.SlotType)
            findings.Add(new("", $"{Member.Name} needs a type-{gate.SlotType} slot; {Receiver.Identity!.Key} is type {slotType}."));
        if (Member.Colon != (Call.Call.Method is not null))
            findings.Add(new("", $"Call {Member.Name} with '{Member.Separator}'."));
    }

    /** Builds the node for a typed call; any other outcome throws. */
    public static MissionApiNode Build(MissionApiAnalysis analysis, MissionApiCall call) => call.Outcome == MissionApiOutcome.Typed
        ? new(analysis, call) : throw new InvalidOperationException("Only a typed call has parameters.");

    /** The parameter at a path; null when there is none. */
    public MissionApiParameter? Find(string path)
    {
        var stack = new Stack<MissionApiParameter>(Parameters);
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (item.Path == path) return item;
            foreach (var child in item.Children) stack.Push(child);
        }
        return null;
    }

    private MissionApiParameter BuildField(string path, MissionApiValue spec, LuaField? field, MissionApiType? receiver) =>
        BuildValue(path, spec, field?.Expression, receiver) with { Field = field };

    /** One value's parameter: its widget, whether that widget can show the value, and its children. */
    private MissionApiParameter BuildValue(string path, MissionApiValue spec, LuaExpression? value, MissionApiType? receiver)
    {
        var widget = WidgetFor(spec);
        string text = value is null ? "" : value.Span.Read(source);
        var parameter = new MissionApiParameter(path, spec, value, widget) { Text = text };
        switch (widget)
        {
            case MissionApiWidget.Boolean:
                return parameter with { Literal = IsBoolean(value) };
            case MissionApiWidget.Integer or MissionApiWidget.Number:
                return parameter with { Literal = Number(value) is not null };
            case MissionApiWidget.Text:
                return parameter with { Literal = IsText(value) };
            case MissionApiWidget.Scalar:
                return parameter with { Literal = IsBoolean(value) || Number(value) is not null || IsText(value) };
            case MissionApiWidget.Reference:
            {
                var choices = Choices(spec, receiver);
                var reference = value is null ? null : Analysis.Reference(value, spec.Ref!);
                bool number = spec.Kind == "integer" && Number(value) is not null;
                return parameter with { Literal = reference is not null || number, Reference = reference, Choices = choices };
            }
            case MissionApiWidget.SlotHandle:
            {
                var lookup = Analysis.Api.Schema.LookupFor(spec.Ref!);
                var reference = value is LuaCall call && Analysis.Find(call) is { Member: { } member } && member == lookup
                    && call.Arguments.Values is [var selector] ? Analysis.Reference(selector, lookup.Arguments[0].Ref!) : null;
                var choices = lookup is null ? [] : Analysis.Api.Table(lookup.Arguments[0].Ref!).AcceptedBy(spec.SlotGate).ToArray();
                return parameter with { Literal = reference is not null, Reference = reference, Choices = choices };
            }
            case MissionApiWidget.Choice:
            {
                var entries = Analysis.Api.Schema.EntryNames(spec.Ref!);
                string? choice = value is null ? null : EnumEntry(value, spec.Ref!);
                return parameter with { Literal = choice is not null, Choice = choice, Entries = entries };
            }
            case MissionApiWidget.List:
            {
                if (value is not LuaTable list || list.Fields.Any(item => !item.Positional)) return parameter with { Literal = value is null };
                var items = list.Fields.Select((item, index) => BuildValue($"{path}[{index + 1}]", spec.Element!, item.Expression, receiver)
                    with { Field = item, Item = true }).ToArray();
                return parameter with { Literal = true, Children = items };
            }
            case MissionApiWidget.Table:
            {
                if (value is null)
                    return parameter with { Literal = true, Children = spec.Fields.Select(field => BuildField(path + "." + field.Name, field, null, receiver)).ToArray() };
                if (value is not LuaTable table) return parameter;
                var children = spec.Fields.Select(field => BuildField(path + "." + field.Name, field, table.Field(field.Name!), receiver)).ToList();
                if (spec.Element is { } element)
                    children.AddRange(table.Fields.Where(item => item.Positional).Select((item, index) =>
                        BuildValue($"{path}[{index + 1}]", element, item.Expression, receiver) with { Field = item, Item = true }));
                return parameter with { Literal = true, Children = children };
            }
            case MissionApiWidget.Atom:
            {
                var kinds = Analysis.Api.Schema.Enum(spec.Ref!);
                var names = Analysis.Api.Schema.EntryNames(spec.Ref!);
                string key = Analysis.Api.Schema.KindField(spec.Ref!);
                if (value is not LuaTable atom) return parameter with { Entries = names };
                string? kind = atom.Field(key) is { } field ? EnumEntry(field.Expression, spec.Ref!, true) ?? field.StringValue : null;
                var entry = kinds?.Entries.FirstOrDefault(item => item.Name == kind);
                var fields = entry?.Fields.Where(item => item.Name != key).Select(item =>
                    BuildField(path + "." + item.Name, item, atom.Field(item.Name!), receiver)).ToArray() ?? [];
                return parameter with { Literal = entry is not null, Choice = kind, Entries = names, Children = fields };
            }
            default: return parameter;
        }
    }

    /** The editor for a value kind; a kind with no typed editor gets Lua. */
    private MissionApiWidget WidgetFor(MissionApiValue spec) => spec.Kind switch
    {
        "boolean" => MissionApiWidget.Boolean,
        "integer" when spec.Ref is { } table && table.StartsWith("mission.", StringComparison.Ordinal) => MissionApiWidget.Reference,
        "integer" => MissionApiWidget.Integer,
        "number" => MissionApiWidget.Number,
        "string" or "name" or "decimal_string" => MissionApiWidget.Text,
        "scalar" => MissionApiWidget.Scalar,
        "selector" or "mission_row" when spec.Ref is not null => MissionApiWidget.Reference,
        "handle" when spec.Ref is { } handle && Analysis.Api.Schema.LookupFor(handle) is not null => MissionApiWidget.SlotHandle,
        "enum" when spec.Ref is not null && Analysis.Api.Schema.Enum(spec.Ref)?.Entries.Count > 0 => MissionApiWidget.Choice,
        "list" when spec.Element is not null => MissionApiWidget.List,
        "table" => MissionApiWidget.Table,
        "atom" when spec.Ref is not null => MissionApiWidget.Atom,
        _ => MissionApiWidget.Lua
    };

    /** Generated entries a reference parameter offers; scoped to the receiver or another argument's slot when the schema says so. */
    private IReadOnlyList<MissionSdkReference> Choices(MissionApiValue spec, MissionApiType? receiver)
    {
        IEnumerable<MissionSdkReference> entries = Analysis.Api.Table(spec.Ref!);
        // A lookup feeding a gated call offers only slots that call accepts.
        var gate = spec.SlotGate ?? (Member.Kind == "lookup" ? Analysis.ConsumerOf(Call.Call)?.Member?.Gate : null);
        entries = entries.AcceptedBy(gate);
        string? owner = Owner(spec, receiver);
        // An entry with no owner fits every scope.
        return (owner is null ? entries : entries.Where(entry => entry.Owner.Length == 0 || entry.Owner == owner)).ToArray();
    }

    /** The slot key a scoped entry must belong to; null when the scope is not known here. */
    private string? Owner(MissionApiValue spec, MissionApiType? receiver)
    {
        if (spec.Scope == "receiver") return receiver?.Identity?.Key;
        if (spec.Scope != "argument" || Table is null) return null;
        // The other argument is the slot handle argument of the same call.
        foreach (var other in Member.Arguments.Where(value => value.Kind == "handle" && value.SlotGate is not null))
            if (Table.Field(other.Name!)?.Expression is LuaCall { Arguments.Values: [var selector] } call && Analysis.Find(call)?.Member is { } lookup
                && lookup.Arguments is [{ Ref: { } table }]) return Analysis.Reference(selector, table)?.Key;
        return null;
    }

    /**
     * The entry name of an enum value: a collection entry read by name, or its name as a string where a string is taken.
     * @param text True for a string field such as an atom's kind.
     */
    private string? EnumEntry(LuaExpression value, string name, bool text = false)
    {
        var schema = Analysis.Api.Schema;
        if (value is LuaLiteral { StringValue: { } literal })
            return text && schema.Enum(name)?.Entries.Any(entry => entry.Name == literal) == true ? literal : null;
        if (value is not LuaMember member || Analysis.TypeOf(member.Target) is not { } target) return null;
        bool collection = target.EnumTable ? target.Receiver == name : schema.Receiver(target.Receiver)?.Entries == name;
        return collection && schema.Enum(name)?.Entries.Any(entry => entry.Name == member.Name) == true ? member.Name : null;
    }

    private static bool IsBoolean(LuaExpression? value) => value is LuaLiteral { Kind: LuaLiteralKind.True or LuaLiteralKind.False };

    /** A quoted string with plain escapes; a long-bracket string is not a text literal. */
    private static bool IsText(LuaExpression? value) => value is LuaLiteral { StringValue: not null };

    /** A number literal, or a negated one; null for anything else. */
    public static double? Number(LuaExpression? value) => value switch
    {
        LuaLiteral { Kind: LuaLiteralKind.Number } literal => ParseNumber(literal.Text),
        LuaUnary { Operator: "-", Operand: LuaLiteral { Kind: LuaLiteralKind.Number } literal } => -ParseNumber(literal.Text),
        _ => null
    };

    private static double? ParseNumber(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hex) ? hex : null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
    }

    /** The checks the runtime makes on literal values; computed values are not checked. */
    private void Check(MissionApiParameter parameter, MissionApiType receiver)
    {
        var spec = parameter.Spec;
        if (!parameter.Present)
        {
            if (!spec.Optional && !parameter.Item) findings.Add(new(parameter.Path, $"{parameter.Label} is required."));
            return;
        }
        var value = parameter.Value!;
        string label = parameter.Label;
        switch (parameter.Widget)
        {
            case MissionApiWidget.Boolean when value is LuaLiteral { Kind: not (LuaLiteralKind.True or LuaLiteralKind.False or LuaLiteralKind.VarArgs) }:
                findings.Add(new(parameter.Path, $"{label} must be a boolean.")); break;
            case MissionApiWidget.Integer or MissionApiWidget.Number or MissionApiWidget.Reference when Number(value) is double number:
                if (spec.Kind == "integer" && number != Math.Floor(number)) findings.Add(new(parameter.Path, $"{label} must be an integer."));
                if (number < spec.Minimum || number > spec.Maximum)
                    findings.Add(new(parameter.Path, $"{label} is {MissionApiSchema.NumberText(number)}; the range is {Range(spec)}."));
                break;
            case MissionApiWidget.Integer or MissionApiWidget.Number when value is LuaLiteral { Kind: LuaLiteralKind.String or LuaLiteralKind.True or LuaLiteralKind.False }:
                findings.Add(new(parameter.Path, $"{label} must be a number.")); break;
            case MissionApiWidget.Text when value is LuaLiteral { StringValue: { } text }:
                int bytes = Encoding.UTF8.GetByteCount(text);
                if (bytes < spec.MinLength || bytes > spec.MaxLength)
                    findings.Add(new(parameter.Path, $"{label} is {bytes} bytes; it must be {spec.MinLength ?? 0} to {spec.MaxLength} bytes."));
                if (spec.Kind == "decimal_string" && (text.Length == 0 || !text.All(char.IsAsciiDigit)))
                    findings.Add(new(parameter.Path, $"{label} must be decimal digits."));
                break;
            case MissionApiWidget.Text when value is LuaLiteral { Kind: LuaLiteralKind.Number or LuaLiteralKind.True or LuaLiteralKind.False }:
                findings.Add(new(parameter.Path, $"{label} must be a string.")); break;
            case MissionApiWidget.Reference:
                CheckReference(parameter, receiver); break;
            case MissionApiWidget.SlotHandle when parameter.Reference is { } slot && spec.SlotGate is { } gate && slot.SlotType is int type && type != gate.SlotType:
                findings.Add(new(parameter.Path, $"{label} needs a type-{gate.SlotType} slot; {slot.Key} is type {type}.")); break;
            case MissionApiWidget.Choice when !parameter.Literal && value is LuaMember member && Analysis.TypeOf(member.Target) is { } target
                && Analysis.Api.Schema.Receiver(target.Receiver)?.Entries == spec.Ref:
                findings.Add(new(parameter.Path, $"{member.Name} is not an entry of {spec.Ref}.")); break;
            case MissionApiWidget.Atom when value is LuaTable atom:
                if (parameter.Choice is null) findings.Add(new(parameter.Path, $"{label} needs a kind from {spec.Ref}."));
                else if (!parameter.Literal) findings.Add(new(parameter.Path, $"{parameter.Choice} is not an atom kind."));
                else if (Analysis.Api.Schema.Enum(spec.Ref!)?.Entries.First(entry => entry.Name == parameter.Choice) is { Closed: true } closed)
                    foreach (var field in atom.Fields.Where(field => field.Name is not null && closed.Fields.All(item => item.Name != field.Name)))
                        findings.Add(new(parameter.Path + "." + field.Name, $"{field.Name} is not a field of a {parameter.Choice} atom."));
                break;
        }
        if (parameter.Widget is MissionApiWidget.List or MissionApiWidget.Table && value is LuaTable counted)
        {
            int count = counted.Fields.Count(field => field.Positional);
            if (count < spec.MinCount || count > spec.MaxCount)
                findings.Add(new(parameter.Path, $"{label} has {count} entries; it takes {spec.MinCount ?? 0} to {spec.MaxCount}."));
        }
        foreach (var child in parameter.Children) Check(child, receiver);
    }

    /** A reference must name an entry of its own table that passes the slot gate and belongs to the scope. */
    private void CheckReference(MissionApiParameter parameter, MissionApiType receiver)
    {
        var spec = parameter.Spec;
        if (parameter.Reference is not { } entry)
        {
            if (Number(parameter.Value) is not null) return;
            string expanded = Analysis.Bindings.Expand(parameter.Text);
            if (expanded.StartsWith(spec.Ref + ".", StringComparison.Ordinal))
                findings.Add(new(parameter.Path, $"{expanded} is not in the installed SDK."));
            else if (expanded.StartsWith("mission.", StringComparison.Ordinal) && Analysis.Api.Find(expanded) is { } other)
                findings.Add(new(parameter.Path, $"{parameter.Label} takes an entry of {spec.Ref}; {other.Table} is another table."));
            return;
        }
        if (spec.SlotGate is { } gate && entry.SlotType is int type && type != gate.SlotType)
            findings.Add(new(parameter.Path, $"{parameter.Label} needs a type-{gate.SlotType} slot; {entry.Key} is type {type}."));
        if (Owner(spec, receiver) is { } owner && entry.Owner.Length > 0 && entry.Owner != owner)
            findings.Add(new(parameter.Path, $"{entry.Key} belongs to {entry.Owner}, not {owner}."));
    }

    private static string Range(MissionApiValue spec) =>
        (spec.Minimum is { } low ? MissionApiSchema.NumberText(low) : "") + ".." + (spec.Maximum is { } high ? MissionApiSchema.NumberText(high) : "");
}

/** Display names for API members. */
public static class MissionApiText
{
    /** Display name: underscores become spaces and the first letter is capitalised. */
    public static string Friendly(string name)
    {
        string value = name.Replace('_', ' ');
        return value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
    }
}
