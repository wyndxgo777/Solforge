# Weapon model CLI

Run from the SolForge repository with the .NET 9 SDK or later.

```powershell
dotnet run --project tools/weapon-model -c Release -- inspect <model.glb>
dotnet run --project tools/weapon-model -c Release -- item <game-root> Borealis
dotnet run --project tools/weapon-model -c Release -- donor <game-root> <entity-tag>
dotnet run --project tools/weapon-model -c Release -- compile <game-root> <model.glb> <mapping.json> <new-output-directory>
```

Inspect lists primitives in mapping order and shows which images each glTF material references.
Item resolves an exact name, decimal item hash, or 0x-prefixed native tag and lists its art variants.
Donor lists installed LOD 0 model tags, part offsets, and palette indices where available.
Compile reads this JSON shape.

```json
{
  "name": "Ice Breaker",
  "entityTag": "<donor entity tag>",
  "modelTag": "<donor model tag>",
  "sourcePartOffset": 0,
  "packageId": "0B13",
  "firstEntryIndex": 0,
  "skeletonEntityTag": null,
  "cloneArtEntity": false,
  "primitives": [
    { "paletteIndex": 0, "materialTag": null }
  ]
}
```

Use the selected donor's actual part offset, an unused package ID, and one mapping row per
primitive. Each primitive is rigidly bound to its selected donor palette index. Input geometry
must already line up with the donor in package space after SolForge's glTF coordinate conversion.
A null material tag compiles the GLB's supported PNG-based PBR material. An explicit tag reuses an
installed native material. The native compiler verifies the selected donor's shader and matrix
profile.
Set `cloneArtEntity` to emit a private entity and component pointing to the new model.
This requires a checked donor entity profile and does not publish an inventory item.

The output contains native asset payloads and a manifest with their package and entry metadata.
The command reads the game packages and writes only to the supplied output directory. Linking
the compiled assets to a cloned item or packaging them as a Parhelion asset graph is a separate
authoring step.

## Import through New Exotic

In SolForge's **Weapons > New Exotic**, choose a rigid art JSON manifest in **Blender asset**
to use the Borealis donor. Keep its GLBs beside the manifest. The importer builds the model,
material and private entity for every selector during the New Exotic project transaction.
It retains the donor's animation rig. It supports rigid parts and embedded PNG textures.

Each entry in the manifest's `variants` array has these fields.

```json
{
  "selector": 0,
  "file": "body.glb",
  "modelTag": "0x80EF45C2",
  "sourcePartOffset": 352,
  "paletteIndices": [0],
  "entitySha256": "<donor variant entity SHA-256>",
  "configSha256": "<donor model config SHA-256>"
}
```

Every selector is one part of the assembled weapon (frame, barrel, scope, stock and so on), not an
alternative look. All selectors draw together in the donor's weapon space, so import one GLB per
selector and retain a donor part only when you want its stock mesh to show.

Set the top-level `version` to 1. Optional top-level `scale` (default 1) and `rotationDegrees`
(`[x, y, z]`, default zero) are applied after SolForge's glTF conversion: scale first, then rotation
about package X, Y and Z. Blender exports of Charm D1 models carry a 0.01 scale and a 90 degree X
node rotation; use `"scale": 100, "rotationDegrees": [-90, 0, 0]` for them. Check the result
against the donor with `donor`: the parts must share the donor's weapon space (X forward, Z up,
metres). An optional top-level `translation` (`[x, y, z]` metres) moves every imported part after
scale and rotation; a per-variant `translation` adds to it for that part only. Use it to put a
scope's optical axis on the donor's sight line (Borealis: eye at x -0.041, z 0.2186, from its scope
component 0x8152B2CC +0x790). An optional `excludePrimitives` array of primitive indices drops primitives such as lens
or reticle cards, which the opaque weapon shader would draw solid; `paletteIndices` still lists
one entry per GLB primitive. glTF materials that differ only in name share one native material. Use `item` to list the donor selectors, `donor` to list their
model parts and palette indices, and `source-hash` to fingerprint each donor entity and model
config. Supply one palette index per GLB primitive and exactly one entry per donor selector.
To keep a donor assembly, give that entry only its `selector`, `entitySha256` and
`"retainDonor": true`. At least one selector must import a GLB.
Check the complete manifest before opening the dialog.

```powershell
dotnet run --project tools/weapon-model -c Release -- validate-rigid <game-root> <manifest.json> <unused-package-id>
```
