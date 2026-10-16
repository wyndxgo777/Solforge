---
name: solforge-map-bounds
description: Use when the user wants to expand, extend, move or open up playable map area, trigger/kill volumes, invisible walls or collision in SolForge; triggers "expand the EDZ into the Dark Forest", "reach the Shard of the Traveler", "remove the invisible wall", "kill volume", "move this object".
---
# Map bounds and area changes

Goal: change where players can go (volumes, walls, placement) using what Studio can actually edit.

## What can and cannot be done
- **Cannot:** create a new map ("Map creation is not supported yet") or activity. Cannot change map streaming/region loading or add scenery that is not in installed packages.
- **Can:** open an existing map or mission, move/rotate/scale placed objects (`edit_placement`), grow a trigger volume's BOX (`edit_bounds`), edit collision, and change mission scripts (Lua).
- Trigger volumes: only the surrounding box is edited; the hull outline cannot move, rotate or scale. The box may only grow to contain the whole hull up to its top (min <= max). You can ENLARGE a trigger, not shrink it below its hull. A kill volume cannot be deleted; neutralize it in the mission script that reacts to it instead.
- Collision edits are unverified in game. Mesh/convex only; removing collision is refused; unequal scale refused; a shape edit affects every placement of that shape. Moving a static object moves its collision.
- If the target area is not inside a loaded region/bubble of the map, placement edits cannot make it load. Check that first.
- Placement may be "read only" when the layout is not writable.

## Steps
1. `get_context`, `get_open_documents`; `list_maps` / `list_missions`; `open_map` the target (e.g. EDZ). A mission using several maps asks "Choose mission map".
2. Locate the goal: `find_object` by viewport name (e.g. "Shard of the Traveler"); `get_object` for position. If it is in another map, `read_panel` (hierarchy, Bubbles) to see whether this map loads it at all. If not, say so and stop.
3. Find blockers near the route: `find_world_objects` with filter/layer `volumes`, `collision`, `zone`, using `near` + `radius`. `get_object` each hit for kind, bounds, owner mission, role (World / Hitbox / Trigger / Other).
4. Per blocker:
   - Trigger/kill volume to enlarge: `edit_bounds` (grow only).
   - Placed wall/prop: `get_placement`, then `edit_placement` (move away, scale, rotate).
   - Collision-only wall: export GLB in the UI, edit in Blender, re-import (UI only; `read_reference` "collision").
   - Script-driven boundary (zone region change, leave-area timer): edit the mission Lua (`list_mission_scripts`, `read_project_file`, `apply_project_edit`); see skill solforge-patrol-content.
5. `set_viewport_rendering` to show Volumes / Collision layers; `get_viewport_screenshot` to check.
6. Each edit needs the user's approve click. Undo with `revert_object_edit`.
7. `build_project` with `install=true` (game closed). Startup `_unp1_` packages cannot be patched.
8. Test: walk the route in game; confirm you are not killed/teleported and the path is open. `wait_for_player_near` with the target point and `get_player_position` (xyz, zone) confirm the player got there; `get_viewport_frame` with Follow player on shows it.

## UI fallback
Layers > Show Volumes / Collision; select object; Inspector > World placement > Apply bounds; Move tool for placement; Revert object or Ctrl+Z.

## Verify
`get_placement`/`get_object` show the new values; `list_project_changes` has one Object row per edit; in game the route is reachable. If the area is unloaded or void, report that the map cannot be extended and why.
