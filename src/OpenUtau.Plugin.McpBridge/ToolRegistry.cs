using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtau.Plugin.McpBridge.Tools;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>
/// The advertised tool surface. Tools whose required capability is missing are not
/// registered, so a Core refactor can only shrink the list, never break calls.
/// </summary>
internal static class ToolRegistry
{
    private sealed record ToolDefinition(
        string Name,
        string Title,
        string Description,
        string[] Requires,
        JsonObject InputSchema,
        Func<JsonElement?, object> Handler);

    private static readonly ToolDefinition[] Definitions =
    {
        new(
            "openutau_status",
            "OpenUtau status",
            "Connection and compatibility facts: bridge/app versions, capability probe, current session and revision. Call this first.",
            Array.Empty<string>(),
            Schema(),
            _ => StatusReport.Build()),

        new(
            "openutau_read_snapshot",
            "Read project snapshot",
            "Project, tracks and voice parts, plus the document revision and change sequence to poll from. Use trackNo and tickRange to narrow large projects.",
            new[] { "notes" },
            Schema(
                P("trackNo", "integer", "Only parts on this track."),
                P("tickRange", "array", "Two ticks: keep parts intersecting [start, end)."),
                P("includeCurves", "boolean", "Include per-part curve summaries."),
                P("includeExpressions", "boolean", "Include project/track expression descriptors.")),
            ReadTools.Snapshot),

        new(
            "openutau_read_changes",
            "Read changes",
            "Commands committed since a change sequence, with the host's own impact kind (part/curves/track/project). `requiresSnapshot` means the gap was too large: take a new snapshot.",
            new[] { "notes" },
            Schema(
                P("sinceSequence", "integer", "Last sequence the caller has seen; 0 for everything buffered."),
                P("limit", "integer", "Max entries (default 500, cap 1000).")),
            ReadTools.ReadChanges),

        new(
            "openutau_list_parts",
            "List parts",
            "Voice parts with stable session ids, track, position, duration and note count.",
            new[] { "notes" },
            Schema(P("trackNo", "integer", "Only parts on this track.")),
            ReadTools.ListParts),

        new(
            "openutau_read_notes",
            "Read notes",
            "Page through a voice part's notes. `detail: full` adds pitch points, vibrato and the per-note phonemizer override.",
            new[] { "notes" },
            Schema(
                P("partId", "string", "Part id from list_parts/read_snapshot.", true),
                P("offset", "integer", "Skip this many notes."),
                P("limit", "integer", "Max notes (default 500, cap 5000)."),
                P("detail", "string", "basic | full")),
            ReadTools.ReadNotes),

        new(
            "openutau_read_pitch",
            "Read note pitch",
            "Pitch points of one note in the editor's own units: xMs is milliseconds from the note start, y is tenths of a semitone.",
            new[] { "pitch_points" },
            Schema(
                P("partId", "string", "Part id.", true),
                P("noteId", "string", "Note id from read_notes.", true)),
            ReadTools.ReadPitch),

        new(
            "openutau_list_expressions",
            "List expressions",
            "Expression descriptors (abbr, name, type, range, default, options) available on the project and on each track.",
            new[] { "expressions" },
            Schema(),
            ReadTools.ListExpressions),

        new(
            "openutau_list_curves",
            "List curves",
            "Curve summaries for a voice part: abbreviation, point count and tick span.",
            new[] { "curves" },
            Schema(P("partId", "string", "Part id.", true)),
            ReadTools.ListCurves),

        new(
            "openutau_read_curve",
            "Read curve",
            "Page through one curve's control points (tick, value). The value scale is the expression's own min..max.",
            new[] { "curves" },
            Schema(
                P("partId", "string", "Part id.", true),
                P("abbr", "string", "Curve abbreviation.", true),
                P("offset", "integer", "Skip this many points."),
                P("limit", "integer", "Max points (default 1000, cap 5000).")),
            ReadTools.ReadCurve),

        new(
            "openutau_add_notes",
            "Add notes",
            "Add 1..1024 notes to a voice part. Defaults to a dry run: pass `dryRun: false` to commit. One call is one undo group.",
            new[] { "notes", "history" },
            Schema(
                P("partId", "string", "Part id.", true),
                P("expectedRevision", "integer", "Document revision from status/snapshot.", true),
                P("dryRun", "boolean", "Default true: validate and preview only."),
                PArr("notes", "Notes to add.", NoteDraft(), true)),
            WriteTools.AddNotes),

        new(
            "openutau_edit_notes",
            "Edit notes",
            "Apply absolute position/duration/tone/lyric changes to existing notes by id. Defaults to a dry run; one call is one undo group.",
            new[] { "notes", "history" },
            Schema(
                P("partId", "string", "Part id.", true),
                P("expectedRevision", "integer", "Document revision from status/snapshot.", true),
                P("dryRun", "boolean", "Default true: validate and preview only."),
                PArr("edits", "Note edits.", NoteEdit(), true)),
            WriteTools.EditNotes),

        new(
            "openutau_delete_notes",
            "Delete notes",
            "Delete notes by id. Defaults to a dry run; one call is one undo group.",
            new[] { "notes", "history" },
            Schema(
                P("partId", "string", "Part id.", true),
                P("expectedRevision", "integer", "Document revision from status/snapshot.", true),
                P("dryRun", "boolean", "Default true: validate and preview only."),
                PArr("noteIds", "Note ids to delete.", new JsonObject { ["type"] = "string" }, true)),
            WriteTools.DeleteNotes),

        new(
            "openutau_undo",
            "Undo",
            "Undo the last undo group, including the user's own edits.",
            new[] { "history" },
            Schema(),
            WriteTools.Undo),

        new(
            "openutau_redo",
            "Redo",
            "Redo the last undone group.",
            new[] { "history" },
            Schema(),
            WriteTools.Redo),

        new(
            "openutau_history_state",
            "History state",
            "Current document revision and how many undo/redo groups are available.",
            new[] { "history" },
            Schema(),
            WriteTools.HistoryState),

        new(
            "openutau_set_pitch",
            "Set note pitch",
            "Replace one note's pitch points (or reset them to the default portamento). Points use the editor's units; the list is sorted by xMs.",
            new[] { "pitch_points", "history" },
            Schema(
                P("partId", "string", "Part id.", true),
                P("noteId", "string", "Note id.", true),
                P("expectedRevision", "integer", "Document revision from status/snapshot.", true),
                P("ifMatch", "string", "Fingerprint from a previous read; rejects the write if the content changed."),
                P("dryRun", "boolean", "Default true: validate and preview only."),
                P("reset", "boolean", "Reset to the host's default pitch points instead of writing a list."),
                P("snapFirst", "boolean", "Whether the first point stays glued to the previous note."),
                PArr("points", "Pitch points; required unless reset is true.", Shape("Pitch point shape, e.g. io or l."))),
            MusicTools.SetPitch),

        new(
            "openutau_set_vibrato",
            "Set vibrato",
            "Replace one note's vibrato. Omitted fields keep their current value; the host clamps each range.",
            new[] { "vibrato", "history" },
            Schema(
                P("partId", "string", "Part id.", true),
                P("noteId", "string", "Note id.", true),
                P("expectedRevision", "integer", "Document revision from status/snapshot.", true),
                P("ifMatch", "string", "Fingerprint from a previous read; rejects the write if the content changed."),
                P("dryRun", "boolean", "Default true: validate and preview only."),
                P("length", "number", "Vibrato coverage, percent of the note."),
                P("period", "number", "Milliseconds."),
                P("depth", "number", "Cents."),
                P("in", "number", "Fade-in, percent of the vibrato length."),
                P("out", "number", "Fade-out, percent of the vibrato length."),
                P("shift", "number", "Phase shift, percent of the period."),
                P("drift", "number", "Up/down drift."),
                P("volLink", "number", "Volume linkage, percent.")),
            MusicTools.SetVibrato),

        new(
            "openutau_set_expression",
            "Set expression",
            "Set or clear one expression on every phoneme of the given notes. Use `value: null` to clear. Only descriptors listed by list_expressions are accepted.",
            new[] { "expressions", "history" },
            Schema(
                P("partId", "string", "Part id.", true),
                P("noteId", "string", "Single note id (alternative to noteIds)."),
                PArr("noteIds", "Note ids to change.", new JsonObject { ["type"] = "string" }),
                P("abbr", "string", "Expression abbreviation.", true),
                P("value", "number", "Value inside the descriptor's range; null clears it."),
                P("expectedRevision", "integer", "Document revision from status/snapshot.", true),
                P("ifMatch", "string", "Fingerprint from a previous read; rejects the write if the content changed."),
                P("dryRun", "boolean", "Default true: validate and preview only.")),
            MusicTools.SetExpression),

        new(
            "openutau_write_curve",
            "Write curve",
            "Replace or clear one expression curve of a part. Ticks must sit on the host's 5-tick grid; values are clamped into the descriptor's range.",
            new[] { "curves", "history" },
            Schema(
                P("partId", "string", "Part id.", true),
                P("abbr", "string", "Curve abbreviation.", true),
                P("mode", "string", "replace (default) or clear."),
                PArr("points", "Points for replace mode.", new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["tick"] = new JsonObject { ["type"] = "integer" },
                        ["value"] = new JsonObject { ["type"] = "integer" },
                    },
                    ["required"] = new JsonArray { "tick", "value" },
                }),
                P("expectedRevision", "integer", "Document revision from status/snapshot.", true),
                P("ifMatch", "string", "Fingerprint from a previous read; rejects the write if the content changed."),
                P("dryRun", "boolean", "Default true: validate and preview only.")),
            MusicTools.WriteCurve),

        new(
            "openutau_edit_part",
            "Edit part",
            "Change a voice part's name, position or duration. Duration cannot cross the host's minimum-length boundary.",
            new[] { "part_geometry", "history" },
            Schema(
                P("partId", "string", "Part id.", true),
                P("name", "string", "New name."),
                P("position", "integer", "New start tick."),
                P("duration", "integer", "New duration in ticks."),
                P("expectedRevision", "integer", "Document revision from status/snapshot.", true),
                P("dryRun", "boolean", "Default true: validate and preview only.")),
            MusicTools.EditPart),

        new(
            "openutau_edit_track",
            "Edit track",
            "Rename/recolor a track or change its mixer values. Name and color are undoable; volume/pan/mute/solo are plain host properties and are applied directly.",
            new[] { "track_props", "history" },
            Schema(
                P("trackNo", "integer", "Track index.", true),
                P("name", "string", "New name."),
                P("color", "string", "Host color name."),
                P("volume", "number", "dB."),
                P("pan", "number", "Pan value."),
                P("mute", "boolean", "Mute the track."),
                P("solo", "boolean", "Solo the track."),
                P("expectedRevision", "integer", "Document revision from status/snapshot.", true),
                P("dryRun", "boolean", "Default true: validate and preview only.")),
            MusicTools.EditTrack),

        new(
            "openutau_set_phoneme_override",
            "Set phoneme override",
            "Set one phoneme's alias or millisecond offset by index. Timing overrides (preutter/overlap/attack/release) need the rendered phoneme list and are not writable yet.",
            new[] { "phoneme_override", "history" },
            Schema(
                P("partId", "string", "Part id.", true),
                P("noteId", "string", "Note id.", true),
                P("index", "integer", "0-based phoneme index.", true),
                P("field", "string", "alias | offset", true),
                P("value", "string", "Alias text, or an integer offset in milliseconds."),
                P("expectedRevision", "integer", "Document revision from status/snapshot.", true),
                P("ifMatch", "string", "Fingerprint from a previous read; rejects the write if the content changed."),
                P("dryRun", "boolean", "Default true: validate and preview only.")),
            MusicTools.SetPhonemeOverride),

        new(
            "openutau_list_singers",
            "List singers",
            "Singers OpenUtau has installed, with type and load state.",
            new[] { "singer_registry" },
            Schema(),
            SingerTools.ListSingers),

        new(
            "openutau_describe_singer",
            "Describe singer",
            "One singer's metadata plus the renderers it supports and the resamplers/wavtools installed for the classic backend.",
            new[] { "singer_registry" },
            Schema(P("singerId", "string", "Singer id or name.", true)),
            SingerTools.DescribeSinger),

        new(
            "openutau_set_track_singer",
            "Set track singer",
            "Bind an installed singer to a track. Installed-only: the bridge cannot install voices.",
            new[] { "singer_registry", "history" },
            Schema(
                P("trackNo", "integer", "Track index.", true),
                P("singerId", "string", "Singer id or name.", true),
                P("expectedRevision", "integer", "Document revision from status/snapshot.", true),
                P("dryRun", "boolean", "Default true: validate and preview only.")),
            SingerTools.SetTrackSinger),

        new(
            "openutau_set_track_renderer",
            "Set track renderer",
            "Choose the renderer (and, for the classic backend, the resampler and wavtool) a track uses.",
            new[] { "singer_registry", "history" },
            Schema(
                P("trackNo", "integer", "Track index.", true),
                P("renderer", "string", "Renderer id, e.g. CLASSIC or WORLDLINE-R."),
                P("resampler", "string", "Resampler name (classic only)."),
                P("wavtool", "string", "Wavtool name (classic only)."),
                P("expectedRevision", "integer", "Document revision from status/snapshot.", true),
                P("dryRun", "boolean", "Default true: validate and preview only.")),
            SingerTools.SetTrackRenderer),

        new(
            "openutau_export_capabilities",
            "Export capabilities",
            "What this project can export right now: formats, modes, estimated duration and anything blocking a render.",
            new[] { "export_audio" },
            Schema(),
            ExportTools.Capabilities),

        new(
            "openutau_export_audio",
            "Export audio",
            "Render a mixdown or one file per track. Runs as a job: the file appears only after the render succeeded and was verified.",
            new[] { "export_audio" },
            Schema(
                P("mode", "string", "mixdown (default) or stems.", true),
                P("path", "string", "Absolute output path; for stems it names the file prefix.", true),
                P("overwrite", "boolean", "Replace existing files."),
                P("timeoutSec", "integer", "Render timeout, 5..1800 seconds (default 300)."),
                P("allowIncomplete", "boolean", "Export even when OpenUtau flags notes as invalid.")),
            ExportTools.ExportAudio),

        new(
            "openutau_job_get",
            "Get job",
            "State, result or error of one background job.",
            new[] { "export_audio" },
            Schema(P("jobId", "string", "Job id.", true)),
            ExportTools.JobGet),

        new(
            "openutau_job_list",
            "List jobs",
            "Recent background jobs, newest first.",
            new[] { "export_audio" },
            Schema(P("limit", "integer", "Max jobs (default 10, cap 100).")),
            ExportTools.JobList),

        new(
            "openutau_job_cancel",
            "Cancel job",
            "Ask a running job to stop. A cancelled export publishes nothing.",
            new[] { "export_audio" },
            Schema(P("jobId", "string", "Job id.", true)),
            ExportTools.JobCancel),

        new(
            "openutau_packages_list",
            "List packages",
            "Dependency packages already installed (GAME, RMVPE, vocoders, themes).",
            new[] { "packages" },
            Schema(),
            PackageTools.List),

        new(
            "openutau_packages_search",
            "Search packages",
            "Search OpenUtau's package registry. Runs as a job because it goes to the network.",
            new[] { "packages" },
            Schema(P("query", "string", "Substring of the id or localized name.")),
            PackageTools.Search),

        new(
            "openutau_packages_install",
            "Install package",
            "Install a package from OpenUtau's registry by id. Runs as a job; only registry ids are accepted.",
            new[] { "packages" },
            Schema(P("id", "string", "Registry package id, e.g. game or rmvpe.", true)),
            PackageTools.Install),

        new(
            "openutau_packages_uninstall",
            "Uninstall package",
            "Remove an installed dependency package. Requires confirm: true.",
            new[] { "packages" },
            Schema(
                P("id", "string", "Package id.", true),
                P("confirm", "boolean", "Must be true.", true)),
            PackageTools.Uninstall),

        new(
            "openutau_packages_refresh",
            "Refresh packages",
            "Rescan singers, dependency packages and the resampler/wavtool folders.",
            new[] { "packages" },
            Schema(),
            PackageTools.Refresh),

        new(
            "openutau_project_info",
            "Project info",
            "Name, path, dirty flag, revision and size of the open project.",
            new[] { "project_lifecycle" },
            Schema(),
            ProjectTools.Info),

        new(
            "openutau_project_new",
            "New project",
            "Replace the open project with a fresh one. Rotates the session id: earlier part/note ids stop being valid.",
            new[] { "project_lifecycle" },
            Schema(P("discardChanges", "boolean", "Allow throwing away unsaved changes.")),
            ProjectTools.New),

        new(
            "openutau_project_open",
            "Open project",
            "Load a project from an absolute path. Refuses to discard unsaved changes unless `discardChanges` is true.",
            new[] { "project_lifecycle" },
            Schema(
                P("path", "string", "Absolute path to a project file.", true),
                P("discardChanges", "boolean", "Allow throwing away unsaved changes.")),
            ProjectTools.Open),

        new(
            "openutau_project_save",
            "Save project",
            "Save to the project's current path. Fails when the project has never been saved; use save_as then.",
            new[] { "project_lifecycle" },
            Schema(),
            ProjectTools.Save),

        new(
            "openutau_project_save_as",
            "Save project as",
            "Save to an absolute path and continue there. Existing files are refused unless `overwrite` is true.",
            new[] { "project_lifecycle" },
            Schema(
                P("path", "string", "Absolute target path.", true),
                P("overwrite", "boolean", "Replace the target if it exists.")),
            ProjectTools.SaveAs),
    };

    // ---- composite schemas -----------------------------------------------------

    private static JsonObject NoteDraft()
    {
        var properties = new JsonObject
        {
            ["position"] = new JsonObject { ["type"] = "integer", ["description"] = "Ticks from the part start." },
            ["duration"] = new JsonObject { ["type"] = "integer", ["description"] = "Ticks." },
            ["tone"] = new JsonObject { ["type"] = "integer", ["description"] = "MIDI note number." },
            ["lyric"] = new JsonObject { ["type"] = "string" },
            ["snapFirst"] = new JsonObject { ["type"] = "boolean" },
            ["pitchPoints"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = Shape("Pitch point shape: see PitchPointShape."),
            },
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray { "position", "duration", "tone" },
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject NoteEdit()
    {
        var properties = new JsonObject
        {
            ["noteId"] = new JsonObject { ["type"] = "string" },
            ["position"] = new JsonObject { ["type"] = "integer" },
            ["duration"] = new JsonObject { ["type"] = "integer" },
            ["tone"] = new JsonObject { ["type"] = "integer" },
            ["lyric"] = new JsonObject { ["type"] = "string" },
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray { "noteId" },
            ["additionalProperties"] = false,
        };
    }

    public static IReadOnlyList<string> ToolNames { get; } = Definitions.Select(d => d.Name).ToArray();

    /// <summary>Tools whose required capabilities all probe as available.</summary>
    internal static List<string> AvailableToolNames(IReadOnlyDictionary<string, string> probes) =>
        Definitions
            .Where(d => d.Requires.All(c => probes.TryGetValue(c, out var level) && level == "full"))
            .Select(d => d.Name)
            .ToList();

    private static List<ToolDefinition> Available() => Available(CapabilityProbe.Probe());

    private static List<ToolDefinition> Available(IReadOnlyDictionary<string, string> probes)
    {
        var names = AvailableToolNames(probes);
        return Definitions.Where(d => names.Contains(d.Name)).ToList();
    }

    internal static JsonArray Describe(IReadOnlyDictionary<string, string> probes)
    {
        var array = new JsonArray();
        foreach (var tool in Available(probes))
        {
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["title"] = tool.Title,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }
        return array;
    }

    public static JsonArray Describe()
    {
        var array = new JsonArray();
        foreach (var tool in Available())
        {
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["title"] = tool.Title,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }
        return array;
    }

    public static object Invoke(string name, JsonElement? arguments)
    {
        var tool = Available().FirstOrDefault(d => d.Name == name);
        if (tool == null)
        {
            throw new BridgeException("unsupported_feature", $"Tool '{name}' is not available in this build or on this OpenUtau version.");
        }

        return Ui.Run(() =>
        {
            ChangeFeed.Instance.EnsureAttached();
            return tool.Handler(arguments);
        });
    }

    // ---- schema helpers --------------------------------------------------------

    private static (string Name, string Description, bool Required, JsonObject Schema) P(
        string name, string type, string description, bool required = false)
        => (name, description, required, new JsonObject { ["type"] = type });

    private static (string Name, string Description, bool Required, JsonObject Schema) PArr(
        string name, string description, JsonObject items, bool required = false)
        => (name, description, required, new JsonObject { ["type"] = "array", ["items"] = items });

    private static (string Name, string Description, bool Required, JsonObject Schema) PObj(
        string name, string description, JsonObject schema, bool required = false)
        => (name, description, required, schema);

    private static JsonObject Shape(string description)
    {
        var properties = new JsonObject
        {
            ["xMs"] = new JsonObject { ["type"] = "number", ["description"] = "Milliseconds from the note start." },
            ["y"] = new JsonObject { ["type"] = "number", ["description"] = "Pitch offset in tenths of a semitone." },
            ["shape"] = new JsonObject { ["type"] = "string", ["description"] = description },
        };
        var required = new JsonArray { "xMs", "y" };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject Schema(params (string Name, string Description, bool Required, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var property in properties)
        {
            var item = property.Schema;
            item["description"] = property.Description;
            props[property.Name] = item;
            if (property.Required)
            {
                required.Add(property.Name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false,
        };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }
        return schema;
    }
}


