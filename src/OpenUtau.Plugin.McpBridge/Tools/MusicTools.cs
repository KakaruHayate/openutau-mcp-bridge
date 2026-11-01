using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Plugin.McpBridge.Tools;

/// <summary>
/// Musical edits beyond notes: pitch points, vibrato, expressions, curves, parts, tracks
/// and the phoneme alias/offset pair. Same contract as note writes: dry run by default,
/// one undo group per call, read-back verification.
/// </summary>
internal static class MusicTools
{
    public static object SetPitch(JsonElement? args)
    {
        var (project, part, note) = NoteTarget(args);
        CheckRevision(args);
        var dryRun = Args.Bool(args, "dryRun", true);
        var reset = Args.Bool(args, "reset", false);
        var points = reset ? null : ParsePoints(args);
        var snapFirst = Args.Bool(args, "snapFirst", note.pitch?.snapFirst ?? true);
        CheckIfMatch(args, Fingerprints.Note(part, note), "The note");

        if (dryRun)
        {
            return new JsonObject
            {
                ["dryRun"] = true,
                ["reset"] = reset,
                ["snapFirst"] = snapFirst,
                ["points"] = points == null ? new JsonArray() : PointsJson(points),
            };
        }

        DocManager.Inst.StartUndoGroup();
        try
        {
            if (reset)
            {
                DocManager.Inst.ExecuteCmd(new ResetPitchPointsCommand(part, note));
            }
            else
            {
                var pitch = new UPitch { snapFirst = snapFirst };
                foreach (var point in points!)
                {
                    pitch.data.Add(new PitchPoint { X = point.X, Y = point.Y, shape = point.Shape });
                }
                DocManager.Inst.ExecuteCmd(new SetPitchPointsCommand(part, note, pitch));
            }
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        if (!reset)
        {
            var applied = note.pitch ?? new UPitch();
            if (applied.data.Count != points!.Count)
            {
                throw new BridgeException("write_verification_failed", "Pitch point count changed after the write.");
            }
            // With snapFirst the host owns the first point's Y: it glues the note to the
            // previous one, so comparing it would reject a correct write.
            var firstOwned = applied.snapFirst;
            for (var i = 0; i < points.Count; i++)
            {
                if (firstOwned && i == 0 && Math.Abs(applied.data[i].X - points[i].X) > 0.001)
                {
                    continue;
                }
                if (Math.Abs(applied.data[i].X - points[i].X) > 0.001
                    || (!(firstOwned && i == 0) && Math.Abs(applied.data[i].Y - points[i].Y) > 0.001))
                {
                    throw new BridgeException("write_verification_failed", $"Pitch point {i} did not keep the requested values.");
                }
            }
        }

        var appliedPitch = note.pitch ?? new UPitch();
        return new JsonObject
        {
            ["dryRun"] = false,
            ["revision"] = Session.Revision,
            ["noteId"] = Session.IdOf(note),
            ["points"] = PointsJson(appliedPitch.data.Select(p => (p.X, p.Y, p.shape)).ToList()),
            ["snapFirst"] = appliedPitch.snapFirst,
        };
    }

    public static object SetVibrato(JsonElement? args)
    {
        var (_, part, note) = NoteTarget(args);
        CheckRevision(args);
        CheckIfMatch(args, Fingerprints.Note(part, note), "The note");
        var dryRun = Args.Bool(args, "dryRun", true);

        var current = note.vibrato ?? new UVibrato();
        var vibrato = new UVibrato
        {
            length = Number(args, "length", current.length),
            period = Number(args, "period", current.period),
            depth = Number(args, "depth", current.depth),
            @in = Number(args, "in", current.@in),
            @out = Number(args, "out", current.@out),
            shift = Number(args, "shift", current.shift),
            drift = Number(args, "drift", current.drift),
            volLink = Number(args, "volLink", current.volLink),
        };

        var payload = new JsonObject
        {
            ["length"] = Math.Round(vibrato.length, 2),
            ["period"] = Math.Round(vibrato.period, 2),
            ["depth"] = Math.Round(vibrato.depth, 2),
            ["in"] = Math.Round(vibrato.@in, 2),
            ["out"] = Math.Round(vibrato.@out, 2),
            ["shift"] = Math.Round(vibrato.shift, 2),
            ["drift"] = Math.Round(vibrato.drift, 2),
            ["volLink"] = Math.Round(vibrato.volLink, 2),
        };

        if (dryRun)
        {
            return new JsonObject { ["dryRun"] = true, ["vibrato"] = payload };
        }

        DocManager.Inst.StartUndoGroup();
        try
        {
            DocManager.Inst.ExecuteCmd(new SetVibratoCommand(part, note, vibrato));
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        var appliedVibrato = note.vibrato ?? new UVibrato();
        if (Math.Abs(appliedVibrato.length - vibrato.length) > 0.01
            || Math.Abs(appliedVibrato.depth - vibrato.depth) > 0.01)
        {
            throw new BridgeException("write_verification_failed", "Vibrato did not keep the requested values.");
        }

        var appliedPitch = note.pitch ?? new UPitch();
        return new JsonObject
        {
            ["dryRun"] = false,
            ["revision"] = Session.Revision,
            ["noteId"] = Session.IdOf(note),
            ["vibrato"] = payload,
        };
    }

    public static object SetExpression(JsonElement? args)
    {
        var project = ReadTools.Project();
        var part = ReadTools.FindPart(project, Args.ReqStr(args, "partId"));
        CheckRevision(args);
        var dryRun = Args.Bool(args, "dryRun", true);
        var abbr = Args.ReqStr(args, "abbr");
        var track = project.tracks[part.trackNo];

        if (!project.expressions.TryGetValue(abbr, out var descriptor))
        {
            throw new BridgeException("invalid_request", $"Unknown expression abbreviation '{abbr}'.");
        }

        var valueElement = Args.Get(args, "value");
        float? value = valueElement is { ValueKind: JsonValueKind.Number } number
            ? (float)number.GetDouble()
            : null;

        if (value is { } v && (v < descriptor.min || v > descriptor.max))
        {
            throw new BridgeException("invalid_request",
                $"'{abbr}' accepts {descriptor.min}..{descriptor.max}, got {v}.");
        }

        var notes = NoteList(part, args);
        foreach (var note in notes)
        {
            CheckIfMatch(args, Fingerprints.Expression(part, note, abbr), "The note expression");
        }
        if (dryRun)
        {
            var ids = new JsonArray();
            foreach (var note in notes)
            {
                ids.Add(Session.IdOf(note));
            }
            return new JsonObject
            {
                ["dryRun"] = true,
                ["abbr"] = abbr,
                ["value"] = value,
                ["notes"] = ids,
            };
        }

        DocManager.Inst.StartUndoGroup();
        try
        {
            DocManager.Inst.ExecuteCmd(new SetNotesSameExpressionCommand(project, track, part, notes, abbr, value));
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        foreach (var note in notes)
        {
            // Read the stored entries, not the effective per-phoneme view: a note without
            // rendered phonemes still holds the value at index 0.
            var stored = note.phonemeExpressions.Where(e => e.abbr == abbr).ToList();
            if (value == null)
            {
                if (stored.Count != 0)
                {
                    throw new BridgeException("write_verification_failed", $"Expression '{abbr}' was cleared but is still set.");
                }
            }
            else if (stored.Count == 0 || stored.Any(e => Math.Abs(e.value - value.Value) > 0.001))
            {
                throw new BridgeException("write_verification_failed", $"Expression '{abbr}' did not keep the requested value.");
            }
        }

        return new JsonObject
        {
            ["dryRun"] = false,
            ["revision"] = Session.Revision,
            ["abbr"] = abbr,
            ["value"] = value,
            ["notes"] = notes.Count,
        };
    }

    public static object WriteCurve(JsonElement? args)
    {
        var project = ReadTools.Project();
        var part = ReadTools.FindPart(project, Args.ReqStr(args, "partId"));
        CheckRevision(args);
        var dryRun = Args.Bool(args, "dryRun", true);
        var abbr = Args.ReqStr(args, "abbr");
        var mode = Args.Str(args, "mode") ?? "replace";

        if (!project.expressions.TryGetValue(abbr, out var descriptor))
        {
            throw new BridgeException("invalid_request", $"Unknown expression abbreviation '{abbr}'.");
        }

        CheckIfMatch(args, Fingerprints.Curve(part, abbr), "The curve");

        var existing = part.curves.FirstOrDefault(c => c.abbr == abbr);
        var oldXs = existing?.xs.ToArray() ?? Array.Empty<int>();
        var oldYs = existing?.ys.ToArray() ?? Array.Empty<int>();

        if (string.Equals(mode, "clear", StringComparison.OrdinalIgnoreCase))
        {
            if (dryRun)
            {
                return new JsonObject { ["dryRun"] = true, ["mode"] = "clear", ["abbr"] = abbr };
            }
            DocManager.Inst.StartUndoGroup();
            try
            {
                DocManager.Inst.ExecuteCmd(new ClearCurveCommand(part, abbr));
            }
            finally
            {
                DocManager.Inst.EndUndoGroup();
            }
            return new JsonObject { ["dryRun"] = false, ["revision"] = Session.Revision, ["abbr"] = abbr, ["cleared"] = true };
        }

        var points = ParseCurvePoints(args, descriptor);
        if (dryRun)
        {
            var preview = new JsonArray();
            foreach (var point in points)
            {
                preview.Add(new JsonObject { ["tick"] = point.X, ["value"] = point.Y });
            }
            return new JsonObject { ["dryRun"] = true, ["mode"] = "replace", ["abbr"] = abbr, ["points"] = preview };
        }

        var newXs = points.Select(p => p.X).ToArray();
        var newYs = points.Select(p => p.Y).ToArray();

        DocManager.Inst.StartUndoGroup();
        try
        {
            DocManager.Inst.ExecuteCmd(new MergedSetCurveCommand(project, part, abbr, oldXs, oldYs, newXs, newYs));
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        var written = part.curves.FirstOrDefault(c => c.abbr == abbr);
        if (written == null || !written.xs.SequenceEqual(newXs))
        {
            throw new BridgeException("write_verification_failed", "The curve did not keep the requested points.");
        }

        return new JsonObject
        {
            ["dryRun"] = false,
            ["revision"] = Session.Revision,
            ["abbr"] = abbr,
            ["points"] = newXs.Length,
        };
    }

    public static object EditPart(JsonElement? args)
    {
        var project = ReadTools.Project();
        var part = ReadTools.FindPart(project, Args.ReqStr(args, "partId"));
        CheckRevision(args);
        var dryRun = Args.Bool(args, "dryRun", true);

        var name = Args.Str(args, "name");
        var position = Args.IntOrNull(args, "position");
        var duration = Args.IntOrNull(args, "duration");

        if (name == null && position == null && duration == null)
        {
            throw new BridgeException("invalid_request", "Nothing to change: pass name, position and/or duration.");
        }
        if (position is < 0)
        {
            throw new BridgeException("invalid_request", "position must be >= 0.");
        }
        if (duration is <= 0)
        {
            throw new BridgeException("invalid_request", "duration must be > 0.");
        }

        if (dryRun)
        {
            return new JsonObject
            {
                ["dryRun"] = true,
                ["name"] = name ?? part.name,
                ["position"] = position ?? part.position,
                ["duration"] = duration ?? part.Duration,
            };
        }

        DocManager.Inst.StartUndoGroup();
        try
        {
            if (name != null && name != part.name)
            {
                DocManager.Inst.ExecuteCmd(new RenamePartCommand(project, part, name));
            }
            if (position is { } newPosition && newPosition != part.position)
            {
                DocManager.Inst.ExecuteCmd(new MovePartCommand(project, part, newPosition, part.trackNo));
            }
            if (duration is { } newDuration && newDuration != part.Duration)
            {
                DocManager.Inst.ExecuteCmd(new ResizeVoicePartCommand(project, part, newDuration - part.Duration, fromStart: false));
            }
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        if (name != null && part.name != name)
        {
            throw new BridgeException("write_verification_failed", "Part name did not change.");
        }
        if (position is { } expectedPosition && part.position != expectedPosition)
        {
            throw new BridgeException("write_verification_failed", "Part position did not change.");
        }
        if (duration is { } expectedDuration && part.Duration != expectedDuration)
        {
            throw new BridgeException("write_verification_failed", $"Part duration is {part.Duration}, expected {expectedDuration}.");
        }

        return new JsonObject
        {
            ["dryRun"] = false,
            ["revision"] = Session.Revision,
            ["name"] = part.name,
            ["position"] = part.position,
            ["duration"] = part.Duration,
        };
    }

    public static object EditTrack(JsonElement? args)
    {
        var project = ReadTools.Project();
        var trackNo = Args.IntOrNull(args, "trackNo")
            ?? throw new BridgeException("invalid_request", "'trackNo' is required.");
        if (trackNo < 0 || trackNo >= project.tracks.Count)
        {
            throw new BridgeException("invalid_request", $"No track {trackNo}.");
        }
        CheckRevision(args);
        var dryRun = Args.Bool(args, "dryRun", true);
        var track = project.tracks[trackNo];

        var name = Args.Str(args, "name");
        var color = Args.Str(args, "color");
        var volume = Args.Get(args, "volume") is { ValueKind: JsonValueKind.Number } volumeElement ? (double?)volumeElement.GetDouble() : null;
        var pan = Args.Get(args, "pan") is { ValueKind: JsonValueKind.Number } panElement ? (double?)panElement.GetDouble() : null;
        var mute = Args.Get(args, "mute") is { ValueKind: JsonValueKind.True or JsonValueKind.False } muteElement ? muteElement.GetBoolean() : (bool?)null;
        var solo = Args.Get(args, "solo") is { ValueKind: JsonValueKind.True or JsonValueKind.False } soloElement ? soloElement.GetBoolean() : (bool?)null;

        if (name == null && color == null && volume == null && pan == null && mute == null && solo == null)
        {
            throw new BridgeException("invalid_request", "Nothing to change.");
        }

        if (dryRun)
        {
            return new JsonObject
            {
                ["dryRun"] = true,
                ["name"] = name ?? track.TrackName,
                ["color"] = color ?? track.TrackColor,
                ["volume"] = volume ?? track.Volume,
                ["pan"] = pan ?? track.Pan,
                ["mute"] = mute ?? track.Mute,
                ["solo"] = solo ?? track.Solo,
            };
        }

        // Name and color are undoable host commands. Mixer values are plain properties in
        // OpenUtau (the track header writes them directly), so they are applied directly
        // and are not part of the undo group.
        var undoable = false;
        DocManager.Inst.StartUndoGroup();
        try
        {
            if (name != null && name != track.TrackName)
            {
                DocManager.Inst.ExecuteCmd(new RenameTrackCommand(project, track, name));
                undoable = true;
            }
            if (color != null && color != track.TrackColor)
            {
                DocManager.Inst.ExecuteCmd(new ChangeTrackColorCommand(project, track, color));
                undoable = true;
            }
        }
        finally
        {
            DocManager.Inst.EndUndoGroup();
        }

        var mixerChanged = false;
        if (volume is { } v)
        {
            track.Volume = v;
            mixerChanged = true;
        }
        if (pan is { } p)
        {
            track.Pan = p;
            mixerChanged = true;
        }
        if (mute is { } m)
        {
            track.Mute = m;
            mixerChanged = true;
        }
        if (solo is { } s)
        {
            track.Solo = s;
            mixerChanged = true;
        }

        return new JsonObject
        {
            ["dryRun"] = false,
            ["revision"] = Session.Revision,
            ["trackNo"] = trackNo,
            ["name"] = track.TrackName,
            ["color"] = track.TrackColor,
            ["volume"] = Math.Round(track.Volume, 2),
            ["pan"] = Math.Round(track.Pan, 2),
            ["mute"] = track.Mute,
            ["solo"] = track.Solo,
            ["undoable"] = undoable,
            ["mixerChanged"] = mixerChanged,
        };
    }

    public static object SetPhonemeOverride(JsonElement? args)
    {
        var (_, part, note) = NoteTarget(args);
        CheckRevision(args);
        var dryRun = Args.Bool(args, "dryRun", true);
        var index = Args.Int(args, "index", -1);
        if (index < 0)
        {
            throw new BridgeException("invalid_request", "'index' is required (0-based phoneme index).");
        }
        var field = Args.ReqStr(args, "field");
        CheckIfMatch(args, Fingerprints.Note(part, note), "The note");

        switch (field)
        {
            case "alias":
            {
                var alias = Args.Str(args, "value");
                if (dryRun)
                {
                    return new JsonObject { ["dryRun"] = true, ["field"] = field, ["value"] = alias };
                }
                DocManager.Inst.StartUndoGroup();
                try
                {
                    DocManager.Inst.ExecuteCmd(new ChangePhonemeAliasCommand(part, note, index, alias));
                }
                finally
                {
                    DocManager.Inst.EndUndoGroup();
                }
                var applied = note.GetPhonemeOverride(index).phoneme;
                if (!string.Equals(applied, alias, StringComparison.Ordinal))
                {
                    throw new BridgeException("write_verification_failed", "The phoneme alias did not change.");
                }
                return new JsonObject { ["dryRun"] = false, ["revision"] = Session.Revision, ["field"] = field, ["value"] = applied };
            }
            case "offset":
            {
                var offset = Args.Int(args, "value", int.MinValue);
                if (offset == int.MinValue)
                {
                    throw new BridgeException("invalid_request", "'value' must be an integer millisecond offset.");
                }
                if (dryRun)
                {
                    return new JsonObject { ["dryRun"] = true, ["field"] = field, ["value"] = offset };
                }
                DocManager.Inst.StartUndoGroup();
                try
                {
                    DocManager.Inst.ExecuteCmd(new PhonemeOffsetCommand(part, note, index, offset));
                }
                finally
                {
                    DocManager.Inst.EndUndoGroup();
                }
                var appliedOffset = note.GetPhonemeOverride(index).offset;
                if (appliedOffset != offset)
                {
                    throw new BridgeException("write_verification_failed", $"Phoneme offset is {appliedOffset}, expected {offset}.");
                }
                return new JsonObject { ["dryRun"] = false, ["revision"] = Session.Revision, ["field"] = field, ["value"] = appliedOffset };
            }
            default:
                throw new BridgeException("unsupported_feature",
                    $"'{field}' needs the rendered phoneme list; only alias and offset are writable today.");
        }
    }

    // ---- helpers ---------------------------------------------------------------

    private static (UProject Project, UVoicePart Part, UNote Note) NoteTarget(JsonElement? args)
    {
        var project = ReadTools.Project();
        var part = ReadTools.FindPart(project, Args.ReqStr(args, "partId"));
        var noteId = Args.ReqStr(args, "noteId");
        var note = Session.NoteById(noteId)
            ?? throw new BridgeException("invalid_request", $"Unknown note id '{noteId}'.");
        if (!part.notes.Contains(note))
        {
            throw new BridgeException("invalid_request", $"Note '{noteId}' is not in this part.");
        }
        return (project, part, note);
    }

    private static List<UNote> NoteList(UVoicePart part, JsonElement? args)
    {
        if (Args.Get(args, "noteIds") is { ValueKind: JsonValueKind.Array } array)
        {
            var result = new List<UNote>();
            foreach (var element in array.EnumerateArray())
            {
                var id = element.GetString();
                var note = id == null ? null : Session.NoteById(id);
                if (note == null || !part.notes.Contains(note))
                {
                    throw new BridgeException("invalid_request", $"Unknown note id '{id}'.");
                }
                result.Add(note);
            }
            if (result.Count == 0)
            {
                throw new BridgeException("invalid_request", "noteIds must not be empty.");
            }
            return result;
        }

        var single = NoteTarget(args);
        return new List<UNote> { single.Note };
    }

    private static void CheckIfMatch(JsonElement? args, string actual, string scope)
    {
        var expected = Args.Str(args, "ifMatch");
        if (string.IsNullOrEmpty(expected))
        {
            return;
        }
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new BridgeException("stale_write", $"{scope} changed since it was read. Re-read it, then retry.");
        }
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

    private static float Number(JsonElement? args, string name, float fallback)
    {
        var element = Args.Get(args, name);
        return element is { ValueKind: JsonValueKind.Number } value ? (float)value.GetDouble() : fallback;
    }

    private static List<(float X, float Y, PitchPointShape Shape)> ParsePoints(JsonElement? args)
    {
        if (Args.Get(args, "points") is not { ValueKind: JsonValueKind.Array } points || points.GetArrayLength() < 2)
        {
            throw new BridgeException("invalid_request", "'points' needs at least two points (start and end).");
        }
        var result = new List<(float, float, PitchPointShape)>();
        foreach (var element in points.EnumerateArray())
        {
            var x = (float)Args.Int(element, "xMs", int.MinValue);
            var y = Args.Get(element, "y") is { ValueKind: JsonValueKind.Number } yElement ? (float)yElement.GetDouble() : float.MinValue;
            if (x == int.MinValue || y == float.MinValue)
            {
                throw new BridgeException("invalid_request", "Each point needs xMs and y.");
            }
            var shapeText = Args.Str(element, "shape") ?? "io";
            if (!Enum.TryParse<PitchPointShape>(shapeText, ignoreCase: true, out var shape))
            {
                throw new BridgeException("invalid_request",
                    $"Unknown shape '{shapeText}'. Valid: {string.Join(", ", Enum.GetNames<PitchPointShape>())}.");
            }
            result.Add((x, y, shape));
        }
        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }

    private static List<(int X, int Y)> ParseCurvePoints(JsonElement? args, UExpressionDescriptor descriptor)
    {
        if (Args.Get(args, "points") is not { ValueKind: JsonValueKind.Array } points || points.GetArrayLength() == 0)
        {
            throw new BridgeException("invalid_request", "'points' must be a non-empty array of {tick, value}.");
        }
        var result = new List<(int, int)>();
        foreach (var element in points.EnumerateArray())
        {
            var tick = Args.Int(element, "tick", int.MinValue);
            var value = Args.Int(element, "value", int.MinValue);
            if (tick == int.MinValue || value == int.MinValue)
            {
                throw new BridgeException("invalid_request", "Each point needs tick and value.");
            }
            if (tick < 0 || tick % UCurve.interval != 0)
            {
                throw new BridgeException("invalid_request",
                    $"Tick {tick} is not on the {UCurve.interval}-tick curve grid.");
            }
            var clamped = Math.Clamp(value, (int)descriptor.min, (int)descriptor.max);
            result.Add((tick, clamped));
        }
        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        for (var i = 1; i < result.Count; i++)
        {
            if (result[i].Item1 == result[i - 1].Item1)
            {
                throw new BridgeException("invalid_request", $"Duplicate tick {result[i].Item1}.");
            }
        }
        return result;
    }

    private static JsonArray PointsJson(IEnumerable<(float X, float Y, PitchPointShape Shape)> points)
    {
        var array = new JsonArray();
        foreach (var point in points)
        {
            array.Add(new JsonObject
            {
                ["xMs"] = point.X,
                ["y"] = point.Y,
                ["semitones"] = Math.Round(point.Y / 10.0, 3),
                ["shape"] = point.Shape.ToString(),
            });
        }
        return array;
    }
}




