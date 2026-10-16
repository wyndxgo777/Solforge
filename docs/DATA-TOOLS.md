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

## Paging

Results come back as `{cursor, count, total | total_at_least, next_cursor, rows}`. To get the next page, pass `next_cursor` back as `cursor`. Lists are sorted deterministically, so cursors stay stable.

## Known gaps

- No dedicated decoders yet for activity and director tags, dialog tables, or loot tables. `infer_tag_schema` and `dump_tag` cover these generically.
- Havok, Criware USM and D1 script bytecode are not decoded.
- The decompiled code has no auto-analysis types. Struct inference works one function at a time, and RTTI class names are stripped in this build.
- The protector's second `.text` section (RVA 0x3CC9000+) is not decompiled.
