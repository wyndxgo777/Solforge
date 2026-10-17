# Client provenance and testkit health

## Which tree built the installed client DLL

The installed `steam_api64.dll` (default `01-clients\sol-client2\bin\x64`, override with `SOL_CLIENT_DIR`) carries only its version
resource, `0.6.0.<build>`. Nothing in it names a source path. Only the `sol-0.6` tree has the counter that produces that number:
`Sunrise\tools\bump-build.ps1` runs before each build, increments `Sunrise\resources\build_number.txt` and writes
`resources\sunrise_version.h` (`SUNRISE_VER_STRING "0.6.0.<n>"`). The `Sunrise` repo and `sol-server-src\sunrise-sol` have no counter.

`get_client_build` (MCP) and the Local Server > Client tab match the DLL like this, best evidence first:

1. Embedded `tree=` in the version Comments (not written yet, see the proposal below): +150.
2. A built `steam_api64.dll` in the tree (`build\x64\Release` and similar) with the same SHA-256: +100; same size and write time (2 s): +60.
3. The tree's `build_number.txt` (or `sunrise_version.h`) equals the DLL build: +30.

Score 100 or more is `exact`, 60 or more `likely`, 30 `weak`, otherwise `none`. The result also gives the tree's git branch, short HEAD, dirty file
count and how many dirty files changed after the DLL was written (source that moved on after the build).

## Proposed client change (not applied)

Make the DLL say where it came from, so no guessing is needed. In the client repo (`sol-0.6\Sunrise`):

1. `tools\bump-build.ps1`: after the counter, run `git rev-parse --short HEAD`, `git rev-parse --abbrev-ref HEAD` and
   `git status --porcelain | Measure-Object`, and add to the generated `sunrise_version.h`:
   `#define SUNRISE_VER_GIT "a345a58"`, `SUNRISE_VER_BRANCH "sunrise-0.6"`, `SUNRISE_VER_DIRTY 7`, `SUNRISE_VER_TREE "sol-0.6"` (folder name of the checkout).
2. `resources\sunrise.rc`: in the `StringFileInfo` block add
   `VALUE "Comments", "tree=" SUNRISE_VER_TREE " git=" SUNRISE_VER_GIT " branch=" SUNRISE_VER_BRANCH " dirty=7"`
   (build the dirty number into a string with a stringify macro). `FileVersionInfo.Comments` then carries it, and `get_client_build` already reads it.
3. Log one line at startup in `sunrise.log`: `build 173 tree=sol-0.6 git=a345a58 dirty=7`, so uploaded player logs show it too.

`dirty` is the file count at build time; the build still works when git is missing (the macros fall back to `unknown`).

## sol-testkit MCP

Launched today by the MCP client from `C:\Users\Seth\Desktop\sol\.mcp.json`: `py -3 ...\tools\testkit\mcp\sol_testkit_mcp.py` over stdio with
`SOL_PAD_PORT=48811` and `SOL_LIVE_PORT=48830`. The client owns the pipe and the script binds the live capture port itself, so Solforge cannot respawn
it for that client. `testkit_status` lists the running `py`/`python` processes, tries both ports, and starts a throwaway copy with
`SOL_LIVE_AUTOSTART=0` for an `initialize` plus `tools/list` handshake. The light is red (script missing or handshake fails), amber (not running,
two instances, or live port not answering) or green. `testkit_restart` (asks for approval) ends the running processes, so the client brings it back on
`/mcp` reconnect; `mode=http` also starts the script's own `--http 48840` server.
