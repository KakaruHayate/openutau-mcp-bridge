# openutau-mcp-bridge

An MCP server that runs **inside** OpenUtau as a plugin DLL, so an AI agent can read and
edit the project that is currently open.

- One managed DLL, no native code: Windows, macOS and Linux use the same file.
- No changes to OpenUtau: the plugin is discovered through the external `BatchEdit`
  mechanism and adds its own entry under **Batch Edits 鈫?External**.
- Loopback only (`127.0.0.1`), no telemetry, and it never saves your project.

> **Status: M2.** Reads (snapshot, notes, pitch, curves, expressions, change feed) and
> note writes (add/edit/delete, dry run, undo/redo) work. Project lifecycle, export and
> package tools follow.

## Install

1. Copy `OpenUtau.Plugin.McpBridge.dll` into OpenUtau's `Plugins` folder
   (`<data folder>/Plugins`, where the data folder is `Documents/OpenUtau` for the
   Windows installer, `~/Library/OpenUtau` on macOS, and
   `${XDG_DATA_HOME:-~/.local/share}/OpenUtau` on Linux).
2. Restart OpenUtau and open a voice part (the bridge starts on first use).
3. Run **Batch Edits 鈫?External 鈫?MCP Bridge: Copy agent prompt** and paste the copied
   text into your agent.

The endpoint is `http://127.0.0.1:43102/mcp`; the port moves to the next free one when
it is taken, and the real port is always written into the copied prompt and into
`OpenUtau.Plugin.McpBridge.status.json` next to the DLL.

## Why a plugin

OpenUtau's own stance is that agent integrations belong outside the application. The
plugin route is the only one that needs no core change: OpenUtau already loads managed
`BatchEdit` implementations from its `Plugins` folder.

## Known constraint (M0)

OpenUtau only instantiates external `BatchEdit` implementations when the piano-roll
window opens, and its plugin scan is metadata-only. That means a plugin cannot run code
at application start: the bridge comes up the first time you open a part. This is
tracked as an upstream follow-up; the dialog that would start it earlier is planned
alongside the app's existing DAW-integration guide.

## Development

See [docs/DEVELOPING.md](docs/DEVELOPING.md). The protocol and compatibility contract
is in [docs/API.md](docs/API.md).

Licensed under the Mozilla Public License 2.0 (see [LICENSE](LICENSE)).

