---
name: solforge-patrol-content
description: Use when the user wants to add or change public events, lost sectors, patrol population, respawns or rewards in an open-world destination script; triggers "new public event", "lost sector", "patrol spawns", "change event reward", "freeroam".
---
# Patrol and public-event content (Lua)

Goal: edit the freeroam Lua for a destination. These are server mission scripts, not package edits.

## Where things are
- Scripts: `C:\Users\Seth\Desktop\sol\02-server\solserver\solserverbase\Destiny-Sol-main\mission-scripts` (live copy on the VPS at `/opt/sol-server/run/Sunrise/scripts`). In a SolForge project they sit in `scripts/<stem>/`.
- Per destination: `<dest>_freeroam/<dest>_freeroam.lua` (tiny: requires the mission, `lib.freeroam`, `content`, then `freeroam.new{name=, mission=, zones=content(mission, freeroam)}`), `content.lua` (GENERATED zone rows: `state`, `region`, `patrol = {freeroam.unit(squad, slot)...}`, `events = {... sensor = mission.Slot.M_PUBLIC_EVENT_SENSOR_...}`), `objectives.lua` (GENERATED squad slot -> combat objective map).
- Engine: `lib/freeroam.lua` (zones, respawn timers, event sites, chests, kill payout, `grant_rewards`), `lib/mission_lib.lua`, `lib/flow.lua`, `lib/combat.lua`.
- Rewards: `loot_pools.lua` (pools by hash/weight; `enemy_kill.<name>` drop rules by activity/bubble).

## Limits
- Sandbox Lua has no `pairs`, `next`, `string.find/match`, `math.random`: use table indexing and `ipairs`. The host keeps 32 timers and up to 64 zones per destination.
- `content.lua` and `objectives.lua` say "Generated, do not edit". Make only small additive edits and warn the user a regenerator may overwrite them.
- You can only use squads/slots in the installed mission data (`mission.Squad.*`, `mission.Slot.*`). New squads or event sensors need package edits Studio cannot create: reuse an existing sensor or squad, or move it to another zone.
- The EDZ script notes "A cleared chest grants nothing: no script call reaches an account". Check before promising rewards. Item rewards go through `loot_pools.lua`.
- Mostly not tested in game. Leave `*.bak*` and `*.wip*` files alone.

## Steps
1. `get_context`; `list_missions`, `list_mission_scripts` for the destination.
2. Read narrowly: `<dest>_freeroam.lua`, then the zone in `content.lua` (`read_project_file` with a range, or `search_project` for the sensor/squad name). Skim a second destination (`eden_freeroam`, `luna_freeroam`) for structure.
3. Identify existing pieces: `open_map`, `list_mission_objects`, `get_mission_object` (slot ids, transforms, Lua lines referencing them), `find_world_objects` near the spot.
4. Edit with `apply_project_edit` (or `open_file` for the user):
   - add/remove `freeroam.unit(...)` entries in a zone's `patrol`;
   - move an event by editing a zone's `events` row;
   - cooldown/respawn tunables at the top of `lib/freeroam.lua` (`DEFAULT_RESPAWN_MS`, `DEFAULT_EVENT_COOLDOWN_MS`, tick ms) only if asked, since it is global;
   - rewards: add hashes to `loot_pools.lua`, or a new `enemy_kill` rule (item keys are decimal hash strings).
5. Check names: a wrong `mission.Squad.X` fails at open. Confirm each with `get_mission_object` or `search_reference`.
6. Deploy: Update running game (needs `studio_attach`) via `build_project`, or Build and install (game closed). The live server needs a restart; flag other players first. Already-started missions need an activity restart.
7. Test in game: enter the zone, check spawns, event start, completion, rewards; read the logs. `get_player_position` says where the player is (zone, nearest slots); `wait_for_player_near` with the event sensor's slot id waits until they reach it; `get_viewport_frame` / `wait_for_viewport_change` show the view (text for models without vision).

## UI fallback
Any Solforge control can be driven with `list_ui_controls` then `ui_invoke` / `ui_set_value` / `ui_set_checked` / `ui_select` (approve click each).
Menus, toolbar toggles and file pickers: `list_commands` then `run_command` (give `path` instead of a picker), `show_panel` before `list_ui_controls`, `export_asset` / `import_asset`, `set_viewport_options` / `set_camera`, `project_change`, `arm_file_dialog`; `get_help` with section `tools` names the tool for any menu, panel or dialog.
Mission Graph > Installed scripts / Mod scripts opens the Lua editor; World window pages (States, Squads, Objects/Triggers) for key lookup.

## Verify
Script loads with no log error; squads appear in the intended zone; event fires and clears; reward pool pays.
