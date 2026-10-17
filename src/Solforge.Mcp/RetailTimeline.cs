/**
 * File: Retail reference timelines (.solforge/reference/<mission>-retail-timeline.md, a 'Video time | Beat' table per
 * retail mission) and the project's record of which beats a mission script covers (.solforge/retail-coverage.json).
 */
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Solforge.Mcp;

/** One row of a retail timeline: the video time range and what happens. */
public sealed record RetailBeat(string Time, string Beat);

/** A parsed timeline file. */
public sealed record RetailTimeline(string Path, string Title, IReadOnlyList<RetailBeat> Beats)
{
    public const string Suffix = "-retail-timeline.md";

    /** The timeline file's mission part ('six' for six-retail-timeline.md). */
    public string Key => System.IO.Path.GetFileName(Path) is { } name && name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
        ? name[..^Suffix.Length].ToLowerInvariant() : System.IO.Path.GetFileNameWithoutExtension(Path).ToLowerInvariant();

    /** Reads the first markdown table's rows (header and separator skipped) and the '# ' title. */
    public static RetailTimeline Parse(string path, string text)
    {
        string title = "";
        var beats = new List<RetailBeat>();
        bool header = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (title.Length == 0 && line.StartsWith("# ", StringComparison.Ordinal)) { title = line[2..].Trim(); continue; }
            if (!line.StartsWith('|')) { if (beats.Count > 0) break; header = false; continue; }
            string[] cells = line.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length < 2) continue;
            if (!header) { header = true; continue; }
            if (cells.All(cell => cell.Length == 0 || cell.All(c => c is '-' or ':'))) continue;
            beats.Add(new(cells[0], string.Join(" | ", cells.Skip(1))));
        }
        return new(path, title, beats);
    }

    public static RetailTimeline Load(string path) => Parse(path, File.ReadAllText(path));

    /** The reference folder of a project, or of the nearest folder above a script that has one; null when none. */
    public static string? ReferenceFolder(string? projectRoot, string? scriptPath)
    {
        if (projectRoot is { Length: > 0 } && Directory.Exists(System.IO.Path.Combine(projectRoot, ".solforge", "reference")))
            return System.IO.Path.Combine(projectRoot, ".solforge", "reference");
        for (string? folder = scriptPath is null ? null : System.IO.Path.GetDirectoryName(scriptPath); folder is not null;
             folder = System.IO.Path.GetDirectoryName(folder))
            if (Directory.Exists(System.IO.Path.Combine(folder, ".solforge", "reference"))) return System.IO.Path.Combine(folder, ".solforge", "reference");
        return null;
    }

    /** Every timeline file in a reference folder, by name. */
    public static IReadOnlyList<string> Available(string? folder) => folder is null || !Directory.Exists(folder) ? []
        : Directory.GetFiles(folder, "*" + Suffix).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    /** The file for a retail name ('Six', '1AU', 'The Farm'), matched on letters and digits; null when none. */
    public static string? Find(string? folder, string? retailName)
    {
        if (retailName is null) return null;
        string slug = Slug(retailName);
        return Available(folder).FirstOrDefault(path => Slug(System.IO.Path.GetFileName(path)[..^Suffix.Length]) == slug);
    }

    private static string Slug(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static readonly Regex StepId = new(@"\bid\s*=\s*""([A-Za-z0-9_\-]+)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /** The step ids a mission script declares ('id = "stairs"'), in order and once each; comments are ignored. */
    public static IReadOnlyList<string> Steps(string source) =>
        StepId.Matches(MissionInsight.StripComments(source)).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
}

/** One beat's record: whether the script covers it and, optionally, the step that does. */
public sealed record RetailMark(bool Covered, string? Step);

/**
 * The project's retail coverage marks, saved as .solforge/retail-coverage.json:
 * { "missions": { "<script name>": { "timeline": "six", "beats": { "<time>": { "covered": true, "step": "cavern" } } } } }.
 */
public sealed class RetailCoverage
{
    public const string FileName = "retail-coverage.json";
    private readonly JsonObject root;

    private RetailCoverage(string path, JsonObject root) { Path = path; this.root = root; }

    public string Path { get; }

    /** The marks file under a project's .solforge folder (or the reference folder's parent); a missing or damaged file is empty. */
    public static RetailCoverage Open(string solforgeFolder)
    {
        string path = System.IO.Path.Combine(solforgeFolder, FileName);
        var root = new JsonObject();
        try { if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject read) root = read; }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        return new(path, root);
    }

    private JsonObject Mission(string script, bool create)
    {
        if (root["missions"] is not JsonObject missions) { missions = new JsonObject(); if (create) root["missions"] = missions; }
        if (missions[script] is JsonObject mission) return mission;
        mission = new JsonObject();
        if (create) missions[script] = mission;
        return mission;
    }

    /** The timeline picked for a script ('six'), or null. */
    public string? Timeline(string script) => Mission(script, false)["timeline"]?.GetValue<string>();

    public void SetTimeline(string script, string? timeline)
    {
        var mission = Mission(script, true);
        if (timeline is null) mission.Remove("timeline"); else mission["timeline"] = timeline;
    }

    public RetailMark Mark(string script, string time) =>
        Mission(script, false)["beats"]?[time] is JsonObject mark
            ? new(mark["covered"]?.GetValue<bool>() == true, mark["step"]?.GetValue<string>()) : new(false, null);

    public void SetMark(string script, string time, RetailMark mark)
    {
        var mission = Mission(script, true);
        if (mission["beats"] is not JsonObject beats) mission["beats"] = beats = new JsonObject();
        if (!mark.Covered && mark.Step is null) { beats.Remove(time); return; }
        var row = new JsonObject { ["covered"] = mark.Covered };
        if (mark.Step is { Length: > 0 } step) row["step"] = step;
        beats[time] = row;
    }

    /** Covered beats of a timeline for a script. */
    public int CoveredCount(string script, RetailTimeline timeline) => timeline.Beats.Count(beat => Mark(script, beat.Time).Covered);

    /** Writes the file (creating the folder), via a temporary file so a crash never leaves half a file. */
    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temporary = Path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, Path, true);
    }
}
