# Weapon authoring in Solforge

Solforge supports two weapon authoring paths: the **Clone workflow** (create a weapon from an existing game item), and the **Recipe workflow** (define a weapon as a `.weapon.json` file and apply it via the Clone workflow or MCP tools).

## Clone workflow

The standard way to create a new weapon. Go to **Assets → Create → Exotic Weapon** (or the equivalent Place tile), pick a donor weapon from the investment catalog, and the workflow generates all required investment rows, art entity, tuning, presentation and icon assets.

The Clone dialog reports any fields it cannot automatically apply (custom socket types, raw patches, locale text, etc.). Those must be set manually in the Details panel or via MCP tools after the clone completes.

## Weapon recipes (`.weapon.json`)

A weapon recipe is a JSON file that fully describes a weapon: donor reference, identity hashes, display text, stat overrides, socket configuration, ammo type, rarity and more.

### Schema

Recipes use `"schema": 1` with snake_case keys. Unknown fields are rejected on load. Hashes are written as `"0xXXXXXXXX"` upper-case strings. The namespace prefix is `solforge.`; `parhelion.` namespaces are also accepted for compatibility with imported recipes.

```json
{
  "schema": 1,
  "namespace": "solforge.my_weapon",
  "donor": "0x12345678",
  "name": "My Weapon",
  "flavor_text": "A custom weapon.",
  "overrides": {
    "slot": "kinetic",
    "ammo": "primary",
    "element": "arc",
    "rarity": "exotic",
    "stats": {
      "rpm": 340,
      "impact": 55
    }
  },
  "tuning": {
    "rpm": 340,
    "damage": 42,
    "magazine": 15
  }
}
```

### MCP tools

The MCP server exposes recipe tools under the `parhelion_*` namespace:

| Tool | What it does |
|---|---|
| `list_weapon_recipes` | Lists `.weapon.json` files in the open project |
| `get_weapon_recipe` | Reads and validates a recipe file |
| `start_parhelion_weapon_recipe` | Opens the Clone workflow with the recipe's donor pre-selected (requires approval) |

### Validation

Static validation runs on load and on `get_weapon_recipe`. Rules include:

- Identity hashes must be nonzero, distinct, and not the empty-name hash.
- The item hash must differ from the donor's hash.
- Every localized hash must sort after the donor bank's terminal hash `0x304D56FE`.
- Socket lane counts, plug membership, socket types, plug-set bounds, weapon pattern/stat-group index and stat bounds are checked against the donor's investment catalog data.
- An empty per-choice condition is allowed (unconditional). A randomized plug set requires a non-empty selection program.

### MCP `apply_item_edit` — socket and perk tools

The MCP also exposes lower-level item editing tools for use without a recipe file:

| Tool | What it does |
|---|---|
| `apply_item_edit` | Apply a field edit to an investment item (requires approval) |
| `set_socket_plug` | Set the installed plug in a socket (requires approval) |
| `create_perk_from` | Clone a perk behavior graph from a source (requires approval) |
| `list_perk_behaviors` | List perk behavior graphs in the project |
| `get_perk_behavior` | Read a perk behavior graph |
| `edit_perk_behavior` | Edit nodes and labels in a perk behavior graph (requires approval) |
| `clone_tags` | Copy installed tags into the project (requires approval) |

## Weapon tuning

The `tuning` block in a recipe maps to Solforge's `WeaponTuning` authoring layer, which sets RPM, damage and magazine size on top of the base donor archetype. These are applied by the Clone workflow and can be edited later in the Details panel (Tuning section).

## Building and installing

Once a weapon is authored (via clone or recipe), it lives in your Solforge project as a set of tag edits. Build the project with **Build → Build Project** to produce a mod package, then **Build → Install Mod** to deploy it to your Sunrise installation under `bin/x64/Sunrise/mods/<mod>/`.

The MCP `build_project` and `install_mod` tools do the same (both require approval).

## Licensing note

The recipe schema and validation rules in `src/Solforge.Weapons` are derived from [Parhelion](https://github.com/KyleThmpsn/sundial) (GPL-3.0-only), rewritten in C#. The implementation is treated as a derived work. See `docs/PARHELION_AUDIT.md` for the full audit. Before distributing a build containing this code, resolve a compatible licensing path with the upstream authors or replace it with a clean-room implementation.
