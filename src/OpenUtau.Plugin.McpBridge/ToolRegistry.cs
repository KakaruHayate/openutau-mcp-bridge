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

    private static List<ToolDefinition> Available()
    {
        var probes = CapabilityProbe.Probe();
        return Definitions
            .Where(d => d.Requires.All(c => probes.TryGetValue(c, out var level) && level == "full"))
            .ToList();
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
