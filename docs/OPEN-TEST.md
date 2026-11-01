# Open test: one prompt to install and connect

Paste the block below into an agent running on a machine that has OpenUtau installed.
It detects the installation, installs the plugin, brings the bridge up with the user's
help, and reports whether the connection works.

## What this can and cannot do

| Step | Possible without a human? |
|---|---|
| Find OpenUtau and its real `Plugins` folder | yes |
| Download the DLL and verify its checksum | yes |
| Install / upgrade the DLL | yes (restart needs the user's OK if the app is open) |
| Start the MCP endpoint | **no** - the plugin is instantiated only when a voice part is opened. One double-click is unavoidable today (a starter project is shipped so that click has a target). |
| Poll for the endpoint, register it, verify 43 tools | yes |
| Report a diagnostic bundle on failure | yes |

The single human step is a design consequence, documented in
[M0-FINDINGS.md](M0-FINDINGS.md): OpenUtau scans plugins as metadata and instantiates
external `BatchEdit` implementations when the piano roll opens. Until an upstream
"connect an agent" dialog exists, the part click is the trigger.

## The prompt

```text
Install and connect the OpenUtau MCP bridge on this machine.

Facts you need:
- The bridge is one managed DLL (net10.0, any CPU) published at
  https://github.com/KakaruHayate/openutau-mcp-bridge/releases
  Use the newest release (or the exact tag you were given). Assets: the DLL, a .sha256,
  and build-info.txt.
- It exposes an MCP server over loopback HTTP, normally http://127.0.0.1:43102/mcp.
  The port walks upward when it is taken, so never assume 43102.
- The endpoint does NOT exist until a voice part is opened in OpenUtau's piano roll.
  That is the only step that needs the human.

1. Find OpenUtau
   - Prefer the executable of a running process named OpenUtau.
   - Otherwise look in the usual places: the PATH, /Applications on macOS, package
     manager locations on Linux, and the Windows install directories.
   - Decide the data folder:
       * portable build (OpenUtau.Core.dll sits next to the executable, or a Plugins
         folder is already there): the folder containing the executable
       * installed build: Documents/OpenUtau on Windows, ~/Library/OpenUtau on macOS,
         ${XDG_DATA_HOME:-~/.local/share}/OpenUtau on Linux
   - The plugin folder is <data folder>/Plugins.
   Report the executable path, the data folder, whether it looks portable, and the app
   version before changing anything.

2. Download and verify
   - Download the DLL and its .sha256 into a temporary directory.
   - Compare the SHA-256. If it does not match, stop and report; do not install.
   - Read build-info.txt and keep its version, protocol and baseline for your report.
   - Do not rebuild the DLL from source: a self-built binary usually targets a different
     .NET version than the installed application and will not load.

3. Install
   - If OpenUtau is running, ask the human to save their work and close it. Do not kill
     the process; unsaved work would be lost.
   - Create <data folder>/Plugins if it does not exist.
   - Copy the DLL in, replacing an older copy of OpenUtau.Plugin.McpBridge.dll.
   - Leave any OpenUtau.Plugin.McpBridge.*.txt or .json files alone; they are diagnostics.

4. Bring it up
   - If OpenUtau is not running, start it with the starter project from the release
     (openutau-mcp-starter.ustx). That file has a single part, so there is something to
     open:
       OpenUtau.exe "/absolute/path/openutau-mcp-starter.ustx"
   - If the human already has a project open, do not replace it; ask them to open one of
     their own parts instead.
   - Ask the human, in one sentence: "double-click the part so the piano roll opens".
   - Then wait up to 120 seconds, checking every 2 seconds:
       * if <Plugins>/OpenUtau.Plugin.McpBridge.boot.txt exists, its last line names the
         real endpoint
       * otherwise POST a tools/list JSON-RPC request to http://127.0.0.1:PORT/mcp for
         PORT in 43102..43111
   - Success means a JSON-RPC result containing a tool list. Record the port and count.

5. Connect
   - If you can register an HTTP MCP server in your own configuration without restarting
     yourself, add http://127.0.0.1:<port>/mcp and continue.
   - Otherwise print the exact command for the human, for example:
       codex mcp add openutau --url http://127.0.0.1:<port>/mcp
       claude mcp add --transport http openutau http://127.0.0.1:<port>/mcp
     and stop after printing it.
   - Once the server is visible to you, call openutau_status.
       * compatibility.ok false: quote compatibility.reasons and stop.
       * compatibility.ok true: tell the human the app version, the tool count and the
         current project revision, then stop. Do not edit the project.

6. Report
   Print a short block with: executable path, plugin path, bridge version and protocol,
   endpoint, tool count, compatibility level, and the project path and revision if one is
   open. If something failed, also include the last 20 lines of boot.txt, the errors array
   from OpenUtau.Plugin.McpBridge.status.json, and the tail of OpenUtau's log folder.

Rules:
- Never modify or save the user's project.
- Do not install anything else: no packages, no runtimes, no voicebanks.
- If a step fails, stop at that step and report, rather than trying alternatives.
```

## Notes for testers

- The tool count is not a fixed constant: the bridge hides tools whose capability probe
  fails on the running OpenUtau, so an older build shows fewer than 43. Report the count
  you see instead of comparing it with a number.
- `openutau_status.compatibility.level` is the fastest way to tell whether the host is
  fully supported (`full`), partially (`partial`), or unusable (`unsupported`).
- Useful things to send back: the six-line report from step 6, `build-info.txt`, and
  `OpenUtau.Plugin.McpBridge.status.json`.
