<p align="center"><img src="src/SunriseStudio.Editor/Assets/Sunrise.source.png" alt="Solforge" width="160"></p>

# Solforge

Mod editor for [Sunrise](https://github.com/stanuwu/Sunrise) — a fork of [Sunrise Studio](https://github.com/stanuwu/SunriseStudio) by [stan](https://github.com/stanuwu), extended with expanded tooling, an AI-assisted workflow, and a UE5-style UI.

- [Sunrise](https://github.com/stanuwu/Sunrise)
- [Blender Add-on](https://github.com/stanuwu/SunriseBlenderPlugin)
- [Upstream: Sunrise Studio](https://github.com/stanuwu/SunriseStudio)

## Features

- View assets, maps, missions and investment data
- Move objects, edit properties and collision
- Create weapons, items, vendors, static props, textures, materials, perk behaviors and more
- Subclass grid authoring and presentation editing
- CUI scene renderer and scene authoring
- Perk display sync and talent display tables
- Weapon recipe authoring via `.weapon.json` (Parhelion DSL)
- Edit mission scripts as graphs (Lua 5.4)
- Export to Blender, import from Blender, rebuild from Blender edits
- Attach to Sunrise with live CUI or in-world view
- Build and install projects as Sunrise mods

## MCP / AI Workspace

Solforge ships `solforge-mcp.exe` — a JSON-RPC 2.0 MCP server that exposes the editor state, project files, investment data, perk tools, item sources, CUI, and workflow tools to any connected AI client.

The `--sol` aggregate merges Solforge tools with sol-testkit, tiger-mcp and optional upstream delegates into a single MCP endpoint. Config files are written automatically for Claude Code, Pi harness, Antigravity and Cline when a project is open.

Settings → MCP shows the active server address. Skills and playbooks ship under `skills/`.

## Editor UI

UE5-style chrome: 30 px menu bar, 40 px toolbar (Back/Forward, transform tools, Place, Build, Render, Quality, Settings, Solforge AI pill), tabbed dock panels with drag-and-drop, and a 24 px status bar. Panels include Viewport, Content Browser, Outliner, Details, Output Log, Place and Solforge AI. The Ctrl+Space asset browser slides open as a spring-animated drawer.

The in-editor AI terminal and engine picker have been removed. A standalone Solforge AI desktop app handles model sessions; the editor's Solforge AI panel shows status cards, approvals, the AI log, AI edits with Undo, and connected sessions.

## Alpha

This is alpha software. Things will break, and some will work in unexpected ways. Keep backups of your projects. Features known to be incomplete carry a **WIP** tag in the editor — hover it to see what's wrong.

## Building

### Requirements

- Windows 10 or 11 with a Direct3D 11 GPU
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- A Sunrise (game) installation — Solforge reads packages and keys from there at runtime

No game packages, package keys or decoder DLLs are part of this repository.

### Build and run

1. Clone the repository
```powershell
git clone https://github.com/wyndxgo777/Solforge
cd Solforge
```

2. Build Solforge
```powershell
.\build.ps1                              # build into a timestamped test folder
.\build.ps1 -Output my-build            # build into a specific new/empty folder
.\build.ps1 -FinalOutput <deploy-path>  # build + sync into an existing deployment directory
```

Or double-click `build.cmd` for a quick build with a pause at the end.

For a low-level publish (just the dotnet step, no validation):
```powershell
.\publish.ps1 -Output publish-new   # use a new or empty folder (see RELEASING.md)
```

On first start, choose your Sunrise installation; Solforge remembers it. `Solforge.exe --help` lists all command-line options.

### Blender add-on

Opening Solforge exports in Blender, and making files Solforge imports from Blender, requires the Sunrise Blender add-on. It lives in `blender/` and ships with every build as `sunrise_blender_plugin-<version>.zip` next to `Solforge.exe`. Install it via Edit → Preferences → Get Extensions → Install from Disk.

### Run checks

```powershell
.\verify.ps1                    # all checks (no game required)
.\verify.ps1 -Game <path>       # adds package-reading checks
```

## Layout

| Path | Holds |
|---|---|
| `src/SunriseStudio.App` | Executable entry point and command line |
| `src/SunriseStudio.Editor` | Editor window, panels, pages, workflows, creation dialogs |
| `src/SunriseStudio.Viewport` | 3D/2D views, cameras, selection, gizmos, Sol theme |
| `src/SunriseStudio.Rendering` | Direct3D 11 renderer, game shaders, ImGui backend |
| `src/SunriseStudio.Scene` | Map, mission and asset scene decoding |
| `src/SunriseStudio.Packages` | Package reading/writing, names, disk cache |
| `src/SunriseStudio.Projects` | Projects, deltas, object edits, builds, mod installs |
| `src/SunriseStudio.Authoring` | Importers and object edit rules; weapon asset compilers |
| `src/SunriseStudio.Formats` | Typed tag readers/editors; glTF, OBJ, DDS, PNG conversion |
| `src/SunriseStudio.Interchange` | Blender bundles and world scene export |
| `src/SunriseStudio.Animation` | Skeletons, clips and skinning |
| `src/SunriseStudio.Audio` | Wwise reading, playback and export |
| `src/SunriseStudio.Missions` | Mission script graphs |
| `src/SunriseStudio.Live` | Link to a running game |
| `src/Solforge.Mcp` | MCP server, tools, approval gate, player tracking |
| `src/Solforge.Mcp.Shim` | `solforge-mcp.exe` — the `--sol` aggregate shim |
| `src/Solforge.Harness` | Provider catalog, model discovery, shared harness resources |
| `src/Solforge.Harness.Runtime` | Worker skills, subscription model worker, editor gateway |
| `src/Solforge.AiLauncher` | Standalone Solforge AI desktop launcher |
| `tests/` | Check programs; `verify.ps1` runs all except RootManifest and Sandbox |
| `licenses/` | Notice files for .NET runtime and Community Toolkit |
| `blender/` | Sunrise Blender add-on (Python, GPLv3, git subtree) |
| `skills/` | MCP playbooks for AI clients |
| `docs/` | Architecture, UI, feature and handoff docs |

## Contributing

Pull requests are welcome. Please follow these rules:

- **No Copyrighted Data** — All game data is read from the user's installation at runtime.
- **Clean Code** — Follow the project's existing style; comment and add docs.
- **Provide Documentation** — Explain what you changed, why, and the effects.
- **Run the Checks** — Run `verify.ps1`, with `-Game` if your change reads game data.
- **One Feature per PR** — Don't bundle unrelated changes.
- **Honest WIP** — Mark anything known to be broken or incomplete as WIP.

## Credits

### Upstream

Solforge is a fork of [Sunrise Studio](https://github.com/stanuwu/SunriseStudio) by [stan](https://github.com/stanuwu). The original project, all its contributors, and its credits apply in full — see [UPSTREAM.md](UPSTREAM.md) and [THIRD_PARTY.md](THIRD_PARTY.md).

### Team

- [stan](https://github.com/stanuwu) — Creator and lead developer of Sunrise Studio (upstream).
- All [Sunrise Studio](https://github.com/stanuwu/SunriseStudio) open source contributors.

### Dependencies

- [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET)
- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (Direct3D 11, DXGI, D3DCompiler)
- [BCnEncoder.NET](https://github.com/Nominom/BCnEncoder.NET)
- [Loretta](https://github.com/LorettaDevs/Loretta)

### Artwork

- [Solus](https://www.youtube.com/@Solus-yt) — Sunrise Studio Logo

### Ported code

Audio decoder ported from:

- [libvorbis](https://xiph.org/vorbis/) — Vorbis decoder and codebooks
- [aoTuV](https://github.com/AO-Yumi/vorbis_aotuv) — same codebooks, a second source
- [ww2ogg](https://github.com/hcs64/ww2ogg) — Wwise codebook layout and setup rebuild
- [vgmstream](https://github.com/vgmstream/vgmstream) — Wwise Vorbis setup rebuild, stereo downmix, float-to-PCM16 conversion

[wwiser](https://github.com/bnnm/wwiser) was used to verify composition results. No wwiser code is in Solforge.

See [THIRD_PARTY.md](THIRD_PARTY.md) for full license details on all ported code and libraries.

### Inspiration / Reference Repos

- https://github.com/zeex64/repkg
- https://github.com/KyleThmpsn/sundial
- https://github.com/OmikronApex/Tenebrae

No source was copied from any of them. See the [Sunrise credits](https://github.com/stanuwu/Sunrise#credits) for the research Sunrise Studio builds on.

## AI Disclaimer

AI was used in the development of this project — for reverse engineering, development and documentation purposes only. All AI-assisted work is reviewed by a human before release. AI was NOT used to create any artwork or creative writing.

## Affiliation Disclaimer

This project is not affiliated with Bungie or Sony in any way.
