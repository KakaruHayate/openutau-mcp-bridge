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

## Confirmed in a running OpenUtau (2026-11-01)

Host: OpenUtau development build `0.0.0+1340b6e`, .NET 10.0.14, Avalonia 12.1.3,
Windows 11, portable layout (`Plugins` next to the executable).

- Gate 1: opening a voice part produced `boot.txt` with `initializing` and
  `started http://127.0.0.1:43102/mcp`, plus `status.json`. The plugin scan itself
  stays silent, exactly as predicted.
- Gate 2: `initialize`, `tools/list` (43 tools) and `tools/call` all answered over
  `127.0.0.1:43102`. A second listener in the same session moved to 43103, so the port
  walk works against a real occupant.
- Gate 3: the menu item produced the notification "install prompt copied to clipboard".
  Two bugs surfaced and were fixed on the way: `MainWindow`/`Clipboard` are exposed
  through interfaces (explicit implementations), and, on Avalonia 12,
  `SetTextAsync` is an extension method on `ClipboardExtensions`, not a member of
  `IClipboard`. Awaiting the returned task while the UI thread is blocked deadlocks it;
  the plugin now pumps `Dispatcher.UIThread.RunJobs()` until the task settles.

Two findings worth reporting upstream:

- A project whose notes have no `vibrato` block loads fine, but `HitTestVibrato`
  (`NotesViewModelHitTest.cs:307`) dereferences the null and throws on mouse move over
  the piano roll. A default `vibrato` on deserialize would fix it.
- `UNote.pitch` and `UNote.vibrato` are plain fields with no initializer, so any
  hand-edited or foreign project can carry nulls into the UI.

The bridge now treats missing `pitch`/`vibrato` as empty instead of failing the read.


## Gate 4 — rendering

Deferred: a meaningful check needs an initialized `DocManager` with a scheduler and
phrase pipeline. It is the first task of the export milestone, together with the
"exceptions are swallowed by `PlaybackManager`" hazard: the bridge will render into a
temporary path and verify the file before publishing.
