using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Plugin.McpBridge.Tools;

/// <summary>Read-only tools. Every one of them runs on the UI thread via <see cref="Ui"/>.</summary>
internal static class ReadTools
{
    private const int DefaultNoteLimit = 500;
    private const int MaxNoteLimit = 5000;
    private const int DefaultChangeLimit = 500;

    public static object Snapshot(JsonElement? args)
    {
        var project = Project();
        var trackNo = Args.IntOrNull(args, "trackNo");
        var range = Args.IntRange(args, "tickRange");
        var includeCurves = Args.Bool(args, "includeCurves", false);
        var includeExpressions = Args.Bool(args, "includeExpressions", false);

        var parts = new JsonArray();
        foreach (var part in Parts(project, trackNo))
        {
            if (range is { } r && (part.End <= r.Start || part.position >= r.End))
            {
                continue;
            }
            var item = new JsonObject
            {
                ["id"] = part.Id.ToString(),
                ["name"] = part.name,
                ["trackNo"] = part.trackNo,
                ["position"] = part.position,
                ["duration"] = part.Duration,
                ["noteCount"] = part.notes.Count,
            };
            if (includeCurves)
            {
                item["curves"] = CurveSummaries(part);
            }
            parts.Add(item);
        }

        var payload = new JsonObject
        {
            ["sessionId"] = Session.Id,
            ["revision"] = Session.Revision,
            ["changeSequence"] = ChangeFeed.Instance.Sequence,
            ["project"] = new JsonObject
            {
                ["name"] = project.name,
                ["path"] = project.FilePath ?? string.Empty,
                ["resolution"] = project.resolution,
                ["lengthTick"] = project.EndTick,
            },
            ["tracks"] = Tracks(project),
            ["parts"] = parts,
        };

        if (includeExpressions)
        {
            payload["expressions"] = Expressions(project, trackNo);
        }
        return payload;
    }

    public static object ListParts(JsonElement? args)
    {
        var project = Project();
        var trackNo = Args.IntOrNull(args, "trackNo");
        var parts = new JsonArray();
        foreach (var part in Parts(project, trackNo))
        {
            parts.Add(new JsonObject
            {
                ["id"] = part.Id.ToString(),
                ["name"] = part.name,
                ["trackNo"] = part.trackNo,
                ["position"] = part.position,
                ["duration"] = part.Duration,
                ["noteCount"] = part.notes.Count,
            });
        }
        return new JsonObject { ["parts"] = parts };
    }

    public static object ReadNotes(JsonElement? args)
    {
        var project = Project();
        var part = FindPart(project, Args.ReqStr(args, "partId"));
        var offset = Math.Max(0, Args.Int(args, "offset", 0));
        var limit = Math.Clamp(Args.Int(args, "limit", DefaultNoteLimit), 1, MaxNoteLimit);
        var detail = Args.Str(args, "detail") ?? "basic";

        var notes = new JsonArray();
        var index = 0;
        foreach (var note in part.notes)
        {
            if (index++ < offset)
            {
                continue;
            }
            if (notes.Count >= limit)
            {
                break;
            }
            notes.Add(Note(project, part, note, detail));
        }

        return new JsonObject
        {
            ["partId"] = part.Id.ToString(),
            ["revision"] = Session.Revision,
            ["total"] = part.notes.Count,
            ["offset"] = offset,
            ["detail"] = detail,
            ["notes"] = notes,
        };
    }

    public static object ReadPitch(JsonElement? args)
    {
        var project = Project();
        var part = FindPart(project, Args.ReqStr(args, "partId"));
        var noteId = Args.ReqStr(args, "noteId");
        var note = Session.NoteById(noteId)
            ?? throw new BridgeException("invalid_request", $"Unknown note id '{noteId}' in this session.");

        var pitch = note.pitch ?? new UPitch();
        var points = new JsonArray();
        foreach (var point in pitch.data)
        {
            points.Add(new JsonObject
            {
                ["xMs"] = point.X,
                ["y"] = point.Y,
                ["semitones"] = Math.Round(point.Y / 10.0, 3),
                ["shape"] = point.shape.ToString(),
            });
        }

        return new JsonObject
        {
            ["partId"] = part.Id.ToString(),
            ["noteId"] = noteId,
            ["fingerprint"] = Fingerprints.Note(part, note),
            ["source"] = "points",
            ["snapFirst"] = pitch.snapFirst,
            ["points"] = points,
        };
    }

    public static object ListExpressions(JsonElement? args)
    {
        var project = Project();
        return new JsonObject
        {
            ["project"] = Expressions(project, null),
            ["tracks"] = Tracks(project),
        };
    }

    public static object ListCurves(JsonElement? args)
    {
        var project = Project();
        var part = FindPart(project, Args.ReqStr(args, "partId"));
        return new JsonObject
        {
            ["partId"] = part.Id.ToString(),
            ["curves"] = CurveSummaries(part),
        };
    }

    public static object ReadCurve(JsonElement? args)
    {
        var project = Project();
        var part = FindPart(project, Args.ReqStr(args, "partId"));
        var abbr = Args.ReqStr(args, "abbr");
        var curve = part.curves.FirstOrDefault(c => string.Equals(c.abbr, abbr, StringComparison.Ordinal))
            ?? throw new BridgeException("invalid_request", $"No curve '{abbr}' on this part.");

        var offset = Math.Max(0, Args.Int(args, "offset", 0));
        var limit = Math.Clamp(Args.Int(args, "limit", 1000), 1, 5000);
        var points = new JsonArray();
        for (var i = offset; i < curve.xs.Count && points.Count < limit; i++)
        {
            points.Add(new JsonObject { ["tick"] = curve.xs[i], ["value"] = curve.ys[i] });
        }

        return new JsonObject
        {
            ["partId"] = part.Id.ToString(),
            ["abbr"] = curve.abbr,
            ["fingerprint"] = Fingerprints.Curve(part, curve.abbr),
            ["total"] = curve.xs.Count,
            ["offset"] = offset,
            ["points"] = points,
        };
    }

    public static object ReadChanges(JsonElement? args)
    {
        var since = Args.Long(args, "sinceSequence", 0);
        var limit = Math.Clamp(Args.Int(args, "limit", DefaultChangeLimit), 1, 1000);
        var entries = ChangeFeed.Instance.Since(since, limit, out var requiresSnapshot);

        var changes = new JsonArray();
        foreach (var entry in entries)
        {
            var item = new JsonObject
            {
                ["sequence"] = entry.Sequence,
                ["command"] = entry.Command,
                ["kind"] = entry.Kind,
                ["at"] = entry.At,
            };
            if (entry.PartId != null)
            {
                item["partId"] = entry.PartId;
            }
            if (entry.TrackNo != null)
            {
                item["trackNo"] = entry.TrackNo.Value;
            }
            if (entry.Curves is { Count: > 0 })
            {
                var curves = new JsonArray();
                foreach (var abbr in entry.Curves)
                {
                    curves.Add(abbr);
                }
                item["curves"] = curves;
            }
            changes.Add(item);
        }

        return new JsonObject
        {
            ["changeSequence"] = ChangeFeed.Instance.Sequence,
            ["revision"] = Session.Revision,
            ["requiresSnapshot"] = requiresSnapshot,
            ["changes"] = changes,
        };
    }

    // ---- helpers ---------------------------------------------------------------

    public static UProject Project()
    {
        UProject? project;
        try
        {
            project = DocManager.Inst.Project;
        }
        catch
        {
            project = null;
        }
        return project ?? throw new BridgeException("no_project", "OpenUtau has no project open.");
    }

    public static UVoicePart FindPart(UProject project, string partId)
    {
        foreach (var part in project.parts.OfType<UVoicePart>())
        {
            if (string.Equals(part.Id.ToString(), partId, StringComparison.OrdinalIgnoreCase))
            {
                return part;
            }
        }
        throw new BridgeException("invalid_request", $"Unknown part id '{partId}'.");
    }

    private static IEnumerable<UVoicePart> Parts(UProject project, int? trackNo) =>
        project.parts.OfType<UVoicePart>().Where(p => trackNo == null || p.trackNo == trackNo.Value);

    private static JsonArray Tracks(UProject project)
    {
        var tracks = new JsonArray();
        for (var i = 0; i < project.tracks.Count; i++)
        {
            var track = project.tracks[i];
            tracks.Add(new JsonObject
            {
                ["index"] = i,
                ["name"] = track.TrackName,
                // UTrack.singer is only refreshed on save; Singer is what the session uses now.
                ["singer"] = track.Singer?.Id ?? track.singer ?? string.Empty,
                ["phonemizer"] = track.phonemizer ?? string.Empty,
                ["mute"] = track.Mute,
                ["solo"] = track.Solo,
                ["volume"] = Math.Round(track.Volume, 2),
                ["pan"] = Math.Round(track.Pan, 2),
                ["color"] = track.TrackColor,
            });
        }
        return tracks;
    }

    private static JsonArray CurveSummaries(UVoicePart part)
    {
        var curves = new JsonArray();
        foreach (var curve in part.curves)
        {
            curves.Add(new JsonObject
            {
                ["abbr"] = curve.abbr,
                ["count"] = curve.xs.Count,
                ["firstTick"] = curve.xs.Count > 0 ? curve.xs[0] : 0,
                ["lastTick"] = curve.xs.Count > 0 ? curve.xs[^1] : 0,
            });
        }
        return curves;
    }

    private static JsonArray Expressions(UProject project, int? trackNo)
    {
        var descriptors = new JsonArray();
        foreach (var pair in project.expressions)
        {
            descriptors.Add(Descriptor(pair.Value));
        }
        if (trackNo is { } index && index >= 0 && index < project.tracks.Count)
        {
            foreach (var descriptor in project.tracks[index].TrackExpressions)
            {
                descriptors.Add(Descriptor(descriptor));
            }
        }
        return descriptors;
    }

    private static JsonObject Descriptor(UExpressionDescriptor descriptor)
    {
        var item = new JsonObject
        {
            ["abbr"] = descriptor.abbr,
            ["name"] = descriptor.name,
            ["type"] = descriptor.type.ToString(),
            ["min"] = descriptor.min,
            ["max"] = descriptor.max,
            ["default"] = descriptor.CustomDefaultValue,
        };
        if (descriptor.options is { Length: > 0 })
        {
            var options = new JsonArray();
            foreach (var option in descriptor.options)
            {
                options.Add(option);
            }
            item["options"] = options;
        }
        if (!string.IsNullOrEmpty(descriptor.flag))
        {
            item["flag"] = descriptor.flag;
        }
        return item;
    }

    private static JsonObject Note(UProject project, UVoicePart part, UNote note, string detail)
    {
        var item = new JsonObject
        {
            ["id"] = Session.IdOf(note),
            ["position"] = note.position,
            ["duration"] = note.duration,
            ["tone"] = note.tone,
            ["toneName"] = MusicMath.GetToneName(note.tone),
            ["lyric"] = note.lyric,
        };

        if (!string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase))
        {
            return item;
        }

        item["tuning"] = note.tuning;
        item["fingerprint"] = Fingerprints.Note(part, note);
        // A hand-edited .ustx can omit these blocks; the host then carries null and its
        // own UI crashes on hover, so the bridge treats them as empty rather than throwing.
        var pitch = note.pitch ?? new UPitch();
        item["snapFirst"] = pitch.snapFirst;
        var points = new JsonArray();
        foreach (var point in pitch.data)
        {
            points.Add(new JsonObject
            {
                ["xMs"] = point.X,
                ["y"] = point.Y,
                ["semitones"] = Math.Round(point.Y / 10.0, 3),
                ["shape"] = point.shape.ToString(),
            });
        }
        item["pitchPoints"] = points;

        var vibrato = note.vibrato ?? new UVibrato();
        item["vibrato"] = new JsonObject
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
        item["phonemizerOverride"] = note.PhonemizerOverride;

        var expressions = new JsonArray();
        foreach (var expression in note.phonemeExpressions)
        {
            expressions.Add(new JsonObject
            {
                ["abbr"] = expression.abbr,
                ["index"] = expression.index,
                ["value"] = Math.Round(expression.value, 4),
            });
        }
        item["expressions"] = expressions;

        var overrides = new JsonArray();
        foreach (var entry in note.phonemeOverrides)
        {
            if (entry.IsEmpty)
            {
                continue;
            }
            overrides.Add(new JsonObject
            {
                ["index"] = entry.index,
                ["phoneme"] = entry.phoneme,
                ["offset"] = entry.offset,
                ["preutterDelta"] = entry.preutterDelta,
                ["overlapDelta"] = entry.overlapDelta,
                ["attackTimeDelta"] = entry.attackTimeDelta,
                ["releaseTimeDelta"] = entry.releaseTimeDelta,
            });
        }
        item["phonemeOverrides"] = overrides;
        return item;
    }
}




