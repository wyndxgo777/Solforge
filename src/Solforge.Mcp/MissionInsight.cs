/**
 * File: Mission insight over the SDK module and the mission's script: the coverage report (what the data has that the
 * script never names), and the extra facts list_mission_objects and get_mission_object show per slot (squads with no
 * squad data, the state a cutscene belongs to, dialogue text, and which trigger volumes overlap the spawn or each other).
 */
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Solforge.Mcp;

/** One mission script file, its source and the retail name its first comment gives ('-- Six. ...'). */
public sealed record MissionScriptFile(string Path, string Source, string? RetailName);

/** One entry of the mission's data the script never names. */
public sealed record CoverageItem(string Key, string Label, string? Detail = null);

/** Coverage of one category: how many entries the data has, how many the script names, and the rest. */
public sealed record CoverageCategory(string Name, int Total, int Used, IReadOnlyList<CoverageItem> Unused, int OutOfScope);

/** The coverage report for one mission. */
public sealed record MissionCoverageReport(string Mission, uint Tag, string? ScriptPath, string? RetailName, bool HasDialogueText,
    IReadOnlyList<CoverageCategory> Categories)
{
    public int UnusedCount => Categories.Sum(category => category.Unused.Count);

    public JsonObject ToJson(int limit)
    {
        var categories = new JsonObject();
        foreach (var category in Categories)
        {
            var unused = new JsonArray();
            foreach (var item in category.Unused.Take(limit))
            {
                var row = new JsonObject { ["key"] = item.Key, ["label"] = item.Label };
                if (item.Detail is { Length: > 0 } detail) row["detail"] = detail;
                unused.Add(row);
            }
            var json = new JsonObject
            {
                ["total"] = category.Total, ["used"] = category.Used, ["unusedCount"] = category.Unused.Count, ["unused"] = unused
            };
            if (category.Unused.Count > limit) json["truncated"] = true;
            if (category.OutOfScope > 0) json["outOfScope"] = category.OutOfScope;
            categories[category.Name] = json;
        }
        var report = new JsonObject
        {
            ["mission"] = Mission, ["tag"] = $"0x{Tag:X8}", ["retailName"] = RetailName, ["script"] = ScriptPath,
            ["dialogueText"] = HasDialogueText, ["categories"] = categories
        };
        if (ScriptPath is null) report["note"] = "No script for this mission was found, so everything counts as unused.";
        if (!HasDialogueText) report["dialogueNote"] = "This mission's data has no dialogue text.";
        if (Categories.Any(category => category.OutOfScope > 0))
            report["scopeNote"] = "Squads and interactables count only prefabs the script already names; all=true lists every prefab on the map.";
        return report;
    }
}

public static class MissionInsight
{
    private static readonly Regex Word = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Retail = new(@"^\s*--\s*([^.\r\n]{1,48}?)\.(\s|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RequiresMission = new(@"missions\.([A-Z0-9_]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ---- scripts ----

    /** The folders mission scripts live in: the project's scripts folder (and the project), then the server scripts. */
    public static IReadOnlyList<string> ScriptFolders(string? projectRoot, IEnumerable<CodeRoot> roots)
    {
        var folders = new List<string>();
        if (projectRoot is { Length: > 0 } && Directory.Exists(Path.Combine(projectRoot, "scripts"))) folders.Add(Path.Combine(projectRoot, "scripts"));
        foreach (var root in roots.Where(root => !root.ByMission && root.Name != "project")) folders.Add(root.Folder);
        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /** The SDK mission folders among the code roots. */
    public static IReadOnlyList<string> SdkFolders(IEnumerable<CodeRoot> roots) => roots.Where(root => root.ByMission).Select(root => root.Folder).ToArray();

    /** The retail name a script's first comment line gives ('-- Six. Red War...' is Six), or null. */
    public static string? RetailName(string source)
    {
        int end = source.IndexOf('\n');
        string first = end < 0 ? source : source[..end];
        return Retail.Match(first) is { Success: true } match ? match.Groups[1].Value.Trim() : null;
    }

    /** The script for an SDK mission name: '<name>/<name>.lua' or '<name>.lua' in a folder, else one requiring missions.<NAME>. */
    public static MissionScriptFile? FindScript(IEnumerable<string> folders, string missionName)
    {
        var list = folders.Where(Directory.Exists).ToArray();
        foreach (string folder in list)
            foreach (string candidate in new[] { Path.Combine(folder, missionName, missionName + ".lua"), Path.Combine(folder, missionName + ".lua") })
                if (File.Exists(candidate)) return Read(candidate);
        string needle = "missions." + missionName.ToUpperInvariant();
        foreach (string path in list.SelectMany(LuaFiles))
        {
            string source = SafeRead(path);
            if (Regex.IsMatch(source, @"\b" + Regex.Escape(needle) + @"\b")) return new(path, source, RetailName(source));
        }
        return null;
    }

    /** Every script with a retail name and the SDK mission it requires, for name lookups ('Six' is mission_calculon). */
    public static IEnumerable<(MissionScriptFile Script, string Mission)> RetailScripts(IEnumerable<string> folders)
    {
        foreach (string path in folders.Where(Directory.Exists).SelectMany(LuaFiles))
        {
            string source = SafeRead(path);
            if (RetailName(source) is not { } retail || RequiresMission.Match(source) is not { Success: true } match) continue;
            yield return (new(path, source, retail), match.Groups[1].Value.ToLowerInvariant());
        }
    }

    private static MissionScriptFile Read(string path) { string source = SafeRead(path); return new(path, source, RetailName(source)); }

    private static string SafeRead(string path)
    {
        try { return new FileInfo(path).Length > 4_000_000 ? "" : File.ReadAllText(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static IEnumerable<string> LuaFiles(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*.lua", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    /** Identifiers the code names, comments removed (a constant in a comment is not a use). */
    public static HashSet<string> Words(string source)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Word.Matches(StripComments(source))) words.Add(match.Value);
        return words;
    }

    /** Source with '--' comments blanked; strings are kept. */
    public static string StripComments(string source)
    {
        var text = new StringBuilder(source.Length);
        char quote = '\0';
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (quote != '\0')
            {
                text.Append(c);
                if (c == '\\' && i + 1 < source.Length) text.Append(source[++i]);
                else if (c == quote || c == '\n') quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; text.Append(c); continue; }
            if (c == '-' && i + 1 < source.Length && source[i + 1] == '-')
            {
                // Block comments --[[ ]] and --[==[ ]==].
                int level = -1;
                if (i + 2 < source.Length && source[i + 2] == '[')
                {
                    int j = i + 3; level = 0;
                    while (j < source.Length && source[j] == '=') { level++; j++; }
                    if (j >= source.Length || source[j] != '[') level = -1;
                }
                if (level >= 0)
                {
                    string close = "]" + new string('=', level) + "]";
                    int end = source.IndexOf(close, i, StringComparison.Ordinal);
                    i = end < 0 ? source.Length : end + close.Length - 1;
                }
                else
                {
                    int end = source.IndexOf('\n', i);
                    i = end < 0 ? source.Length : end - 1;
                }
                text.Append(' ');
                continue;
            }
            text.Append(c);
        }
        return text.ToString();
    }

    // ---- coverage ----

    /**
     * What the mission's data has that the script never names. Squads and interactables count only prefabs (registries)
     * the script already names unless all is true, since an SDK module carries every prefab on the map.
     */
    public static MissionCoverageReport Coverage(MissionSdkData sdk, MissionScriptFile? script, bool all = false)
    {
        string source = script?.Source ?? "";
        var words = Words(source);
        var touched = new HashSet<uint>(sdk.Slots.Where(slot => words.Contains(slot.Key)).Select(slot => slot.Registry));
        bool scoped = !all && touched.Count > 0;
        var categories = new List<CoverageCategory>();

        CoverageCategory Slots(string name, IEnumerable<SdkSlot> slots, Func<SdkSlot, string?> detail, bool scope)
        {
            var list = slots.ToArray();
            var inScope = scope && scoped ? list.Where(slot => touched.Contains(slot.Registry)).ToArray() : list;
            var unused = inScope.Where(slot => !words.Contains(slot.Key)).Select(slot => new CoverageItem(slot.Key, slot.Name, detail(slot))).ToArray();
            return new(name, inScope.Length, inScope.Length - unused.Length, unused, list.Length - inScope.Length);
        }

        categories.Add(Slots("squads", sdk.Slots.Where(slot => slot.Type == MissionSdkData.SquadType),
            slot => sdk.SquadHasData(slot) ? null : "no squad data; cannot be placed", true));

        var directives = sdk.Directives.Where(directive => !words.Contains(directive.Key))
            .Select(directive => new CoverageItem(directive.Key, directive.Title, directive.Description)).ToArray();
        categories.Add(new("directives", sdk.Directives.Count, sdk.Directives.Count - directives.Length, directives, 0));

        // Dialogue: a cue is used as alias.CUE_n (local alias = mission.DialogueCue.SENSOR) or DialogueCue.SENSOR.CUE_n.
        var stripped = StripComments(source);
        var lines = new List<CoverageItem>();
        int cueTotal = 0;
        foreach (var (sensor, cues) in sdk.DialogueCues)
        {
            var prefixes = new List<string> { @"DialogueCue\s*\.\s*" + Regex.Escape(sensor) };
            foreach (Match alias in Regex.Matches(stripped, @"(\w+)\s*=\s*mission\s*\.\s*DialogueCue\s*\.\s*" + Regex.Escape(sensor) + @"\b"))
                prefixes.Add(@"\b" + Regex.Escape(alias.Groups[1].Value));
            foreach (var (key, number) in cues)
            {
                cueTotal++;
                bool used = prefixes.Any(prefix => Regex.IsMatch(stripped, prefix + @"\s*\.\s*" + Regex.Escape(key) + @"\b"));
                if (used) continue;
                var text = sdk.Lines(sensor, number);
                lines.Add(new($"{sensor}.{key}", text.Count > 0 ? text[0] : "(no text)", text.Count > 1 ? $"{text.Count} variants" : null));
            }
        }
        categories.Add(new("dialogue", cueTotal, cueTotal - lines.Count, lines, 0));

        categories.Add(Slots("cutscenes", sdk.Slots.Where(slot => slot.Type == MissionSdkData.CinematicType), _ => null, false));
        categories.Add(Slots("interactables", sdk.Slots.Where(slot => slot.Type == MissionSdkData.DeviceType
            || slot.Name.Contains("interact", StringComparison.OrdinalIgnoreCase)), slot => slot.AuthType, true));

        var scenes = sdk.Scenes.Where(scene => !words.Contains(scene.Key))
            .Select(scene => new CoverageItem(scene.Key, scene.Key.ToLowerInvariant(), $"resource 0x{scene.ResourceTag:X8}")).ToArray();
        categories.Add(new("scenes", sdk.Scenes.Count, sdk.Scenes.Count - scenes.Length, scenes, 0));

        return new(sdk.Name, sdk.Tag, script?.Path, script?.RetailName, sdk.HasDialogueText, categories);
    }

    // ---- per-slot facts ----

    /** Where a cutscene's state is read from: raw tag bytes 0x0C-0x0F. */
    public const int CutsceneStateOffset = 0x0C;

    /**
     * The state a cutscene belongs to: the u32 at 0x0C of the first candidate tag (the slot's asset, its prefab, its registry)
     * whose value names a state of the mission; else the first readable value, raw.
     */
    public static JsonObject? CutsceneState(MissionSdkData? sdk, IEnumerable<uint> candidates, Func<uint, byte[]?> read)
    {
        JsonObject? first = null;
        foreach (uint tag in candidates.Where(tag => tag != 0).Distinct())
        {
            byte[]? bytes;
            try { bytes = read(tag); }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException) { continue; }
            if (bytes is null || bytes.Length < CutsceneStateOffset + 4) continue;
            uint value = BitConverter.ToUInt32(bytes, CutsceneStateOffset);
            var row = new JsonObject { ["fromTag"] = $"0x{tag:X8}", ["bytes0C"] = $"0x{value:X8}" };
            if (sdk?.StateFor(value) is { } state)
            {
                row["state"] = state.Key; row["stateId"] = state.Id; row["index"] = state.Index; row["ordinal"] = state.Ordinal;
                row["region"] = state.Region;
                return row;
            }
            first ??= row;
        }
        if (first is not null) first["note"] = "Bytes 0x0C-0x0F name no state of this mission's SDK module.";
        return first;
    }

    /** Dialogue lines of a dialog sensor slot: cue key, number and text variants; null when the slot is not one. */
    public static JsonArray? DialogueLines(MissionSdkData sdk, string slotId, int limit = 200)
    {
        if (sdk.SensorKey(slotId) is not { } sensor) return null;
        var rows = new JsonArray();
        foreach (var (key, number) in sdk.DialogueCues[sensor].Take(limit))
        {
            var text = new JsonArray();
            foreach (string line in sdk.Lines(sensor, number)) text.Add(line);
            rows.Add(new JsonObject { ["cue"] = key, ["number"] = number, ["text"] = text });
        }
        return rows;
    }

    /** Trigger volumes that overlap a spawn point or another volume, each with what it overlaps. */
    public static JsonArray TriggerMap(MissionSdkData sdk, IReadOnlyList<(string Name, float[] Position)> spawns, int limit = 500)
    {
        var rows = new JsonArray();
        var volumes = sdk.TriggerVolumes;
        foreach (var volume in volumes)
        {
            if (rows.Count >= limit) break;
            if (Facts(sdk, volume, spawns) is { } row) rows.Add(row);
        }
        return rows;
    }

    /** One volume's overlaps (other volumes, spawn points); null when it overlaps nothing. */
    public static JsonObject? Facts(MissionSdkData sdk, SdkTriggerVolume volume, IReadOnlyList<(string Name, float[] Position)> spawns)
    {
        var others = new JsonArray();
        foreach (var other in sdk.TriggerVolumes)
            if (!ReferenceEquals(other, volume) && volume.Overlaps(other)) others.Add(other.Label);
        var inside = new JsonArray();
        foreach (var (name, position) in spawns)
            if (volume.Contains(position)) inside.Add(name);
        if (others.Count == 0 && inside.Count == 0) return null;
        var row = new JsonObject
        {
            ["volume"] = volume.Label, ["number"] = volume.Number, ["registry"] = $"0x{volume.Registry:X8}", ["slotIndex"] = volume.SlotIndex
        };
        if (inside.Count > 0) row["containsSpawn"] = inside;
        if (others.Count > 0) row["overlaps"] = others;
        return row;
    }

    /** Spawn points of the open mission: activity spawn point slots and slots named for a spawn point, with a position. */
    public static IReadOnlyList<(string Name, float[] Position)> Spawns(MissionSnapshot mission) =>
        mission.Slots.Where(slot => slot.Position is { Length: >= 3 } && (slot.Type == MissionSdkData.SpawnPointType
                || slot.Name.Contains("spawn_point", StringComparison.OrdinalIgnoreCase) || slot.Name.Contains("player_spawn", StringComparison.OrdinalIgnoreCase)))
            .Select(slot => (slot.Name, slot.Position!)).ToArray();

    /** Adds the insight facts to one list_mission_objects row (compact) or get_mission_object details (full). */
    public static void Annotate(JsonObject row, MissionSlot slot, MissionSdkData sdk, bool full,
        IReadOnlyList<(string Name, float[] Position)> spawns, Func<uint, byte[]?>? read)
    {
        var info = sdk.SlotById(slot.SlotId);
        if (slot.Type == MissionSdkData.SquadType && info is not null && !sdk.SquadHasData(info))
        {
            row["squadData"] = false;
            row["squadNote"] = "No squad data for this slot, so it cannot be placed.";
        }
        if (slot.Type == MissionSdkData.CinematicType && read is not null
            && CutsceneState(sdk, [slot.AssetTag, slot.PrefabTag, slot.RegistryKey], read) is { } state)
            row["cutsceneState"] = state;
        if (DialogueLines(sdk, slot.SlotId, full ? 200 : 3) is { } lines)
        {
            if (full) row["dialogue"] = lines;
            else
            {
                string sensor = sdk.SensorKey(slot.SlotId)!;
                row["dialogueCues"] = sdk.DialogueCues[sensor].Count;
                if (lines.Select(line => line?["text"]?.AsArray()).FirstOrDefault(text => text is { Count: > 0 }) is { } text)
                    row["dialogueFirst"] = text[0]?.GetValue<string>();
            }
            if (!sdk.HasDialogueText) row["dialogueNote"] = "This mission's data has no dialogue text.";
        }
        if (sdk.VolumeFor(slot.RegistryKey, slot.Type, slot.Index, slot.Name) is { } volume)
        {
            if (Facts(sdk, volume, spawns) is { } facts)
            {
                if (full) row["triggerMap"] = facts;
                else
                {
                    if (facts["containsSpawn"] is JsonArray spawn) row["triggerContainsSpawn"] = spawn.Count;
                    if (facts["overlaps"] is JsonArray overlaps) row["triggerOverlaps"] = overlaps.Count;
                }
            }
            else if (full) row["triggerMap"] = new JsonObject { ["volume"] = volume.Label, ["note"] = "Overlaps no spawn point and no other trigger volume." };
            if (full)
                row["triggerBounds"] = new JsonObject
                {
                    ["minimum"] = new JsonArray(volume.Minimum.Select(v => (JsonNode)Math.Round(v, 3)).ToArray()),
                    ["maximum"] = new JsonArray(volume.Maximum.Select(v => (JsonNode)Math.Round(v, 3)).ToArray())
                };
        }
    }
}
