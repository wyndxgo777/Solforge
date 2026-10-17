using System.Text;
using System.Text.RegularExpressions;
using KeraState = KeraLua.Lua;

namespace SunriseStudio.Missions;

/**
 * One finding or trace line from the mission host. Kind is syntax, key, hash, object, check, error, stall,
 * problem, mock, step, trace, module, info or summary; Line is the script's 1-based line, 0 when none.
 */
public sealed record MissionHostRecord(string Kind, int Line, string Text)
{
    /** Kinds that mean the script is wrong or would stall; mocks, trace and notes are not problems. */
    public static readonly IReadOnlySet<string> ProblemKinds =
        new HashSet<string>(StringComparer.Ordinal) { "syntax", "key", "hash", "object", "check", "error", "stall", "problem" };

    public bool IsProblem => ProblemKinds.Contains(Kind);

    /** "line 12: text", or the text alone without a line. */
    public override string ToString() => Line > 0 ? $"line {Line}: {Text}" : Text;
}

/** What check_mission_script or simulate_mission found; Mode is "check" or a simulation mode. */
public sealed record MissionHostReport(string ScriptPath, string Module, string Mode, IReadOnlyList<MissionHostRecord> Records)
{
    public IEnumerable<MissionHostRecord> Problems => Records.Where(record => record.IsProblem);
    public IEnumerable<MissionHostRecord> Mocks => Records.Where(record => record.Kind == "mock");
    /** Step headers and host events in order. */
    public IEnumerable<MissionHostRecord> Trace => Records.Where(record => record.Kind is "step" or "trace");
    public IEnumerable<MissionHostRecord> Stalls => Records.Where(record => record.Kind == "stall");
    /** Generated mission modules the script required, as "missions.name 0xTAG". */
    public IEnumerable<string> Modules => Records.Where(record => record.Kind == "module").Select(record => record.Text);
    /** The mission tag of the first mission module the script required; null when it required none. */
    public uint? MissionTag => Modules.Select(TagOf).FirstOrDefault(tag => tag is not null);
    public bool Passed => !Problems.Any();

    private static uint? TagOf(string module) => module.Split(' ') is [_, var hex] && hex.StartsWith("0x", StringComparison.Ordinal)
        && uint.TryParse(hex.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint tag) ? tag : null;
}

/**
 * Checks and simulates a server mission script (Graph panel "Check script" and "Simulate"; MCP check_mission_script and
 * simulate_mission). Syntax is read in-process by Loretta (LuaScript); keys, hashes, the script library's own check()
 * and the step walk run the script itself in a bundled Lua 5.4 (KeraLua, lua54.dll) with Simulation/mission_host.lua,
 * which grew out of the Red War walker in tools/mission-sim/walk2.lua.
 */
public static partial class MissionHost
{
    /** Simulation modes: play every cutscene, skip them, the outro cutscene never loads, the player rejoins mid-mission. */
    public static readonly string[] Modes = ["play", "skip", "cutscene_never_loads", "rejoin"];

    private static readonly Lazy<string> HostSource = new(() =>
    {
        using var stream = typeof(MissionHost).Assembly.GetManifestResourceStream("SunriseStudio.Missions.mission_host.lua")
            ?? throw new InvalidOperationException("The mission host script is missing from SunriseStudio.Missions.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    /** A mode name, also walk2's names (guard, reload) and spaces or dashes; null when unknown. */
    public static string? NormalizeMode(string? mode)
    {
        string name = (mode ?? "play").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        return name switch
        {
            "" or "play" => "play",
            "skip" => "skip",
            "guard" or "cutscene_never_loads" or "never_loads" or "no_cutscene" => "cutscene_never_loads",
            "reload" or "rejoin" or "rejoins" => "rejoin",
            _ => null
        };
    }

    /**
     * Where require starts and the script's module name: scripts/name/name.lua is module name.name under scripts;
     * any other file is its own stem under its folder.
     */
    public static (string ScriptsRoot, string Module) Locate(string scriptPath)
    {
        string path = Path.GetFullPath(scriptPath);
        string folder = Path.GetDirectoryName(path) ?? throw new ArgumentException("A script path needs a folder.", nameof(scriptPath));
        string stem = Path.GetFileNameWithoutExtension(path);
        if (string.Equals(Path.GetFileName(folder), stem, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(folder) is { } root)
            return (root, stem + "." + stem);
        return (folder, stem);
    }

    /** The SDK Lua folder of a game installation (bin/x64/Sunrise/sdk/lua). */
    public static string SdkRootOf(string gameRoot) => Path.Combine(gameRoot, "bin", "x64", "Sunrise", "sdk", "lua");

    /**
     * Check script: Lua syntax, every key and hash the script names against the generated mission data, and the
     * script library's check(). With an open mission whose tag matches the script's, slotKnown (MissionSnapshot.Find, the
     * lookup list_mission_objects uses) must also know each Slot constant.
     */
    public static MissionHostReport Check(string scriptPath, string sdkRoot, uint? openMissionTag = null, Func<string, bool>? slotKnown = null)
    {
        var (_, module) = Locate(scriptPath);
        string text = File.ReadAllText(scriptPath);
        var syntax = Syntax(text);
        if (syntax.Count > 0) return new(Path.GetFullPath(scriptPath), module, "check", syntax);
        var report = Run(scriptPath, sdkRoot, "check");
        if (slotKnown is null || openMissionTag is null || report.MissionTag != openMissionTag) return report;
        var records = report.Records.ToList();
        var reported = records.Where(record => record.Kind == "key").Select(record => (record.Line, Key(record.Text)))
            .ToHashSet();
        string[] lines = text.Replace("\r", "").Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string code = lines[index];
            int comment = code.IndexOf("--", StringComparison.Ordinal);
            if (comment >= 0) code = code[..comment];
            foreach (Match match in SlotConstant().Matches(code))
            {
                string key = match.Groups[1].Value;
                if (reported.Add((index + 1, key)) && !slotKnown(key))
                    records.Add(new("object", index + 1, $"Slot.{key} is not among the open mission's objects (list_mission_objects)"));
            }
        }
        return report with { Records = records };
    }

    /** Simulate: walks the script's steps through the fake host in one mode (see Modes). */
    public static MissionHostReport Simulate(string scriptPath, string sdkRoot, string mode)
    {
        string normalized = NormalizeMode(mode) ?? throw new ArgumentException($"Unknown mode {mode}; use {string.Join(", ", Modes)}.", nameof(mode));
        var (_, module) = Locate(scriptPath);
        var syntax = Syntax(File.ReadAllText(scriptPath));
        return syntax.Count > 0 ? new(Path.GetFullPath(scriptPath), module, normalized, syntax) : Run(scriptPath, sdkRoot, normalized);
    }

    /** Loretta's syntax errors ("line L, column C: message") as records. */
    public static IReadOnlyList<MissionHostRecord> Syntax(string text) =>
        LuaScript.Read(text).Errors.Select(error => ErrorLine().Match(error) is { Success: true } match
            ? new MissionHostRecord("syntax", int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), error)
            : new MissionHostRecord("syntax", 0, error)).ToArray();

    /** Runs mission_host.lua on the script in a fresh Lua state; mode is "check" or a normalized simulation mode. */
    private static MissionHostReport Run(string scriptPath, string sdkRoot, string mode)
    {
        string path = Path.GetFullPath(scriptPath);
        var (root, module) = Locate(path);
        if (!Directory.Exists(sdkRoot))
            return new(path, module, mode, [new("error", 0, $"The game's Lua SDK folder {sdkRoot} is missing; start the game once to write it.")]);
        string output;
        bool failed;
        using (var lua = new KeraState())
        {
            lua.Encoding = Encoding.UTF8;
            Global(lua, "SCRIPTS_ROOT", Slashes(root));
            Global(lua, "SDK_ROOT", Slashes(Path.GetFullPath(sdkRoot)));
            Global(lua, "MODULE", module);
            Global(lua, "SOURCE_PATH", path);
            Global(lua, "MODE", mode);
            failed = lua.LoadString(HostSource.Value, "=mission_host") != KeraLua.LuaStatus.OK
                || lua.PCall(0, 1, 0) != KeraLua.LuaStatus.OK;
            output = lua.ToString(-1) ?? "";
        }
        if (failed) return new(path, module, mode, [new("error", 0, "The mission host failed: " + output)]);
        var records = new List<MissionHostRecord>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t', 3);
            if (parts.Length == 3 && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out int number))
                records.Add(new(parts[0], number, parts[2]));
        }
        return new(path, module, mode, records);
    }

    private static void Global(KeraState lua, string name, string value)
    {
        lua.PushString(value);
        lua.SetGlobal(name);
    }

    // Lua's package.path joins with '/', which Windows accepts.
    private static string Slashes(string path) => path.Replace('\\', '/').TrimEnd('/');

    // "mission.Slot.KEY does not exist..." -> KEY.
    private static string Key(string text)
    {
        string name = text.Split(' ')[0];
        return name[(name.LastIndexOf('.') + 1)..];
    }

    [GeneratedRegex(@"^line (\d+),")]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"\bSlot\.([A-Z][A-Z0-9_]*)")]
    private static partial Regex SlotConstant();
}
