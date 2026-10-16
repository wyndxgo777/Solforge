# Solforge MCP server

Solforge runs a plain MCP server whenever a project is open. Any MCP client can use it: Claude, Gemini, GPT, Cline or a local model. The model needs nothing special.

| Feature | How it works |
| --- | --- |
| Transports | stdio, through the shim `solforge-mcp.exe` next to `Solforge.exe`, and streamable HTTP on `127.0.0.1` with a Bearer token |
| Tool inputs | JSON Schema objects. Every property has a description. |
| Images | Each image tool also returns text: frame id, size, time, what changed and the labelled objects on screen. Models without vision can work from the text. OCR is not done; the on-screen labels come from Solforge's own scene. |
| Approval | Action tools (edits, builds, UI clicks, opening maps, the relay stream) wait for the user's click on the approval card in Solforge's AI workspace. This works for every client, because the approval lives in Solforge, not in the client. |
| Skills | Plain Markdown in `skills/` (one `SKILL.md` per job). Any agent can read them. |

## Client config

`<solforge>` is the folder that holds `Solforge.exe`; `<project>` is the Solforge project folder. The shim finds the Solforge window that has that project open.

**Claude Code** (`.mcp.json` in the project, or `claude mcp add solforge -- "<solforge>\solforge-mcp.exe" --project "<project>"`):
```json
{ "mcpServers": { "solforge": { "type": "stdio", "command": "<solforge>\\solforge-mcp.exe", "args": ["--project", "<project>"] } } }
```

**Claude Desktop** (`%APPDATA%\Claude\claude_desktop_config.json`), **Cursor** (`.cursor\mcp.json` or `%USERPROFILE%\.cursor\mcp.json`), **Cline** (MCP Servers > Configure > `cline_mcp_settings.json`):
```json
{ "mcpServers": { "solforge": { "command": "<solforge>\\solforge-mcp.exe", "args": ["--project", "<project>"] } } }
```

**Gemini CLI / Antigravity** (`%USERPROFILE%\.gemini\settings.json`, or Antigravity's MCP config, which takes the same shape):
```json
{ "mcpServers": { "solforge": { "command": "<solforge>\\solforge-mcp.exe", "args": ["--project", "<project>"], "timeout": 130000 } } }
```

**Codex CLI** (`%USERPROFILE%\.codex\config.toml`):
```toml
[mcp_servers.solforge]
command = 'C:\path\to\solforge\solforge-mcp.exe'
args = ["--project", 'C:\path\to\project']
startup_timeout_sec = 20
tool_timeout_sec = 130
```

**HTTP-only clients.** Solforge writes `%LOCALAPPDATA%\Solforge\mcp\<pid>.json` with `url` (`http://127.0.0.1:<port>/mcp`) and `token`. Send `Authorization: Bearer <token>`. The port and token change on every run, so prefer the shim when the client can start a command.

Use tool timeouts of about 130 s: `wait_for_player_near` waits up to 120 s, and `wait_for_viewport_change` up to 60 s.

## Two subscriptions: Claude Code + Gemini (Antigravity CLI)

Each engine runs on its own official client and its own sign-in. Claude Code uses your Claude plan, and agy uses your Google AI Pro login.

- **`ask_gemini` (stdio server `solforge-mcp --gemini`).** This tool runs `agy -p <task> --output-format json --mode plan|accept-edits` in the folder you give it. It returns Gemini's reply and a conversation id, which you pass back as `conversation` to continue.
  - It does not need Solforge to be running.
  - When agy is installed, Solforge adds it to the Claude Code config it launches.
  - To use it in any Claude Code session, run: `claude mcp add --scope user gemini -- "<solforge>\solforge-mcp.exe" --gemini`.
- **Hand off (AI workspace "..." menu).** This stops the running engine and writes `.solforge/HANDOFF.md` with that session's recent requests and last reply. It then starts the other engine on the same project, MCP and skills, with a first prompt that points at the note.
- **Not allowed (both providers' terms).** Never copy or proxy a subscription's OAuth token into another client. That rules out pointing Claude Code at Gemini through an Antigravity-login proxy, and putting a Claude login in a third-party harness. Google suspends accounts that do the first. Gemini inside Claude Code itself is possible only with a paid Gemini API key through an LLM gateway such as LiteLLM. That is billed API use, not AI Pro, and Anthropic doesn't support it.

## Solforge's own UI

- `list_ui_controls` lists the controls drawn right now: buttons, menus, checkboxes, tabs, text fields, combos, sliders and list rows. Each row has a stable id (`main:1A2B3C4D`), its kind, label, panel, scope, rect, value and options.
- `ui_invoke`, `ui_set_value`, `ui_set_checked` and `ui_select` act on a control by `id` or by visible label (`query`, narrowed by `window` and `kind`). A query that matches several controls equally is refused and lists them.
- Every action needs the approval click.
- The AI workspace and Settings > AI are never listed and cannot be driven.

## Live viewport

Solforge captures the 3D viewport continuously while someone watches: a frame tool used in the last 2 minutes, a browser stream or the relay. The capture goes into a ring of 32 JPEG frames. A new frame is kept only when the picture changes. No call waits for a GPU readback.

- `get_viewport_frame`: the latest frame, at once. `since=<id>` returns text only if nothing is newer.
- `get_viewport_frames`: frames since an id.
- `wait_for_viewport_change`: blocks until the view changes, up to the timeout.
- `read_screen_text`: the text on screen via Windows OCR (`Windows.Media.Ocr`, built into Windows, CPU only): lines with `[x, y, w, h]` boxes in image pixels; `region`, `contains`, `words`, `source=screenshot` for full size. `get_viewport_frame` and `wait_for_viewport_change` take `ocr: true` to add the same text to a frame.
- `get_viewport_stream`: a local URL, `http://127.0.0.1:<port>/viewport?key=<view key>`, for an MJPEG stream on this PC. The view key is not the MCP token.
- `set_viewport_stream` (approval): pushes frames out to the relay, so viewers can watch at `https://relay.example.invalid/stream/<name>?k=<key>`.
  - It is off by default. Seth can also turn it on with View > Stream to phone, which shows the link as a QR code.
  - It needs `SOLFORGE_OWNER_SECRET` (`tools/stream-relay/pc-secret.ps1` sets it; no restart needed).
  - Only accounts on the relay's allow-list can watch.
  - See `tools/stream-relay/README.md`.

## Player position

- `get_player_position` returns where the player is now:
  - the source;
  - map, zone (bubble), activity and mission;
  - `xyz` in map space and `yaw` in degrees (0 = +X, 90 = +Y);
  - the nearest named objects and mission slots, with distances;
  - other players on the local server;
  - for each source, why it is silent if it is.
- `wait_for_player_near` waits until the player is within `radius` m of an object, a slot or a point `[x, y, z]`.

Sources, first that answers wins:
1. **Live link** (`studio_attach`, Live page > Attach). The most exact.
2. **Test recorder.** The newest `bin\x64\Sunrise\logs\recorder\<session>\snapshots.jsonl` (launch with `SOL_RECORDER=1`; `SOLFORGE_RECORDER` overrides the folder). The recorder's `pos` freezes at spawn after a load, so the camera (`cam`, lowered 1.7 m) is used whenever `pos` is missing, stale or more than 3 m away from it.
3. **Local server.** The admin page `http://127.0.0.1:8099/coop` lists every joined player (`SOLFORGE_SERVER_ADMIN` overrides the address; `SOLFORGE_PLAYER` picks the player).

All three report game world space, which is the space Solforge draws maps in.

In the viewport, the player shows as an orange ring with a facing arrow and a breadcrumb trail. The toolbar's **Follow player** toggle keeps the camera on the player. With the recorder or the server, it also opens the player's map when their activity matches a known mission. With the Live link, Live > Sync > Map does that.

## Maps and missions

- `open_map` takes a map, a mission, or both. A mission alone opens on its own map, and a map alone lists its missions.
- `list_missions` shows only the open map's missions unless you give `map` or `all=true`.
