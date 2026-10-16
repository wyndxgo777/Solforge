namespace SunriseStudio.Packages;

/** Studio's labels for the package slot type numbers; 7, 9, 55, 56 and 59 have no slot type. */
public static class SlotTypeNames
{
    private static readonly Dictionary<int, string> Labels = new()
    {
        [1] = "squad sensor", [2] = "combatant sensor", [3] = "objective sensor", [4] = "object sensor",
        [5] = "sequence sensor", [6] = "cinematic sensor", [8] = "HUD sensor", [10] = "all-players volume",
        [11] = "music sensor", [12] = "music section", [13] = "player SPASAHA sensor", [14] = "activity team",
        [15] = "team picker", [16] = "scoreboard", [17] = "activity lifetime", [18] = "activity timer",
        [19] = "player navigation", [20] = "damage component", [21] = "distance sensor",
        [22] = "object volume", [23] = "device sensor", [24] = "channel sensor", [25] = "look trigger",
        [26] = "hop-on sensor", [27] = "race track", [28] = "hard wipe", [29] = "object monitor",
        [30] = "player monitor", [31] = "player trigger", [32] = "toggle sensor", [33] = "player objective",
        [34] = "object filter", [35] = "activity hard-wipe globals", [36] = "loot sensor",
        [37] = "map generator", [38] = "task sensor", [39] = "passenger sensor", [40] = "target sensor",
        [41] = "new-user-experience sensor", [42] = "performance sensor", [43] = "scene sensor",
        [44] = "firing area", [45] = "firing-area set", [46] = "squad group", [47] = "activity point",
        [48] = "AI point set", [49] = "landing point", [50] = "activity spawn point",
        [51] = "activity teleport point", [52] = "spawn influencer", [53] = "dialog sensor",
        [54] = "dialog event", [57] = "bubble reference", [58] = "command script", [60] = "trigger volume",
        [61] = "utility script", [62] = "phase", [63] = "slot reference", [64] = "squad inspector",
        [65] = "ghost-link sensor", [66] = "spawn rule", [67] = "team-side sensor", [68] = "directive sensor",
        [69] = "directive", [70] = "encounter engagement", [71] = "public event", [72] = "encounter flag"
    };

    public static int Count => Labels.Count;
    /** Name of a slot type number, or null. */
    public static string? Name(int type) => Labels.GetValueOrDefault(type);
}
