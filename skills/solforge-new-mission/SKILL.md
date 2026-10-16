---
name: solforge-new-mission
description: Use when the user wants a new mission, quest, objective chain or scripted encounter on an existing map in SolForge; triggers "new mission", "add a quest", "objective", "mission graph", "script a scene".
---
# New mission (Mission Graph + Lua)

Goal: a playable scripted mission on an existing activity. Missions are plain Lua files, never package deltas.

## Real limits
- **Activity and map creation are not supported** ("Activity creation / Map creation is not supported yet"). A mission binds to an installed activity (one script per activity) and uses its existing map, states, squads, triggers, devices, dialogue and scenes. You can script new flow but cannot add new world objects.
- A mission naming no map cannot be opened. Scene pickers come from `mission.SceneInput/SceneOutput`. The script needs `scripts/lib/flow.lua`.
- A mod script needs a project; an installed-script edit saves in place under `bin/x64/Sunrise/scripts/<stem>/<stem>.lua`.
- Sandbox Lua has no `pairs`, `next`, `string.find/match`, `math.random`. The host has 32 timers.
- Update running game needs `studio_attach` true and the mod installed once; a bad script keeps the old one running; not-started missions need an activity restart.

## Steps
1. `get_context`, `get_project`; `list_missions`, `list_mission_scripts`; pick the target activity.
2. Survey the world: `open_map`; `list_mission_objects`, `get_mission_object` (states, squads, triggers, devices, navpoints, directives), `find_world_objects` near the area. Note slot ids. `read_reference` "psd-mission-scripting-new-mission", "first-script", "recipes".
3. Plan 3-5 beats (start, what the player sees, end). Keep keys in `scripts/<m>/content.lua` returning `function(mission) ... lib.one(...) end`, so wrong keys fail at open.
4. Create: for any activity type (story, strike, patrol, public_event, lost_sector, raid, encounter, pvp, quest, custom) call `create_mission_script` {activity, mission (missions.lua key or map), name}: it saves a template into the project scripts and opens it in the Graph panel (`get_context` graph_kind `mission_script`); fix every Checks line (CHANGE_ME placeholders, names the map lacks). Otherwise `start_creation` for a mission script if `list_creation_types`/`describe_creation` show it available; otherwise UI Mission Graph > New script (Activity, Arrival region, Graph key, Save to this project). The user presses Create.
5. Write beats with `lib.flow` (key e.g. "mm"). Facts: `arrived`, `gate_area`, triggers via `lib.is_slot`. Steps: arrive, reach (directive + navpoint), arm trigger only after the area loaded, fight (`assign_combat_objective`, place, await `cohort.cleared`), open (device + dialogue), exit, done (`clear_directives`, `complete_mission`). Wire `on_start` and `on_load` to `graph:advance`; route `client_state_changed`, `region_changed`, `squad_state`, `device_state`, `player_trigger` into `graph:handle`; disarm a trigger on its first report.
6. Save with `apply_project_edit` (or `open_file` for the user). Check schema/check output via `read_panel` (mission graph, report).
7. Deploy: `build_project` (Update running game for scripts only; else `install=true` with game closed). `get_build_status`.
8. Test in game: start the activity, walk each beat, read the log for script errors (`read_reference` "psd-mission-scripting-debugging"). Track the player with `get_player_position` (map, zone, nearest mission slots) and `wait_for_player_near` (slot id, radius); `open_map` with only `mission` opens it on its own map.

## UI fallback
Any Solforge control can be driven with `list_ui_controls` then `ui_invoke` / `ui_set_value` / `ui_set_checked` / `ui_select` (approve click each).
Mission Graph tab templates: Immediate step, Arm trigger, Show objective, Play dialogue, Select state, Complete mission, Start scene, Send scene input. Campaign steps: Reach trigger, Complete ghost scan, Use interactable. Ctrl+S saves; Export Lua for review.

## Verify
Script loads without errors, each beat advances, `complete_mission` fires; restart the activity mid-way to confirm `on_load` resumes.
