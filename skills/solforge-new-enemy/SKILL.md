---
name: solforge-new-enemy
description: Use when the user wants a new enemy, boss, squad or different enemy mix, damage or population in SolForge; triggers "new enemy", "add a boss", "change squad", "tougher enemies", "spawn these enemies here".
---
# New enemies and squads

Goal: get different enemies into the world with what Studio supports.

## Real limits
- **No new enemy entity.** "Entity clone is not supported yet": Enemy copy, Guardian and Enemy New are gray. AI, health, weapons, loot and faction are not known or editable in Studio. Guardians need character data Studio cannot write.
- Editable on an existing enemy: Local definition (class 0x8080815F: display name and action table), Damage part scale, per-type scale, pool start fraction. These change the shared config used by every enemy of that type and may not work in game.
- A new skinned model: Entities > New uses Import skinned model, Hob rig only.
- In practice "new enemy" means reusing existing squads/prefabs, changing where they appear (Lua), or tuning a shared config.

## Steps
1. `get_context`, `get_open_documents`. Decide: reuse placement (Lua) or tune a config.
2. Find candidates: `search_assets` (enemy name), `find_object`, `list_mission_objects` (squads in the mission), `get_mission_object` (squad slot, prefab, children, Lua lines naming it). `read_panel` for the Enemies category / Components tab.
3. Reuse in patrol/freeroam: add `freeroam.unit(mission.Squad.X, mission.Slot.X)` to a zone's `patrol` in `<dest>_freeroam/content.lua`. See skill solforge-patrol-content. The squad must exist in that destination's installed mission data.
4. In a mission script: place squads with `lib.combat`/`lib.flow` (`assign_combat_objective` before placing; await `cohort.cleared`). See skill solforge-new-mission.
5. Tune a shared enemy: `open_asset`, `get_tag`, `get_package_entry`, then edit damage scales in the UI Components tab. Warn the user it affects every use of that config.
6. Kill drops: `loot_pools.lua` `enemy_kill.<name>` rule (activity/bubble substring, pool, chance).
7. `build_project` (`install=true` with game closed; Update running game for Lua-only changes).
8. Test: go to the zone, confirm the squad spawns, fights and drops.

## UI fallback
Any Solforge control can be driven with `list_ui_controls` then `ui_invoke` / `ui_set_value` / `ui_set_checked` / `ui_select` (approve click each).
Enemies/NPCs category > Components and Parts tabs; Mission Graph / Lua editor for placement.

## Verify
Squad present in `list_mission_objects`; log shows it placed; in game the enemies appear and behave (prefab AI only, which cannot be changed).
