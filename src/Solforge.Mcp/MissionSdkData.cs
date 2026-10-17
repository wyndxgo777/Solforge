/**
 * File: The facts mission_coverage, list_mission_objects and get_mission_object need from one generated SDK mission module
 * (sdk\lua\missions\<name>_<tag>.lua): slots, squad data, directives, dialogue cues and their text, scenes, states and
 * trigger volumes. The module is read as text, a line at a time; it is never executed.
 */
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Solforge.Mcp;

/** A mission.slots row: constant key, slot id ('slot/<registry>/<index>/<index>/<type>'), name, type and sensor kind. */
public sealed record SdkSlot(string Key, string Id, string Name, int Type, string? AuthType)
{
    /** The registry hash in the slot id (the owning prefab's key). */
    public uint Registry => Id.Split('/') is { Length: > 1 } parts
        && uint.TryParse(parts[1], NumberStyles.HexNumber, null, out uint value) ? value : 0;
}

/** A mission.Directive row: what the HUD shows. */
public sealed record SdkDirective(string Key, string Title, string Description, string Sensor);

/** A scenes row (mission.Scene): an authored scene (FX, force-look, cutscene staging) and its tags. */
public sealed record SdkScene(string Key, string Id, uint ConfigTag, uint ResourceTag);

/** A states row: 'state/<mission>/<index>/<ordinal>/<tag>', with its hash and region. */
public sealed record SdkState(string Key, string Id, uint Tag, uint Hash, int Index, int Ordinal, int Region);

/** A trigger_volumes row (1-based Number as in mission.trigger_volumes[n]): an axis-aligned box in world space. */
public sealed record SdkTriggerVolume(int Number, IReadOnlyList<string> Names, uint Registry, int SlotType, int SlotIndex,
    float[] Minimum, float[] Maximum)
{
    public string Label => Names.Count > 0 ? Names[0] : $"trigger_volumes[{Number}]";
    public bool Contains(float[] point) => point.Length >= 3
        && point[0] >= Minimum[0] && point[0] <= Maximum[0] && point[1] >= Minimum[1] && point[1] <= Maximum[1]
        && point[2] >= Minimum[2] && point[2] <= Maximum[2];
    public bool Overlaps(SdkTriggerVolume other) =>
        Minimum[0] <= other.Maximum[0] && Maximum[0] >= other.Minimum[0] && Minimum[1] <= other.Maximum[1]
        && Maximum[1] >= other.Minimum[1] && Minimum[2] <= other.Maximum[2] && Maximum[2] >= other.Minimum[2];
}

/** One parsed SDK mission module; immutable once read. */
public sealed class MissionSdkData
{
    public const int SquadType = 1, CinematicType = 6, DeviceType = 23, TriggerVolumeType = 60, DialogType = 53, SpawnPointType = 50;

    public string Path { get; private init; } = "";
    public string Name { get; private set; } = "";
    public uint Tag { get; private set; }
    public List<SdkSlot> Slots { get; } = [];
    /** Keys of the squads table (squads with spawn data), with their member counts. */
    public Dictionary<string, int> Squads { get; } = new(StringComparer.Ordinal);
    public List<SdkDirective> Directives { get; } = [];
    public List<SdkScene> Scenes { get; } = [];
    public List<SdkState> States { get; } = [];
    public List<SdkTriggerVolume> TriggerVolumes { get; } = [];
    /** mission.DialogueCue: sensor key to (cue key, cue number), in file order. */
    public Dictionary<string, List<(string Key, int Number)>> DialogueCues { get; } = new(StringComparer.Ordinal);
    /** mission.DialogueCueVariants: sensor key to cue number to the line's variants (subtitle text). */
    public Dictionary<string, Dictionary<int, List<string>>> DialogueText { get; } = new(StringComparer.Ordinal);

    private readonly Dictionary<string, SdkSlot> slotById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SdkSlot> slotByKey = new(StringComparer.Ordinal);

    /** True when any cue has at least one line of text. */
    public bool HasDialogueText => DialogueText.Values.Any(cues => cues.Values.Any(lines => lines.Count > 0));
    public SdkSlot? SlotById(string id) => slotById.GetValueOrDefault(id);
    public SdkSlot? SlotByKey(string key) => slotByKey.GetValueOrDefault(key);
    /** The dialogue sensor key (mission.DialogueCue.<key>) whose slot id is this, or null. */
    public string? SensorKey(string slotId) => slotById.GetValueOrDefault(slotId) is { } slot && DialogueCues.ContainsKey(slot.Key) ? slot.Key : null;
    /** Text variants of one cue; empty when the module has none. */
    public IReadOnlyList<string> Lines(string sensor, int cue) =>
        DialogueText.GetValueOrDefault(sensor)?.GetValueOrDefault(cue) is { } lines ? lines : [];

    /** A squad slot whose key has no squads row (or a row with no members) cannot be placed. */
    public bool SquadHasData(SdkSlot slot) => Squads.TryGetValue(slot.Key, out int members) && members > 0;

    /** The trigger volume a slot is: same registry, type and index, or a slot named in the volume's names. */
    public SdkTriggerVolume? VolumeFor(uint registry, int type, int index, string name) =>
        TriggerVolumes.FirstOrDefault(volume => volume.Registry == registry && volume.SlotType == type && volume.SlotIndex == index)
        ?? TriggerVolumes.FirstOrDefault(volume => volume.Registry == registry && volume.Names.Contains(name, StringComparer.OrdinalIgnoreCase));

    /** The state whose tag, hash or index is this value (cutscene state bytes), or null. */
    public SdkState? StateFor(uint value) =>
        States.FirstOrDefault(state => state.Tag == value) ?? States.FirstOrDefault(state => state.Hash == value)
        ?? (value < 0x10000 ? States.FirstOrDefault(state => state.Index == (int)value && state.Ordinal == 0) : null);

    // ---- reading ----

    private static readonly Regex KeyLine = new(@"^\s*(?:local\s+(?<local>\w+)|mission\.(?<table>\w+)|(?<key>\w+)|\[(?<index>-?\d+)\])\s*=\s*(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Field = new(@"\b(?<name>\w+)\s*=\s*(?:""(?<text>(?:[^""\\]|\\.)*)""|(?<value>0x[0-9A-Fa-f]+|-?[0-9.]+(?:e-?\d+)?))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Quoted = new(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Vector = new(@"\b(?<name>minimum|maximum)\s*=\s*\{\s*x\s*=\s*(?<x>[-0-9.e]+),\s*y\s*=\s*(?<y>[-0-9.e]+),\s*z\s*=\s*(?<z>[-0-9.e]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly object CacheLock = new();
    private static readonly List<(string Path, long Stamp, MissionSdkData Data)> Cache = [];

    /** The module for a mission tag in any of these folders ('*_<tag>.lua'), cached per file and write time; null when none. */
    public static MissionSdkData? ForTag(IEnumerable<string> folders, uint tag)
    {
        foreach (string folder in folders.Where(Directory.Exists))
        {
            string? path = Directory.EnumerateFiles(folder, $"*_{tag:x8}.lua").FirstOrDefault();
            if (path is not null) return Load(path);
        }
        return null;
    }

    /** The module for a mission name such as mission_calculon (file '<name>_<tag>.lua'); null when none. */
    public static MissionSdkData? ForName(IEnumerable<string> folders, string name)
    {
        foreach (string folder in folders.Where(Directory.Exists))
            foreach (string path in Directory.EnumerateFiles(folder, name + "_*.lua"))
                if (System.IO.Path.GetFileNameWithoutExtension(path) is { } stem && stem.Length == name.Length + 9) return Load(path);
        return null;
    }

    /** Reads one module, cached by path and write time (the two most recent stay). */
    public static MissionSdkData Load(string path)
    {
        var info = new FileInfo(path);
        long stamp = info.LastWriteTimeUtc.Ticks ^ info.Length;
        lock (CacheLock)
            if (Cache.FirstOrDefault(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase) && entry.Stamp == stamp) is { Data: { } hit })
                return hit;
        MissionSdkData data;
        using (var reader = new StreamReader(path, Encoding.UTF8)) data = Read(reader, path);
        lock (CacheLock)
        {
            Cache.RemoveAll(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
            Cache.Add((path, stamp, data));
            if (Cache.Count > 2) Cache.RemoveAt(0);
        }
        return data;
    }

    /** Parses module text (tests and Load). */
    public static MissionSdkData Parse(string text, string path = "") => Read(new StringReader(text), path);

    private static MissionSdkData Read(TextReader reader, string path)
    {
        var data = new MissionSdkData { Path = path };
        var stack = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var match = KeyLine.Match(line);
            string? key = null;
            if (match.Success)
                key = match.Groups["local"].Success ? match.Groups["local"].Value
                    : match.Groups["table"].Success ? "mission." + match.Groups["table"].Value
                    : match.Groups["key"].Success ? match.Groups["key"].Value : "[" + match.Groups["index"].Value + "]";
            data.Visit(stack, key, line);
            int net = Net(line);
            for (int i = 0; i < net; i++) stack.Add(i == 0 ? key ?? "?" : "?");
            for (int i = 0; i > net && stack.Count > 0; i--) stack.RemoveAt(stack.Count - 1);
        }
        foreach (var slot in data.Slots)
        {
            data.slotById.TryAdd(slot.Id, slot);
            data.slotByKey.TryAdd(slot.Key, slot);
        }
        return data;
    }

    /** Applies one line given the open tables above it. */
    private void Visit(List<string> stack, string? key, string line)
    {
        int depth = stack.Count;
        string top = depth > 0 ? stack[0] : "";
        if (depth == 1 && top == "mission" && key is not null)
        {
            if (key == "name" && Quoted.Match(line) is { Success: true } name) Name = name.Groups[1].Value;
            else if (key == "tag" && Fields(line).GetValueOrDefault("tag") is { } tag) Tag = Hex(tag);
            return;
        }
        if (depth == 2 && top == "mission" && key is not null && !key.StartsWith('['))
        {
            switch (stack[1])
            {
                case "slots":
                    var slot = Fields(line);
                    if (slot.GetValueOrDefault("id") is { } id)
                        Slots.Add(new(key, id, slot.GetValueOrDefault("name") ?? "", Int(slot.GetValueOrDefault("type")), slot.GetValueOrDefault("auth_type")));
                    break;
                case "squads":
                    Squads.TryAdd(key, 0);
                    break;
                case "scenes":
                    var scene = Fields(line);
                    if (scene.GetValueOrDefault("id") is { } sceneId)
                        Scenes.Add(new(key, sceneId, Hex(scene.GetValueOrDefault("config_tag")), Hex(scene.GetValueOrDefault("resource_tag"))));
                    break;
                case "states":
                    var state = Fields(line);
                    if (state.GetValueOrDefault("id") is { } stateId)
                    {
                        string[] parts = stateId.Split('/');
                        States.Add(new(key, stateId, parts.Length > 4 ? Hex(parts[4]) : 0, Hex(state.GetValueOrDefault("hash")),
                            parts.Length > 2 ? (int)Hex(parts[2]) : 0, Int(state.GetValueOrDefault("ordinal")), Int(state.GetValueOrDefault("region_index"))));
                    }
                    break;
            }
            return;
        }
        // A squad member row (members = { { actor_class = ... }, ... }).
        if (depth >= 3 && top == "mission" && stack[1] == "squads" && line.Contains("actor_class", StringComparison.Ordinal)
            && Squads.ContainsKey(stack[2]))
        {
            Squads[stack[2]]++;
            return;
        }
        if (depth == 1 && top == "mission.Directive" && key is not null)
        {
            var directive = Fields(line);
            if (directive.ContainsKey("id"))
                Directives.Add(new(key, Unescape(directive.GetValueOrDefault("title") ?? key), Unescape(directive.GetValueOrDefault("description") ?? ""),
                    directive.GetValueOrDefault("sensor") ?? ""));
            return;
        }
        if (depth == 2 && top == "mission.DialogueCue" && key is not null && !key.StartsWith('['))
        {
            var cues = DialogueCues.TryGetValue(stack[1], out var list) ? list : DialogueCues[stack[1]] = [];
            if (int.TryParse(line.Split('=', 2)[1].Trim().TrimEnd(','), out int number)) cues.Add((key, number));
            return;
        }
        if (depth == 3 && top == "mission.DialogueCueVariants" && stack[2].StartsWith('[') && int.TryParse(stack[2].Trim('[', ']'), out int cue))
        {
            var bySensor = DialogueText.TryGetValue(stack[1], out var table) ? table : DialogueText[stack[1]] = [];
            var lines = bySensor.TryGetValue(cue, out var existing) ? existing : bySensor[cue] = [];
            foreach (Match text in Quoted.Matches(line)) lines.Add(Unescape(text.Groups[1].Value));
            return;
        }
        if (depth == 2 && top == "mission.DialogueCueVariants" && key is not null && key.StartsWith('[') && int.TryParse(key.Trim('[', ']'), out int empty))
        {
            // '[2] = {},' opens and closes on one line: the cue exists with no text.
            var bySensor = DialogueText.TryGetValue(stack[1], out var table) ? table : DialogueText[stack[1]] = [];
            bySensor.TryAdd(empty, []);
            return;
        }
        if (depth == 1 && top == "mission.trigger_volumes" && line.Contains("registry_key", StringComparison.Ordinal))
        {
            var fields = Fields(line);
            var names = new List<string>();
            int open = line.IndexOf("names", StringComparison.Ordinal), close = open < 0 ? -1 : line.IndexOf('}', open);
            if (open >= 0 && close > open) foreach (Match name in Quoted.Matches(line[open..close])) names.Add(name.Groups[1].Value);
            float[] min = [0, 0, 0], max = [0, 0, 0];
            foreach (Match vector in Vector.Matches(line))
            {
                float[] target = vector.Groups["name"].Value == "minimum" ? min : max;
                target[0] = Float(vector.Groups["x"].Value); target[1] = Float(vector.Groups["y"].Value); target[2] = Float(vector.Groups["z"].Value);
            }
            TriggerVolumes.Add(new(TriggerVolumes.Count + 1, names, Hex(fields.GetValueOrDefault("registry_key")), Int(fields.GetValueOrDefault("slot_type")),
                Int(fields.GetValueOrDefault("slot_index")), min, max));
        }
    }

    /** The scalar fields of one line, first occurrence wins (nested vectors are read separately). */
    private static Dictionary<string, string> Fields(string line)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Field.Matches(line))
            fields.TryAdd(match.Groups["name"].Value, match.Groups["text"].Success ? match.Groups["text"].Value : match.Groups["value"].Value);
        return fields;
    }

    /** Opened minus closed braces outside string literals. */
    private static int Net(string line)
    {
        int net = 0;
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c == '-' && i + 1 < line.Length && line[i + 1] == '-') break;
            else if (c == '{') net++;
            else if (c == '}') net--;
        }
        return net;
    }

    private static string Unescape(string text) => text.Contains('\\')
        ? text.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\'", "'").Replace("\\\\", "\\") : text;

    private static uint Hex(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value) ? value : 0;
    }

    private static int Int(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static float Float(string text) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : 0;
}
