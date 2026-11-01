# API contract

Version: `protocol = 1` (the tool-surface contract; independent of the OpenUtau version).

## Transport

- MCP over Streamable HTTP, **stateless**: one `POST /mcp` per JSON-RPC message.
- The listener binds a numeric loopback address only (`http://127.0.0.1:<port>/`).
- Default port `43102`; when taken, the next free port is used and reported everywhere
  the endpoint appears.
- Requests must carry `Content-Type: application/json`. A non-empty `Origin` header is
  accepted only when it names this exact endpoint host and port.
- Notifications are answered with `HTTP 202` and an empty body.

## Methods

| Method | Behaviour |
|---|---|
| `initialize` | Returns protocol version, `capabilities.tools`, `serverInfo`, and short `instructions`. |
| `notifications/initialized` | Accepted, no response. |
| `ping` | Empty result. |
| `tools/list` | Only tools whose required capabilities probe as available. |
| `tools/call` | Returns MCP content blocks; business failures arrive as `isError: true` with a JSON `{error, message}` payload. |

## `openutau_status`

Always available. Shape:

```json
{
  "bridge": { "version": "0.1.0", "protocol": 1, "builtAgainst": "1.0.0.0", "target": "net10.0" },
  "app": { "version": "1.0.0.0", "informational": "…", "core": "1.0.0.0", "runtime": "10.0.14", "os": "Win32NT" },
  "compatibility": { "ok": true, "level": "partial|full|unsupported", "reasons": ["…"],
    "capabilities": { "notes": "full", "pitch_points": "full", "…": "missing" } },
  "tools": { "implemented": ["openutau_status"] },
  "project": { "sessionId": "…", "revision": 42, "dirty": false, "path": "…" },
  "server": { "endpoint": "http://127.0.0.1:43102/mcp", "port": 43102, "pid": 1234, "startedAt": "…" }
}
```

`revision` is OpenUtau's own document revision (`DocManager.Revision`), not a bridge
counter. Clients send it back on writes as `expectedRevision`.

## Capabilities

`CapabilityProbe` reflects over the running `OpenUtau.Core` for every member the bridge
needs. A capability is `full`, `partial` or `missing`; a tool whose required capability
is `missing` is **not advertised**. This is how an upstream refactor or a .NET upgrade
degrades the surface instead of breaking calls.

| Capability | Needs |
|---|---|
| `notes` | `UNote`, `UVoicePart` |
| `pitch_points` | `UProject`, `UNote` pitch points |
| `vibrato` | `UVibrato` |
| `phoneme_override` | `UPhonemeOverride` |
| `expressions` | `UExpressionDescriptor` |
| `curves` | `UCurve` |
| `part_geometry` | `UVoicePart` |
| `track_props` | `UTrack` |
| `history` | `DocManager.StartUndoGroup`, `DocManager.Undo` |
| `project_lifecycle` | `LoadProjectNotification`, `SaveProjectNotification` |
| `singer_registry` | `SingerManager` |
| `export_audio` | `PlaybackManager.RenderMixdown`, `PlaybackManager.RenderToFiles` |
| `packages` | `PackageManager.InstallAsync` |

## Error codes

| Code | Meaning |
|---|---|
| `invalid_request` | Malformed tool name or arguments. |
| `not_initialized` | The host has not finished starting. |
| `no_project` | No project is open. |
| `stale_session` | The project was replaced; ids from the previous session are void. |
| `revision_conflict` | `expectedRevision` does not match the document revision. |
| `stale_write` | An `ifMatch` fingerprint no longer matches. |
| `unsupported_feature` | The running OpenUtau lacks the capability this call needs. |
| `missing_package` | A Core package (GAME, RMVPE, a vocoder, …) is required but absent. |
| `user_busy` | The UI thread could not service the call in time. |
| `write_verification_failed` | The write landed but read-back disagrees. |
| `path_not_allowed` | Path outside the configured access roots. |
| `overwrite_refused` | Target exists and `overwrite` was not set. |
| `render_failed` | Rendering or export failed; nothing was published. |
| `render_incomplete` | Rendering produced failed ranges and `allowIncomplete` was not set. |
| `job_cancelled` | The job was cancelled by the caller. |
| `payload_too_large` | Request body exceeded the limit. |
| `internal_error` | Unexpected failure; the message is diagnostic only. |

## Files written next to the plugin

| File | Purpose |
|---|---|
| `OpenUtau.Plugin.McpBridge.status.json` | Same facts as `openutau_status`, readable when the endpoint is down. |
| `OpenUtau.Plugin.McpBridge.boot.txt` | Append-only trace of plugin startup; distinguishes "never loaded" from "loaded and failed". |
| `mcp-bridge-prompt.txt` | Last generated install prompt. |
