namespace SunriseStudio.Missions;

/** The schema and the script's generated SDK entries; typed nodes need both. */
public sealed class MissionApiContext(MissionApiSchema schema, IReadOnlyList<MissionSdkReference> references)
{
    private Dictionary<string, IReadOnlyList<MissionSdkReference>>? byTable;
    public MissionApiSchema Schema { get; } = schema;
    public IReadOnlyList<MissionSdkReference> References { get; } = references;

    /** The generated SDK reference for an expression, or null. */
    public MissionSdkReference? Find(string expression) => MissionSdkIndex.For(References).First(expression);

    /** Entries of a generated table such as mission.Slot, nested entries included. */
    public IReadOnlyList<MissionSdkReference> Table(string table) =>
        (byTable ??= References.GroupBy(value => value.Table)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<MissionSdkReference>)group.ToArray()))
        .GetValueOrDefault(table) ?? [];
}

/** What a value is: a receiver handle, one or more event views, or an enum table. */
public sealed record MissionApiType(string Receiver)
{
    public const string EventReceiver = "event";
    /** The events an event view may be; more than one when a callback serves several. */
    public IReadOnlyList<MissionApiEvent> Events { get; init; } = [];
    /** The generated entry a lookup was made with, such as the mission.Slot entry of a slot handle. */
    public MissionSdkReference? Identity { get; init; }
    /** An enum table such as context.sdk.atom_kinds; Receiver is the enum name. */
    public bool EnumTable { get; init; }
    public int? SlotType => Identity?.SlotType;

    /** Short text naming what the call resolved to. */
    public string Describe() => this switch
    {
        { Receiver: EventReceiver, Events: [var only] } => only.Name.ToLowerInvariant() + " event",
        { Receiver: EventReceiver } => "event",
        { Identity: { } identity } => Receiver + " " + identity.Key,
        _ => Receiver
    };
}

/** How a Lua call resolved: typed API call, unknown receiver or member, not an API call, or dynamic. */
public enum MissionApiOutcome { Typed, UnknownReceiver, UnknownMember, NotApi, Dynamic }

/**
 * One call of a script and what the schema says about it.
 * @param Reason Why the call is not typed; empty when it is.
 */
public sealed record MissionApiCall(LuaCall Call, MissionApiOutcome Outcome, string Reason)
{
    public MissionApiType? Receiver { get; init; }
    public MissionApiMember? Member { get; init; }
    /** The typed names visible at the call. */
    public IReadOnlyDictionary<string, MissionApiType> Names { get; init; } = new Dictionary<string, MissionApiType>();
}

/** Every call of a script with its receiver type, found from callback signatures, lookups and locals. */
public sealed class MissionApiAnalysis
{
    private readonly Dictionary<LuaCall, MissionApiCall> calls = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LuaCall, MissionApiCall> consumers = new(ReferenceEqualityComparer.Instance);
    private readonly List<MissionApiCall> callList = [];
    private readonly Dictionary<LuaBlock, IReadOnlyDictionary<string, MissionApiType>> scopes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LuaFunction, IReadOnlyList<MissionApiEvent>> eventCallbacks = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LuaFunction, string> callbacks = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LuaExpression, MissionApiType> types = new(ReferenceEqualityComparer.Instance);
    public MissionApiContext Api { get; }
    public MissionScriptBindings Bindings { get; }
    public IReadOnlyList<MissionApiCall> Calls => callList;

    public string Source { get; }

    private MissionApiAnalysis(MissionApiContext api, MissionScriptBindings bindings, string source)
    { Api = api; Bindings = bindings; Source = source; }

    /** Walks the script, tracking local bindings, and resolves every call against the schema. */
    public static MissionApiAnalysis Analyze(LuaScript script, MissionApiContext api)
    {
        var analysis = new MissionApiAnalysis(api, new MissionScriptBindings(script), script.Text);
        new Resolver(analysis).Run(script);
        return analysis;
    }

    /** The analysis of a call, or null when it was not analysed. */
    public MissionApiCall? Find(LuaCall call) => calls.GetValueOrDefault(call);

    /** The typed call whose receiver this call returns, such as set_directive for its context:slot lookup; null when none. */
    public MissionApiCall? ConsumerOf(LuaCall call) => consumers.GetValueOrDefault(call);

    /** The type of a name, member read or call result; null when it has none. */
    public MissionApiType? TypeOf(LuaExpression expression) => types.GetValueOrDefault(expression);

    /** The generated entry an expression names, through the script's aliases or by its id string; null when none. */
    public MissionSdkReference? Reference(LuaExpression value, string table)
    {
        var found = Api.Find(Bindings.Expand(value.Span.Read(Source)));
        if (found is not null && found.Table == table) return found;
        string? identity = (value as LuaLiteral)?.StringValue;
        return identity is null ? null : Api.Table(table).FirstOrDefault(entry => entry.Identity == identity);
    }

    /** Typed names visible at the end of a block; a new statement there may use them. */
    public IReadOnlyDictionary<string, MissionApiType> ScopeAt(LuaBlock block) =>
        scopes.GetValueOrDefault(block) ?? new Dictionary<string, MissionApiType>();

    /** The program key a callback function is bound to; null for any other function. */
    public string? CallbackOf(LuaFunction function) => callbacks.GetValueOrDefault(function);

    /** The events a callback function serves; empty for a start callback or any other function. */
    public IReadOnlyList<MissionApiEvent> EventsOf(LuaFunction function) => eventCallbacks.GetValueOrDefault(function) ?? [];

    /** @param Module The value is a required Lua module, whose methods are never API calls. */
    private readonly record struct Binding(MissionApiType? Type, string Reason, bool Module = false);

    /** One lexical level of names, each with its type or the reason it has none. */
    private sealed class Scope(Scope? outer)
    {
        private readonly Scope? parent = outer;
        private readonly Dictionary<string, Binding> names = new(StringComparer.Ordinal);
        /** Binds a local name in the current scope. */
        public void Define(string name, Binding value) => names[name] = value;

        /** Finds a name in the current or an enclosing scope. */
        public Binding? Lookup(string name)
        {
            for (var scope = this; scope is not null; scope = scope.parent)
                if (scope.names.TryGetValue(name, out var value)) return value;
            return null;
        }

        /** An assignment keeps a binding only when it stores the same receiver type. */
        public void Assign(string name, MissionApiType? type)
        {
            for (var scope = this; scope is not null; scope = scope.parent)
                if (scope.names.TryGetValue(name, out var value))
                {
                    if (value.Type is not null && value.Type.Receiver != type?.Receiver) scope.names[name] = new(null, $"'{name}' is assigned again");
                    return;
                }
        }

        /** The bindings with a known API type. */
        public Dictionary<string, MissionApiType> Typed()
        {
            var result = new Dictionary<string, MissionApiType>(StringComparer.Ordinal);
            for (var scope = this; scope is not null; scope = scope.parent)
                foreach (var (name, value) in scope.names)
                    if (value.Type is not null) result.TryAdd(name, value.Type);
            return result;
        }
    }

    /** One walk over the script that types names and classifies every call. */
    private sealed class Resolver(MissionApiAnalysis owner)
    {
        private const int MaximumDepth = 512;
        private readonly MissionApiSchema schema = owner.Api.Schema;
        private int depth;

        /** Analyses the script's statements in order. */
        public void Run(LuaScript script)
        {
            FindCallbacks(script);
            var root = new Scope(null);
            Block(script.Chunk, root);
        }

        /** Program keys bind their functions; a key naming a top-level local function binds that function. */
        private void FindCallbacks(LuaScript script)
        {
            var functions = new Dictionary<string, LuaFunction>(StringComparer.Ordinal);
            var tables = new Dictionary<string, LuaTable>(StringComparer.Ordinal);
            LuaTable? program = null;
            foreach (var statement in script.Chunk.Statements)
            {
                if (statement is LuaFunctionStatement { Name: var name } declared) functions[name] = declared.Function;
                else if (statement is LuaLocalStatement { Names: [var local], Values: [var value] })
                {
                    if (value is LuaFunction function) functions[local.Name] = function;
                    else if (value is LuaTable table) tables[local.Name] = table;
                }
                else if (statement is LuaReturnStatement { Values: [var returned] })
                    program = returned as LuaTable ?? (returned is LuaName named ? tables.GetValueOrDefault(named.Name) : null);
            }
            if (program is null) return;
            var starts = schema.Program.Entries.Where(value => value.Kind == "function").Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var field in program.Fields)
            {
                if (field.Name is not { } key) continue;
                var function = field.Expression as LuaFunction ?? (field.Expression is LuaName name ? functions.GetValueOrDefault(name.Name) : null);
                if (function is null) continue;
                if (schema.EventForCallback(key) is { } value)
                {
                    var events = owner.eventCallbacks.GetValueOrDefault(function) ?? [];
                    owner.eventCallbacks[function] = events.Append(value).Distinct().ToArray();
                    owner.callbacks.TryAdd(function, key);
                }
                else if (starts.Contains(key)) owner.callbacks.TryAdd(function, key);
            }
        }

        private void Block(LuaBlock block, Scope scope)
        {
            foreach (var statement in block.Statements) Statement(statement, scope);
            owner.scopes[block] = scope.Typed();
        }

        /** Defines or reassigns the statement's names and walks its expressions and blocks. */
        private void Statement(LuaStatement statement, Scope scope)
        {
            Enter();
            try
            {
                switch (statement)
                {
                    case LuaLocalStatement local:
                    {
                        var values = local.Values.Select(value => Expression(value, scope)).ToArray();
                        for (int i = 0; i < local.Names.Count; i++)
                        {
                            string name = local.Names[i].Name;
                            if (i < local.Values.Count && IsRequire(local.Values[i]))
                            {
                                string module = ((LuaCall)local.Values[i]).Arguments.Inner.Read(owner.Source);
                                scope.Define(name, new(null, $"'{name}' is the Lua module {module}", true));
                            }
                            else scope.Define(name, i < values.Length ? values[i] : new(null, $"'{name}' has no value"));
                        }
                        break;
                    }
                    case LuaAssignmentStatement assignment:
                    {
                        foreach (var target in assignment.Targets)
                            foreach (var child in LuaScript.Children(target)) Expression((LuaExpression)child, scope);
                        var values = assignment.Values.Select(value => Expression(value, scope)).ToArray();
                        for (int i = 0; i < assignment.Targets.Count; i++)
                            if (assignment.Targets[i] is LuaName name) scope.Assign(name.Name, i < values.Length ? values[i].Type : null);
                        break;
                    }
                    case LuaCallStatement call: Expression(call.Expression, scope); break;
                    case LuaFunctionStatement function:
                        if (function.Local) scope.Define(function.Name, new(null, $"'{function.Name}' is a function"));
                        Function(function.Function, scope, function.Name.Contains(':') ? function.Name : null);
                        break;
                    case LuaReturnStatement value: foreach (var item in value.Values) Expression(item, scope); break;
                    case LuaControlStatement { Control: var control }: Control(control, scope); break;
                }
            }
            finally { depth--; }
        }

        /** Each body gets its own scope; loop variables are untyped. */
        private void Control(LuaControlBlock control, Scope scope)
        {
            switch (control.Kind)
            {
                case "if":
                    foreach (var body in control.Bodies)
                    {
                        if (body.Condition is { } condition) Expression(condition, scope);
                        Block(body, new Scope(scope));
                    }
                    break;
                case "repeat":
                {
                    // The until condition sees the body's locals.
                    var inner = new Scope(scope);
                    Block(control.Bodies[0], inner);
                    foreach (var item in control.Header) Expression(item, inner);
                    break;
                }
                default:
                {
                    foreach (var item in control.Header) Expression(item, scope);
                    var inner = new Scope(scope);
                    foreach (string variable in control.Variables) inner.Define(variable, new(null, $"'{variable}' is a loop variable"));
                    Block(control.Bodies[0], inner);
                    break;
                }
            }
        }

        private void Enter()
        {
            if (++depth > MaximumDepth) throw new InvalidDataException("Lua nesting exceeds the typed node limit.");
        }

        private static bool IsRequire(LuaExpression value) => value is LuaCall { Target: LuaName { Name: "require" }, Method: null };

        /**
         * A callback's parameters take the schema's callback signature; any other parameter is typed by its name.
         * @param method The name of a method declared with ':', whose hidden first parameter is self.
         */
        private void Function(LuaFunction function, Scope scope, string? method = null)
        {
            var inner = new Scope(scope);
            if (method is not null) inner.Define("self", new(null, $"'self' is the table of {method}"));
            string? callback = owner.CallbackOf(function);
            var events = owner.EventsOf(function);
            var signature = events.Count > 0 ? schema.Program.EventParameters : schema.Program.StartParameters;
            for (int i = 0; i < function.Parameters.Count; i++)
            {
                string name = function.Parameters[i];
                if (name == "...") continue;
                if (callback is not null)
                    inner.Define(name, i < signature.Count ? Parameter(signature[i], events)
                        : new(null, $"'{name}' is not a parameter of {callback}"));
                else if (schema.Program.StartParameters.Contains(name) || schema.Program.EventParameters.Contains(name))
                    inner.Define(name, Parameter(name, schema.Events));
                else inner.Define(name, new(null, $"'{name}' is a parameter of a function that is not a callback"));
            }
            Block(function.Body, inner);
        }

        /** A parameter named after a receiver is that receiver; the event parameter is an event view. */
        private Binding Parameter(string name, IReadOnlyList<MissionApiEvent> events)
        {
            if (schema.Receiver(name) is not null) return new(new MissionApiType(name), "");
            if (name == schema.Program.EventParameters.LastOrDefault() && events.Count > 0)
                return new(new MissionApiType(MissionApiType.EventReceiver) { Events = events }, "");
            return new(null, $"'{name}' has no receiver in the schema");
        }

        private Binding Expression(LuaExpression expression, Scope scope)
        {
            var result = Read(expression, scope);
            if (result.Type is not null) owner.types[expression] = result.Type;
            return result;
        }

        /** The binding of an expression; nested expressions are walked for their calls. */
        private Binding Read(LuaExpression expression, Scope scope)
        {
            Enter();
            try
            {
                switch (expression)
                {
                    case LuaName name:
                        return scope.Lookup(name.Name) ?? new(null, $"'{name.Name}' is a global name");
                    case LuaMember member:
                    {
                        var target = Expression(member.Target, scope);
                        return target.Type is null ? target : Field(target.Type, member.Name);
                    }
                    case LuaCall call: return Call(call, scope);
                    case LuaParenthesized parenthesized: return Expression(parenthesized.Inner, scope);
                    case LuaFunction function: Function(function, scope); return new(null, "a function");
                    default:
                        foreach (var child in LuaScript.Children(expression)) Expression((LuaExpression)child, scope);
                        return new(null, expression switch
                        {
                            LuaIndex => "an indexed value", LuaTable => "a table", LuaLiteral => "a literal", _ => "a computed value"
                        });
                }
            }
            finally { depth--; }
        }

        /** The binding of type.name: a handle or enum table field, or a collection entry read by name. */
        private Binding Field(MissionApiType type, string name)
        {
            if (type.EnumTable) return new(null, $"'{name}' is an entry of {type.Receiver}");
            var member = Members(type).FirstOrDefault(value => value.Name == name);
            if (member is { Callable: false, Type: { } value }) return Value(value, type.Describe() + "." + name);
            if (member is not null) return new(null, $"{name} is a method, not a value");
            // A collection serves its entries by name.
            if (schema.Receiver(type.Receiver)?.Entries is { } entries && schema.Enum(entries) is { } collection
                && collection.Entries.Any(entry => entry.Name == name) && schema.ReceiverForClass(collection.LuaClass) is { } element)
                return new(new MissionApiType(element.Name), "");
            return new(null, $"{type.Describe()} has no field {name}");
        }

        private Binding Value(MissionApiValue value, string origin)
        {
            if (value.Kind == "handle" && value.Ref is { } handle && schema.Receiver(handle) is not null)
                return new(new MissionApiType(handle), "");
            if (value.Kind == "table" && value.Ref is { } table && schema.Enum(table) is not null)
                return new(new MissionApiType(table) { EnumTable = true }, "");
            return new(null, $"{origin} is a {value.Kind}");
        }

        private IEnumerable<MissionApiMember> Members(MissionApiType type) => type.Receiver == MissionApiType.EventReceiver
            ? type.Events.SelectMany(value => value.Fields) : schema.Receiver(type.Receiver)?.Members ?? [];

        /** Records the call's outcome; a typed call binds the handle it returns. */
        private Binding Call(LuaCall call, Scope scope)
        {
            LuaExpression? receiver = call.Method is not null ? call.Target : (call.Target as LuaMember)?.Target;
            string? name = call.Method ?? (call.Target as LuaMember)?.Name;
            var target = receiver is null ? Expression(call.Target, scope) : Expression(receiver, scope);
            foreach (var value in call.Arguments.Values) Expression(value, scope);
            var result = Classify(call, receiver, name, target, scope);
            owner.callList.Add(result); owner.calls[call] = result;
            if (receiver is LuaCall inner && result.Outcome == MissionApiOutcome.Typed) owner.consumers[inner] = result;
            if (result is not { Outcome: MissionApiOutcome.Typed, Member: { } member, Receiver: { } type })
                return new(null, result.Reason.Length > 0 ? result.Reason : $"{name} returns no handle");
            if (member.Returns.FirstOrDefault() is not { } returned) return new(null, $"{member.Name} returns nothing");
            var handle = Value(returned, type.Describe() + ":" + member.Name);
            // A lookup by one selector names the entry it looked up.
            if (handle.Type is { } found && member.Arguments is [{ Kind: "selector", Ref: { } table }] && call.Arguments.Values is [var selector, ..])
                handle = handle with { Type = found with { Identity = owner.Reference(selector, table) } };
            return handle;
        }

        /** Whether the schema types a call, from its receiver's binding; an untyped call says why. */
        private MissionApiCall Classify(LuaCall call, LuaExpression? receiver, string? name, Binding target, Scope scope)
        {
            if (receiver is null || name is null)
                return call.Target is LuaName plain ? new(call, MissionApiOutcome.NotApi, $"'{plain.Name}' is a plain function")
                    : new(call, MissionApiOutcome.Dynamic, "the called function is computed");
            if (target.Type is null)
            {
                if (!schema.IsMemberName(name, call.Method is not null)) return new(call, MissionApiOutcome.NotApi, $"'{name}' is not in the mission API");
                if (target.Module) return new(call, MissionApiOutcome.NotApi, target.Reason);
                if (!Static(receiver)) return new(call, MissionApiOutcome.Dynamic, "the receiver is computed: " + target.Reason);
                return new(call, MissionApiOutcome.UnknownReceiver, target.Reason);
            }
            var type = target.Type;
            var members = Members(type).Where(value => value.Name == name).ToArray();
            if (members.Length == 0)
            {
                bool complete = type.Receiver == MissionApiType.EventReceiver || type.EnumTable || schema.Receiver(type.Receiver)?.Complete != false;
                return new(call, MissionApiOutcome.UnknownMember, complete ? $"{type.Describe()} has no member {name}"
                    : $"{type.Describe()} also serves keys the schema does not list") { Receiver = type };
            }
            var member = Pick(members, type, call);
            if (!member.Callable) return new(call, MissionApiOutcome.UnknownMember, $"{name} is a field, not a call") { Receiver = type };
            return new(call, MissionApiOutcome.Typed, "") { Receiver = type, Member = member, Names = scope.Typed() };
        }

        /** Forms of one name differ by slot type: the receiver's slot picks one, else the last form that takes every passed key. */
        private static MissionApiMember Pick(IReadOnlyList<MissionApiMember> members, MissionApiType type, LuaCall call)
        {
            if (members.Count == 1) return members[0];
            if (type.SlotType is int slot && members.FirstOrDefault(value => value.Gate?.SlotType == slot) is { } exact) return exact;
            var keys = call.Arguments.Table?.Fields.Select(field => field.Name).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
            return members.LastOrDefault(value => keys.All(key => value.Arguments.Any(argument => argument.Name == key))) ?? members[^1];
        }

        private static bool Static(LuaExpression expression) => expression switch
        {
            LuaName => true, LuaMember member => Static(member.Target), LuaCall call => Static(call.Target), _ => false
        };
    }
}
