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

## Several subscriptions: Claude Code + Gemini (Antigravity CLI) + ClinePass

Each engine runs on its own official client and its own sign-in. Claude Code uses your Claude plan, and agy uses your Google AI Pro login.

- **`ask_gemini` (stdio server `solforge-mcp --gemini`).** This tool runs `agy -p <task> --output-format json --mode plan|accept-edits` in the folder you give it. It returns Gemini's reply and a conversation id, which you pass back as `conversation` to continue.
  - It does not need Solforge to be running.
  - When agy is installed, Solforge adds it to the Claude Code config it launches.
  - To use it in any Claude Code session, run: `claude mcp add --scope user gemini -- "<solforge>\solforge-mcp.exe" --gemini`.
- **`ask_clinepass` (stdio server `solforge-mcp --clinepass`).** This tool works like `ask_gemini` but runs your own Cline CLI on ClinePass: `cline --json -P cline-pass [-m cline-pass/<model>] [-p] -t <seconds> -- <task>`. The default `plan` mode reads and answers only; `act` lets the model edit files and run commands. The prompt is capped at 24,000 characters and the time limit at 30 minutes. It returns the reply plus the model and finish reason. The headless Cline CLI can't resume a session in JSON mode, so each call starts fresh.
  - It does not need Solforge to be running. Sign in once with `cline auth` and pick ClinePass, or set `CLINE_API_KEY`.
  - Solforge adds it to the Claude Code config it launches when "Give Claude Code ask_clinepass" is ticked in the AI workspace "..." menu and cline is installed.
  - To use both tools in any Claude Code session, run:
    - `claude mcp add --scope user gemini -- "<solforge>\solforge-mcp.exe" --gemini`
    - `claude mcp add --scope user clinepass -- "<solforge>\solforge-mcp.exe" --clinepass`
- **Claude Code on ClinePass (provider for Claude Code).** Claude Code itself runs on ClinePass models through a translator on 127.0.0.1 inside Solforge, which converts Anthropic Messages to OpenAI Chat Completions against `https://api.cline.bot/api/v1`. Cline documents this API for your own scripts and apps with a ClinePass API key ([ClinePass docs](https://docs.cline.bot/getting-started/clinepass)), and its FAQ allows ClinePass "with other coding agents by using your ClinePass API key" ([cline.bot/cline-pass](https://cline.bot/cline-pass)).
  - Setup: create a key at app.cline.bot (Settings > API Keys), set it as the user environment variable `CLINE_API_KEY`, and pick the provider "Claude Code on ClinePass" for Claude Code.
  - The model field takes a `cline-pass/` id; the default is `cline-pass/kimi-k3`. Inside Claude Code, `/model cline-pass/qwen3.8-max` (or any other `cline-pass/` id) switches the model, and the haiku alias maps to `cline-pass/deepseek-v4.1-flash`.
  - That run has every Anthropic and Claude login variable removed. Claude Code gets only a random local token that opens nothing but the translator port. Your Claude and Google sign-ins never pass through it, and Antigravity stays tool-only (`ask_gemini`). The key is read from your environment per request and never stored or logged.
  - It is personal use only: Cline's [terms](https://cline.bot/tos) forbid sharing a subscription or transferring API keys.
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

## Shaders, animations and audio

Read-only metadata from the installed game. No map needs to be open, and these tools keep working while context publishing is paused. They use the same package readers as the editor's material, animation and audio pages, and they decode nothing those readers don't already read.

| Tool | Gives |
| --- | --- |
| `list_shaders` | Shader headers, paged: tag, stage, bytecode tag, package, size. Filter by `stage`, `package` or `filter`. |
| `get_shader` | A shader header tag gives its profile (for example `ps_5_0`), constant buffers (slot, size in float4 registers and bytes), texture and sampler slots, `dcl_` declarations, and the materials that use it (from the owner index, which can be partial). A material tag gives every stage, plus the bound textures (stage, slot, texture tag, size, format, sRGB), empty slots and diagnostics. `disassembly=true` adds the DXBC disassembly, one page of lines at a time (`offset`, `limit`). |
| `list_animations` | Animation clips, paged: tag, name, package. With `entity`, you get only that entity's clips, with definition, clip index and config. |
| `get_animation` | A clip gives its frame count, sample rate, length in seconds and payload sections, plus the definitions that list it (`users`). When the clip's codec is supported, you also get its control tracks: node and control counts, groups, and the key counts of each track, paged. Add `entity` to get that entity's skeleton too (bones with name, hash and parent, paged). An entity tag gives its skeleton and a count of its clips. |
| `list_audio` | Wwise events, banks and streams, paged: tag, kind, name, package, size and Wwise id. Filter by `kind`. |
| `get_audio` | An event, bank or stream gives its Wwise event id, related event id, linked bank (id, Wwise version, object counts by HIRC type, embedded media count), media (id, stream tag, name, size, embedded or streamed) and diagnostics. No audio is decoded. |

The first `get_animation` call scans every animation definition once to find each clip's users, so it can be slow. Later calls use the cached result.

## Automatic mode

Keeps the AI working on one goal until it is done.

- Seth types `/automatic <goal>` in the AI terminal (or in the remote page's AI chat). Solforge catches the line before the engine sees it, sends the goal with working instructions, and answers one-time allow prompts while it runs. `/automatic status` shows the state, and `/automatic stop` ends it.
- When the engine's screen stays still for the idle time and there is no allow prompt, Solforge pastes a continue nudge. The nudge holds the goal, the last `goal_progress` and the remaining steps.
- A nudge round counts as no progress when the screen is the same as at the last nudge, or when there was no Solforge tool call and no `goal_progress`. After 3 rounds like that, the nudge asks for a different tactic.
- It stops on `goal_complete` (summary, evidence), `goal_blocked` (reason), `/automatic stop` or `automatic_stop`, or at the limits. The limits are in Settings > AI: max nudges (default 50), max duration (default 240 min) and idle seconds (default 45).
- The AI can start this mode on itself with `automatic_start(goal)` for a long multi-step goal. External clients can also use `automatic_status` and `automatic_stop`.
  - None of the automatic tools needs an approve click.
  - Only one goal runs at a time. A second `automatic_start` is refused unless it passes `replace: true`. A typed `/automatic` always replaces.
  - An engine can't type `/automatic` into its own input line, so it uses the tools.
- The running goal is saved to `automatic-goal.json` beside the settings file. After a restart, the AI panel asks whether to resume it or discard it.
- `get_editor_state` and the remote AI state both include `automatic`, and the stream page's Status tab shows it.
