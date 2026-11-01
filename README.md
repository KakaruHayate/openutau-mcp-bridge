# openutau-mcp-bridge

An MCP server that runs **inside** OpenUtau as a plugin DLL, so an AI agent can read and
edit the project that is currently open.

- One managed DLL, no native code: Windows, macOS and Linux use the same file.
- No changes to OpenUtau: the plugin is discovered through the external `BatchEdit`
  mechanism and adds its own entry under **Batch Edits -> External**.
- Loopback only (`127.0.0.1`), no telemetry, and it never saves your project.

> **Status: M8.** 41 tools: reads, note writes, musical edits, project lifecycle,
> singer/renderer binding, export (mixdown and stems) with a job ledger, and package
> management with missing-package self-healing. CI runs the smoke test on three
> platforms against two OpenUtau revisions.

## Install

1. Copy `OpenUtau.Plugin.McpBridge.dll` from a
   [release](https://github.com/KakaruHayate/openutau-mcp-bridge/releases) into OpenUtau's
   `Plugins` folder (portable: next to `OpenUtau.exe`; installed: `Documents/OpenUtau`;
   macOS: `~/Library/OpenUtau`; Linux: `${XDG_DATA_HOME:-~/.local/share}/OpenUtau`).
2. Restart OpenUtau and open a voice part; the bridge starts the first time a part opens.
3. Run **Batch Edits -> External -> MCP Bridge: Copy agent prompt** and paste the copied
   text into your agent.

For the one-paste install prompt (what to hand an agent before the DLL is there), see
[docs/INSTALL.md](docs/INSTALL.md).

The endpoint is `http://127.0.0.1:43102/mcp`. When that port is taken the next free one is
used, and the real port is written into the copied prompt and into
`OpenUtau.Plugin.McpBridge.status.json` next to the DLL.

## Why a plugin

OpenUtau's own stance is that agent integrations belong outside the application. The
plugin route is the only one that needs no core change: OpenUtau already loads managed
`BatchEdit` implementations from its `Plugins` folder.

## Known constraint

OpenUtau instantiates external `BatchEdit` implementations when the piano-roll window
opens, and its plugin scan is metadata-only. A plugin therefore cannot run code at
application start: the bridge comes up the first time you open a part. The planned
upstream follow-up (a "connect an agent" dialog next to the existing DAW-integration
guide) would make it start earlier.

## Development

See [docs/DEVELOPING.md](docs/DEVELOPING.md). The protocol and compatibility contract is
in [docs/API.md](docs/API.md); the M0 findings are in
[docs/M0-FINDINGS.md](docs/M0-FINDINGS.md).

Licensed under the Mozilla Public License 2.0 (see [LICENSE](LICENSE)).
