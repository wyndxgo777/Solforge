/**
 * File: The server mission-library calls (lib.mission_lib) Solforge offers as graph nodes, template lines and MCP
 * tools: fade/area transitions with teleport, mid-mission cutscenes, boss shield/pylon/invulnerability phases and
 * ordered endings. One table here feeds the node types (NodeLibrary.Mission), the compiler (MissionGraphCompiler),
 * the script graph's Lua parse (SunriseStudio.Missions) and the MCP tools, so a rename is one edit.
 */
using System.Globalization;
using System.Text;

namespace Solforge.Graphs;

/** How a library parameter is written in Lua. Slot and Lua are raw expressions (Slot.X, mission.states.Y, {...}). */
public enum MissionLibValue { Integer, Number, Bool, Text, Choice, Slot, Lua }

/** One named field of a library call's option table. An empty value leaves the field out (the library default). */
public sealed record MissionLibParam(string Name, string Label, MissionLibValue Kind, string Default = "", string Help = "",
    bool Required = false, IReadOnlyList<string>? Choices = null);

/**
 * A library call: module.Function(context, {fields}). Pending is true while the name is Solforge's placeholder for
 * a call the server branch mission-lib-transitions has not landed yet.
 */
public sealed record MissionLibCall(string Function, string Title, string Feature, string Description,
    IReadOnlyList<MissionLibParam> Parameters, bool Pending = true)
{
    /** Graph node type id for this call. */
    public string NodeType => MissionLibrary.NodePrefix + Function;
    public MissionLibParam? Parameter(string name) => Parameters.FirstOrDefault(parameter => parameter.Name == name);
}

/** A parsed call: its catalog entry and the field values as written (raw Lua text; strings unquoted). */
public sealed record MissionLibMatch(MissionLibCall Call, IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Unknown);

/** The mission-library catalog, Lua rendering, parsing and insertion. */
public static class MissionLibrary
{
    public const string Module = "lib.mission_lib";
    /** The local name generated scripts bind the module to; "lib" (the libraries' own alias) also parses. */
    public const string Alias = "mission_lib";
    public const string NodePrefix = "lib.";
    public const string Require = "local " + Alias + " = require(\"" + Module + "\")";

    // TODO(mission-lib-transitions): every Function name below is a placeholder until the server branch
    // mission-lib-transitions (sol-server-0.6) lands; rename here to match its lib.mission_lib exports, then set
    // Pending = false. Parameters follow the existing libs: slots are Slot.X, states mission.states.X, times in ms.
    public static IReadOnlyList<MissionLibCall> Calls { get; } =
    [
        new("transition", "Fade Transition", "transition",
            "Fades the fireteam to black, optionally teleports them to a spawn slot and selects a new activity state (area change) while black, then fades back in.",
            [
                new("fade_out_ms", "Fade out (ms)", MissionLibValue.Integer, "500"),
                new("hold_ms", "Hold black (ms)", MissionLibValue.Integer, "1000"),
                new("fade_in_ms", "Fade in (ms)", MissionLibValue.Integer, "500"),
                new("teleport", "Teleport to slot", MissionLibValue.Slot, "", "Spawn point slot the fireteam moves to while black, e.g. Slot.SPAWN_2"),
                new("state", "Area state", MissionLibValue.Lua, "", "Activity state selected while black, e.g. mission.states.AREA_2"),
                new("signal", "Signal when done", MissionLibValue.Text, "", "Signal the library raises after the fade-in"),
            ]),
        new("play_cutscene", "Play Cutscene", "cutscene",
            "Plays a mid-mission cinematic on its slot (set_cinematic_active), with an optional fade around it; the mission waits until it ends or is skipped.",
            [
                new("cinematic", "Cinematic slot", MissionLibValue.Slot, "", "Slot that owns the cinematic, e.g. Slot.CINEMATIC_1", true),
                new("scene", "Scene", MissionLibValue.Lua, "", "Optional authored scene to activate with it, e.g. mission.Scene.X"),
                new("skippable", "Skippable", MissionLibValue.Bool, "true"),
                new("fade", "Fade in and out", MissionLibValue.Bool, "true"),
                new("signal", "Signal when done", MissionLibValue.Text, "cutscene_done", "Signal raised when the cutscene ends or is skipped"),
            ]),
        new("boss_phase", "Boss Phase", "boss",
            "Starts a boss damage phase: at a health fraction the boss turns invulnerable (and shielded) until its pylons are destroyed, optionally placing add squads.",
            [
                new("boss", "Boss slot", MissionLibValue.Slot, "", "The boss combatant slot, e.g. Slot.SQ_BOSS", true),
                new("phase", "Phase", MissionLibValue.Integer, "1"),
                new("at_health", "At health", MissionLibValue.Number, "0.5", "Health fraction (0-1) that starts the phase"),
                new("invulnerable", "Invulnerable", MissionLibValue.Bool, "true"),
                new("shield", "Shield", MissionLibValue.Choice, "immune", "Shield element shown while invulnerable",
                    Choices: ["none", "immune", "arc", "solar", "void"]),
                new("pylons", "Pylons", MissionLibValue.Lua, "", "Slots to destroy before damage resumes, e.g. {Slot.PYLON_1, Slot.PYLON_2}"),
                new("adds", "Add squads", MissionLibValue.Lua, "", "Squads placed when the phase starts, e.g. {Squad.ADDS_1}"),
                new("signal", "Signal when broken", MissionLibValue.Text, "boss_vulnerable", "Signal raised when the shield breaks"),
            ]),
        new("set_invulnerable", "Set Invulnerable", "boss",
            "Turns invulnerability on or off for a combatant or object slot.",
            [
                new("target", "Target slot", MissionLibValue.Slot, "", "Combatant or object slot", true),
                new("invulnerable", "Invulnerable", MissionLibValue.Bool, "true"),
            ]),
        new("break_shield", "Break Boss Shield", "boss",
            "Ends a boss phase's invulnerability at once (as if every pylon fell); for shields broken by your own script.",
            [
                new("boss", "Boss slot", MissionLibValue.Slot, "", "The boss combatant slot", true),
                new("signal", "Signal", MissionLibValue.Text, "boss_vulnerable"),
            ]),
        new("ending", "Ordered Ending", "ending",
            "Ends the mission in a fixed order: final cutscene, fade, rewards, then completion; each step waits for the one before.",
            [
                new("cutscene", "Final cinematic slot", MissionLibValue.Slot, "", "Optional ending cinematic"),
                new("fade_ms", "Fade (ms)", MissionLibValue.Integer, "1000"),
                new("experience", "Experience", MissionLibValue.Integer, "0"),
                new("loot_rolls", "Loot rolls", MissionLibValue.Integer, "0"),
                new("loot_chance", "Loot chance", MissionLibValue.Number, "1"),
                new("complete", "Complete mission", MissionLibValue.Bool, "true"),
                new("order", "Order", MissionLibValue.Text, "cutscene,fade,rewards,complete", "Comma list of cutscene, fade, rewards, complete"),
            ]),
    ];

    /** The call for a function name, or null. */
    public static MissionLibCall? Find(string function) => Calls.FirstOrDefault(call => call.Function == function);

    /** The call a graph node type stands for, or null. */
    public static MissionLibCall? ForNode(string nodeType) =>
        nodeType.StartsWith(NodePrefix, StringComparison.Ordinal) ? Find(nodeType[NodePrefix.Length..]) : null;

    /** Field values with defaults filled; keys not in the call are dropped. */
    public static Dictionary<string, string> WithDefaults(MissionLibCall call, IReadOnlyDictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in call.Parameters)
            result[parameter.Name] = values is not null && values.TryGetValue(parameter.Name, out var value) ? value : parameter.Default;
        return result;
    }

    /** Problems with the values: required fields left empty, numbers that do not parse, choices not offered. */
    public static IReadOnlyList<string> Check(MissionLibCall call, IReadOnlyDictionary<string, string>? values)
    {
        var problems = new List<string>();
        foreach (var (name, raw) in WithDefaults(call, values))
        {
            var parameter = call.Parameter(name)!;
            string value = raw.Trim();
            if (value.Length == 0) { if (parameter.Required) problems.Add($"{call.Title}: {parameter.Label} is required."); continue; }
            bool ok = parameter.Kind switch
            {
                MissionLibValue.Integer => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                MissionLibValue.Number => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                MissionLibValue.Bool => value is "true" or "false",
                MissionLibValue.Choice => parameter.Choices?.Contains(value) != false,
                _ => !value.Contains('\n') || parameter.Kind == MissionLibValue.Lua
            };
            if (!ok) problems.Add($"{call.Title}: {parameter.Label} cannot be '{value}'.");
        }
        return problems;
    }

    /** One Lua statement: mission_lib.function(context, {field = value, ...}); empty fields are left out. */
    public static string Render(MissionLibCall call, IReadOnlyDictionary<string, string>? values, string alias = Alias)
    {
        var fields = new List<string>();
        foreach (var (name, raw) in WithDefaults(call, values))
        {
            string value = raw.Trim();
            if (value.Length == 0) continue;
            var kind = call.Parameter(name)!.Kind;
            fields.Add(name + " = " + (kind is MissionLibValue.Text or MissionLibValue.Choice ? Quote(value) : value));
        }
        return $"{alias}.{call.Function}(context, {{{string.Join(", ", fields)}}})";
    }

    /**
     * Matches a call's callee text (mission_lib.x or lib.x) and argument text (inside the parentheses) to a library
     * call; null when the callee is not one. Values are raw Lua with string quotes removed.
     */
    public static MissionLibMatch? Match(string callee, string arguments)
    {
        string[] path = callee.Replace(" ", "").Split('.');
        if (path.Length != 2 || path[0] is not (Alias or "lib") || Find(path[1]) is not { } call) return null;
        var args = Split(arguments.Trim(), ',');
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var unknown = new List<string>();
        if (args.Count >= 2 && args[1].StartsWith('{') && args[1].EndsWith('}'))
            foreach (string field in Split(args[1][1..^1], ',', ';'))
            {
                int equals = TopLevelEquals(field);
                if (equals < 0) { unknown.Add(field); continue; }
                string name = field[..equals].Trim(), value = field[(equals + 1)..].Trim();
                if (call.Parameter(name) is null) { unknown.Add(name); continue; }
                values[name] = value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0] ? Unquote(value) : value;
            }
        return new(call, values, unknown);
    }

    /**
     * Inserts a call as the first statement of a function in a script: "name" matches a `name = function(` field, a
     * `function name(` or a `local function name(`. Adds the require line after the last top-level require (or at
     * the top) when the script has none. Throws when the function is not found.
     */
    public static string InsertIntoLua(string source, string function, string statement)
    {
        string newline = source.Contains("\r\n") ? "\r\n" : "\n";
        var lines = source.Replace("\r\n", "\n").Split('\n').ToList();
        int at = lines.FindIndex(line =>
        {
            string text = line.Trim();
            return text.StartsWith(function + " = function(", StringComparison.Ordinal) || text.StartsWith(function + "=function(", StringComparison.Ordinal)
                || text.StartsWith("function " + function + "(", StringComparison.Ordinal) || text.StartsWith("local function " + function + "(", StringComparison.Ordinal);
        });
        if (at < 0) throw new InvalidDataException($"No function or callback named {function} in the script.");
        string header = lines[at];
        string indent = header[..(header.Length - header.TrimStart().Length)] + "    ";
        // A one-line `f = function(...) end` opens up so the call goes inside it.
        int end = header.LastIndexOf(" end", StringComparison.Ordinal);
        if (end > header.IndexOf("function(", StringComparison.Ordinal) && header.TrimEnd().TrimEnd(',').EndsWith("end", StringComparison.Ordinal))
        {
            string tail = header[(end + 1)..];
            lines[at] = header[..end];
            lines.Insert(at + 1, header[..(header.Length - header.TrimStart().Length)] + tail.Trim());
        }
        lines.Insert(at + 1, indent + statement);
        if (!lines.Any(line => line.Contains("require(\"" + Module + "\")", StringComparison.Ordinal)))
        {
            int last = lines.FindLastIndex(line => line.StartsWith("local ", StringComparison.Ordinal) && line.Contains("require(", StringComparison.Ordinal));
            lines.Insert(last + 1, Require);
        }
        return string.Join(newline, lines);
    }

    /** Lua string literal with escapes. */
    public static string Quote(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (char c in text)
            builder.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => c.ToString() });
        return builder.Append('"').ToString();
    }

    private static string Unquote(string literal)
    {
        var builder = new StringBuilder();
        for (int i = 1; i < literal.Length - 1; i++)
        {
            char c = literal[i];
            if (c == '\\' && i + 1 < literal.Length - 1)
            {
                char next = literal[++i];
                builder.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => next });
            }
            else builder.Append(c);
        }
        return builder.ToString();
    }

    /** Splits at separators outside strings and brackets; empty trailing parts are dropped. */
    private static List<string> Split(string text, params char[] separators)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0') { if (c == '\\') i++; else if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
            else if (depth == 0 && separators.Contains(c)) { parts.Add(text[start..i].Trim()); start = i + 1; }
        }
        parts.Add(text[start..].Trim());
        while (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
        return parts;
    }

    /** Index of the field's `=` outside strings and brackets (not ==, ~=, <=, >=), or -1. */
    private static int TopLevelEquals(string field)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i < field.Length; i++)
        {
            char c = field[i];
            if (quote != '\0') { if (c == '\\') i++; else if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
            else if (depth == 0 && c == '=' && (i + 1 >= field.Length || field[i + 1] != '=') && (i == 0 || field[i - 1] is not ('=' or '~' or '<' or '>')))
                return i;
        }
        return -1;
    }
}
