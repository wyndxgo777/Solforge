using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

/** StudioForm part: the map and mission lists with cached names and filtering. */
public sealed partial class StudioForm
{
    private sealed record MapBrowserRow(MapEntry Map, string Name);
    private sealed record MissionBrowserRow(WorldMissionEntry Mission, string Name);
    private WorldCatalog? browserWorldCatalog;
    private int browserWorldNames = -1;
    private MapBrowserRow[] mapBrowserRows = [], matchingMaps = [];
    private MissionBrowserRow[] missionBrowserRows = [], matchingMissions = [];
    private string? matchedMapSearch, matchedMissionSearch;
    private uint? matchedMissionMap;

    /** Name resolution and sorting happen only when the catalog or its labels change. */
    private void PrepareWorldBrowser()
    {
        if (ReferenceEquals(browserWorldCatalog, workspace.WorldCatalog) && browserWorldNames == workspace.NameRevision) return;
        browserWorldCatalog = workspace.WorldCatalog;
        browserWorldNames = workspace.NameRevision;
        mapBrowserRows = workspace.WorldCatalog?.Maps.Select(map => new MapBrowserRow(map, workspace.DisplayName(map.Tag)))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        missionBrowserRows = workspace.WorldCatalog?.Missions.Select(mission => new MissionBrowserRow(mission, workspace.DisplayName(mission.Tag)))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        matchedMapSearch = matchedMissionSearch = null;
    }

    /** Filtering reuses resolved names until the search text changes. */
    private MapBrowserRow[] MatchingMaps()
    {
        PrepareWorldBrowser();
        if (matchedMapSearch != mapSearch)
        {
            matchingMaps = mapSearch.Length == 0 ? mapBrowserRows : mapBrowserRows
                .Where(row => MatchesText(mapSearch, row.Name, row.Map.Tag.ToString("X8"))).ToArray();
            matchedMapSearch = mapSearch;
        }
        return matchingMaps;
    }

    /** Mission filtering also follows the selected map. */
    private MissionBrowserRow[] MatchingMissions()
    {
        PrepareWorldBrowser();
        uint? map = workspace.World?.Map.Tag;
        if (matchedMissionSearch != missionSearch || matchedMissionMap != map)
        {
            matchingMissions = missionBrowserRows.Where(row => (map is null || row.Mission.MapTags.Contains(map.Value))
                && (missionSearch.Length == 0 || MatchesText(missionSearch, row.Name, row.Mission.Tag.ToString("X8")))).ToArray();
            matchedMissionSearch = missionSearch; matchedMissionMap = map;
        }
        return matchingMissions;
    }
}
