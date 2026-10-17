---
name: solforge-new-armor
description: Use when the user asks to create, clone or modify an armor piece, set, ornament, shader or cosmetic item in SolForge; triggers "new armor", "clone this helmet", "armor set", "armor stats/perks", "new shader/emblem/ghost".
---
# New armor in SolForge

Goal: a cloned and edited armor item (stats, perks, name, icon, collections, vendor) built into a mod.

## Know the limits first
- Armor **Clone** works. Armor **New** is gray: there is no armor model writer. A genuinely new armor mesh cannot be made; the clone reuses the base item's art.
- Clone keeps undecoded art, gear entity, lore and policies. Per-class variants are separate items: clone each.
- Shaders keep dye data; Emblems/Clan banners keep banner art; Ghosts/Vehicles/Ships keep their model. Clone them the same way.
- Recolor via textures (`read_reference` "textures", "materials"): PNG replace must be single-mip RGBA8/BGRA8, same size; BC6H needs DDS. Texture edits are deltas and hit every user of that texture.
- Socket arrays cannot grow. Perk graph edits are shared by all items with that perk.
- Game must be closed to install.

## Steps
1. `get_context`, `get_open_documents`, `get_project`.
2. Find the base: `search_items`; `get_item` (stats, sockets, perks, bucket).
3. `list_creation_types`, `describe_creation` for the armor Clone type; stop and tell the user if a Blocker is set.
4. `start_creation`: Base item, Name, unused Package ID, Description, Collections page, Icon from item, Rarity, Inventory bucket, base stats, sandbox perks, socket plugs. The user presses Create.
5. `open_asset` the new item, then `apply_item_edit` (set_field, add_stat, remove_stat, add_perk, remove_perk). `search_perks` for perk hashes. A stat value of 0 uses the level curve; Computed/Conditional stats are expressions, edit them in the UI.
6. Icon and name/flavor text: UI Edit workflow (icon PNG 96x96; mods/shaders 96 + 40).
7. Sell it: `list_vendors`, `get_vendor`, `apply_vendor_edit` (duplicate_sale, set_sale_item, edit_cost; <= 4 prices).
8. Loot: add the hash to the server's `loot_pools.lua` (`Destiny-Sol-main\mission-scripts\loot_pools.lua`; `titan`/`hunter`/`warlock` lists for class armor).
9. `build_project` with `install=true` (game closed). Read the receipt.
10. Test in game: item shows in inventory/vendor, equips, stats match.

## UI fallback
Any Solforge control can be driven with `list_ui_controls` then `ui_invoke` / `ui_set_value` / `ui_set_checked` / `ui_select` (approve click each).
Menus, toolbar toggles and file pickers: `list_commands` then `run_command` (give `path` instead of a picker), `show_panel` before `list_ui_controls`, `export_asset` / `import_asset`, `set_viewport_options` / `set_camera`, `project_change`, `arm_file_dialog`; `get_help` with section `tools` names the tool for any menu, panel or dialog.
Armor > Clone dialog; Investment tab; Edit workflow; Build > Build and install.

## Verify
`get_item` shows the new stats/perks; `list_project_changes` is clean; build receipt has the new tag; item is visible and equippable in game.
