---
name: solforge-new-weapon
description: Use when the user asks to make, clone, copy or tweak a weapon (incl. an exotic, its perks, stats, icon, model, sockets, collections, loot or vendor placement) in SolForge; triggers "new weapon", "clone this gun", "make an exotic", "give it a perk", "sell it at the vendor", "add to loot".
---
# New weapon in SolForge

Goal: a new weapon item (clone of an existing one, or a new exotic) that builds, installs and works in game.

## Know the limits first
- Every item category has **Clone**; only **Weapons has New** (the exotic flow). Base frame for New exotic: Tommy's Matchbook only.
- There is no scratch item writer. Always start from a Clone of the closest stock weapon.
- Clone keeps undecoded art, gear entity, lore and policies. A new 3D model needs the Blender exotic flow (step 4).
- Perk behavior edits are shared: changing a perk graph changes every item that uses it. Clone the perk first if the change must be private.
- Perk effects verified in game: only "When activated", "After a delay", "Adjust ammo drops". Other graph nodes may compile but not run.
- Tuning that works in game: Fire rate RPM (60-6000), Damage per shot, Magazine rounds. Other gameplay values may not.
- Sunrise server sells at static prices only. Socket arrays cannot grow. Vendor categories cannot be added.
- Game must be closed to Build and install.

## Steps
1. Orient: `get_context`, `get_open_documents`, `get_project`. If there is no project, ask the user to create one (Build > Mod details needs it).
2. Find the base: `search_items` (name or hash), then `get_item` for stats, sockets, perks. Pick a weapon of the same frame/family.
3. Check the type: `list_creation_types`, `describe_creation` (weapon Clone / exotic New). If a Blocker is set, tell the user plainly and stop.
4. Create: `start_creation` with the type key and fields: Base item, Name, unused Package ID, Description, Collections page, Icon from item, Rarity, Inventory bucket, base stats, sandbox perks, socket plugs. The user presses Create. Confirm with `list_project_changes`.
   - Shared frames: clone the frame in "Mods and plugs" first, then pick it in Socket plugs.
   - Exotic New: needs a Blender export (`<name>.glb`, `.sunrise-original.json`, `.sunrise-weapon.json`; five-bone rig, four art parts). `read_reference` "exotic-weapon" and "original-asset". Names <= 200 chars, descriptions <= 4000.
5. Open the new item with `open_asset`, then edit with `apply_item_edit` (ops: set_field, add_stat, remove_stat, add_perk, remove_perk). Use `search_perks` for perk hashes. Each op needs the user's approve click.
6. Perk behavior: new/clone a perk via `start_creation` (Perks), then edit in the perk graph UI (Assets > Edit perk behavior; no MCP tool). Use only the verified nodes above.
7. Tuning, icon, frame text, HUD silhouette, weapon type label: UI only (Edit workflow). Icon PNG single-mip RGBA8 (96x96; perk icons 96 + 40; HUD silhouette 137x76). `read_reference` "textures".
8. Collections: set the Collections page in the Clone dialog.
9. Loot: add the item hash to `loot_pools.lua` (server file `Destiny-Sol-main\mission-scripts\loot_pools.lua`; pools hold decimal-hash keys with weights; `catalog = "exotic_weapons"` adds every installed exotic). Use `read_project_file`/`apply_project_edit` if it is in the project, else tell the user.
10. Vendor: `list_vendors`, `get_vendor`; `apply_vendor_edit` (duplicate_sale, set_sale_item, edit_cost). Max 4 prices per sale. Refused if sale data is undecoded.
11. Build: `get_build_status`, then `build_project` with `install=true` (ask the user to close the game first). Read the receipt for errors.
12. Test: start the game, find the item in Collections/vendor/inventory; check name, icon, stats, perks; fire it.

## UI fallback
Any Solforge control can be driven with `list_ui_controls` then `ui_invoke` / `ui_set_value` / `ui_set_checked` / `ui_select` (approve click each).
Weapons > Clone / New dialog; Investment tab for fields, stats, perks; Edit workflow for icon/tuning; Build > Build and install.

## Verify
- `get_item` on the new hash shows the intended stats and perks.
- `list_project_changes` has no unexpected rows.
- Build receipt lists the new tag; installed mod folder has a fresh timestamp.
- In game: item appears, no crash on equip, perk triggers.
