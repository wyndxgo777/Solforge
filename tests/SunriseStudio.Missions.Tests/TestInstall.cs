/** A small Sunrise install in a temp folder: SDK indexes, two generated activities and one script. */
internal static class TestInstall
{
    public const string CleanModule = "missions.test_mission_80aa0001", DamagedModule = "missions.test_damaged_80aa0002";
    public const string CleanKey = "TEST_MISSION", DamagedKey = "TEST_DAMAGED";

    /** Path of the SDK Lua folder inside an installation. */
    public static string Sdk(string game) => Path.Combine(game, "bin", "x64", "Sunrise", "sdk", "lua");
    /** Path of the mission scripts folder inside an installation. */
    public static string Scripts(string game) => Path.Combine(game, "bin", "x64", "Sunrise", "scripts");
    /** Path of an SDK Lua module file. */
    public static string ModuleFile(string game, string module) => Path.Combine(Sdk(game), module.Replace('.', Path.DirectorySeparatorChar) + ".lua");
    /** Path of the test mission script. */
    public static string ScriptFile(string game) => Path.Combine(Scripts(game), "test_mission", "test_mission.lua");

    /** A script bound to one activity key, with one context call the API schema types. */
    public static string Script(string key) => "local missions = require(\"missions\")\n"
        + $"local mission = require(missions.{key})\n"
        + "return {\n"
        + "    on_start = function(context, state)\n"
        + "        context:set_variable(\"ready\", true)\n"
        + "    end,\n"
        + "}\n";

    /**
     * Writes the install. The damaged module ends inside mission.Slot; it has no mission_api.json and no lib/flow.lua.
     * @param scripts False leaves out the scripts folder.
     * @return The game root.
     */
    public static string Write(string game, bool scripts = true)
    {
        string sdk = Sdk(game);
        Write(Path.Combine(sdk, "missions.lua"), Index("mission", CleanModule, DamagedModule));
        Write(Path.Combine(sdk, "activities.lua"), Index("activity", "activities.test_mission_0001", "activities.test_damaged_0002"));
        Write(ModuleFile(game, "activities.test_mission_0001"), Activity("test_mission", CleanModule));
        Write(ModuleFile(game, "activities.test_damaged_0002"), Activity("test_damaged", DamagedModule));
        Write(ModuleFile(game, CleanModule), Module("test_mission", "80aa0001"));
        string damaged = Module("test_damaged", "80aa0002");
        Write(ModuleFile(game, DamagedModule), damaged[..(damaged.IndexOf("SCOREBOARD = \"slot", StringComparison.Ordinal) + 14)]);
        if (scripts) Write(ScriptFile(game), Script(CleanKey));
        return game;
    }

    /** The full text of a generated mission module. */
    public static string Module(string name, string tag) => $$"""
        -- Generated from installed mission data. Do not edit.
        local sdk = require("sunrise.activity_sdk")

        local mission = {
            name = "{{name}}",
            id = "scenario/{{tag}}",
            states = {
                ARRIVAL = { id = "state/{{tag}}/0000/0000/00000001", ordinal = 0, slice_set_index = 0, region_index = 0, value = 0 },
                LANDING = { id = "state/{{tag}}/0001/0001/00000002", ordinal = 1, slice_set_index = 0, region_index = 1, value = 1 },
            },
            slots = {
                SCOREBOARD = { id = "slot/{{tag}}/000000/0000/0010", name = "scoreboard", index = 0, type = 16 },
            },
            squads = {
            },
        }

        mission.Slot = {
            SCOREBOARD = "slot/{{tag}}/000000/0000/0010",
        }

        return mission
        """ + "\n";

    private static string Index(string kind, string clean, string damaged) =>
        $"-- Generated {kind} module names. Do not edit.\nreturn {{\n    {CleanKey} = \"{clean}\",\n    {DamagedKey} = \"{damaged}\",\n}}\n";

    private static string Activity(string name, string module) => $$"""
        -- Generated from installed activity data. Do not edit.
        local activity = {
            name = "{{name}}",
            display_name = "{{name}}",
            mission = require("{{module}}"),
        }

        return activity
        """ + "\n";

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
