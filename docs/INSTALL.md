# Install

The bridge is a single managed DLL. Nothing is installed system-wide and OpenUtau itself is
not modified.

## Paste this into your agent

```text
Install the OpenUtau MCP bridge on this machine.

1. Find OpenUtau's plugin folder (create it if it does not exist):
   - Windows portable: the "Plugins" folder next to OpenUtau.exe
   - Windows installer: %USERPROFILE%\Documents\OpenUtau\Plugins
   - macOS: ~/Library/OpenUtau/Plugins
   - Linux: ${XDG_DATA_HOME:-~/.local/share}/OpenUtau/Plugins

2. Download OpenUtau.Plugin.McpBridge.dll from
   https://github.com/KakaruHayate/openutau-mcp-bridge/releases
   (the newest release, or the exact tag you were given) and copy it into that folder.
   Do NOT build it from source: a self-built DLL usually targets a different .NET
   version than the installed application and will fail to load.

3. Restart OpenUtau and open a voice part. The bridge starts the first time a part is
   opened; before that there is no endpoint.

4. Run "Batch Edits -> External -> MCP Bridge: Copy agent prompt" and paste what it
   copies here. That text carries the real port and versions of this machine.

If the endpoint never appears, read OpenUtau.Plugin.McpBridge.boot.txt and
OpenUtau.Plugin.McpBridge.status.json in the plugin folder and quote them back.
```

## Manual install

1. Copy `OpenUtau.Plugin.McpBridge.dll` into OpenUtau's `Plugins` folder.
2. Restart OpenUtau, open a voice part.
3. **Batch Edits → External → MCP Bridge: Copy agent prompt**.
4. Paste into your MCP client, or register the URL by hand:

   ```text
   http://127.0.0.1:43102/mcp
   ```

   Codex: `codex mcp add openutau --url http://127.0.0.1:43102/mcp`
   Claude Code: `claude mcp add --transport http openutau http://127.0.0.1:43102/mcp`

## Troubleshooting

| Symptom | Look at |
|---|---|
| No menu item | Is the DLL in the folder OpenUtau actually uses? The portable build uses its own `Plugins` folder, not `Documents`. |
| Menu item exists, no endpoint | `OpenUtau.Plugin.McpBridge.boot.txt` next to the DLL — it records every start attempt. |
| Endpoint up but tools missing | `openutau_status.compatibility` — capabilities the running OpenUtau does not expose are not advertised. |
| Nothing at all | OpenUtau's `Logs` folder; a load failure is logged as `Failed to load <file>`. |

Files the plugin writes next to itself: `OpenUtau.Plugin.McpBridge.boot.txt`,
`OpenUtau.Plugin.McpBridge.status.json`, `mcp-bridge-prompt.txt`, and optionally
`OpenUtau.Plugin.McpBridge.json` (port/token configuration).

## Uninstall

Delete the DLL (and those files) from the `Plugins` folder. Nothing else is touched.
