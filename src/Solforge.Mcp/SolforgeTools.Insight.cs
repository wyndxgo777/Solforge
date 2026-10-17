/**
 * File: mission_coverage, and the SDK facts list_mission_objects and get_mission_object add per slot (MissionInsight).
 */
using System.Text.Json.Nodes;
using Solforge.Mcp.Data;

namespace Solforge.Mcp;

public static partial class SolforgeTools
{
    /** The SDK module of a mission given as a tag, SDK name (mission_calculon) or retail name (Six); null and why when none. */
    public static (MissionSdkData? Sdk, MissionScriptFile? Script, string? Problem) ResolveMission(ISolforgeHost host, string? mission, uint openTag)
    {
        var sdkFolders = MissionInsight.SdkFolders(host.CodeRoots);
        if (sdkFolders.Count == 0) return (null, null, "No SDK mission folder (bin\\x64\\Sunrise\\sdk\\lua\\missions) was found next to the game or project.");
        var scriptFolders = MissionInsight.ScriptFolders(host.ProjectRoot, host.CodeRoots);
        MissionSdkData? sdk = null;
        MissionScriptFile? script = null;
        string text = (mission ?? "").Trim();
        if (text.Length == 0)
        {
            if (openTag == 0) return (null, null, "Give a mission (tag, SDK name such as mission_calculon, or retail name such as Six), or open one.");
            sdk = MissionSdkData.ForTag(sdkFolders, openTag);
        }
        else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || (text.Length == 8 && text.All(Uri.IsHexDigit)))
        {
            if (ParseHash(text) is { } tag) sdk = MissionSdkData.ForTag(sdkFolders, tag);
        }
        else
        {
            sdk = MissionSdkData.ForName(sdkFolders, text.ToLowerInvariant());
            if (sdk is null)
                foreach (var (file, name) in MissionInsight.RetailScripts(scriptFolders))
                    if (string.Equals(file.RetailName, text, StringComparison.OrdinalIgnoreCase)
                        && MissionSdkData.ForName(sdkFolders, name) is { } found) { sdk = found; script = file; break; }
        }
        if (sdk is null) return (null, null, $"No SDK mission module matches '{text}'. list_mission_scripts lists the scripts; give an SDK name or tag.");
        script ??= sdk.Name.Length > 0 ? MissionInsight.FindScript(scriptFolders, sdk.Name) : null;
        return (sdk, script, null);
    }

    /** Raw tag bytes when the host reads packages; null otherwise. */
    private static Func<uint, byte[]?>? TagReader(ISolforgeHost host) =>
        host is ISolforgeDataHost data && data.DataUnavailable is null ? (Func<uint, byte[]?>)(hash => data.ReadTag(hash, false)) : null;

    /** The open mission's SDK module, or null (insight facts are then left out). */
    private static MissionSdkData? OpenMissionSdk(ISolforgeHost host, MissionSnapshot mission)
    {
        if (mission.MissionTag == 0) return null;
        try { return MissionSdkData.ForTag(MissionInsight.SdkFolders(host.CodeRoots), mission.MissionTag); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /** list_mission_objects plus squad data, cutscene states, dialogue text and trigger overlaps; triggerMap adds the map. */
    private static JsonObject ListWithInsight(ISolforgeHost host, MissionSnapshot mission, JsonObject args)
    {
        var page = MissionSlots.List(mission, McpJson.String(args, "filter"), McpJson.Bool(args, "placed", false),
            McpJson.Int(args, "offset", 0), McpJson.Int(args, "limit", 100));
        if (OpenMissionSdk(host, mission) is not { } sdk) return page;
        var spawns = MissionInsight.Spawns(mission);
        var read = TagReader(host);
        if (page["rows"] is JsonArray rows)
            foreach (var row in rows.OfType<JsonObject>())
                if (row["slotId"]?.GetValue<string>() is { } id && mission.Find(id) is { } slot)
                    MissionInsight.Annotate(row, slot, sdk, false, spawns, read);
        page["dialogueText"] = sdk.HasDialogueText;
        if (!sdk.HasDialogueText) page["dialogueNote"] = "This mission's data has no dialogue text.";
        int noData = sdk.Slots.Count(slot => slot.Type == MissionSdkData.SquadType && !sdk.SquadHasData(slot));
        if (noData > 0) page["squadsWithoutData"] = noData;
        if (McpJson.Bool(args, "triggerMap", false)) page["triggerMap"] = MissionInsight.TriggerMap(sdk, spawns);
        return page;
    }

    /** Adds the insight facts to get_mission_object details. */
    private static void DescribeWithInsight(ISolforgeHost host, MissionSnapshot mission, MissionSlot slot, JsonObject details)
    {
        if (OpenMissionSdk(host, mission) is not { } sdk) return;
        MissionInsight.Annotate(details, slot, sdk, true, MissionInsight.Spawns(mission), TagReader(host));
    }

    /** Yields mission_coverage. */
    private static IEnumerable<McpTool> InsightTools(ISolforgeHost host,
        Func<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>, McpTool> read)
    {
        yield return read("mission_coverage", "Coverage report for one mission: the squads, directives, dialogue lines, cutscenes, " +
            "interactables and scenes its SDK data has that its script never names, with totals and used counts. Squads with no " +
            "squad data (cannot be placed) are marked. Squads and interactables count only prefabs the script already names unless all is true.",
            McpJson.Schema(("mission", "string", "Tag (0x80B42BAA), SDK name (mission_calculon) or retail name (Six); default the open mission", false),
                ("all", "boolean", "Count every prefab on the map, not just the ones the script names (default false)", false),
                ("limit", "integer", "Most unused rows per category (default 100, max 1000)", false)),
            async (args, cancel) =>
            {
                uint open = 0;
                if (string.IsNullOrWhiteSpace(McpJson.String(args, "mission")) && await host.MissionAsync(cancel).ConfigureAwait(false) is { } mission)
                    open = mission.MissionTag;
                return await Task.Run(() =>
                {
                    var (sdk, script, problem) = ResolveMission(host, McpJson.String(args, "mission"), open);
                    if (sdk is null) return McpToolResult.Error(problem!);
                    var report = MissionInsight.Coverage(sdk, script, McpJson.Bool(args, "all", false));
                    return McpToolResult.Json(report.ToJson(Math.Clamp(McpJson.Int(args, "limit", 100), 1, 1000)));
                }, cancel).ConfigureAwait(false);
            });
    }
}
