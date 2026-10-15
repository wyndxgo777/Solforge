# Solforge architecture (as of upstream 007dd7a)

This describes the code that exists in the repository today, audited from `src/`, `tests/`, the
PowerShell scripts, `Directory.Build.props` and `Sunrise Studio.sln`. The target design lives in
`sol\08-docs\Studio.md` §3; the mapping is at the end. Projects, assemblies and namespaces keep
their `SunriseStudio.*` names to keep upstream merges cheap. Only the exe (`Solforge.exe`) and the
user-visible strings are rebranded (Phase 03).

## Toolchain

| Item | Actual |
|---|---|
| Language | C# 13 (`LangVersion` 13.0), nullable + implicit usings on, `AllowUnsafeBlocks` on |
| Frameworks | `net9.0` (portable libraries) and `net9.0-windows` (UI, renderer, audio, live link) |
| Platform | x64 only (`PlatformTarget` x64), deterministic builds (`ContinuousIntegrationBuild` on CI), warnings are errors in `src/` (not tests, not NuGet audit), version 0.1.0 |
| Native code in repo | None. No C++ or Rust projects; the solution is 100% C# |
| UI stack | WinForms host window (`UseWindowsForms`) + Dear ImGui (ImGui.NET) drawn by the D3D11 renderer. No WPF. Layout is Studio's own split tree (`StudioLayout`, `StudioForm.Layout.cs`): draggable splitters, panel show/hide, maximize, pop-out windows, saved per user. ImGui's docking flag is off on purpose (see Phase 03 below) |
| Renderer | Direct3D 11 (feature level 11_0) via Vortice.Direct3D11 / DXGI / D3DCompiler 3.8.3. No D3D12, SharpDX or Veldrid |
| Lua parsing | Loretta.CodeAnalysis.Lua 0.2.13 with `LuaSyntaxOptions.Lua54` |
| Texture encode | BCnEncoder.Net 2.3.0 |
| Package restore | `.nuget/packages` inside the repo (`RestorePackagesPath`) |
| Tests | 35 console check programs (`OutputType` Exe), no xUnit/NUnit. 33 are in the .sln; the two weapon checks build from `verify.ps1` |
| CI | `.github/workflows/build.yml`: windows-latest, .NET 9.0.x, `build.ps1`, `verify.ps1 -SkipBuild` (no game, no Blender), then `publish.ps1` |
| Blender add-on | `blender/` (Python, Blender 5.2+, `sunrise_blender_plugin` 1.13.19, GPLv3), git subtree |

## Scripts

| Script | Does |
|---|---|
| `build.ps1` | `dotnet build "Sunrise Studio.sln"` (Release by default, serial, no shared compiler) |
| `publish.ps1` | Self-contained single-file `win-x64` publish of `SunriseStudio.App` to `publish\Solforge.exe`; notices are embedded |
| `run.ps1` | Starts the developer build; `-Game`, `-Project`, `-Tag`, `-Warp` pass through |
| `verify.ps1` | Builds and runs every check program; `-Game <install>` adds checks that read real packages; `-Blender`/`-Plugin` add Blender round-trip checks |

## Projects and dependencies

`App -> Editor -> {Packages, Projects, Formats, Authoring, Interchange, Missions, Live, Audio, Viewport}`

| Project | TFM | References | Packages | .cs / LOC | Holds |
|---|---|---|---|---|---|
| Packages | net9.0 | - | - | 38 / 5.5k | Package header/entry/block reading (`PackageFile`, `PackageCatalog` + disk cache), AES keys (`PackageKeys`), Oodle (`OodleDecoder`), `PackageWriter`, string banks, name caches |
| Animation | net9.0 | Packages | - | 25 / 3.5k | Skeletons, clips, skinning |
| Projects | net9.0 | Packages | - | 23 / 4.4k | `sunrise.project.json` (`StudioProject`), binary deltas, object edits, `ProjectBuilder`, `ModInstaller` (`mod.json`, writes only under `bin/x64/Sunrise/mods`) |
| Scene | net9.0 | Packages, Animation | - | 82 / 14.4k | Map/world/scenario decoding, static and entity meshes, terrain, volumes, collision, spawns, squads |
| Formats | net9.0 | Packages, Scene, Animation | - | 81 / 14.9k | Typed tag readers/editors: investment (items, sockets, stats, vendors, lore), perk behavior graphs, CUI, Tome, glTF/OBJ/DDS/PNG |
| Authoring | net9.0 | Projects, Formats, Packages | - | 70 / 13.6k | Importers and edit rules; weapon asset compilers (model, material, animation, particle, reticle, tracer, tuning) |
| Missions | net9.0 | Projects | Loretta | 22 / 4.8k | Mission script graphs, Lua <-> graph conversion, mission SDK index/API schema, validation |
| Rendering | net9.0-windows | Packages | Vortice x3, ImGui.NET | 60 / 11.3k | D3D11 renderer, game shaders, ImGui backend, `Data/NativeRenderStates86657.json` |
| Viewport | net9.0-windows | Packages, Scene, Animation, Rendering | - | 56 / 15.9k | 3D/2D views, cameras, selection, gizmos, shortcuts |
| Interchange | net9.0-windows | Authoring, Rendering, Formats, Viewport | BCnEncoder.Net | 34 / 8.2k | Blender bundles, world scene export |
| Audio | net9.0-windows | Packages | - | 27 / 5.7k | Wwise/WEM reading, C# Vorbis decoder port, playback/export |
| Live | net9.0-windows | - | - | 14 / 1.6k | Link to a running game over shared memory |
| Editor | net9.0-windows (WinForms) | 9 projects above | - | 155 / 27.7k | `StudioForm` partials, pages, creation dialogs (`Creation/`: items, exotic weapons, perks, vendors, materials, textures, static props) |
| App | net9.0-windows WinExe | Editor | - | 1 / 74 | Entry point and command line |

## Phase 03 notes (shell)

- **Branding:** window, pop-out and toolbar titles, `--help`, error boxes and two project messages say
  Solforge, as do the loading screen and the empty workspace title (SOLFORGE); the exe is `Solforge.exe`. Help menu and the toolbar version tooltip credit Sunrise Studio
  by stan; `THIRD_PARTY.md` opens with the same credit. Kept on purpose: AppData folders
  (`%LOCALAPPDATA%\Sunrise Studio\...` for settings, cache, live reports, so existing users keep them),
  glTF/OBJ `generator` strings and bundle readmes (the Blender add-on checks them), the "Sunrise Studio"
  shortcut preset name (tests and saved settings use it), and the Blender add-on.
- **Docking:** not switched to ImGui docking. Studio already has a docking-style layout (split tree,
  splitters, pop-outs, saved layouts) that the 3D viewport and every page depend on; replacing it would be
  a large rewrite of upstream code with no new capability.
- **Status bar:** `StudioForm.Solforge.cs` adds a right-aligned block to the existing status bar:
  `D2 86657 | packages: N | keys: export/exe scan | cache: <name cache state> | mem: N MB`, refreshed
  once a second; hover for details.
- **UI-thread I/O audit:** long work already runs through `StudioWorkspace.Run` / background tasks
  (load, build, install, import, export). The per-frame `Directory.Exists` in the status bar notice was
  the only clear offender and is now cached per folder. Left as is (short, user-triggered, one file):
  icon/asset `File.ReadAllBytes` in creation dialogs and Import delta, settings/layout saves, and the
  `Directory.Exists` in the mission script combo (runs only while the combo is open with no scripts).

## Phase 03B notes (layout presets)

- **Presets** (`Editor/StudioLayout.Presets.cs`, new partial): `Solforge` (the default: Asset browser left,
  Workspace centre, Object tree over Inspector right, Project changes | Mission Graph bottom row),
  `Solforge (classic)` and the upstream `Sunrise Studio` tree. `Remove`/`Reinsert` take an area out and put it
  back beside the same neighbour (the Ctrl+Space Asset browser drawer). Reset layout gives `Solforge`.
- **First run:** `Solforge` applies only when there is no settings file and no saved layout, and never in smoke
  runs: the UI checks were written against the upstream default and have not been re-baselined.
- **Viewport** (`Viewport/AssetViewport.Fly.cs`, new partial): right drag + W/A/S/D/Q/E flies the orbit camera,
  the wheel sets fly speed meanwhile; an unbound right drag is a fly look. Q/W/E/R pick the transform tool,
  never while flying, dragging or in the player camera (which owns W/A/S/D, Space and Ctrl).

## Phase 03C notes (Sol theme)

- **SolTheme** (`Viewport/SolTheme.cs`, new file): the sol.css token set, launcher sizes (title 17, body 14,
  label 11, button 12.5 px; 28 px controls) and draw helpers: `ChamferFrame`, `ChamferGradient`/`PrimaryButton`
  (sun-grad), `SectionHeader`/`CollapsingSection`, `PanelHeader` (notch + uppercase title), `ModeButton`
  (sun underline), `LeftBar`, `Chip`, `IconTint`. Lives in Viewport so the viewport and `ImGuiIcons` can use it.
- **Fonts:** Chakra Petch 600/700 and Rajdhani 500/600 are embedded (`Solforge.Fonts.*`) and added through
  `ImGuiController.HostFonts`, which the editor sets before creating its controller; the controller's 16 px
  body size gives the DPI scale. Without the hook (tests, other hosts) the controller keeps Segoe UI.
- **Upstream files** keep small edits: `StudioTheme` constants and `Apply` forward to SolTheme, `BeginPanel`
  draws the panel gradient, `StudioUi.Heading` draws a section header, and each restyled call site is one line.
  Viewport selection outlines and bubble boxes keep their colours.

## Runtime and native dependencies

- **Oodle**: `oo2core_3_win64.dll` is loaded with `NativeLibrary.Load` from the user's game install
  (`<game>\bin\x64\`). It is never shipped or committed.
- **Package keys**: `<game>\bin\x64\Sunrise\exports\package_keys.bin` written by the Sunrise client
  (`client.dump_package_keys`), with a fallback that signature-scans the installed executable. Keys
  never enter project files.
- **cimgui**: native library inside the ImGui.NET NuGet package.
- **Win32 P/Invoke**: user32, kernel32, dwmapi, dwrite, winmm. D3D compiler comes from Windows.
- **Live link**: memory-mapped files `Local\SunriseStudio.{Live,Command,Cui,Object,Pose,World}.<pid>`
  shared with the Sunrise client DLL. No sockets.
- **Mods**: a build installs to `<game>\bin\x64\Sunrise\mods\<mod>` with `mod.json`, loaded by the
  Sunrise 0.6 mod loader.

## Package format facts in code

- Header read at fixed offsets: build `0x18`, variant `0x1A`, patch `0x20`, entry count `0xB4`,
  entry offset `0xB8` (or `0x110`+96 for variant 1), block count `0xD0`, block offset `0xD4`.
- Tag id = `0x80800000 + (packageId << 13) + entryIndex` (13-bit index field).
- Entry block info: start block = low 14 bits, start offset = next 14 bits << 4.

## Studio.md §3 mapping

| Studio.md subsystem | Exists today | To build |
|---|---|---|
| Solforge.App / Editor | `App`, `Editor` (WinForms + ImGui) | Rebrand, AI dropdown/terminal pane |
| Solforge.Projects | `Projects`: `sunrise.project.json`, deltas, object edits, transactions | `.solproj` naming, `overlay/tags/*.tagpatch` layout, base-tag SHA256 snapshots |
| Solforge.Packages | `Packages`: reader, catalog cache, Oodle, AES keys, writer | Memory-mapped indexer, entry-limit sharder into `w64_sol_custom_*` packages |
| Solforge.Scene / Viewport | `Scene`, `Viewport`, `Rendering` (D3D11, gizmos, selection) | D3D12 (if wanted), PBR/cubemap/terrain tessellation per spec, spawn/flight-path markers beyond current set |
| Solforge.Formats | `Formats` (+ `Animation`, `Audio`) | FourCC table catalog as spec describes |
| Solforge.Missions / Graphs | `Missions` (graphs + Lua conversion, Lua 5.4 parser) | Separate Graphs lib (optional), Tiger VM limit validator, Lua language server |
| Solforge.Weapons | No project. Weapon creation is spread over `Authoring` (Weapon*), `Formats` (Investment*, WeaponConfigEdits, NativePerkBehavior) and `Editor/Creation` (ExoticWeaponWorkflow, PerkBehaviorWorkflow) | Parhelion port, `.weapon.json` recipe DSL, stat/TTK validator, consolidation into one library |
| Solforge.Content | Partial: investment items/vendors/sockets/stats, CUI, Tome editors in `Formats` | Armor 2.0, subclass trees, combatant/AI squads, raid encounters, seasonal artifact |
| Solforge.Build (`solc`) | `ProjectBuilder` + `ModInstaller` inside the GUI | CLI compiler, `.solmod` container, rollback snapshots |
| Solforge.Live | `Live` over shared memory (CUI, pose, world, object, commands/reload) | Teleport/spawn actions, telemetry HUD; spec's TCP `127.0.0.1:30978` does not exist |
| Solforge.AI | None | All of Phase 47B |
| (not in §3) | `Interchange` (Blender), `Authoring`, `Animation`, `Audio`, `Rendering` | Keep as-is; add to §3 |

Real count is 14 libraries (incl. App), not 20.
