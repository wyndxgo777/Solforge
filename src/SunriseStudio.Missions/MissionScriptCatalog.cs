/**
 * File: Catalog of mission scripts and the generated SDK entries (slots, scenes, directives) they can
 * reference.
 */
using System.Text;

namespace SunriseStudio.Missions;

/** A mission script file: name, relative and full path, and the SDK module it targets. */
public sealed record MissionScriptEntry(string Name, string RelativePath, string FullPath, string? SdkModule);
/** A generated SDK entry a script can reference. */
public sealed record MissionSdkReference(string Expression, string Category, string Name, string? Identity)
{
    public int? SlotType { get; init; }
    public string Description { get; init; } = "";
    public string Sensor { get; init; } = "";
    /** Owning scene of a scene input or output; the same key names it in mission.Scene and mission.Slot. */
    public string Scene { get; init; } = "";
    /** The generated table the expression indexes, such as mission.Slot. */
    public string Table => Expression.Split('.') is { Length: > 2 } parts ? parts[0] + "." + parts[1] : "";
    /** The slot key an entry belongs to: the scene of mission.SceneInput.SCENE.KEY, or a directive's sensor; empty when unowned. */
    public string Owner
    {
        get => owner ?? (Expression.Split('.') is { Length: > 3 } parts ? parts[2] : "");
        init => owner = value;
    }
    private readonly string? owner;
    /** The entry's own key, the last part of the expression. */
    public string Key => Expression[(Expression.LastIndexOf('.') + 1)..];
}

/** Installed Lua identities are read as data; modules and callbacks are never executed. */
public sealed partial class MissionScriptCatalog
{
    public string GameRoot { get; }
    public string ScriptsRoot { get; }
    public string SdkRoot { get; }
    public IReadOnlyList<MissionScriptEntry> Scripts => scripts;
    private readonly List<MissionScriptEntry> scripts = [];
    public IReadOnlyList<MissionSdkActivity> Activities { get; }
    private readonly IReadOnlyList<MissionSdkActivity> missionModules;
    /** The missions.lua rows (key and SDK module) a new script can require, one per installed mission. */
    public IReadOnlyList<MissionSdkActivity> Missions => missionModules;
    private (DateTime Stamp, MissionApiSchema? Schema, string Status)? api;
    /** Rows per module file and table list, newest last; a regenerated module misses the cache. */
    private readonly List<(string Key, string Stamp, MissionSdkReference[] Rows)> referenceCache = [];
    // The open script and the New script pick; more would keep every module a session opened.
    private const int CachedModules = 2;
    // Why an SDK file read short, by module or index file name.
    private readonly Dictionary<string, string> sdkProblems = new(StringComparer.Ordinal);

    /** The generated mission API; null when the installed SDK has none or it does not read. */
    public MissionApiSchema? Api => CurrentApi().Schema;
    /** Why Api is null; empty when it loaded. */
    public string ApiStatus => CurrentApi().Status;
    /** The schema and its status from one read, so the two always agree. */
    public (MissionApiSchema? Schema, string Status) ApiState
    {
        get { var (_, schema, status) = CurrentApi(); return (schema, status); }
    }

    /** Re-read when the file's write time changes, so a first game boot shows up without a restart. */
    private (DateTime Stamp, MissionApiSchema? Schema, string Status) CurrentApi()
    {
        string path = MissionApiSchema.InstalledPath(SdkRoot);
        // A missing file stamps as 1601, so its result is cached too.
        var stamp = File.GetLastWriteTimeUtc(path);
        if (api is { } cached && cached.Stamp == stamp) return cached;
        var (schema, status) = LoadApi(path);
        api = (stamp, schema, status);
        return api.Value;
    }

    private static (MissionApiSchema?, string) LoadApi(string path)
    {
        if (!File.Exists(path)) return (null, "mission_api.json is missing. Start the game once, then reload.");
        try { return (MissionApiSchema.Load(path), ""); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return (null, error.Message); }
    }

    /** Why an SDK file read short; empty when it read cleanly. The module indexes come before the module. */
    public string SdkProblem(string? module)
    {
        foreach (string index in (string[])["missions.lua", "activities.lua"])
            if (sdkProblems.GetValueOrDefault(index) is { } problem) return problem;
        return module is not null && sdkProblems.GetValueOrDefault(module) is { } own ? own : "";
    }

    /** Keeps the first syntax error of an SDK file, or clears it once the file reads cleanly. */
    private void NoteSdkRead(string name, LuaScript lua)
    {
        if (lua.Errors.Count == 0) sdkProblems.Remove(name);
        else sdkProblems[name] = $"SDK file {name} is damaged ({lua.Errors[0]}). Start the game to rebuild it.";
    }

    /** @return The message it noted. */
    private string NoteSdkMissing(string name) => sdkProblems[name] = $"SDK file {name} is missing. Start the game to rebuild it.";

    /**
     * Reads an SDK file; a file that does not read is noted as a problem instead of thrown.
     * @return The text; null when it did not read.
     */
    private string? TryReadSdk(string name, string path)
    {
        try { return ReadSdkSource(path); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException
            or ArgumentException)
        {
            sdkProblems[name] = $"SDK file {name} did not read ({error.Message}). Start the game to rebuild it.";
            return null;
        }
    }

    /** Reads the SDK indexes and lists each installed stem/stem.lua; lib and linked folders are skipped. */
    public MissionScriptCatalog(string gameRoot)
    {
        GameRoot = Path.GetFullPath(gameRoot);
        ScriptsRoot = Path.Combine(GameRoot, "bin", "x64", "Sunrise", "scripts");
        SdkRoot = Path.Combine(GameRoot, "bin", "x64", "Sunrise", "sdk", "lua");
        missionModules = ReadModuleIndex("missions.lua");
        Activities = ReadModuleIndex("activities.lua");
        if (Directory.Exists(ScriptsRoot))
            foreach (string directory in Directory.EnumerateDirectories(ScriptsRoot).Order(StringComparer.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                string name = Path.GetFileName(directory), path = Path.Combine(directory, name + ".lua");
                if (name == "lib" || !File.Exists(path)) continue;
                scripts.Add(Describe(name, name + "/" + name + ".lua", path));
            }
    }

    /** A script file's entry; a file that does not read stays listed with no module, and opening it shows the error. */
    public MissionScriptEntry Describe(string name, string relativePath, string path)
    {
        try { return DescribeSource(name, relativePath, ReadSource(path)) with { FullPath = path }; }
        catch (Exception error) when (error is InvalidDataException or IOException
            or UnauthorizedAccessException or ArgumentException) { return new(name, relativePath, path, null); }
    }

    /** Lists a newly saved installed script in name order; the caches stay. */
    public void AddInstalled(MissionScriptEntry entry)
    {
        scripts.RemoveAll(value => value.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
        int at = scripts.FindIndex(value => StringComparer.OrdinalIgnoreCase.Compare(value.Name, entry.Name) > 0);
        scripts.Insert(at < 0 ? scripts.Count : at, entry);
    }

    /**
     * The generated SDK entries a script can reference; cached per module file and table list.
     * @param tables More generated tables to list, such as the mission tables the API schema names.
     */
    public IReadOnlyList<MissionSdkReference> ReadReferences(MissionScriptEntry script, IEnumerable<string>? tables = null)
    {
        string? module = script.SdkModule;
        if (module is null) return [];
        if (module.Split('.').Any(part => !LuaScript.IsIdentifier(part)))
            throw new InvalidDataException("SDK module name is invalid.");
        string missionPath = Path.Combine(SdkRoot, module.Replace('.', Path.DirectorySeparatorChar) + ".lua");
        if (!File.Exists(missionPath)) { NoteSdkMissing(module); return []; }
        string[] extra = (tables ?? []).Where(table => table.StartsWith("mission.", StringComparison.Ordinal)).Distinct().ToArray();
        var file = new FileInfo(missionPath);
        string key = missionPath + "|" + string.Join(",", extra), stamp = file.LastWriteTimeUtc.Ticks + ":" + file.Length;
        int hit = referenceCache.FindIndex(entry => entry.Key == key);
        if (hit >= 0)
        {
            var cached = referenceCache[hit];
            referenceCache.RemoveAt(hit);
            if (cached.Stamp == stamp) { referenceCache.Add(cached); return cached.Rows; }
        }
        // A file that did not read is not cached, so the next open tries again.
        if (ReadModule(module, missionPath, extra) is not { } rows) return [];
        referenceCache.Add((key, stamp, rows));
        if (referenceCache.Count > CachedModules) referenceCache.RemoveAt(0);
        return rows;
    }

    /** Every reference row of one SDK module; a damaged module still gives the rows that read, and an unreadable one null. */
    private MissionSdkReference[]? ReadModule(string module, string path, string[] tables)
    {
        if (TryReadSdk(module, path) is not { } source) return null;
        var lua = LuaScript.Read(source);
        NoteSdkRead(module, lua);
        if (FirstTable(lua) is not { } mission) return [];
        var assignments = ReadAssignments(lua);
        var references = new List<MissionSdkReference>();
        foreach (var category in mission.Fields.Where(field => field.Table is not null && field.Name is not null
            && field.Name is not ("slots" or "squads")))
            Visit(category.Table!, "mission." + category.Name, category.Name!, 0, source, references);
        foreach (string symbol in tables.Select(table => table[8..]).Where(symbol => !TypedTables.Contains(symbol)))
            if (assignments.TryGetValue(symbol, out var extra)) Rows(extra, "mission." + symbol, 0, source, references);
        var slots = new SlotLookup(mission, assignments.GetValueOrDefault("Slot"));
        foreach (string symbol in TypedTables)
        {
            if (!assignments.TryGetValue(symbol, out var table)) continue;
            if (symbol is "SceneInput" or "SceneOutput") ReadSceneKeys(symbol, table, source, references);
            else if (symbol == "DialogueCue") ReadDialogueCues(table, source, references);
            else ReadTypedRows(symbol, table, slots, source, references);
        }
        return references.ToArray();
    }

    /** Tables with their own row shape, in the order their rows are listed. */
    private static readonly string[] TypedTables = ["Slot", "Squad", "Directive", "DialogueCue", "Scene", "SceneInput", "SceneOutput"];

    /** Each `mission.X = { ... }` table by X; the first assignment wins. */
    private static Dictionary<string, LuaTable> ReadAssignments(LuaScript lua)
    {
        var assignments = new Dictionary<string, LuaTable>(StringComparer.Ordinal);
        foreach (var node in lua.Descendants())
            if (node is LuaAssignmentStatement { Targets: [LuaMember { Target: LuaName { Name: "mission" }, Name: var symbol }],
                Values: [LuaTable table, ..] }) assignments.TryAdd(symbol, table);
        return assignments;
    }

    /** The slot facts typed rows need, built once per module. */
    private sealed class SlotLookup
    {
        /** mission.slots entries by their id. */
        public readonly Dictionary<string, LuaTable> InfoById = new(StringComparer.Ordinal);
        /** mission.slots entries by key; a scene's slot has the scene's key. */
        public readonly Dictionary<string, LuaField> ByKey = new(StringComparer.Ordinal);
        /** mission.Slot keys by value; a directive names its sensor by that value. */
        public readonly Dictionary<string, string> KeyById = new(StringComparer.Ordinal);

        public SlotLookup(LuaTable mission, LuaTable? slotTable)
        {
            foreach (var field in mission.Field("slots")?.Table?.Fields ?? [])
            {
                if (field.Name is not null) ByKey.TryAdd(field.Name, field);
                if (field.Table?.Field("id")?.StringValue is { } id) InfoById.TryAdd(id, field.Table);
            }
            foreach (var field in slotTable?.Fields ?? [])
                if (field.Name is not null && field.StringValue is not null) KeyById.TryAdd(field.StringValue, field.Name);
        }
    }

    /** mission.SceneInput and mission.SceneOutput: one row per key, owned by its scene. */
    private static void ReadSceneKeys(string symbol, LuaTable table, string source, List<MissionSdkReference> references)
    {
        foreach (var scene in table.Fields.Where(field => field.Name is not null && field.Table is not null))
            foreach (var gate in scene.Table!.Fields.Where(field => field.Name is not null))
                if (ReadKey(gate.Value.Read(source)) is { } key)
                    references.Add(new($"mission.{symbol}.{scene.Name}.{gate.Name}",
                        symbol == "SceneInput" ? "scene_inputs" : "scene_outputs", gate.Name!.Replace('_', ' '), $"0x{key:X8}")
                    { Scene = scene.Name! });
    }

    /** mission.DialogueCue: one row per cue, owned by its sensor slot. */
    private static void ReadDialogueCues(LuaTable table, string source, List<MissionSdkReference> references)
    {
        foreach (var sensor in table.Fields.Where(field => field.Name is not null && field.Table is not null))
            foreach (var cue in sensor.Table!.Fields.Where(field => field.Name is not null))
                if (int.TryParse(cue.Value.Read(source), out int number))
                    references.Add(new($"mission.DialogueCue.{sensor.Name}.{cue.Name}", "dialogue",
                        $"{sensor.Name!.Replace('_', ' ')} / Cue {number}", number.ToString())
                    { Sensor = "mission.Slot." + sensor.Name });
    }

    /** mission.Slot, Squad, Directive and Scene: one row per id, titled from the slot facts. */
    private static void ReadTypedRows(string symbol, LuaTable table, SlotLookup slots, string source,
        List<MissionSdkReference> references)
    {
        foreach (var field in table.Fields.Where(field => field.Name is not null))
        {
            string? id = field.StringValue ?? field.Table?.Field("id")?.StringValue;
            if (id is null) continue;
            var metadata = slots.InfoById.GetValueOrDefault(id);
            string? slotName = symbol == "Scene" ? slots.ByKey.GetValueOrDefault(field.Name!)?.Table?.Field("name")?.StringValue : null;
            string title = field.Table?.Field("title")?.StringValue ?? metadata?.Field("name")?.StringValue ?? slotName ?? field.Name!;
            references.Add(new("mission." + symbol + "." + field.Name,
                symbol switch { "Slot" => "slots", "Squad" => "squads", "Scene" => "scenes", _ => "directives" }, title.Replace('_', ' '), id)
            {
                SlotType = metadata?.Field("type") is { } type && int.TryParse(type.Value.Read(source), out int value) ? value : null,
                Description = field.Table?.Field("description")?.StringValue ?? "",
                Owner = symbol == "Directive" && field.Table?.Field("sensor")?.StringValue is { } sensor
                    ? slots.KeyById.GetValueOrDefault(sensor, "") : ""
            });
        }
    }

    /** A table of tables nests by owner; a table of scalars is one row. */
    private static void Rows(LuaTable table, string prefix, int depth, string source, List<MissionSdkReference> references)
    {
        if (depth > 4) return;
        foreach (var field in table.Fields.Where(field => field.Name is not null))
        {
            string expression = prefix + "." + field.Name;
            if (field.Table is { } nested && nested.Fields.Any(value => value.Table is not null))
            { Rows(nested, expression, depth + 1, source, references); continue; }
            string identity = field.Table?.Field("id")?.StringValue ?? (field.Table is null ? field.Value.Read(source) : expression);
            string category = expression.Split('.')[1];
            string title = field.Table?.Field("title")?.StringValue ?? field.Table?.Field("name")?.StringValue
                ?? expression[("mission." + category + ".").Length..].Replace('.', ' ');
            references.Add(new(expression, category, title.Replace('_', ' '), identity));
        }
    }

    /** A category of the module's returned table: every nested table with an id is one row. */
    private static void Visit(LuaTable table, string prefix, string category, int depth, string source,
        List<MissionSdkReference> references)
    {
        if (depth > 5) return;
        int index = 0;
        foreach (var field in table.Fields)
        {
            index++;
            string expression = prefix + (field.Name is null ? $"[{index}]" : "." + field.Name);
            if (field.Table is not { } nested) continue;
            string? id = nested.Field("id")?.StringValue;
            if (id is not null)
            {
                string title = nested.Field("name")?.StringValue ?? field.Name ?? id;
                if (category == "states" && nested.Field("region_index") is { } region && nested.Field("ordinal") is { } ordinal)
                    title = "Region " + region.Value.Read(source) + " / State " + ordinal.Value.Read(source);
                references.Add(new(expression, category, title, id));
            }
            else Visit(nested, expression, category, depth + 1, source, references);
        }
    }

    private static uint? ReadKey(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier, null, out uint hex) ? hex : null
        : uint.TryParse(text, out uint number) ? number : null;

    /** The installed file a script with this runtime path lives in; the file may not exist yet. */
    public string InstalledPath(string relativePath) => MissionProject.Target(ScriptsRoot, relativePath);

    /**
     * Saves an installed mission script in place. Installed scripts belong to no project.
     * @return The file written.
     */
    public string SaveInstalled(string relativePath, string source)
    {
        string target = InstalledPath(relativePath);
        MissionProject.Write(target, source);
        return target;
    }

    /** The SDK module of a `missions.KEY` binding; null when the SDK has no such key. */
    private string? ModuleForKey(string? key) =>
        key is null ? null : missionModules.FirstOrDefault(activity => activity.Key == key)?.Module;

    // Scripts are hand-written. Generated SDK modules reach 12 MiB, so they use the Lua reader's cap.
    private const long ScriptLimit = 8L * 1024 * 1024;

    /** Reads a script file under the script size limit. */
    public static string ReadSource(string path) => ReadSource(path, ScriptLimit);

    /** Reads an SDK source file under the Lua size limit. */
    internal static string ReadSdkSource(string path) => ReadSource(path, LuaScript.MaxLength);

    /** The table whose brace is the first in the file; a generated module returns or binds it. */
    internal static LuaTable? FirstTable(LuaScript lua) => lua.Descendants().OfType<LuaTable>().FirstOrDefault();

    private static string ReadSource(string path, long limit)
    {
        var info = new FileInfo(path);
        if (info.Length > limit || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("File is too large or is a linked file.");
        return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
    }
}
