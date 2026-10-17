---
name: solforge-decomp
description: Use when the user asks how the game client does something, wants a function, offset, struct, vtable, log string or tag class traced in destiny2.exe (build 86657), or wants raw tag data, an unknown tag class decoded, localized text by hash, or data exported to JSON/CSV in SolForge; triggers "find the function that", "what calls", "decompile", "xrefs", "what's at +0x390", "which code reads this tag type", "dump this tag", "export to csv".
---
# Decompiled code and deep data in SolForge

These are plain MCP tools: JSON-schema inputs, text output. They work with any MCP client (Claude, Codex, Gemini/Antigravity, Cline, Cursor) and need no open map.

## Paging
Big results are paged. Each page has `rows`, `count`, `total` (or `total_at_least` for scans), and `next_cursor`. To get the next page, call the same tool with `cursor` set to `next_cursor`. A null `next_cursor` means the last page. `get_function` pages source by line in the same way, printing `next_cursor: N` in its header.

## Addresses
All addresses are RVAs (offsets from the image start), for example `0xE2DBE0`. `FUN_00E2DBE0` and `DAT_01C277D0` in the C are RVAs too. Tools accept `0xE2DBE0`, `E2DBE0`, `FUN_00E2DBE0`, or a known name.

## Start
1. Call `decomp_status` to see the analysis folder, function count, named count, whether the image (strings and vtables) is present, and the source roots.
2. To find code:
   - by name or signature: `search_functions`;
   - by text inside bodies (constants, offsets, callees): `grep_decomp`;
   - from a log line or asset path: `find_string`, which returns the RVA plus the functions that reference it.
3. Read a function with `get_function`.
4. Walk the graph with `xrefs_to` (callers, data users, containing vtables) and `xrefs_from` (callees, data with strings, tag class constants).
5. Struct layouts come from `get_struct`:
   - `name=` searches the Sunrise client and server C/C++ headers;
   - `function=` with `variable=param_1` infers fields by offset and type from the decompiled C.
6. Classes and virtual methods: `get_class` (`class=` a name, vtable RVA or method; no args lists classes, `rtti=true` only RTTI-named ones) gives bases, derived classes, vtables, methods by slot, constructors and fields inferred from this-pointer use. `get_vtables` (`function=` lists the vtables containing it; `address=` lists the entries and owners).
   - The class layer comes from MSVC RTTI in the image (mostly Havok classes; Bungie code has no RTTI) plus pointer-run vtables named `vtable_RVA`. Unnamed virtual functions are shown as `Class::vfN`; a name you give with `rename_function` wins. It is built once per image and cached beside the decomp index.
7. Tag type to code: `tag_type_handlers class=0x8080xxxx` lists the exe functions that compare against that class hash and the Sunrise source lines that name it.
8. When you work out what a function does, name it with `rename_function` (the user approves). Names are saved to `solforge-names.csv` in the analysis folder and shared with every agent and the Data explorer window.

## Package data (needs an installation open in SolForge)
- `list_tag_classes unlabelled=true` lists the classes SolForge has no decoder for.
- `infer_tag_schema class=` gives a per-offset layout across many tags (tag refs, strings, floats, arrays, stable or varying).
- `dump_tag hash=` decodes one tag generically: root fields and typed arrays (marker 0x80809FBD) with their element classes and strides.
- Known classes have a schema in the tag schema registry (`list_tag_schemas`, `get_tag_schema schema=`): named fields, each confirmed or guessed with its source. `dump_tag` and `get_tag` add them under `named`. `validate_tag` checks bytes against the schema. `search_tags_by_field class= field= value=` finds tags by a field value (`table[].record->.internal_name` walks arrays and pointers).
- Game tables: `list_activities`, `get_activity`, `get_loot_table` (client reward lists; drop tables are server-side) and `get_dialog_table`.
- `read_tag_bytes` gives a hex dump. Pass `original=true` for the shipped bytes instead of project edits.
- `get_string hash=` and `search_strings query=` read localized text. The first call loads every bank.

## Export
`export_data tool=<any read tool> args={...} format=csv|json` pages through every result and writes `<project>\exports\<file>`. It returns the path.

## Rules
- Decompiled output has Ghidra default types and no auto-analysis, so caller lists count direct calls only. Indirect calls go through vtables, so check `get_vtables`.
- A function listed in FAILED.txt has no C. Read its assembly in `full\text\*.asm` instead.
- Struct inference covers one function only. Merge it with the callers' view before trusting a layout.
