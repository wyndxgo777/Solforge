---
name: solforge-retail-port
description: Use when the user wants to read, inspect or backport content from the retail Destiny 2 build (245048) into Project Sol (86657) with SolForge; triggers "retail", "live build", "port a mission from", "backport", "retail map", "retail strings", "tiger-mcp".
---
# Retail backport (retail_* tools)

Goal: find retail content, understand what porting it involves, and write what can be ported today into the open project. Nothing retail is ever written into game packages by these tools.

## Real limits
- Retail is build 245048 (package v53); Sol is 86657 (v38). Class hashes differ, the typed-array marker differs (0x80809FB8 vs 0x80809FBD) and struct layouts changed. Byte-copying a retail tag is never correct.
- Decoded and verified: localized strings, activities and destinations, bubbles (map node transforms, component kinds, entities), static instances, static meshes and terrain (untextured). Every schema field says `confirmed` or `guessed`; say which when you report a value.
- Not decoded: entity models, textures, materials, audio, video, entity scripts and encounter logic. A mission port is a skeleton: an activity document with one objective per retail phase plus a Lua script with `CHANGE_ME` placeholders. Encounters, AI, loot and dialogue must be written by hand on 86657 objects.
- `retail_convert_tag` only rewrites array markers, maps element classes the class map names and relinks the references you give it. Only tags the port plan marks `portable` can be expected to load, and none has been tested in game.
- tiger-mcp (port 3033) is optional. Do not start, stop or restart it. Without it, incoming references and localized-string search fail cleanly and everything else still works.
- The first index build takes about a minute. A `{loading: true}` reply means call again shortly.

## Steps
1. `retail_status`: roots exist, index loaded, lookup cache loaded, schema and class map counts, engine online or not.
2. Find the content:
   - Missions: `retail_activity` (no args lists destinations; `query` searches activity names such as `schism`, `mission_`, `strike_`), then `retail_activity` {activity} for phases, bubbles and entity links.
   - Maps: `retail_map` {activity} lists the bubbles per phase; `retail_map` {bubble} pages the nodes (filter with `kind`), `geometry: true` adds terrain and static counts.
   - Strings: `retail_strings` {container} offline, or {query} through tiger-mcp. A destination's string container is in `retail_activity` {destination}.
   - Anything else: `retail_search` (name, class, type/subtype, package), `retail_tag`, `retail_read` (decoded, annotated, hex), `retail_references`.
3. Look at maps: `retail_open_map` {bubble} (approval) shows it in the viewport with markers, terrain and statics. Use `get_viewport_screenshot`, `focus_camera` and `select_object` as on 86657 maps; the Retail workspace's Map viewer tab names the retail tag behind the selection.
4. Plan: `retail_port_plan` {tag} marks each dependency portable, needs_converter (strings, mission, map_view, geometry, layout, investment) or blocked, with the reason. `retail_class_map` shows the retail-to-86657 class rows and their evidence. Report the totals to the user before writing anything.
5. Preview, then write (each write asks the user in Solforge):
   - Missions: `retail_preview_mission` {activity}, then `retail_port_mission` {activity}. It writes `content/activity/<id>.json` and `retail_ports/<id>/<id>.lua` with `report.json`.
   - Strings: `retail_port_strings` {container} writes `retail_ports/strings/<tag>.json`.
   - Single tags: `retail_convert_tag` {tag, relink} writes `retail_ports/tags/<tag>.bin` and `.json`, with the plan verdict.
6. Finish a mission port on the 86657 side with the solforge-new-mission skill:
   - Bind the script to an installed activity and map.
   - Replace every `CHANGE_ME` using `list_mission_objects` and `get_mission_object`.
   - Run `check_mission_script` and `simulate_mission`, then build.
   - The ported activity document is checked with `validate_content`.
7. Tell the user what was ported, what was a skeleton and what was left out (the report's `not_ported` list), and mark guessed fields as guessed.

## Without the editor
`solforge-retail-mcp.exe` (next to `Solforge.exe`) serves the read tools over stdio. It honours `--root`, `--media`, `--lookup`, `--engine` and `--cache`, or the `SOLFORGE_RETAIL_*` environment variables. Its config is in docs/MCP.md. The write tools and `retail_open_map` need the editor with a project open.

## UI
Use **View > Retail workspace**. Its tabs are:
- **Activities**: destinations, activity search, details, port plan and bubble buttons.
- **Map viewer**: open a bubble in the viewport with statics and terrain toggles, and see the selection's retail tag.
- **Tags**: info, decoded, annotated, hex and references.
- **Strings**: search, and write to the project.
- **Port**: preview, and port a mission into the project.
- **Status**.
