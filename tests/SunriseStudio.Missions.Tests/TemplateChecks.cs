using SunriseStudio.Missions;

/** New graph templates: every activity type gives parseable server Lua on the installed libs, and Checks flags what to fill. */
internal static class TemplateChecks
{
    private static MissionSdkReference Row(string table, string key) => new($"mission.{table}.{key}", table, key, null);

    /** A small mission SDK: a state, sensors, an objective, a boss, a chest, squads with slots, and directives. */
    internal static readonly MissionSdkReference[] Sdk =
    [
        Row("states", "STATE_A"),
        Row("Slot", "M_DIRECTIVE_SENSOR_1"), Row("Slot", "M_DIALOG_SENSOR_1"), Row("Slot", "M_PUBLIC_EVENT_SENSOR_1"),
        Row("Slot", "OBJ_MAIN"), Row("Slot", "SQ_BOSS"), Row("Slot", "CHEST_1"),
        Row("Slot", "SQ_A"), Row("Slot", "SQ_B"), Row("Slot", "SQ_C"),
        Row("Slot", "CINEMATIC_1"), Row("Slot", "SPAWN_2"), Row("Slot", "PYLON_1"), Row("Slot", "PYLON_2"),
        Row("Squad", "SQ_A"), Row("Squad", "SQ_B"), Row("Squad", "SQ_C"), Row("Squad", "SQ_BOSS"),
        Row("Directive", "GO_THERE"), Row("Directive", "KILL_BOSS"),
    ];

    /** Runs the checks that use generated fixtures instead of installed game content. */
    public static void Synthetic()
    {
        Check(MissionTemplates.Types.Count == Enum.GetValues<MissionActivityType>().Length, "Every activity type needs a template row.");
        foreach (var info in MissionTemplates.Types)
        {
            Check(MissionTemplates.Parse(info.Id) == info.Type, "Activity id does not parse back: " + info.Id);
            string lua = MissionTemplates.Create(info.Type, "TEST_MISSION", "test_" + info.Id, Sdk);
            Check(lua.Contains("require(missions.TEST_MISSION)"), info.Id + " does not require its mission.");
            var document = MissionGraphDocument.Parse(lua);
            Check(document.Diagnostics.Count == 0, info.Id + " template has document problems: "
                + string.Join("; ", document.Diagnostics.Select(value => value.Message)));
            Check(MissionTemplates.Check(lua, Sdk).Count == 0, info.Id + " template has findings with a full SDK: "
                + string.Join("; ", MissionTemplates.Check(lua, Sdk).Select(value => value.Message)));
            string library = info.Type switch
            {
                MissionActivityType.Patrol or MissionActivityType.PublicEvent or MissionActivityType.LostSector => "lib.freeroam",
                MissionActivityType.Pvp => "lib.mission_lib",
                MissionActivityType.Custom => "missions",
                _ => "lib.campaign"
            };
            Check(lua.Contains($"require(\"{library}\")"), info.Id + " does not use " + library);
            if (library == "lib.campaign")
                Check(document.Groups.Any(group => group.IsCampaignSteps) && document.Nodes.Count > 0, info.Id + " shows no campaign nodes.");
        }
        Check(MissionTemplates.Create(MissionActivityType.Strike, "M", "k", Sdk).Contains("barrier = true"), "A strike has no boss barrier.");
        Check(MissionTemplates.Create(MissionActivityType.PublicEvent, "M", "k", Sdk).Contains("Slot.M_PUBLIC_EVENT_SENSOR_1"), "Event sensor not picked.");
        Check(MissionTemplates.Create(MissionActivityType.LostSector, "M", "k", Sdk).Contains("boss = Slot.SQ_BOSS"), "Sector boss not picked.");

        // With no SDK every name is a placeholder: the script still parses and Checks lists what to fill.
        string blank = MissionTemplates.Create(MissionActivityType.Strike, "M", "k", []);
        MissionGraphDocument.Parse(blank);
        var placeholders = MissionTemplates.Check(blank, []);
        Check(placeholders.Count > 0 && placeholders.All(finding => finding.Message.Contains(MissionTemplates.Placeholder) && finding.Line > 0),
            "Placeholders are not listed with their lines.");

        // A name the map's SDK does not have is flagged; comments and tables the SDK lacks are not.
        var unknown = MissionTemplates.Check("local x = mission.Slot.NOT_THERE\n-- Slot.ALSO_NOT_THERE\nlocal y = Squad.SQ_A\nlocal z = mission.Scene.ANY\n", Sdk);
        Check(unknown.Count == 1 && unknown[0].Message.Contains("NOT_THERE") && unknown[0].Line == 1, "Unknown SDK names are not flagged exactly.");
        Check(MissionTemplates.Check("local x = Directive.NOPE\n", [Row("Slot", "A")]).Count == 0, "A table the SDK did not read was checked.");

        Check(MissionTemplates.Parse("crucible") == MissionActivityType.Pvp && MissionTemplates.Parse("Lost Sector") == MissionActivityType.LostSector
            && MissionTemplates.Parse("public-event") == MissionActivityType.PublicEvent && MissionTemplates.Parse("") == MissionActivityType.Custom,
            "Activity aliases do not parse.");
        Reject(() => MissionTemplates.Parse("karaoke"));
        Reject(() => MissionTemplates.Create(MissionActivityType.Story, "bad key", "k", Sdk));
        Reject(() => MissionTemplates.Create(MissionActivityType.Story, "M", " ", Sdk));
        // Bad Lua never becomes a document, so the unified Graph panel keeps the old one and shows the error in Checks.
        Reject(() => MissionGraphDocument.Parse("return {\n"));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidDataException("Bad input was accepted.");
    }
}
