# Sunrise client and testkit

## Client build identity

The installed `steam_api64.dll` carries a version resource (`0.6.0.<build>`). The `get_client_build` MCP tool and the Local Server → Client tab in Solforge identify which source tree built it using this scoring:

| Evidence | Score |
|---|---|
| `tree=` embedded in version Comments field | +150 |
| Built DLL in the tree with matching SHA-256 | +100 |
| Same size and write time (within 2 s) | +60 |
| `build_number.txt` / `sunrise_version.h` matches DLL build number | +30 |

Score 100+ → `exact`, 60+ → `likely`, 30+ → `weak`, otherwise `none`. The result also reports the tree's git branch, short HEAD, dirty file count, and how many dirty files changed after the DLL was written.

## Embedding build provenance (proposed, not yet applied)

To make client builds self-identifying, add to the client repo (`sol-0.6\Sunrise`):

1. **`tools/bump-build.ps1`**: after the counter, capture git metadata and write to `sunrise_version.h`:
   ```
   #define SUNRISE_VER_GIT "a345a58"
   #define SUNRISE_VER_BRANCH "sunrise-0.6"
   #define SUNRISE_VER_DIRTY 7
   #define SUNRISE_VER_TREE "sol-0.6"
   ```
2. **`resources/sunrise.rc`**: add to `StringFileInfo`:
   ```
   VALUE "Comments", "tree=" SUNRISE_VER_TREE " git=" SUNRISE_VER_GIT " branch=" SUNRISE_VER_BRANCH " dirty=7"
   ```
3. **`sunrise.log`** startup line: `build 173 tree=sol-0.6 git=a345a58 dirty=7`

The build still succeeds when git is missing (macros fall back to `unknown`).

## sol-testkit MCP

The testkit MCP is launched by the MCP client from the project's `.mcp.json`:

```
py -3 ...\tools\testkit\mcp\sol_testkit_mcp.py
```

with `SOL_PAD_PORT=48811` and `SOL_LIVE_PORT=48830` over stdio. The client owns the pipe; Solforge cannot respawn it for that client.

`testkit_status` lists running `py`/`python` processes, tries both ports, and starts a throwaway copy for an `initialize` + `tools/list` handshake:

- 🟢 Green — running and healthy
- 🟡 Amber — not running, two instances, or live port not answering
- 🔴 Red — script missing or handshake failed

`testkit_restart` (requires approval) ends the running processes so the client restarts them on the next `/mcp` reconnect. `mode=http` also starts the script's own `--http 48840` server.

## Player position sources

Solforge tracks the player from three sources, in priority order:

1. **Live link** (`studio_attach`) — most accurate. Attach via the Live page or `studio_attach` in game settings.
2. **Test recorder** — reads the newest `snapshots.jsonl` under `SOL_RECORDER_ROOT`, `SOL_CLIENT_DIR` or `SOLFORGE_RECORDER`. Set `SOL_RECORDER=1` at game launch to enable recording. The recorder's position freezes at spawn after a load; the camera position is used when it differs by more than 3 m.
3. **Local server** — reads `http://127.0.0.1:8099/coop`. Override the address with `SOLFORGE_SERVER_ADMIN`, pick a specific player with `SOLFORGE_PLAYER`.

All three report game world space, which matches the coordinate space Solforge draws maps in.

The viewport shows the player as an orange ring with a facing arrow and a breadcrumb trail. The **Follow player** toolbar toggle keeps the camera on the player. With the recorder or local server it also opens the player's map when their activity matches a known mission.

## Recorder diagnostics

A client DLL built with `SolDiagnostics=false` (release build in `sol-0.6`) excludes the Studio bridge and test recorder. A diagnostics-enabled build is required for live bridge and recorder tests. The `get_player_position` MCP tool reports why each source is silent if it is.
