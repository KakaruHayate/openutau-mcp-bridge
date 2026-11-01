using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Plugin.McpBridge.Tools;

/// <summary>Note editing tools: batched, revision-checked, one undo group per call.</summary>
internal static class WriteTools
{
    private const int MaxBatch = 1024;

    public static object AddNotes(JsonElement? args)
    {
        var (project, part) = Target(args);
        CheckRevision(args);
        var dryRun = Args.Bool(args, "dryRun", true);
        var items = RequireArray(args, "notes");
        if (items.Count == 0 || items.Count > MaxBatch)
        {
            throw new BridgeException("invalid_request", $"'notes' must contain 1..{MaxBatch} items.");
        }

        var drafts = new List<(int Position, int Duration, int Tone, string Lyric, List<PitchPoint>? Points, bool SnapFirst)>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new BridgeException("invalid_request", $"notes[{i}] must be an object.");
            }
            var position = Args.Int(item, "position", int.MinValue);
            var duration = Args.Int(item, "duration", int.MinValue);
            var tone = Args.Int(item, "tone", int.MinValue);
            if (position < 0 || duration <= 0)
            {
                throw new BridgeException("invalid_request", $"notes[{i}] needs position >= 0 and duration > 0.");
            }
            if (tone is < 0 or > 127)
            {
                throw new BridgeException("invalid_request", $"notes[{i}] has tone outside 0..127.");
            }
            var lyric = Args.Str(item, "lyric") ?? "a";
            drafts.Add((position, duration, tone, lyric, ParsePoints(item), Args.Bool(item, "snapFirst", true)));
        }

        if (dryRun)
        {
            var preview = new JsonArray();
            foreach (var draft in drafts)
            {
                preview.Add(new JsonObject
                {
                    ["position"] = draft.Position,
                    ["duration"] = draft.Duration,
                    ["tone"] = draft.Tone,
                    ["toneName"] = MusicMath.GetToneName(draft.Tone),
                    ["lyric"] = draft.Lyric,
                });
            }
            return new JsonObject { ["dryRun"] = true, ["wouldAdd"] = preview, ["count"] = preview.Count };
        }

        var created = new List<UNote>();
        DocManager.Inst.StartUndoGroup();
        try
        {
            foreach (var draft in drafts)
            {
                var note = project.CreateNote(draft.Tone, part.position + draft.Position, draft.Duration);
                note.lyric = draft.Lyric;
                note.position = draft.Position;
                if (draft.Points is { Count: >= 2 })
                {
                    note.pitch.data.Clear();
                    note.pitch.data.AddRange(draft.Points);
                    note.pitch.snapFirst = draft.SnapFirst;
                }
                created.Add(note);
            }
            DocManager.Inst.ExecuteCmd(new AddNoteCommand(part, created));
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        Verify.Added(part, created);
        var ids = new JsonArray();
        foreach (var note in created)
        {
            ids.Add(Session.IdOf(note));
        }
        return new JsonObject
        {
            ["dryRun"] = false,
            ["revision"] = Session.Revision,
            ["added"] = ids,
        };
    }

    public static object EditNotes(JsonElement? args)
    {
        var (_, part) = Target(args);
        CheckRevision(args);
        var dryRun = Args.Bool(args, "dryRun", true);
        var items = RequireArray(args, "edits");
        if (items.Count == 0 || items.Count > MaxBatch)
        {
            throw new BridgeException("invalid_request", $"'edits' must contain 1..{MaxBatch} items.");
        }

        var plan = new List<(UNote Note, int NewPosition, int NewDuration, int NewTone, string NewLyric)>();
        var preview = new JsonArray();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var noteId = Args.Str(item, "noteId")
                ?? throw new BridgeException("invalid_request", $"edits[{i}] needs noteId.");
            var note = Session.NoteById(noteId)
                ?? throw new BridgeException("invalid_request", $"edits[{i}] refers to an unknown note id.");
            if (!part.notes.Contains(note))
            {
                throw new BridgeException("invalid_request", $"edits[{i}] note is not in this part.");
            }
            var position = Args.Int(item, "position", note.position);
            var duration = Args.Int(item, "duration", note.duration);
            var tone = Args.Int(item, "tone", note.tone);
            var lyric = Args.Str(item, "lyric") ?? note.lyric;
            if (position < 0 || duration <= 0 || tone is < 0 or > 127)
            {
                throw new BridgeException("invalid_request", $"edits[{i}] is out of range.");
            }
            plan.Add((note, position, duration, tone, lyric));
            preview.Add(new JsonObject
            {
                ["noteId"] = noteId,
                ["before"] = Describe(note),
                ["after"] = new JsonObject
                {
                    ["position"] = position,
                    ["duration"] = duration,
                    ["tone"] = tone,
                    ["toneName"] = MusicMath.GetToneName(tone),
                    ["lyric"] = lyric,
                },
            });
        }

        if (dryRun)
        {
            return new JsonObject { ["dryRun"] = true, ["changes"] = preview };
        }

        DocManager.Inst.StartUndoGroup();
        try
        {
            foreach (var item in plan)
            {
                var positionDelta = item.NewPosition - item.Note.position;
                var toneDelta = item.NewTone - item.Note.tone;
                var durationDelta = item.NewDuration - item.Note.duration;
                if (positionDelta != 0 || toneDelta != 0)
                {
                    DocManager.Inst.ExecuteCmd(new MoveNoteCommand(part, item.Note, positionDelta, toneDelta));
                }
                if (durationDelta != 0)
                {
                    DocManager.Inst.ExecuteCmd(new ResizeNoteCommand(part, item.Note, durationDelta));
                }
                if (!string.Equals(item.NewLyric, item.Note.lyric, StringComparison.Ordinal))
                {
                    DocManager.Inst.ExecuteCmd(new ChangeNoteLyricCommand(part, item.Note, item.NewLyric));
                }
            }
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        var expectations = new List<(UNote Note, int Position, int Duration, int Tone, string Lyric)>();
        foreach (var item in plan)
        {
            expectations.Add((item.Note, item.NewPosition, item.NewDuration, item.NewTone, item.NewLyric));
        }
        Verify.Edited(expectations);

        return new JsonObject
        {
            ["dryRun"] = false,
            ["revision"] = Session.Revision,
            ["edited"] = plan.Count,
        };
    }

    public static object DeleteNotes(JsonElement? args)
    {
        var (_, part) = Target(args);
        CheckRevision(args);
        var dryRun = Args.Bool(args, "dryRun", true);
        var items = RequireArray(args, "noteIds");
        if (items.Count == 0 || items.Count > MaxBatch)
        {
            throw new BridgeException("invalid_request", $"'noteIds' must contain 1..{MaxBatch} ids.");
        }

        var notes = new List<UNote>();
        var preview = new JsonArray();
        foreach (var element in items)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new BridgeException("invalid_request", "noteIds must be strings.");
            }
            var id = element.GetString()!;
            var note = Session.NoteById(id)
                ?? throw new BridgeException("invalid_request", $"Unknown note id '{id}'.");
            if (!part.notes.Contains(note))
            {
                throw new BridgeException("invalid_request", $"Note '{id}' is not in this part.");
            }
            notes.Add(note);
            preview.Add(Describe(note));
        }

        if (dryRun)
        {
            return new JsonObject { ["dryRun"] = true, ["wouldDelete"] = preview, ["count"] = preview.Count };
        }

        DocManager.Inst.StartUndoGroup();
        try
        {
            DocManager.Inst.ExecuteCmd(new RemoveNoteCommand(part, notes));
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        return new JsonObject { ["dryRun"] = false, ["revision"] = Session.Revision, ["deleted"] = notes.Count };
    }

    public static object Undo(JsonElement? args)
    {
        DocManager.Inst.Undo();
        return new JsonObject { ["revision"] = Session.Revision, ["history"] = History() };
    }

    public static object Redo(JsonElement? args)
    {
        DocManager.Inst.Redo();
        return new JsonObject { ["revision"] = Session.Revision, ["history"] = History() };
    }

    public static object HistoryState(JsonElement? args) => new JsonObject
    {
        ["revision"] = Session.Revision,
        ["history"] = History(),
    };

    // ---- helpers ---------------------------------------------------------------

    private static JsonObject History()
    {
        var type = typeof(DocManager);
        var undo = type.GetField("undoQueue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var redo = type.GetField("redoQueue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return new JsonObject
        {
            ["undo"] = Count(undo?.GetValue(DocManager.Inst)),
            ["redo"] = Count(redo?.GetValue(DocManager.Inst)),
        };
    }

    private static int Count(object? queue)
    {
        if (queue == null)
        {
            return 0;
        }
        var property = queue.GetType().GetProperty("Count");
        return property?.GetValue(queue) is int value ? value : 0;
    }

    private static (UProject Project, UVoicePart Part) Target(JsonElement? args)
    {
        var project = ReadTools.Project();
        var part = ReadTools.FindPart(project, Args.ReqStr(args, "partId"));
        return (project, part);
    }

    private static void CheckRevision(JsonElement? args)
    {
        var expected = Args.Long(args, "expectedRevision", -1);
        if (expected < 0)
        {
            throw new BridgeException("invalid_request", "'expectedRevision' is required; read openutau_status or a snapshot first.");
        }
        var current = Session.Revision;
        if (expected != current)
        {
            throw new BridgeException("revision_conflict",
                $"expectedRevision {expected} does not match the document revision {current}. Re-read, then retry.");
        }
    }

    private static List<JsonElement> RequireArray(JsonElement? args, string name)
    {
        var element = Args.Get(args, name);
        if (element is not { ValueKind: JsonValueKind.Array } array)
        {
            throw new BridgeException("invalid_request", $"'{name}' must be a non-empty array.");
        }
        return array.EnumerateArray().ToList();
    }

    private static List<PitchPoint>? ParsePoints(JsonElement item)
    {
        if (Args.Get(item, "pitchPoints") is not { ValueKind: JsonValueKind.Array } points || points.GetArrayLength() == 0)
        {
            return null;
        }
        var result = new List<PitchPoint>();
        foreach (var element in points.EnumerateArray())
        {
            var x = Args.Int(element, "xMs", int.MinValue);
            var y = Args.Get(element, "y") is { ValueKind: JsonValueKind.Number } yElement
                ? (float)yElement.GetDouble()
                : float.MinValue;
            if (x == int.MinValue || y == float.MinValue)
            {
                throw new BridgeException("invalid_request", "Each pitch point needs xMs and y.");
            }
            var shapeText = Args.Str(element, "shape") ?? "io";
            if (!Enum.TryParse<PitchPointShape>(shapeText, ignoreCase: true, out var shape))
            {
                throw new BridgeException("invalid_request",
                    $"Unknown pitch point shape '{shapeText}'. Valid: {string.Join(", ", Enum.GetNames<PitchPointShape>())}.");
            }
            result.Add(new PitchPoint { X = x, Y = y, shape = shape });
        }
        return result;
    }

    private static JsonObject Describe(UNote note) => new()
    {
        ["noteId"] = Session.IdOf(note),
        ["position"] = note.position,
        ["duration"] = note.duration,
        ["tone"] = note.tone,
        ["toneName"] = MusicMath.GetToneName(note.tone),
        ["lyric"] = note.lyric,
    };
}

/// <summary>Post-write read-back: a write that silently did not land is a bug, not a success.</summary>
internal static class Verify
{
    public static void Added(UVoicePart part, IReadOnlyList<UNote> notes)
    {
        foreach (var note in notes)
        {
            if (!part.notes.Contains(note))
            {
                throw new BridgeException("write_verification_failed", "A note was reported added but is not in the part.");
            }
        }
    }

    public static void Edited(IEnumerable<(UNote Note, int Position, int Duration, int Tone, string Lyric)> expected)
    {
        foreach (var item in expected)
        {
            if (item.Note.position != item.Position
                || item.Note.duration != item.Duration
                || item.Note.tone != item.Tone
                || !string.Equals(item.Note.lyric, item.Lyric, StringComparison.Ordinal))
            {
                throw new BridgeException("write_verification_failed",
                    $"Note {Session.IdOf(item.Note)} did not keep the requested values.");
            }
        }
    }
}
