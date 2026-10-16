using System.Globalization;
using System.Text.Json;

namespace SunriseStudio.Missions;

/** The slot a slot method or slot argument accepts; Exact means the runtime also matches the slot's catalog shape. */
public sealed record MissionApiGate(int SlotType, IReadOnlyList<string> Checks)
{
    public bool Exact => Checks.Contains("exact_join");
}

/** Slot-gate filters for generated entries. */
public static class MissionApiGates
{
    /** The entries whose slot type the gate accepts; all entries when there is no gate. */
    public static IEnumerable<MissionSdkReference> AcceptedBy(this IEnumerable<MissionSdkReference> entries, MissionApiGate? gate) =>
        gate is null ? entries : entries.Where(entry => entry.SlotType == gate.SlotType);
}

/**
 * One argument, field, return value, list element or table field of the mission Lua API.
 * @param Default An omitted argument's value as Lua source; null when the schema gives none.
 */
public sealed record MissionApiValue(string Kind, string? Name, string? Ref, bool Optional, string? Default, string? DefaultText)
{
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public int? MinCount { get; init; }
    public int? MaxCount { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public MissionApiGate? SlotGate { get; init; }
    /** receiver, same_registry, same_object or argument. */
    public string? Scope { get; init; }
    public MissionApiValue? Element { get; init; }
    public IReadOnlyList<MissionApiValue> Fields { get; init; } = [];
    public string? Note { get; init; }
}

/**
 * One field or method of a receiver, or of an event view.
 * @param Receiver The receiver name; an event member carries its event's callback name.
 * @param Call "named", "positional" or null for a field.
 */
public sealed record MissionApiMember(string Receiver, string Name, string Kind, string? Call, bool Colon, MissionApiGate? Gate,
    IReadOnlyList<MissionApiValue> Arguments, IReadOnlyList<MissionApiValue> Returns, MissionApiValue? Type)
{
    public bool Callable => Call is not null;
    public bool Named => Call == "named";
    /** The call separator: ":" for a method, "." otherwise. */
    public string Separator => Colon ? ":" : ".";
    public string? Effect { get; init; }
    public string? Note { get; init; }
    public bool RefusesUnknownKeys { get; init; }
    /** The event field group; null outside an event view. */
    public string? Group { get; init; }
}

/** An API receiver (a Lua object type) and where its entries come from. */
public sealed record MissionApiReceiver(string Name, string LuaClass, bool Complete, string? Entries)
{
    public IReadOnlyList<MissionApiMember> Members { get; init; } = [];
}

/** A game event: its callback name and payload fields. */
public sealed record MissionApiEvent(string Name, string Callback, IReadOnlyList<MissionApiMember> Fields, string? Note);

/** One entry of an API enum and its fields. */
public sealed record MissionApiEnumEntry(string Name, IReadOnlyList<MissionApiValue> Fields, bool Closed);

/** An API enum and its entries. */
public sealed record MissionApiEnum(string Name, string LuaClass, IReadOnlyList<MissionApiEnumEntry> Entries);

/** The table a mission script returns, and the parameters its callbacks receive. */
public sealed record MissionApiProgram(IReadOnlyList<MissionApiValue> Entries, IReadOnlyList<string> StartParameters,
    IReadOnlyList<string> EventParameters);

/** The mission Lua API as the runtime generates it into sdk/lua/sunrise/mission_api.json. */
public sealed class MissionApiSchema
{
    public const string SchemaName = "sunrise-mission-lua-api-v1";
    // The generated file is about 220 KiB; this only bounds a damaged file.
    private const long MaximumBytes = 16L * 1024 * 1024;
    private readonly Dictionary<string, MissionApiReceiver> receivers;
    private readonly Dictionary<string, MissionApiEvent> callbacks;
    private readonly HashSet<string> memberNames;

    public IReadOnlyList<MissionApiReceiver> Receivers { get; }
    public IReadOnlyList<MissionApiMember> Members { get; }
    public IReadOnlyList<MissionApiEvent> Events { get; }
    public MissionApiProgram Program { get; }
    public IReadOnlyList<MissionApiEnum> Enums { get; }
    /** Every generated mission table a value refers to, such as mission.Slot. */
    public IReadOnlyList<string> MissionTables { get; }

    /** Groups members under their receivers and indexes callbacks, callable names and mission tables. */
    private MissionApiSchema(IReadOnlyList<MissionApiReceiver> receiverList, IReadOnlyList<MissionApiMember> members,
        IReadOnlyList<MissionApiEvent> events, MissionApiProgram program, IReadOnlyList<MissionApiEnum> enums)
    {
        Members = members; Events = events; Program = program; Enums = enums;
        Receivers = receiverList.Select(receiver => receiver with { Members = members.Where(member => member.Receiver == receiver.Name).ToArray() })
            .ToArray();
        receivers = Receivers.ToDictionary(receiver => receiver.Name, StringComparer.Ordinal);
        callbacks = events.ToDictionary(value => value.Callback, StringComparer.Ordinal);
        memberNames = members.Concat(events.SelectMany(value => value.Fields)).Where(member => member.Callable)
            .Select(member => member.Separator + member.Name).ToHashSet(StringComparer.Ordinal);
        MissionTables = AllValues().Select(value => value.Ref).OfType<string>()
            .Where(name => name.StartsWith("mission.", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .ToArray();
    }

    /** The schema file of an installed generated SDK; it may not exist. */
    public static string InstalledPath(string sdkLuaRoot) => Path.Combine(sdkLuaRoot, "sunrise", "mission_api.json");

    /** Loads mission_api.json; a file above the size limit or a malformed one throws. */
    public static MissionApiSchema Load(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumBytes) throw new InvalidDataException("mission_api.json is too large.");
        return Parse(File.ReadAllText(path));
    }

    /** Reads the schema; a wrong schema name, a missing key, or an unknown value kind, call form or receiver throws. */
    public static MissionApiSchema Parse(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException error) { throw new InvalidDataException("mission_api.json is not valid JSON: " + error.Message, error); }
        using (document)
        {
            try
            {
                return Read(document.RootElement);
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentException)
            {
                throw new InvalidDataException("mission_api.json has the wrong shape: " + error.Message, error);
            }
        }
    }

    /** Builds the schema from the root object; every member must name a known receiver. */
    private static MissionApiSchema Read(JsonElement root)
    {
        string schema = root.TryGetProperty("schema", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : "";
        if (schema != SchemaName)
        {
            throw new InvalidDataException(
                $"mission_api.json has schema \"{schema}\"; Studio reads \"{SchemaName}\". Update Studio or regenerate the SDK.");
        }
        var reader = new Reader(Strings(Required(root, "value_kinds")));
        var receiverList = Required(root, "receivers").EnumerateArray().Select(value => new MissionApiReceiver(
            Text(value, "name"), Text(value, "lua_class"), Bool(value, "complete"), Optional(value, "entries"))).ToArray();
        var known = receiverList.Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        var members = Required(root, "members").EnumerateArray().Select(value => reader.Member(value, Text(value, "receiver"))).ToArray();
        foreach (var member in members)
        {
            if (!known.Contains(member.Receiver))
                throw new InvalidDataException($"mission_api.json: member {member.Name} names unknown receiver {member.Receiver}.");
        }
        var events = Required(root, "events").EnumerateArray().Select(reader.Event).ToArray();
        var programValue = Required(root, "program");
        var program = new MissionApiProgram(Required(programValue, "entries").EnumerateArray().Select(reader.Value).ToArray(),
            Strings(Required(programValue, "start_parameters")), Strings(Required(programValue, "event_parameters")));
        var enums = Required(root, "enums").EnumerateArray().Select(reader.EnumType).ToArray();
        return new(receiverList, members, events, program, enums);
    }

    /** Receiver by name, or null. */
    public MissionApiReceiver? Receiver(string name) => receivers.GetValueOrDefault(name);
    /** Receiver by its Lua class, or null. */
    public MissionApiReceiver? ReceiverForClass(string luaClass) => Receivers.FirstOrDefault(value => value.LuaClass == luaClass);
    /** Event by callback name, or null. */
    public MissionApiEvent? EventForCallback(string callback) => callbacks.GetValueOrDefault(callback);
    /** Enum by name, or null. */
    public MissionApiEnum? Enum(string name) => Enums.FirstOrDefault(value => value.Name == name);
    /** Entry names of an enum; empty when it is unknown. */
    public IReadOnlyList<string> EntryNames(string name) => Enum(name)?.Entries.Select(entry => entry.Name).ToArray() ?? [];

    /** The receiver whose entries are this enum, such as the collection a script reads it from; null when none. */
    public MissionApiReceiver? CollectionFor(string name) => Receivers.FirstOrDefault(receiver => receiver.Entries == name);

    /** The member that looks a handle up by one selector, such as context:slot for a slot handle; null when none does. */
    public MissionApiMember? LookupFor(string handle) => Members.FirstOrDefault(member =>
        member is { Kind: "lookup", Call: "positional", Arguments: [{ Kind: "selector", Ref: not null }] }
        && member.Returns is [{ Kind: "handle" } returned] && returned.Ref == handle);

    /** The field of a tagged table, such as an atom, that names its entry: the field whose ref is the enum itself. */
    public string KindField(string name) => Enum(name)?.Entries
        .SelectMany(entry => entry.Fields).FirstOrDefault(field => field.Ref == name)?.Name
        ?? throw new InvalidDataException($"mission_api.json: {name} entries have no field that names their kind.");

    /** True when some receiver or event view has a callable member of this name, called with ':' or with '.'. */
    public bool IsMemberName(string name, bool colon) => memberNames.Contains((colon ? ":" : ".") + name);

    /** Callback keys a script's returned table may hold, in schema order. */
    public IEnumerable<string> CallbackNames() => Program.Entries.Where(value => value.Kind == "function" && value.Name is not null)
        .Select(value => value.Name!).Concat(Events.Select(value => value.Callback));

    /** Every value of the schema, nested elements and fields included. */
    private IEnumerable<MissionApiValue> AllValues()
    {
        var stack = new Stack<MissionApiValue>(Members.Concat(Events.SelectMany(value => value.Fields))
            .SelectMany(member => member.Arguments.Concat(member.Returns).Concat(member.Type is null ? [] : [member.Type]))
            .Concat(Program.Entries).Concat(Enums.SelectMany(value => value.Entries).SelectMany(entry => entry.Fields)));
        while (stack.Count > 0)
        {
            var value = stack.Pop();
            yield return value;
            if (value.Element is { } element) stack.Push(element);
            foreach (var field in value.Fields) stack.Push(field);
        }
    }

    /** Checks every kind against value_kinds while it reads. */
    private sealed class Reader(IReadOnlyList<string> kinds)
    {
        private readonly HashSet<string> known = kinds.ToHashSet(StringComparer.Ordinal);

        /** One member; its call form must be named, positional or absent. */
        public MissionApiMember Member(JsonElement value, string receiver)
        {
            string name = Text(value, "name"), kind = Text(value, "kind");
            string? call = Optional(value, "call");
            if (call is not (null or "named" or "positional")) throw new InvalidDataException($"mission_api.json: {name} has call form {call}.");
            return new(receiver, name, kind, call, Flag(value, "colon"),
                value.TryGetProperty("gate", out var gate) ? Gate(gate) : null,
                Values(value, "arguments"), Values(value, "returns"),
                value.TryGetProperty("type", out var type) ? Value(type) : null)
            {
                Effect = Optional(value, "effect"), Note = Optional(value, "note"), Group = Optional(value, "group"),
                RefusesUnknownKeys = Flag(value, "refuses_unknown_keys")
            };
        }

        /** One value with its nested element and fields; its kind must be in value_kinds. */
        public MissionApiValue Value(JsonElement value)
        {
            string kind = Text(value, "kind");
            if (!known.Contains(kind)) throw new InvalidDataException($"mission_api.json: value kind {kind} is not in value_kinds.");
            return new(kind, Optional(value, "name"), Optional(value, "ref"), Flag(value, "optional"),
                value.TryGetProperty("default", out var initial) ? Lua(initial) : null, Optional(value, "default_text"))
            {
                Minimum = Number(value, "minimum"), Maximum = Number(value, "maximum"),
                MinCount = (int?)Number(value, "min_count"), MaxCount = (int?)Number(value, "max_count"),
                MinLength = (int?)Number(value, "min_length"), MaxLength = (int?)Number(value, "max_length"),
                SlotGate = value.TryGetProperty("slot_gate", out var gate) ? Gate(gate) : null, Scope = Optional(value, "scope"),
                Element = value.TryGetProperty("element", out var element) ? Value(element) : null,
                Fields = Values(value, "fields"),
                Note = Optional(value, "note")
            };
        }

        /** Reads one event from schema JSON. */
        public MissionApiEvent Event(JsonElement value)
        {
            string callback = Text(value, "callback");
            return new(Text(value, "name"), callback,
                Required(value, "fields").EnumerateArray().Select(field => Member(field, callback)).ToArray(), Optional(value, "note"));
        }

        /** Reads one enum from schema JSON. */
        public MissionApiEnum EnumType(JsonElement value) =>
            new(Text(value, "name"), Text(value, "lua_class"), Required(value, "entries").EnumerateArray().Select(EnumEntry).ToArray());

        private MissionApiEnumEntry EnumEntry(JsonElement entry) => new(Text(entry, "name"), Values(entry, "fields"), Flag(entry, "closed"));

        private IReadOnlyList<MissionApiValue> Values(JsonElement value, string name) =>
            value.TryGetProperty(name, out var found) ? found.EnumerateArray().Select(Value).ToArray() : [];

        private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var found) && found.GetBoolean();

        private static MissionApiGate Gate(JsonElement value) => new(Int(value, "slot_type"), Strings(Required(value, "checks")));
    }

    private static JsonElement Required(JsonElement value, string name) => value.TryGetProperty(name, out var found)
        ? found : throw new InvalidDataException($"mission_api.json: a required key \"{name}\" is missing.");

    private static string Text(JsonElement value, string name) => Required(value, name) is { ValueKind: JsonValueKind.String } text
        ? text.GetString()! : throw new InvalidDataException($"mission_api.json: \"{name}\" must be a string.");

    private static string? Optional(JsonElement value, string name) => value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String
        ? found.GetString() : null;

    private static bool Bool(JsonElement value, string name) => Required(value, name).ValueKind == JsonValueKind.True;
    private static int Int(JsonElement value, string name) => Required(value, name).GetInt32();

    private static double? Number(JsonElement value, string name) => value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.Number
        ? found.GetDouble() : null;

    private static IReadOnlyList<string> Strings(JsonElement value) => value.EnumerateArray().Select(item => item.GetString()
        ?? throw new InvalidDataException("mission_api.json: a list holds a non-string.")).ToArray();

    /** A JSON scalar as Lua source. */
    private static string? Lua(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "true", JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.String => LuaScript.Quote(value.GetString()!),
        _ => null
    };

    /** A number as the shortest Lua literal; integers never gain a decimal point. */
    public static string NumberText(double value) => value == Math.Floor(value) && Math.Abs(value) < 9.2e18
        ? ((long)value).ToString(CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture);
}
