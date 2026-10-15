# Solforge feature inventory (upstream Sunrise Studio 007dd7a + Solforge phases)

What the fork already has, per Studio.md area, so each phase builds only its gaps. Items marked
**(Exists)** are upstream code that Solforge keeps as is; **Solforge** marks additions made here.
Project facts (TFM, references, sizes) are in ARCHITECTURE.md; this file is about capability.

## Packages (`SunriseStudio.Packages`, Phases 05-06, 08)
- (Exists) `PackageFile`: 0x180 header, variant 0/1 tables, 16-byte entries, 48-byte blocks (§5).
- (Exists) `PackageCatalog`: newest patch per id, `TryGetEntry`/`ContainsTag`/`Resolve64`,
  `Read`/`ReadOriginal`, 1024-block decoded LRU, bounded file handles, read overrides and overlays
  (`PackageOverlayEntry`), `ReadStatistics`, name rows (`NamedTag`, `Names`).
- (Exists) `StudioDiskCache` (gzip, SHA-256, atomic rename, size budget) with
  `CacheFileIdentity` (length + Windows change time + file id); `Names.Cache`, `ComponentSlots.Cache`.
- (Exists) `PackageKeys` (AES-128-GCM keys + nonce from `exports/package_keys.bin` or the exe) and
  `OodleDecoder` (`oo2core_3_win64.dll` from the install; 256 KiB blocks, pooled buffers).
- (Exists) `PackageWriter.WriteNew/WritePatch/WriteReplacement`, `StringBankWriter`,
  `PackageManifest`, `PackageDependencies`, `PackageNativeDependencies`, `TextureDataReader`,
  `LocalizedStrings`, `ComponentIndex`, `ClassNames`/`GameNames`/`ObjectTypeNames`.
- Solforge: metadata index (`PackageCatalog.Index.cs`, Phase 05), class/group search
  (`PackageCatalog.Search.cs`, Phase 05), bounded parallel block reads (`PackageCatalog.Parallel.cs`, Phase 06).
- Solforge (Phase 08): `PackageDelta.cs` (`PackageBlockAllocator` places replaced/appended entries in raw
  blocks after the source's last block and writes an "SFPD" project delta with entry redirections, target
  patch block records and SHA-1s; `PackageDelta` reads/validates it, `ReadEntry`, `BuildEntryTable`,
  `Matches`; entry/block/id limits). (Exists) `PackageWriter.WritePatch` turns replacements into the next
  patch file and enforces the same limits.
- Gaps: memory mapping (not needed by profiling yet), sharding across package ids (Phase 41).

## Tags (`SunriseStudio.Formats`, Phase 07)
- (Exists) `TagReader` and typed readers: investment (items, sockets, stats, vendors, lore,
  collections), perk behavior graphs (`BehaviorInspector`, `BehaviorInstruction`), CUI documents and
  bindings, `AssetGraph`/`AssetInspection`, `AssetConnections`, glTF/OBJ/DDS/PNG interchange.
- (Exists) Phase 07 typed metadata deserializers: Strings (`Packages/LocalizedStrings.cs`), Entity
  Templates (`Packages/EntityComponents.cs` `ComponentIndex.EntityConfigs`, `ComponentRoles.ReadConfig`),
  Sound Banks (`Audio/AudioCatalog.cs` `ReadBank`, `Audio/WwiseSoundBank.cs` `Nodes`).
- Solforge (Phase 07): `TagHeaders` (§5.4 hash <-> package id + entry index, the §5.2 record parse
  `PackageFile` now uses), `PackageCatalog.TryLocate`, `TagDependencyGraph` (DAG that rejects cycle edges,
  topological order, bounded `Build` walk), all in `SunriseStudio.Packages`.
- Gaps: a generic class-hash -> struct schema registry and field-level validation.

## Viewport and rendering (`SunriseStudio.Rendering`, `SunriseStudio.Viewport`, Phases 10, 13)
- (Exists) `Dx11Renderer` (Vortice D3D11, WARP fallback), `ImGuiController`, `GpuMesh`/`GpuTexture`,
  `GpuFrameTimer`, game shader execution (`GameShaderInfo`, `NativeMaterialPipeline`,
  `NativeDeferredShading`, `NativeAtmosphere`, local lights, cubemaps), `EffectRenderer`.
- (Exists) `AssetViewport`, `ViewerCamera` (+ Solforge fly controls), `SceneGrid`, `SceneBackdrop`,
  `SceneSelection`/`SelectionBounds`, `PickFaces`, `TransformTool` gizmos, `ShortcutMap`/presets,
  `ViewportFrameTimings`, `NativeSceneRenderer`.
- Gaps: D3D12 (not planned; D3D11 stays), 1M-triangle FPS benchmark check (Phase 14).

## Meshes and world (`SunriseStudio.Scene`, Phase 11)
- (Exists) `StaticMeshDecoder`, `EntityMeshDecoder`, `TerrainDecoder`, `CompressedMesh`,
  decorators, collision scenes (`CollisionScene`, shapes, connectivity), spawns/squads, effects,
  guardian appearance, map/scenario loading (`AssetSceneLoader`).

## Materials and shaders (`SunriseStudio.Rendering` + `Formats`, Phase 12)
- (Exists) `GameMaterialReader`, `MaterialSampler`, `MaterialStageData`, DXBC text/checksum,
  `Data/NativeRenderStates86657.json`, Editor `MaterialsPage`/`ShadersPage`/`MaterialWorkflow`.

## Project system and overlay (`SunriseStudio.Projects`, Phases 15-16, 18, 43)
- (Exists) `StudioProject` (`sunrise.project.json`), `ProjectManifest`, `ProjectAssetEdits`,
  `BinaryDelta`/`DeltaOperation`, `ObjectEdit` + `ObjectEditCompiler`, `ProjectBuilder`
  (native/socket dependency resolution, `BuiltProjectAsset`), `ModInstaller` + `ModManifest`
  (`mod.json`, mods under `bin/x64/Sunrise/mods`), `MergeSidecarArtifact`, `ModUpdatePlan`.
- Gaps: `.solproj` naming (spec), rollback snapshots (Phase 43), `.solmod` container (Phase 42).

## Interchange (`SunriseStudio.Interchange`, `Authoring`, Phase 17)
- (Exists) Blender bundles (`AssetBundle*`, mesh/texture/collision codecs), world scene export
  (`WorldSceneExport`), effect export, `MeshAssetImporter`, `AnimationAssetImporter`,
  `OriginalAssetImporter`, `CollisionImport`, `HavokPackfileBuilder`, `CompressedMeshEncoder`.

## Weapons (`Authoring` + `Editor/Creation`, Phases 20-23)
- (Exists) `ExoticWeaponWorkflow`, `ExoticWeaponDefinition`, `PreparedWeapon*` compilers
  (archetype, art entity, attachments, clips, firing effects, HUD icon, materials, particles,
  presentation, reticle, rigid model, tuning), `WeaponAssetReimport`.
- Gaps: Parhelion domain (`src/Solforge.Weapons`, Phase 20), recipe DSL/schema (21), stat/recoil
  sandbox (23).

## Missions, Live, Audio (Phases 30-33, 45-47)
- (Exists) `LuaScript` AST + Loretta conversion, `MissionScriptGraph`, `MissionApiSchema`,
  mission SDK validation; `LiveConnection` over shared memory, CUI/object/pose/world protocols;
  Wwise bank reading, WEM decoding, event composition, playback.

## Checks (`tests/*.Tests`, run by verify.ps1)
- (Exists) 36 console check programs (no test framework); `-Game` adds installation checks,
  `-Blender` the add-on checks; CI runs the install-free set on WARP.
- Solforge: `Architecture.Tests` (Phase 04), `Packages.Tests` (Phases 05-08).
