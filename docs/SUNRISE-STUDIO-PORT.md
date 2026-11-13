# Sunrise Studio upstream port (weapons, maps, CUI)

Compared on 2026-11-12. Upstream is `E:\SunriseStudio` at 3a0e6f7 (2026-11-08). Solforge forked at upstream
007dd7a (2026-10-15). Two upstream commits came after the fork:

| commit | date | content |
|---|---|---|
| 0f41c40 | 2026-10-16 | GPL-3.0 `LICENSE`, a fix to world-scene motion export, a clear error for missing package keys, a Help > Licenses menu |
| 3a0e6f7 | 2026-11-08 | "tag rewrite, cleanup, more": 571 files, +287k/-9.5k lines (most of it generated code and a 670k-line registry JSON) |

Before this batch, Solforge contained none of 0f41c40 or 3a0e6f7 (checked: `EnsureKeys`, the motion notice and `Notices.License` don't exist
in Solforge).

## The main finding

Most of 3a0e6f7 is a refactor and adds no new features. It adds three projects that Solforge lacks:

- `SunriseStudio.Tags`: tag documents, views, a cloner, the TFT compiler and diffs.
- `SunriseStudio.Tags.Generated`: 174 files of typed C# structs for every tag class.
- `SunriseStudio.World`: destination and activity content.

It also adds `registry/86657.json` (the tag schema registry) and `data/*.json` (seed registry, layout conflicts). Nearly
every weapon, CUI and investment file was then rewritten from raw offsets (`r.U32(p + 16)`) to typed fields
(`TagView.At<SGearRow>(...).GearKey...`). In the weapon and CUI editors the behaviour is the same before and after. I
checked this hunk by hunk for `CuiDocument`, `CuiWidgetAuthoring`, `CuiBindings`, `CuiFonts`, `CuiConfigBindingCompiler`,
`WeaponArchetypeAuthoring`, `ModelSkinReader`, `WeaponPage`, `CuiPage*` and `StudioWorkspace.Exotic`. The offsets match:
for example, `WeaponTranslationDefinition` at +0x3E8 plus `Groups` at +72 gives the old absolute 0x430.

So upstream's genuinely new features fall into two groups:

- **Behind the Tags layer.** These are the map features: adding and removing statics, destinations, world bundle import,
  live bubble reload and the generic tag inspector. None of them can be ported without first adopting
  `SunriseStudio.Tags` and the generated structs, or rewriting them against raw offsets.
- **Small hardening changes.** New guards and error messages inside the migrated files.

For CUI, upstream adds nothing beyond Solforge. Solforge is ahead there: the CUI scene renderer, `CuiSceneInfo`,
`CuiSubclassGrid`, AI CUI tools and live CUI.

For weapons, upstream adds no new weapon workflow:

- It is still Tommy's Matchbook only (`ExoticWeaponWorkflow`, `WeaponAssetEntityCompiler`).
- It has no rigid or non-Tommy import; Solforge's `RigidExoticImport.cs` is ahead.
- It has no perk-graph authoring and no Parhelion support.

Its weapon changes are the refactor plus guards, and one policy change (item W2 below).

Effort: S = under 1 hour, M = a few hours, L = days or a new subsystem. Risk refers to Solforge's own additions and to
data written to game packages.

## Cross-cutting prerequisite

| # | feature | upstream files | Solforge counterpart | effort | risk | order |
|---|---|---|---|---|---|---|
| T1 | Typed tag layer: generated structs, `TagView`/`TagArray`/`TagPointer`, `TagDocument`, `TagCloner`, `TagRelocator`, TFT compiler, registry | `src/SunriseStudio.Tags/**`, `src/SunriseStudio.Tags.Generated/**` (174 files), `src/SunriseStudio.Packages/Registry/*`, `registry/86657*.json`, `data/*.json`, `src/SunriseStudio.App/TftCommand.cs`, `Packages/{StableJson,StringHash,TagHashes,WordScan,Crc32,PackageHeaders,PackageMisc}.cs` | none. Solforge uses `TagReader`, `NativeRows` and literal offsets | L | High. Touches most Formats/Authoring/Scene files and conflicts with every Solforge edit in them (`InvestmentItemArt`, `InvestmentPerkDisplayEdits`, `WeaponAssetEntityCompiler`, perk display sync, subclass grid, `RigidExoticImport`). Adding the projects without migrating existing files is M and low risk, because they are additive and nothing would call them yet | Decide first. Without it, M1-M5 must be rewritten against raw offsets |

## 1. Weapons

| # | feature | upstream files | Solforge counterpart | effort | risk | order |
|---|---|---|---|---|---|---|
| W1 | Reimport guard: when a weapon package runs out of entries, refuse instead of producing a tag in the next package (`nextEntry` past 8191 overflowed into package+1) | `Authoring/WeaponAssetReimporter.cs` | same file, without the guard | S | Low | **Ported** |
| W2 | Templates checked by structure instead of SHA-256 pins. Upstream dropped the "unchanged checked provider/profile" hashes and added `ValidateTemplate` (point provider), class and size checks (particle profile, presentation configs, art source). Weapon points, particles and presentation then still work when those templates were already changed by the project or a game data update | `Authoring/WeaponAttachmentAuthoring.cs` (`ValidateTemplate`), `WeaponParticleBinding.cs` (`Read`, `NodeOwner`), `WeaponPresentationAuthoring.cs`, `WeaponAssetEntityCompiler.cs` | same files, which keep the SHA pins | M | Medium. Relaxes a safety check, and the guards must be translated to raw offsets. `WeaponAssetEntityCompiler.cs` has uncommitted Solforge edits | Seth's decision (see below); after T1 if taken |
| W3 | Translation config: follow `SEntityConfig.Definition` instead of fixed absolute offsets, refuse a config with no translation definition, guard a negative group | `Formats/WeaponTranslationEdits.cs` | same file, using absolute 0x430/0x620 | S | Low. The offsets are equal for stock layout | **Ported** |
| W4 | Tracer sequence walked by structure (`TrailRow`, `HasColours`) instead of offset 0xFA0 | `Authoring/WeaponTracerAuthoring.cs` | same file (identical to fork) | S-M | Low. The feature is still marked "WIP, broken in game" upstream too | Low value; skip until the tracer works |
| W5 | Shared vertex-stream codec and skin-stream readers (refactor; strides 8/12/16 already supported) | `Authoring/VertexStreamCodec.cs`, `Animation/SkinPositionStream.cs`, `Animation/HavokClothPackfile.cs` | logic inline in `CompatibleSkinnedMesh`, `WeaponAssetModelCompiler`, `ModelSkinReader`, `HavokSkinBinding` | M | Medium. Touches the files `RigidExoticImport` and `tools\weapon-model` depend on, with no gain in behaviour | Only with T1 |
| W6 | `ItemWorkflow` info texts (collectible/node wording, translation-group explanation) | `Editor/Creation/ItemWorkflow.cs` | same file | S | Low | Optional |
| W7 | Socket expression copy and vendor additional-field handling in the typed rewrite | `Formats/InvestmentSocketEdits.cs`, `InvestmentVendorEdits.cs`, `InvestmentItemClone.cs` (clone moved toward `TagCloner`) | same files | M | Medium. Behaviour unverified; needs a hunk-by-hunk review once T1 is decided | After T1 |

## 2. Maps

| # | feature | upstream files | Solforge counterpart | effort | risk | order |
|---|---|---|---|---|---|---|
| M0 | World-scene export: a shader-program vertex motion no longer leaks into the exported world motion (it carries only colour-wave constants); a "Vertex motion / Not exported" notice is added instead (0f41c40 "fix motion shader values") | `Interchange/WorldSceneExport.Geometry.cs`, test `tests/SunriseStudio.Interchange.Tests/WorldSceneChecks.cs` (`ProgramMotion`) | same files | S | Low | **Ported** |
| M1 | Copy and remove static instances in a map: `ObjectEditKind.AddInstance/RemoveInstance/AddPlacement` and `SceneEdits.CreateCopyEdit/CreateRemoveEdit`. A copied static gets its own table, bounds, isolated owner and placement list, so no installed table changes. Removal goes through the cull or visibility map | `Authoring/StaticAdditionRule.cs`, `StaticPlacement.cs`, `BoxBounds.cs`, `InstanceCullRule.cs`, `SceneEdits.cs`, `InstanceIsolation*.cs`, `Projects/ObjectEdits.cs`, `Editor/StudioForm.Selection.cs`, `Rendering/Dx11Renderer.Scene.cs`, `Scene/Collision/CollisionSceneLoader*.cs` | `ObjectEditKind` has only `PlacementTransform, TriggerBounds`; Solforge has `edit_placement`/`edit_bounds` MCP tools | L | High. Writes map packages, and needs T1 or a raw-offset rewrite of about 600 lines | **First after T1**; highest-value map item. Expose it as MCP `copy_static`/`remove_static` next to `edit_placement` |
| M2 | Destinations and activities: create a new destination or activity from a template world, cut a world, and per-mod activity SDK packs | `World/**` (WorldTemplate, WorldCut, WorldFiles, WorldContent, WorldIdentity, WorldSkeleton, ActivityRows, Sdk/*), `Editor/Creation/DestinationWorkflow.cs`, `ActivityWorkflow.cs`, `Editor/StudioWorkspace.Destinations.cs`, `Projects/ReferenceMap*.cs`, `Scene/SdkShard.cs` | none (Solforge has the D1 and retail map tools, which are separate) | L | High. New content type; it interacts with ProjectBuilder merge sidecars and with Solforge's D1 work (D1 must stay local) | After M1 |
| M3 | World Import Bundle: place the statics of a Blender `bubble.json`+glb bundle in a destination bubble, anchored on its first spawn | `Authoring/WorldBundleImport.cs` | none (Solforge imports single meshes) | M once M1+M2 exist | Medium | After M2 |
| M4 | Live map reload: reload a bubble or a changed mod in a running game without restarting | `Live/LiveReloadManifest.cs`, `Projects/{LiveModState,LivePoke,ModInstaller.Live}.cs`, `Editor/LivePage.Updates.cs`, `Editor/StudioWorkspace.Mods.cs` (`ReloadBubble`, `LiveUpdateReport`) | Solforge has `update_running_game` and `reload_mission_scripts`. The Sunrise client side must support the reload command | M-L | Medium. Needs the matching Sunrise client version; `LivePoke`/`ModInstaller.Live` use T1 | Check client support first |
| M5 | Squad placement additions, decorator/terrain buffer reads (`GpuBufferData`), collision payload reads | `Scene/AuthoredSquads.cs` (`AddSquadPlacement`), `Scene/GpuBufferData.cs`, `Scene/DecoratorPlacements.cs`, `Scene/Collision/StaticCollisionReader.cs`, `Scene/PlacementPayloads.cs` | same files, older | M | Medium | After T1 |
| M6 | Generic tag field editor (Tag page and inspector: edit any tag field by schema, tag diffs) | `Editor/TagPage.cs`, `Editor/TagInspector.cs`, `Editor/StudioWorkspace.Tags.cs`, `Editor/StudioWorkspace.ProjectContent.cs`, `Formats/ViewProperty.cs`, `Projects/TagDocumentDeltas.cs` | Solforge reads raw tags via MCP (`retail_read`, decomp tools) and has a tag diff document | L | Medium | With T1 |

## 3. CUI / UI

| # | feature | upstream files | Solforge counterpart | effort | risk | order |
|---|---|---|---|---|---|---|
| C1 | CUI tree, widget, binding, font, property and preview code moved to typed structs | `Formats/Cui*.cs`, `Authoring/CuiConfigBindingCompiler*.cs`, `Editor/CuiPage*.cs`, `Editor/LivePage.Cui.cs` | same files plus Solforge-only `CuiPage.Scene.cs`, `CuiSceneRenderer.cs`, `CuiSceneInfo.cs`, `CuiSubclassGrid.cs`, `StudioForm.Ai.Cui.cs`, `SubclassPresentationEdits.cs` | M (with T1) | Medium. Conflicts with the Solforge CUI scene and subclass-grid code | Only with T1. Behaviour is identical: the 64 MiB limit, font filename bounds, binding comparison checks and the `TagSize` word at +0 all exist already |
| C2 | Help > Licenses dialog showing Studio's GPL-3.0 licence alongside the third-party notices | `Editor/StudioForm.Notices.cs`, `StudioForm.Commands.cs`, `Editor.csproj` (embeds `LICENSE`) | Solforge has a "Third-party notices" dialog | S | Low | After Seth's licence decision |

## 4. Other upstream changes (outside the three areas)

| feature | upstream files | effort | note |
|---|---|---|---|
| Clear error when Sunrise hasn't exported package keys (`client.dump_package_keys`), with keys loaded when the install opens | `Packages/PackageKeys.cs`, `PackageCatalog.EnsureKeys`, `StudioWorkspace.cs` | S | Useful for testers. Solforge's `PackageCatalog`/`StudioWorkspace` have diverged (131 and 93 changed lines), so merge by hand |
| Package remap, package profiles, merge recipe tables and sidecars (generated) | `Packages/PackageRemap.cs`, `Projects/PackageProfile.cs`, `Projects/Generated/*` | M-L | Needed by M2. Interacts with Solforge's `ProjectBuilder` changes |
| Perk behaviour graph rewrite (`NativePerkBehavior*`, `PerkBehaviorCensus`, labels) | `Formats/NativePerkBehavior*.cs` | L | Conflicts with Solforge perk-graph authoring (`Solforge.Graphs`, `CustomPerkAuthoring`, `StandalonePerkAuthoring`). Don't port blindly |

## Solforge additions an upstream merge must not regress

- MCP: `src/Solforge.Mcp` (`SolforgeTools.*`, including the uncommitted `SolforgeTools.Project.cs`), `Solforge.Mcp.Shim`, `Solforge.Retail.Mcp`, and the `--sol` aggregate.
- Parhelion bridge: `src/Solforge.Weapons`, `start_parhelion_weapon_recipe`, `.weapon.json`.
- Weapon-model CLI: `tools/weapon-model` (uncommitted), plus `src/SunriseStudio.Authoring/RigidExoticImport.cs` (uncommitted Borealis rigid import). It depends on the current raw-offset `WeaponAssetModelCompiler`, `WeaponAssetEntityCompiler` and `CompatibleSkinnedMesh`, so T1, W2 and W5 must keep its call surface.
- Uncommitted Solforge edits to `WeaponAssetEntityCompiler.cs`, `ExoticWeaponWorkflow.cs`, `StudioWorkspace.Exotic.cs`, `StudioWorkspace.WeaponReimport.cs`, `InvestmentItemArt.cs`, `InvestmentPerkDisplayEdits.cs`, `StudioForm.Ai.Perks.cs`, `StudioForm.Ai.Query.cs` and `Formats.csproj`. Upstream rewrote all of these except the AI files.
- Perk-graph authoring (`Solforge.Graphs`, custom and standalone perks, perk display sync), subclass grid and art, the CUI scene renderer, the UE5 UI (`UiAuto*`, `UiMotion*`, `StudioForm.EditorUi.cs`).
- Retail tools (`Solforge.Retail*`) and the D1 work (local only, kept in separate commits).
- Upstream deleted `Formats/TagReader.cs`, `Formats/InvestmentInspector.cs`, `Authoring/NativeRows.cs` and the `TagData.cs` files in Animation and Scene. Solforge-only code still uses `TagReader` and `NativeRows`, so a T1 merge must keep them or migrate those callers too.

## Recommended order

1. Seth decides on the licence and on T1 (see below).
2. Done in this batch: W1, W3, M0.
3. The 0f41c40 missing-keys error (S, hand merge).
4. If T1 is accepted: first add `SunriseStudio.Tags` + `Tags.Generated` + `Packages/Registry` as additive projects, with no migration. Then port M1 (copy/remove statics), the highest-value map feature, and expose it through MCP. Then M5, M6, M2, M3 and M4.
5. If T1 is rejected: rewrite M1 against raw offsets, taking offsets from the generated structs and cross-checking them against stock tags. This is about two days.
6. W2 only if Seth wants weapon edits on already-modified templates.
7. C1 and W5 only as part of a full T1 migration; they add no behaviour on their own.

## Licence and attribution

Upstream added `LICENSE` (GNU GPL v3 text) in 0f41c40. Its README now says: "Sunrise Studio is licensed under
GPL-3.0-or-later ... Third-party code and libraries keep their own licenses ... The Sunrise logo artwork is by Solus and
is not covered by the GPL." Upstream code before 0f41c40 had no licence, which is what `docs/THIRD_PARTY.md` >
Provenance still says. That section is now out of date.

Logo: Solforge's README and exe use `src/SunriseStudio.Editor/Assets/Sunrise.source.png`. That artwork is by Solus and is
not GPL, so Solforge needs Solus's permission to keep it, or a replacement.

What GPL-3.0 requires for anything taken from upstream, including this batch:

- Keep upstream's copyright and licence notices. The work is "Sunrise Studio by stan (https://github.com/stanuwu/SunriseStudio)", already credited in the README and THIRD_PARTY.
- Mark modified files with a notice and date. GPL-3.0 §5a asks for a prominent notice that the work was modified; a line in THIRD_PARTY and the git history are the usual way to meet it.
- Ship the GPL-3.0 text with any binary (`Solforge.exe`) and offer the complete corresponding source to everyone who receives the binary.
- Distribute Solforge as a whole under GPL-3.0 once it is distributed with upstream code.

Consequence: if Solforge is licensed GPL-3.0, the THIRD_PARTY rule "`Solforge.Weapons` (Parhelion, GPL-3.0-only) must not
be linked into a released `Solforge.exe`" no longer blocks anything. GPL-3.0-or-later upstream code combined with
GPL-3.0-only Parhelion code is distributable as GPL-3.0. Seth needs to decide this; THIRD_PARTY.md was not edited.

## Ported in this batch

- W1: `src/SunriseStudio.Authoring/WeaponAssetReimporter.cs`
- W3: `src/SunriseStudio.Formats/WeaponTranslationEdits.cs`
- M0: `src/SunriseStudio.Interchange/WorldSceneExport.Geometry.cs`, `tests/SunriseStudio.Interchange.Tests/WorldSceneChecks.cs`

Verified:

- `dotnet build src\SunriseStudio.App\SunriseStudio.App.csproj -c Release` gives 0 warnings and 0 errors.
- `dotnet run -c Release --project tests\SunriseStudio.Interchange.Tests` gives `interchange result=pass`, including the new
  `ProgramMotion` check.
- W3 has no test project. I checked it with a scratch program on a synthetic stock-layout config (definition at +0x3E8):
  group count, group selection and selector keys come out the same as the old absolute offsets, and a config without a
  definition is refused. This only shows the code is self-consistent.
- Independent evidence for the layout: Solforge's own `WeaponParticleBinding` (unchanged from 007dd7a) already reads real
  configs the same way, `Pointer(config, 24)` followed by a class check at -4. Upstream's generated
  `WeaponTranslationDefinition` puts Groups at +72 and Selectors at +568, which with a definition at +0x3E8 gives the old
  0x430 and 0x620.
- No fixture in `tests\Fixtures` holds a translation config, and the change was not run against installed game tags.

None of these is a user-facing operation, so no MCP tools were added. W3 is reached through `WeaponTuningAuthoring`,
which is used by the item and exotic-weapon creation dialogs (`ItemWorkflow`, `ExoticWeaponAuthoring`,
`CustomPerkAuthoring`) and by `Solforge.Weapons/WeaponRecipe.cs` (the Parhelion recipe path).
