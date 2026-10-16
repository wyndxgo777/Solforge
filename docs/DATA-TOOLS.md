# SolForge data and decompiled-code tools

SolForge exposes these over its local MCP server (plain MCP, so any client works) and in **View > Data explorer**. Every button in that window calls the same tool handler an agent calls.

## Sources

| Source | Where | Override |
|---|---|---|
| Decompiled C (Ghidra, build 86657) | `<sol>\04-reverse-engineering\analysis\decomp\all\decomp_*.c` and `decomp\func_*.c` | `SOLFORGE_DECOMP=<analysis folder>` |
| Function sizes | `analysis\full\functions.csv` | |
| Names | `NOTES.md` tables, `listings\<name>_<RVA>.asm`, `solforge-names.csv` (written by rename_function) | |
| Strings, vtables | `analysis\image\destiny2_image.bin` and `image.json` (load base) | |
| Struct definitions, tag class uses | Sunrise client and server `src` | `SOLFORGE_SOURCE_ROOTS=a;b` |
| Tags, localized strings | the installation open in SolForge | |

The index is cached at `%LOCALAPPDATA%\Solforge\decomp\index-*.bin` and rebuilt when a decomp file changes. You can change the cache folder with `SOLFORGE_DECOMP_CACHE`.

## Tools

| Tool | What it returns |
|---|---|
| decomp_status | folder, counts, image, source roots and class-layer coverage |
| search_functions | functions by name, RVA or signature |
| get_function | C source paged by line, with signature, callers and callees |
| xrefs_to / xrefs_from | callers, data users and vtables / callees, data (with strings) and tag classes |
| find_string | exe strings with RVA and referencing functions |
| grep_decomp | full-text search of all function bodies |
| get_struct | image classes with inferred fields, header struct definitions, or fields inferred from a function's pointer use |
| get_vtables | vtables with their RTTI class, their entries and owners, and the vtables containing a function |
| get_class | a class: RTTI name, bases and derived classes, vtables, methods by slot, constructors, inferred fields, tag classes |
| tag_type_handlers | exe functions and source lines that use a tag class hash |
| rename_function | saves a name (approval) |
| get_string / search_strings | localized text |
| list_tag_classes / list_tags_of_class | classes with counts and labels; the tags of one class |
| read_tag_bytes / dump_tag | hex dump; generic decode of root fields and typed arrays |
| infer_tag_schema | per-offset layout for a class, built from many samples |
| export_data | runs any read tool over all pages and writes JSON or CSV to `<project>\exports` |
| havok_packfile | a Havok 2012 packfile (26/7): root, kind, class counts, imports, every collision shape decoded, cloth collidables, notes |
| export_collision_obj | the collision a packfile places by itself, as OBJ under `exports\collision` |
| audio_info | a Wwise event, bank (26/5) or stream (26/6): media ids, codec, rate, channels, length |
| export_audio_wav | Wwise media decoded to PCM16 WAV under `exports\audio` |
| video_info | a CRI USM cutscene (type 27): video, ADX audio tracks and subtitle streams |
| export_video | USM demux under `exports\video`: MPEG-1 `.m2v`, each ADX track as `.adx` plus decoded `.wav`, `.sbt` subtitles |

## Paging

Results come back as `{cursor, count, total | total_at_least, next_cursor, rows}`. To get the next page, pass `next_cursor` back as `cursor`. Lists are sorted deterministically, so cursors stay stable.

## Known gaps

- No dedicated decoders yet for activity and director tags, dialog tables, or loot tables. `infer_tag_schema` and `dump_tag` cover these generically.
- Havok: every collision shape class in build 86657 decodes (BV compressed mesh, static compound, list, convex vertices incl. plane-less hulls, box/sphere/capsule/cylinder/transform, shape arrays, physics bodies) and cloth collidables are drawn at rest. Ragdoll/hinge constraint data, cloth simulation data, cloth planes and cloth convex geometry are counted, not decoded. The build has no Havok animation.
- USM: the container, ADX audio (type 3, unencrypted) and subtitles decode; MPEG-1 video is exported as an elementary stream for an external player, not decoded in SolForge. HCA audio and encrypted USM/ADX are refused (none ship in this build).
- D1 script bytecode is not decoded.
- The decompiled code has no auto-analysis types. Struct inference works one function at a time, and RTTI class names are stripped in this build.
- The protector's second `.text` section (RVA 0x3CC9000+) is not decompiled.
