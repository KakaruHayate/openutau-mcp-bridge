# Developing

## Requirements

- .NET SDK 10
- An OpenUtau build to compile against. Pass its `OpenUtau.Core.dll` explicitly; the
  repository deliberately has no default path, because no machine layout is assumed:

  ```sh
  dotnet build src/OpenUtau.Plugin.McpBridge/OpenUtau.Plugin.McpBridge.csproj \
    -c Release -p:OpenUtauCorePath=/path/to/OpenUtau.Core.dll
  ```

- The output is a single managed assembly:
  `src/OpenUtau.Plugin.McpBridge/bin/Release/net10.0/OpenUtau.Plugin.McpBridge.dll`.

## Trying it locally

1. Copy the DLL into the `Plugins` folder of an OpenUtau data directory.
2. Start OpenUtau and open a voice part.
3. `curl -s http://127.0.0.1:43102/mcp -H 'Content-Type: application/json' -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'`

Two files appear next to the DLL:

- `OpenUtau.Plugin.McpBridge.status.json` — version, compatibility probe, endpoint, pid.
- `OpenUtau.Plugin.McpBridge.boot.txt` — append-only trace written when the host first
  touches the plugin; the fastest way to tell "never loaded" from "loaded and failed".

## Why there is no default OpenUtau path

The repository must not encode where a checkout lives on a contributor's machine. Use
`-p:OpenUtauCorePath=...` on the command line, or a `Directory.Build.local.props` file
that `.gitignore` already excludes.

## Layout

```
src/OpenUtau.Plugin.McpBridge/
  McpBridgeBatchEdit.cs   the only BatchEdit entry (menu item + startup trigger)
  BridgeHost.cs           idempotent start, boot marker
  BridgeController.cs     loopback HTTP listener, origin checks
  McpRequestHandler.cs    JSON-RPC 2.0 / MCP methods
  ToolRegistry.cs         advertised tools, filtered by capability probe
  CapabilityProbe.cs      reflection probe over OpenUtau.Core
  StatusReport.cs         status payload shared by tool and status file
  AgentPrompt.cs          the "connect me" prompt and clipboard delivery
```

## Support matrix

The bridge is compiled against one OpenUtau version and probes the rest at runtime.
When a future OpenUtau removes a member the bridge needs, the related capability turns
`missing`, its tools disappear from `tools/list`, and `openutau_status` explains why.
