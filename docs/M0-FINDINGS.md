# M0 gate report

Scope: prove the four mechanisms the whole design rests on before building the tool
surface.

| Gate | Question | Result |
|---|---|---|
| 1 | Does a managed `BatchEdit` DLL get discovered and can it run code? | **Mechanism passes, trigger is later than hoped** |
| 2 | Does the loopback HTTP listener work from inside OpenUtau's process? | **Passes on Windows**; macOS/Linux to be covered by CI |
| 3 | Can the plugin put text on the clipboard without referencing Avalonia? | **Not yet exercised in a real window**; fallback path confirmed |
| 4 | Do `RenderMixdown` / `RenderToFiles` run inside the plugin? | **Not started**; scheduled as the first task of the export milestone |

## Gate 1 — discovery and code execution

What we verified:

- Building against the host's `OpenUtau.Core.dll` and dropping the DLL into the
  `Plugins` folder is enough for the host to expose the type as an external batch edit.
- Instantiating that type (which the piano-roll does) starts the HTTP server and the
  full MCP handshake succeeds: `initialize`, `tools/list`, `tools/call`.

What did not work:

- `[ModuleInitializer]` does not run when the host loads the DLL with
  `Assembly.LoadFile` and reflects over its exported types; it runs only when code from
  the module first executes. Measured directly with a harness that loads the assembly
  and calls `GetExportedTypes()`: no endpoint, no boot marker.
- The host instantiates external `BatchEdit` implementations **inside
  `PianoRoll.InitializePianoRollWindowAsync`**, which runs when a voice part is opened,
  not at application start.

Consequences:

- The bridge comes up the first time the user opens a part, or when they click the
  plugin's menu item. A user who has no project open cannot connect yet.
- The install prompt tells the agent to ask the user to open a voice part when the
  endpoint is unreachable, and the status/boot files make the difference between
  "not loaded" and "loaded but not started" visible.
- Recommended upstream follow-up (small, not MCP-specific): instantiate external batch
  edits once during the plugin scan, or expose a documented plugin-initialization hook.
  That would let a plugin start at application launch without touching the MCP surface.

## Gate 2 — listener

- `HttpListener` with a numeric loopback prefix starts without elevation and without a
  URL ACL entry on Windows.
- The server answers `initialize` / `tools/list` / `tools/call` and returns `202` for
  notifications; unknown paths get `404`, non-POST gets `405`, foreign `Origin` gets
  `403`.
- macOS and Linux are untested locally; the code uses only the managed
  `HttpListener`, so this is a CI-matrix item rather than a design risk.

## Gate 3 — clipboard

- The plugin is compiled without any Avalonia reference and reaches the clipboard
  through reflection (`Application.Current` → lifetime → `MainWindow.Clipboard` →
  `SetTextAsync`).
- When those types are absent (as in a bare harness), the call returns `false` and the
  prompt is written to `mcp-bridge-prompt.txt` instead; the notification names the file.
- The success path needs a real window, i.e. the same "open a part" step as gate 1.

## Gate 4 — rendering

Deferred: a meaningful check needs an initialized `DocManager` with a scheduler and
phrase pipeline. It is the first task of the export milestone, together with the
"exceptions are swallowed by `PlaybackManager`" hazard: the bridge will render into a
temporary path and verify the file before publishing.
