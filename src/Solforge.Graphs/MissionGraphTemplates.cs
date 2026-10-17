/**
 * File: Mission node-graph templates built on the mission-library nodes (MissionLibrary): mid-mission cutscenes,
 * moving the fireteam between areas during a fade, and boss phases with pylons and invulnerability. Each is modelled
 * on a Red War beat (see the project's .solforge/reference/*-retail-timeline.md) and is a starting graph to edit.
 */
namespace Solforge.Graphs;

/** One template: its id (create_graph template=...), title, the retail beats it models and what it builds. */
public sealed record MissionGraphTemplate(string Id, string Title, string Examples, string Description);

/** Builds mission graphs from the templates. Slot names are CHANGE_ME until the author picks the map's slots. */
public static class MissionGraphTemplates
{
    public const string Placeholder = "CHANGE_ME";

    public static IReadOnlyList<MissionGraphTemplate> All { get; } =
    [
        new("cutscene_sky_battle", "Mid-mission cutscene between areas",
            "Homecoming: fade at the bazaar, sky battle cinematic (Amanda, ships), black, spawn on the Command Ship",
            "On a signal: fade out into the cutscene area state, play the cinematic, then fade again and teleport the fireteam to the next area's spawn."),
        new("cutscene_light_restored", "Story cutscene at an objective",
            "Spark: reach the Traveler shard, cutscene, Light restored",
            "On a signal (e.g. the shard interaction): play the cinematic, remember it played (a reload does not replay it), then send light_restored."),
        new("cutscene_boss_intro", "Boss intro cutscene",
            "Chosen: intro cutscene of Ghaul in the arena before the fight",
            "Once, when the fireteam enters the arena trigger: play the boss intro cinematic, make the boss invulnerable until it ends, place the boss squad."),
        new("fade_area_move", "Move the fireteam during a fade",
            "Adieu: black with location cards (City Outskirts, Twilight Gap) between areas",
            "Once, on a trigger: fade to black, hold for the location card, teleport to the next spawn and select its state, fade in, then set the next phase."),
        new("boss_pylons", "Boss phases with pylons and invulnerability",
            "Chosen: Ghaul's shield pylons and OF_GHAUL_NO_DIE (he cannot die until the last phase)",
            "At start the boss is invulnerable (the NO_DIE rule); at 66% and 33% health a shield phase starts that only its pylons break; after the last phase he can die, and his death runs the ordered ending."),
    ];

    /** The template by id, or null. */
    public static MissionGraphTemplate? Find(string? id) => All.FirstOrDefault(template => template.Id == (id ?? "").Trim().ToLowerInvariant());

    /** Builds the graph for a template; throws for an unknown id. */
    public static GraphDocument Build(string id, string name)
    {
        var template = Find(id) ?? throw new InvalidDataException("Unknown mission graph template " + id + "; use one of " + string.Join(", ", All.Select(value => value.Id)) + ".");
        var document = GraphDocument.Create(GraphKind.Mission, name);
        var editor = new GraphEditor(document);
        string start = document.Nodes[0].Id;
        float x = 0, y = 0;
        string Add(string type, params (string Key, string Value)[] properties)
        {
            x += 260;
            return editor.AddNode(type, x, y, properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)).Id;
        }
        void Chain(params string[] ids)
        {
            for (int i = 0; i + 1 < ids.Length; i++)
                if (editor.Connect(ids[i], "then", ids[i + 1], "exec") is { } problem) throw new InvalidOperationException(problem);
        }
        string Slot(string what) => "Slot." + Placeholder + "_" + what;
        // TODO(mission-lib-transitions): the lib.* nodes use MissionLibrary's placeholder names until the server branch lands.
        switch (template.Id)
        {
            case "cutscene_sky_battle":
            {
                editor.Remove([start]);
                x = -260;
                string signal = Add("mission.on_signal", ("signal", "enter_sky_battle"));
                Chain(signal,
                    Add("lib.transition", ("state", "mission.states." + Placeholder + "_SKY_BATTLE_STATE"), ("hold_ms", "500")),
                    Add("lib.play_cutscene", ("cinematic", Slot("SKY_BATTLE_CINEMATIC")), ("fade", "false"), ("signal", "sky_battle_done")),
                    Add("lib.transition", ("teleport", Slot("COMMAND_SHIP_SPAWN")), ("state", "mission.states." + Placeholder + "_COMMAND_SHIP_STATE"), ("hold_ms", "1500")),
                    Add("flow.send_signal", ("signal", "EncounterComplete")));
                break;
            }
            case "cutscene_light_restored":
            {
                editor.Remove([start]);
                x = -260;
                Chain(Add("mission.on_signal", ("signal", "shard_reached")), Add("flow.do_once"),
                    Add("lib.play_cutscene", ("cinematic", Slot("LIGHT_RESTORED_CINEMATIC")), ("signal", "light_restored_done")),
                    Add("state.set_flag", ("name", "light_restored")), Add("flow.send_signal", ("signal", "light_restored")));
                break;
            }
            case "cutscene_boss_intro":
            {
                editor.Remove([start]);
                x = -260;
                Chain(Add("mission.event", ("event", "trigger_entered")), Add("flow.do_once"),
                    Add("lib.set_invulnerable", ("target", Slot("BOSS")), ("invulnerable", "true")),
                    Add("lib.play_cutscene", ("cinematic", Slot("BOSS_INTRO_CINEMATIC")), ("signal", "boss_intro_done")),
                    Add("mission.place_squad", ("name", Placeholder + "_BOSS_SQUAD")),
                    Add("lib.set_invulnerable", ("target", Slot("BOSS")), ("invulnerable", "false")));
                break;
            }
            case "fade_area_move":
            {
                editor.Remove([start]);
                x = -260;
                string phase = Add("mission.set_phase", ("phase", "2"));
                Chain(Add("mission.event", ("event", "trigger_entered")), Add("flow.do_once"),
                    Add("lib.transition", ("fade_out_ms", "1000"), ("hold_ms", "4000"), ("fade_in_ms", "1000"),
                        ("teleport", Slot("NEXT_AREA_SPAWN")), ("state", "mission.states." + Placeholder + "_NEXT_AREA_STATE"), ("signal", "area_entered")),
                    phase);
                break;
            }
            case "boss_pylons":
            {
                string boss = Slot("BOSS"), pylons = "{" + Slot("PYLON_1") + ", " + Slot("PYLON_2") + ", " + Slot("PYLON_3") + "}";
                // Start: the NO_DIE rule (invulnerable until the last phase), then the first shield phase at 66%.
                Chain(start, Add("lib.set_invulnerable", ("target", boss), ("invulnerable", "true")),
                    Add("lib.boss_phase", ("boss", boss), ("phase", "1"), ("at_health", "0.66"), ("invulnerable", "true"), ("pylons", pylons), ("signal", "phase_1_broken")));
                x = -260; y = 200;
                Chain(Add("mission.on_signal", ("signal", "phase_1_broken")),
                    Add("lib.boss_phase", ("boss", boss), ("phase", "2"), ("at_health", "0.33"), ("invulnerable", "true"), ("pylons", pylons), ("signal", "phase_2_broken")));
                x = -260; y = 400;
                Chain(Add("mission.on_signal", ("signal", "phase_2_broken")), Add("lib.set_invulnerable", ("target", boss), ("invulnerable", "false")),
                    Add("state.set_flag", ("name", "boss_can_die")));
                // Any death raises entity_died; only one after the last phase (boss_can_die) ends the mission. Filter the boss here.
                x = -260; y = 600;
                string died = Add("mission.event", ("event", "entity_died"));
                string branch = Add("flow.branch");
                string flag = editor.AddNode("state.get_flag", x - 260, y + 140, new Dictionary<string, string> { ["name"] = "boss_can_die" }).Id;
                if (editor.Connect(flag, "value", branch, "condition") is { } problem) throw new InvalidOperationException(problem);
                Chain(died, branch);
                string once = Add("flow.do_once");
                if (editor.Connect(branch, "true", once, "exec") is { } broken) throw new InvalidOperationException(broken);
                Chain(once, Add("lib.ending", ("cutscene", Slot("BOSS_DEFEATED_CINEMATIC")), ("experience", "5000"), ("loot_rolls", "1")));
                break;
            }
        }
        return editor.Document;
    }
}
